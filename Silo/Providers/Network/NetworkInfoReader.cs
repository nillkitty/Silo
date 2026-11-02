using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Win32;
using Telefrag.DI;

namespace Silo.Providers.Network;

/// <summary>One address of an interface, as a CIDR (<c>192.168.1.5/24</c>).</summary>
public record NetAddress(string Interface, IPAddress Address, int PrefixLength, string Origin, string Family)
{
    public string Cidr => $"{Address}/{PrefixLength}";
}

/// <summary>One entry of the IP routing table.</summary>
public record NetRoute(
    int       InterfaceIndex,
    string    Interface,
    IPAddress Prefix,
    int       PrefixLength,
    IPAddress NextHop,
    int       Metric,
    string    Protocol,
    string    Family,
    uint      AgeSeconds)
{
    public string Destination => $"{Prefix}/{PrefixLength}";

    /// <summary>True for the default route (<c>0.0.0.0/0</c> or <c>::/0</c>).</summary>
    public bool IsDefault => PrefixLength == 0;
}

/// <summary>A network interface as the operating system reports it (the live state, DNS included).</summary>
public record NetInterface(
    string                Name,
    string                Description,
    string                Id,
    int                   Index,
    NetworkInterfaceType  Type,
    OperationalStatus     Status,
    string                Mac,
    long                  Speed,
    int                   Mtu,
    bool                  DhcpEnabled,
    bool                  DnsEnabled,
    bool                  DynamicDns,
    string?               DnsSuffix,
    IReadOnlyList<IPAddress> DnsServers,
    IReadOnlyList<IPAddress> Gateways,
    IReadOnlyList<NetAddress> Addresses);

/// <summary>
///     An adapter as configured (its driver's registry settings), whether or not it is currently present: a disabled
///     adapter is not listed by the live interface table at all.
/// </summary>
public record AdapterConfig(
    string  Name,
    string  Description,
    string  Id,
    bool    Present,
    string  Status,
    string  Type,
    string? Mac,
    string? MacOverride,
    string? Driver,
    string? DriverVersion,
    string? Provider,
    long?   Speed);

/// <summary>A saved wireless profile. Key material is never read.</summary>
public record WifiProfile(
    string Interface,
    string Name,
    string Ssid,
    string Authentication,
    string Encryption,
    string ConnectionMode,
    bool   Hidden,
    bool   AllUsers);

/// <summary>The host-wide network settings (not those of one interface).</summary>
public record HostNetworkConfig(
    string                   HostName,
    string                   DomainName,
    string                   NodeType,
    string?                  DhcpScopeName,
    bool                     IsWinsProxy,
    IReadOnlyList<IPAddress> DnsServers,
    IReadOnlyList<string>    DnsSuffixes);

/// <summary>One host-global networking parameter, the kind <c>netsh</c> shows (<c>Group</c> "IPv6", <c>Name</c> "Use Temporary Addresses").</summary>
public record HostSetting(string Group, string Name, string Value);

/// <summary>Where the network connector reads the machine's network configuration and state from.</summary>
public interface INetworkInfoReader
{
    /// <summary>The host-wide IP, IPv6 privacy, TCP stack and offload parameters. A group the machine cannot report is left out.</summary>
    IReadOnlyList<HostSetting> GetHostSettings();

    IReadOnlyList<NetInterface>  GetInterfaces();
    IReadOnlyList<NetRoute>      GetRoutes();
    IReadOnlyList<AdapterConfig> GetAdapters();
    IReadOnlyList<WifiProfile>   GetWifiProfiles();
    HostNetworkConfig            GetHostConfig();
    IReadOnlyList<PortOwner>     GetSockets();
}

/// <summary>The <see cref="INetworkInfoReader" /> for this Windows machine.</summary>
[Singleton(typeof(INetworkInfoReader))]
public sealed class SystemNetworkInfoReader : INetworkInfoReader
{
    private const string AdapterClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    private readonly WindowsLocalPortOwnershipProvider _ports = new();

