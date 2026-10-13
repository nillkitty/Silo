using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Silo.Contracts.Providers.Network;
using Telefrag.DI;

namespace Silo.Providers.Network;

/// <summary>
///     <see cref="ILocalPortOwnershipProvider" /> for Windows, from the extended TCP/UDP tables (IPv4 and IPv6) that
///     <c>iphlpapi.dll</c> keeps, which -- unlike <see cref="IPGlobalProperties" /> -- name the owning process.
/// </summary>
[Singleton(typeof(ILocalPortOwnershipProvider))]
public sealed class WindowsLocalPortOwnershipProvider : ILocalPortOwnershipProvider
{
    /// <summary>Test seam: where the raw socket list comes from. Defaults to the operating system.</summary>
    internal Func<IReadOnlyList<RawSocket>> Snapshot { get; init; } = ReadSystemTables;

    /// <summary>Test seam: how a process id is described. Defaults to the operating system.</summary>
    internal Func<int, LocalProcessInfo?> Describe { get; init; } = DescribeProcess;

    public IReadOnlyList<PortOwner> GetOwners(int port, PortProtocol protocol = PortProtocol.Tcp, int? remotePort = null)
    {
        if (port is < 1 or > 65535) return [];

        var matches = Snapshot()
                      .Where(s => s.Protocol == protocol && s.LocalPort == port && (remotePort is null || s.RemotePort == remotePort))
                      .OrderBy(s => s.State == TcpState.Listen ? 0 : 1)
                      .ThenBy(s => s.State == TcpState.Established ? 0 : 1)
                      .ThenBy(s => s.Pid)
                      .ToList();

        var processes = new Dictionary<int, LocalProcessInfo?>();
        var owners    = new List<PortOwner>(matches.Count);
        foreach (var s in matches)
        {
            if (!processes.TryGetValue(s.Pid, out var info)) processes[s.Pid] = info = s.Pid == 0 ? null : Describe(s.Pid);

            var local  = new IPEndPoint(s.LocalAddress, s.LocalPort);
            var remote = s.State == TcpState.Listen || s.RemoteAddress is null ? null : new IPEndPoint(s.RemoteAddress, s.RemotePort);
            owners.Add(new PortOwner(s.Protocol, local, remote, s.State, s.Pid, info));
        }

        return owners;
    }

    public PortOwner? GetOwner(int port, PortProtocol protocol = PortProtocol.Tcp, int? remotePort = null)
    {
        return GetOwners(port, protocol, remotePort).FirstOrDefault();
    }

    public PortOwner? GetClient(IPEndPoint remote, int serverPort)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (!IsThisComputer(remote.Address)) return null;