    // ---- interfaces ------------------------------------------------------------------------------------------

    public IReadOnlyList<NetInterface> GetInterfaces()
    {
        var result = new List<NetInterface>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var props = nic.GetIPProperties();
            var index = IndexOf(props);
            var addrs = props.UnicastAddresses
                             .Select(u => new NetAddress(nic.Name, u.Address, u.PrefixLength, u.PrefixOrigin.ToString(),
                                                         u.Address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6"))
                             .ToList();

            var dhcp = false;
            try
            {
                dhcp = props.GetIPv4Properties().IsDhcpEnabled;
            }
            catch (NetworkInformationException)
            {
                // no IPv4 on this interface
            }

            var mtu = 0;
            try
            {
                mtu = props.GetIPv4Properties().Mtu;
            }
            catch (NetworkInformationException)
            {
            }

            result.Add(new NetInterface(nic.Name, nic.Description, nic.Id, index, nic.NetworkInterfaceType, nic.OperationalStatus,
                                        FormatMac(nic.GetPhysicalAddress()), nic.Speed, mtu, dhcp, props.IsDnsEnabled,
                                        props.IsDynamicDnsEnabled, props.DnsSuffix, props.DnsAddresses.ToList(),
                                        props.GatewayAddresses.Select(g => g.Address).ToList(), addrs));
        }

        return result;
    }

    private static int IndexOf(IPInterfaceProperties props)
    {
        try
        {
            return props.GetIPv4Properties().Index;
        }
        catch (NetworkInformationException)
        {
            try
            {
                return props.GetIPv6Properties().Index;
            }
            catch (NetworkInformationException)
            {
                return -1;
            }
        }
    }

    internal static string FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? "" : string.Join("-", bytes.Select(b => b.ToString("X2")));
    }

    // ---- host ------------------------------------------------------------------------------------------------

    public HostNetworkConfig GetHostConfig()
    {
        var g       = IPGlobalProperties.GetIPGlobalProperties();
        var servers = new List<IPAddress>();
        var suffix  = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            var p = nic.GetIPProperties();
            foreach (var d in p.DnsAddresses.Where(d => !servers.Contains(d))) servers.Add(d);
            if (!string.IsNullOrEmpty(p.DnsSuffix) && !suffix.Contains(p.DnsSuffix, StringComparer.OrdinalIgnoreCase)) suffix.Add(p.DnsSuffix);
        }

        string? scope = null;
        try
        {
            scope = g.DhcpScopeName;
        }
        catch (NetworkInformationException)
        {
        }