        // the client's own socket is the one whose local port is the port we saw, connected to our server port
        return GetOwners(remote.Port, PortProtocol.Tcp, serverPort).FirstOrDefault(o => !o.IsListener);
    }

    private static bool IsThisComputer(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                                   .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                                   .Any(u => u.Address.Equals(address));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    // ---- the raw table --------------------------------------------------------------------------------------

    /// <summary>One row of the operating system's socket table.</summary>
    internal readonly record struct RawSocket(PortProtocol Protocol, IPAddress LocalAddress, int LocalPort, IPAddress? RemoteAddress, int RemotePort,
                                              TcpState? State, int Pid);

    private static IReadOnlyList<RawSocket> ReadSystemTables()
    {
        var all = new List<RawSocket>();
        all.AddRange(ParseTcp(GetTable(true, AfInet), false));
        all.AddRange(ParseTcp(GetTable(true, AfInet6), true));
        all.AddRange(ParseUdp(GetTable(false, AfInet), false));
        all.AddRange(ParseUdp(GetTable(false, AfInet6), true));
        return all;
    }

    // MIB_TCPROW_OWNER_PID (24 bytes): state, localAddr, localPort, remoteAddr, remotePort, pid
    // MIB_TCP6ROW_OWNER_PID (56):      localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
    // MIB_UDPROW_OWNER_PID (12):       localAddr, localPort, pid
    // MIB_UDP6ROW_OWNER_PID (28):      localAddr[16], localScope, localPort, pid
    // Each table is a 4-byte row count followed by the rows. Ports are network-order in the low 16 bits of a DWORD.
    internal static List<RawSocket> ParseTcp(ReadOnlySpan<byte> table, bool v6)
    {
        var rows = new List<RawSocket>();
        int size = v6 ? 56 : 24;
        for (int i = 0, off = 4, n = RowCount(table, size); i < n; i++, off += size)
        {
            var row = table.Slice(off, size);
            rows.Add(v6
                         ? new RawSocket(PortProtocol.Tcp, Address6(row, 0, 16), Port(row, 20), Address6(row, 24, 40), Port(row, 44), State(U32(row, 48)),
                                         (int)U32(row, 52))
                         : new RawSocket(PortProtocol.Tcp, new IPAddress(row.Slice(4, 4)), Port(row, 8), new IPAddress(row.Slice(12, 4)), Port(row, 16),
                                         State(U32(row, 0)), (int)U32(row, 20)));
        }

        return rows;
    }

    internal static List<RawSocket> ParseUdp(ReadOnlySpan<byte> table, bool v6)
    {
        var rows = new List<RawSocket>();
        int size = v6 ? 28 : 12;
        for (int i = 0, off = 4, n = RowCount(table, size); i < n; i++, off += size)
        {
            var row = table.Slice(off, size);
            rows.Add(v6
                         ? new RawSocket(PortProtocol.Udp, Address6(row, 0, 16), Port(row, 20), null, 0, null, (int)U32(row, 24))
                         : new RawSocket(PortProtocol.Udp, new IPAddress(row.Slice(0, 4)), Port(row, 4), null, 0, null, (int)U32(row, 8)));
        }

        return rows;
    }

    /// <summary>The row count, clamped to what the buffer can actually hold so a short buffer can never be over-read.</summary>
    private static int RowCount(ReadOnlySpan<byte> table, int rowSize)
    {
        if (table.Length < 4) return 0;
        return (int)Math.Min(U32(table, 0), (uint)((table.Length - 4) / rowSize));
    }

    private static uint U32(ReadOnlySpan<byte> row, int offset)
    {
        return BitConverter.ToUInt32(row.Slice(offset, 4));
    }

    private static int Port(ReadOnlySpan<byte> row, int offset)
    {
        var raw = U32(row, offset);
        return (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
    }

    // the scope id follows the 16 address bytes
    private static IPAddress Address6(ReadOnlySpan<byte> row, int offset, int scopeOffset)
    {
        return new IPAddress(row.Slice(offset, 16), U32(row, scopeOffset));
    }

    private static TcpState State(uint mib)
    {
        return Enum.IsDefined(typeof(TcpState), (int)mib) ? (TcpState)(int)mib : TcpState.Unknown;
    }

    private const int AfInet  = 2;
    private const int AfInet6 = 23;

    private static byte[] GetTable(bool tcp, int family)
    {
        const int insufficientBuffer = 122;

        var size   = 0;
        var buffer = (byte[]?)null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var rc = tcp
                         ? GetExtendedTcpTable(buffer, ref size, false, family, 5 /* TCP_TABLE_OWNER_PID_ALL */, 0)
                         : GetExtendedUdpTable(buffer, ref size, false, family, 1 /* UDP_TABLE_OWNER_PID */, 0);
            if (rc == 0) return buffer is null ? [] : buffer[..size];
            if (rc != insufficientBuffer) throw new Win32Exception(rc);

            buffer = new byte[size + 4096]; // the table can grow between the calls
            size   = buffer.Length;
        }

        throw new InvalidOperationException("The socket table kept growing while it was being read.");
    }

    // ---- the owning process ---------------------------------------------------------------------------------

    private static LocalProcessInfo? DescribeProcess(int pid)
    {
        try
        {
            string  name;
            DateTime? started = null;
            try
            {
                using var p = Process.GetProcessById(pid);
                name = p.ProcessName;
                try
                {
                    started = p.StartTime.ToUniversalTime();
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // protected process: no start time
                }
            }
            catch (ArgumentException)
            {
                return null; // already gone
            }

            string? path = null, user = null;
            int?    parent = null;
            using (var h = OpenProcess(ProcessQueryLimitedInformation, false, pid))
            {
                if (!h.IsInvalid)
                {
                    path   = ImagePath(h);
                    parent = ParentId(h);
                    user   = UserOf(h);
                }
            }

            return new LocalProcessInfo(pid, name, path, user, parent, started);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return new LocalProcessInfo(pid, "(unknown)", null, null, null, null);
        }
    }

    private static string? ImagePath(SafeProcessHandle h)
    {
        var sb   = new StringBuilder(1024);
        var size = sb.Capacity;
        return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
    }

    private static int? ParentId(SafeProcessHandle h)
    {
        var info = new ProcessBasicInformation();
        return NtQueryInformationProcess(h, 0 /* ProcessBasicInformation */, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                   ? (int)info.InheritedFromUniqueProcessId
                   : null;
    }

    private static string? UserOf(SafeProcessHandle h)
    {
        if (!OpenProcessToken(h, 0x0008 /* TOKEN_QUERY */, out var token)) return null;

        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.Name;
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(byte[]? table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass,
                                                  uint     reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedUdpTable(byte[]? table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass,
                                                  uint     reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out IntPtr token);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, ref ProcessBasicInformation info, int length,
                                                        out int returnLength);
}