        return new HostNetworkConfig(g.HostName, g.DomainName, g.NodeType.ToString(), scope, g.IsWinsProxy, servers, suffix);
    }

    // (group, CIM class, WQL filter) of the settings classes behind `netsh int ip|ipv6|tcp show global` and `netsh int ipv6 show privacy`
    private static readonly (string Group, string Class, string? Filter)[] HostSettingClasses =
    [
        ("IPv4", "MSFT_NetIPv4Protocol", null),
        ("IPv6", "MSFT_NetIPv6Protocol", null),
        ("TCP", "MSFT_NetTCPSetting", "SettingName = 'InternetCustom'"),
        ("TCP", "MSFT_NetTCPSetting", "SettingName = 'Internet'"),
        ("Offload", "MSFT_NetOffloadGlobalSetting", null)
    ];

    private static readonly HashSet<string> SkippedSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "InstanceID", "Caption", "Description", "ElementName", "SettingName", "Name", "PSComputerName"
    };

    public IReadOnlyList<HostSetting> GetHostSettings()
    {
        var result = new List<HostSetting>();
        var done   = new HashSet<string>();
        foreach (var (group, cls, filter) in HostSettingClasses)
        {
            if (done.Contains(group)) continue; // the Custom TCP template wins over the plain one

            try
            {
                var rows = ReadCim(group, cls, filter);
                if (rows.Count == 0) continue;

                done.Add(group);
                result.AddRange(rows);
            }
            catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or COMException)
            {
                // no such class (an older Windows) or no access: this group is left out
            }
        }

        return result;
    }

    private static List<HostSetting> ReadCim(string group, string cls, string? filter)
    {
        var scope = new System.Management.ManagementScope(@"\\.\root\StandardCimv2");
        scope.Connect();

        var rows = new List<HostSetting>();
        using var searcher = new System.Management.ManagementObjectSearcher(scope, new System.Management.ObjectQuery(
                                                                                 $"SELECT * FROM {cls}" + (filter is null ? "" : $" WHERE {filter}")));
        using var results = searcher.Get();
        foreach (System.Management.ManagementObject o in results)
        {
            foreach (var p in o.Properties)
            {
                if (SkippedSettings.Contains(p.Name) || p.Name.StartsWith("__", StringComparison.Ordinal) || p.Value is null) continue;

                rows.Add(new HostSetting(group, Spaced(p.Name), CimValue(p)));
            }

            break; // a global setting has one instance
        }

        return rows;
    }

    private static string CimValue(System.Management.PropertyData p)
    {
        if (p.Value is Array array) return string.Join(", ", array.Cast<object?>().Select(v => MapEnum(p, v)));

        return MapEnum(p, p.Value);
    }

    /// <summary>An enum-like value (<c>ValueMap</c> / <c>Values</c> qualifiers) as its name, else the value as it is.</summary>
    private static string MapEnum(System.Management.PropertyData p, object? value)
    {
        var text = value switch { bool b => b ? "Yes" : "No", null => "", _ => value.ToString() ?? "" };
        try
        {
            if (value is bool || p.Qualifiers["ValueMap"]?.Value is not string[] map || p.Qualifiers["Values"]?.Value is not string[] names) return text;

            var i = Array.IndexOf(map, text);
            return i >= 0 && i < names.Length ? names[i] : text;
        }
        catch (System.Management.ManagementException)
        {
            return text; // the property has no such qualifiers
        }
    }

    /// <summary>"UseTemporaryAddresses" as "Use Temporary Addresses".</summary>
    internal static string Spaced(string name)
    {
        return System.Text.RegularExpressions.Regex.Replace(name, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
    }

    // ---- adapters --------------------------------------------------------------------------------------------

    public IReadOnlyList<AdapterConfig> GetAdapters()
    {
        var live   = NetworkInterface.GetAllNetworkInterfaces().ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<AdapterConfig>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var cls = Registry.LocalMachine.OpenSubKey(AdapterClassKey))
        {
            foreach (var name in cls?.GetSubKeyNames().Where(n => n.All(char.IsDigit)) ?? [])
            {
                try
                {
                    using var k  = cls!.OpenSubKey(name);
                    var       id = k?.GetValue("NetCfgInstanceId") as string;
                    if (k is null || string.IsNullOrEmpty(id)) continue;

                    seen.Add(id);
                    var over = k.GetValue("NetworkAddress") as string;
                    result.Add(Describe(id, live.GetValueOrDefault(id), k.GetValue("DriverDesc") as string, NormalizeMac(over),
                                        k.GetValue("ComponentId") as string, k.GetValue("DriverVersion") as string,
                                        k.GetValue("ProviderName") as string));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // an adapter key we may not read: skip it
                }
            }
        }

        // live interfaces with no driver key (loopback, some virtual ones)
        foreach (var nic in live.Values.Where(n => !seen.Contains(n.Id)))
            result.Add(Describe(nic.Id, nic, null, null, null, null, null));

        return result.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static AdapterConfig Describe(string id, NetworkInterface? nic, string? driverDesc, string? macOverride, string? component,
                                          string? version, string? provider)
    {
        var present = nic is not null;
        return new AdapterConfig(nic?.Name ?? driverDesc ?? id, nic?.Description ?? driverDesc ?? "", id, present,
                                 present ? nic!.OperationalStatus.ToString() : "Disabled or not present",
                                 nic?.NetworkInterfaceType.ToString() ?? "", present ? FormatMac(nic!.GetPhysicalAddress()) : null,
                                 macOverride, component, version, provider, present ? nic!.Speed : null);
    }

    /// <summary>A <c>NetworkAddress</c> driver value (twelve hex digits) as <c>AA-BB-...</c>; null when there is none or it is not one.</summary>
    public static string? NormalizeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var digits = new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (digits.Length != 12) return null;

        return string.Join("-", Enumerable.Range(0, 6).Select(i => digits.Substring(i * 2, 2)));
    }

    // ---- routes ----------------------------------------------------------------------------------------------

    public IReadOnlyList<NetRoute> GetRoutes()
    {
        var names = new Dictionary<int, string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var p = nic.GetIPProperties();
            try
            {
                names[p.GetIPv4Properties().Index] = nic.Name;
            }
            catch (NetworkInformationException)
            {
            }

            try
            {
                names[p.GetIPv6Properties().Index] = nic.Name;
            }
            catch (NetworkInformationException)
            {
            }
        }

        var rc = GetIpForwardTable2(0 /* AF_UNSPEC */, out var table);
        if (rc != 0) throw new System.ComponentModel.Win32Exception(rc);

        try
        {
            var count = Marshal.ReadInt32(table);
            var bytes = new byte[8 + count * RowSize];
            Marshal.Copy(table, bytes, 0, bytes.Length);
            return ParseRoutes(bytes, names);
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    // MIB_IPFORWARD_TABLE2: ULONG NumEntries (padded to 8) then MIB_IPFORWARD_ROW2 (104 bytes):
    //   0 LUID(8) | 8 InterfaceIndex | 12 DestinationPrefix.Prefix SOCKADDR_INET(28) | 40 PrefixLength | 44 NextHop SOCKADDR_INET(28) |
    //   72 SitePrefixLength | 76 ValidLifetime | 80 PreferredLifetime | 84 Metric | 88 Protocol | 92..95 flags | 96 Age | 100 Origin
    private const int RowSize = 104;

    internal static List<NetRoute> ParseRoutes(ReadOnlySpan<byte> table, IReadOnlyDictionary<int, string> interfaceNames)
    {
        var rows = new List<NetRoute>();
        if (table.Length < 8) return rows;

        var n = (int)Math.Min(BitConverter.ToUInt32(table), (uint)((table.Length - 8) / RowSize));
        for (var i = 0; i < n; i++)
        {
            var row   = table.Slice(8 + i * RowSize, RowSize);
            var index = (int)BitConverter.ToUInt32(row.Slice(8, 4));
            var dest  = SockAddr(row.Slice(12, 28), out var v6);
            var hop   = SockAddr(row.Slice(44, 28), out _);
            rows.Add(new NetRoute(index, interfaceNames.GetValueOrDefault(index, $"#{index}"), dest, row[40], hop,
                                  (int)BitConverter.ToUInt32(row.Slice(84, 4)), RouteProtocol(BitConverter.ToUInt32(row.Slice(88, 4))),
                                  v6 ? "IPv6" : "IPv4", BitConverter.ToUInt32(row.Slice(96, 4))));
        }

        return rows;
    }

    // SOCKADDR_INET: family(2) then sockaddr_in {port(2), addr(4)} or sockaddr_in6 {port(2), flow(4), addr(16), scope(4)}
    private static IPAddress SockAddr(ReadOnlySpan<byte> s, out bool v6)
    {
        v6 = BitConverter.ToUInt16(s) == 23; // AF_INET6
        return v6 ? new IPAddress(s.Slice(8, 16), BitConverter.ToUInt32(s.Slice(24, 4))) : new IPAddress(s.Slice(4, 4));
    }

    internal static string RouteProtocol(uint p)
    {
        return p switch
        {
            1  => "Other",
            2  => "Local",
            3  => "Network Management",
            4  => "ICMP",
            8  => "RIP",
            13 => "OSPF",
            14 => "BGP",
            16 => "Static (auto)",
            17 => "Static",
            18 => "Static (non-DoD)",
            _  => p.ToString()
        };
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    // ---- sockets ---------------------------------------------------------------------------------------------

    public IReadOnlyList<PortOwner> GetSockets()
    {
        return _ports.GetAllOwners();
    }

    // ---- wifi ------------------------------------------------------------------------------------------------

    public IReadOnlyList<WifiProfile> GetWifiProfiles()
    {
        var result = new List<WifiProfile>();
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return result; // no WLAN AutoConfig service

        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return result;

            try
            {
                var count = Marshal.ReadInt32(list);
                for (var i = 0; i < count; i++)
                {
                    var item = list + 8 + i * 532; // WLAN_INTERFACE_INFO: GUID(16) + WCHAR[256] + state
                    var guid = Marshal.PtrToStructure<Guid>(item);
                    var desc = Marshal.PtrToStringUni(item + 16, 256).Split('\0')[0];
                    result.AddRange(ReadProfiles(handle, guid, desc));
                }
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }

        return result;
    }

    private static IEnumerable<WifiProfile> ReadProfiles(IntPtr handle, Guid guid, string interfaceName)
    {
        if (WlanGetProfileList(handle, ref guid, IntPtr.Zero, out var list) != 0) yield break;

        var names = new List<(string Name, uint Flags)>();
        try
        {
            var count = Marshal.ReadInt32(list);
            for (var i = 0; i < count; i++)
            {
                var item = list + 8 + i * 516; // WLAN_PROFILE_INFO: WCHAR[256] name + flags
                names.Add((Marshal.PtrToStringUni(item, 256).Split('\0')[0], (uint)Marshal.ReadInt32(item + 512)));
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }

        foreach (var (name, flags) in names)
        {
            // flags is null, so the profile comes back without its key
            if (WlanGetProfile(handle, ref guid, name, IntPtr.Zero, out var xmlPtr, IntPtr.Zero, IntPtr.Zero) != 0) continue;

            string xml;
            try
            {
                xml = Marshal.PtrToStringUni(xmlPtr) ?? "";
            }
            finally
            {
                WlanFreeMemory(xmlPtr);
            }

            yield return ParseProfile(interfaceName, name, xml, (flags & 0x1) != 0 /* WLAN_PROFILE_ALL_USER */);
        }
    }

    public static WifiProfile ParseProfile(string interfaceName, string name, string xml, bool allUsers)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return new WifiProfile(interfaceName, name, "", "", "", "", false, allUsers);
        }

        string Find(params string[] path)
        {
            var e = doc.Root;
            foreach (var step in path) e = e?.Elements().FirstOrDefault(x => x.Name.LocalName == step);
            return e?.Value ?? "";
        }

        var ssid = Find("SSIDConfig", "SSID", "name");
        return new WifiProfile(interfaceName, name, ssid.Length > 0 ? ssid : name, Find("MSM", "security", "authEncryption", "authentication"),
                               Find("MSM", "security", "authEncryption", "encryption"), Find("connectionMode"),
                               string.Equals(Find("SSIDConfig", "nonBroadcast"), "true", StringComparison.OrdinalIgnoreCase), allUsers);
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanGetProfileList(IntPtr handle, ref Guid interfaceGuid, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern int WlanGetProfile(IntPtr handle, ref Guid interfaceGuid, string profileName, IntPtr reserved, out IntPtr xml,
                                             IntPtr flags, IntPtr access);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
