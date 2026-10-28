using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Silo.Providers.Tree;

/// <summary>The record types the DNS tree knows by name (RFC 1035 and the DNSSEC and service-location types after it).</summary>
public enum DnsType : ushort
{
    A      = 1,
    NS     = 2,
    CNAME  = 5,
    SOA    = 6,
    PTR    = 12,
    MX     = 15,
    TXT    = 16,
    AAAA   = 28,
    SRV    = 33,
    OPT    = 41,
    DS     = 43,
    RRSIG  = 46,
    DNSKEY = 48,
    CAA    = 257
}

/// <summary>One resource record of an answer, its data written the way zone files write it.</summary>
/// <param name="Name">The owner name, lower case, with the root dot ("example.com.").</param>
/// <param name="Type">The type; a number the enum does not name is still carried (<c>(DnsType)99</c>).</param>
/// <param name="Class">1 for the Internet.</param>
/// <param name="Ttl">Seconds the record may be kept.</param>
/// <param name="Data">The data: "93.184.216.34", "10 mail.example.com.", "\"v=spf1 -all\"", the fields of an SOA, a key as base 64...</param>
public sealed record DnsRecord(string Name, DnsType Type, ushort Class, uint Ttl, string Data)
{
    /// <summary>The type as written ("A", "MX"; "TYPE99" for one that has no name).</summary>
    public string TypeName => Enum.IsDefined(Type) ? Type.ToString() : "TYPE" + (ushort)Type;

    public override string ToString() => $"{Name} {Ttl} IN {TypeName} {Data}";
}

/// <summary>What a name server answered.</summary>
/// <param name="ResponseCode">0 no error, 2 server failure, 3 the name does not exist (NXDOMAIN), 5 refused...</param>
/// <param name="Authenticated">The server says it checked the DNSSEC signatures (the AD flag).</param>
/// <param name="Authoritative">The answer comes from a server that holds the zone (the AA flag).</param>
/// <param name="Truncated">The answer did not fit and was cut (TC); a query over UDP asks again over TCP.</param>
public sealed record DnsAnswer(IReadOnlyList<DnsRecord> Answers,       IReadOnlyList<DnsRecord> Authority, IReadOnlyList<DnsRecord> Additional,
                               int                      ResponseCode,  bool                     Authenticated,
                               bool                     Authoritative, bool                     Truncated, IPAddress? Server = null)
{
    /// <summary>The meaning of <see cref="ResponseCode" />.</summary>
    public string Status => ResponseCode switch
                            {
                                0 => "NOERROR",
                                1 => "FORMERR",
                                2 => "SERVFAIL",
                                3 => "NXDOMAIN",
                                4 => "NOTIMP",
                                5 => "REFUSED",
                                _ => "RCODE" + ResponseCode
                            };
}

/// <summary>An answer that could not be read, or no server to ask.</summary>
public sealed class DnsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
///     A small DNS client: the .NET class library only resolves addresses, and the tree needs every kind of record. It asks
///     the computer's own resolvers (a query over UDP, over TCP when the answer is cut), with EDNS0 and the DNSSEC-OK flag so the
///     signatures and keys come too.
/// </summary>
public static class DnsClient
{
    private const int  UdpSize = 1232;
    private const int  Timeout = 3000;
    private const int  Port    = 53;

    /// <summary>The name servers the computer is set to use, else two public ones.</summary>
    public static IReadOnlyList<IPAddress> Resolvers()
    {
        var found = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var address in nic.GetIPProperties().DnsAddresses)
                {
                    // fec0::/10 are the site-local addresses Windows lists for DNS when nothing is configured
                    if (address.AddressFamily == AddressFamily.InterNetworkV6 && (address.IsIPv6SiteLocal || address.IsIPv6LinkLocal)) continue;
                    if (!found.Contains(address)) found.Add(address);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // no list: the public servers below
        }

        if (found.Count == 0) found.AddRange([IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")]);
        return found;
    }

    /// <summary>Asks for the records of <paramref name="type" /> at <paramref name="name" />.</summary>
    /// <param name="server">The server to ask; null: the computer's resolvers, one after the other until one answers.</param>
    public static async Task<DnsAnswer> QueryAsync(string name, DnsType type, IPAddress? server = null, CancellationToken cancel = default)
    {
        var servers = server is null ? Resolvers() : [server];

        Exception? last = null;
        foreach (var candidate in servers)
        {
            try
            {
                return await QueryServerAsync(name, type, candidate, cancel);
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or DnsException or IOException)
            {
                last = ex;
            }
        }

        throw new DnsException($"No name server answered the query for {type} {name}" + (last is null ? "" : ": " + last.Message), last);
    }

    private static async Task<DnsAnswer> QueryServerAsync(string name, DnsType type, IPAddress server, CancellationToken cancel)
    {
        ushort id    = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
        byte[] query = BuildQuery(name, type, id);

        using var udp = new UdpClient(server.AddressFamily);
        udp.Connect(server, Port);
        await udp.SendAsync(query, cancel);

        DnsAnswer? answer = null;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            timeout.CancelAfter(Timeout);
            try
            {
                while (answer is null)
                {
                    var received = await udp.ReceiveAsync(timeout.Token);
                    if (received.Buffer.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(received.Buffer) == id)
                        answer = ParseResponse(received.Buffer, server);
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new TimeoutException($"{server} did not answer in time");
            }
        }

        return answer.Truncated ? await QueryTcpAsync(query, id, server, cancel) : answer;
    }

    private static async Task<DnsAnswer> QueryTcpAsync(byte[] query, ushort id, IPAddress server, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(Timeout * 2);
        try
        {
            using var tcp = new TcpClient(server.AddressFamily);
            await tcp.ConnectAsync(server, Port, timeout.Token);
            await using var stream = tcp.GetStream();

            byte[] framed = new byte[query.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
            query.CopyTo(framed, 2);
            await stream.WriteAsync(framed, timeout.Token);

            byte[] length = new byte[2];
            await stream.ReadExactlyAsync(length, timeout.Token);
            byte[] body = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
            await stream.ReadExactlyAsync(body, timeout.Token);

            if (body.Length < 2 || BinaryPrimitives.ReadUInt16BigEndian(body) != id) throw new DnsException("The answer is not to the question");
            return ParseResponse(body, server);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"{server} did not answer in time");
        }
    }

    /// <summary>The bytes of a query: one question, recursion wanted, and an EDNS0 record that asks for DNSSEC data.</summary>
    public static byte[] BuildQuery(string name, DnsType type, ushort id)
    {
        using var stream = new MemoryStream();
        void U16(int value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        U16(id);
        U16(0x0100); // recursion desired
        U16(1);      // one question
        U16(0);
        U16(0);
        U16(1); // one additional record: OPT

        foreach (string label in Labels(name))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new DnsException($"'{label}' is not a valid DNS label");

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        U16((ushort)type);
        U16(1); // IN

        stream.WriteByte(0); // OPT: the root as owner
        U16((ushort)DnsType.OPT);
        U16(UdpSize);
        U16(0);      // extended rcode, version
        U16(0x8000); // DNSSEC OK
        U16(0);      // no options
        return stream.ToArray();
    }

    /// <summary>The labels of a name, without the root ("www.example.com." gives www, example, com).</summary>
    public static IEnumerable<string> Labels(string name) => name.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Reads the bytes of an answer.</summary>
    /// <exception cref="DnsException">The answer is cut short or makes no sense.</exception>
    public static DnsAnswer ParseResponse(byte[] message, IPAddress? server = null)
    {
        try
        {
            if (message.Length < 12) throw new DnsException("The answer is too short");

            int flags   = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2));
            int qd      = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4));
            int an      = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6));
            int ns      = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(8));
            int ar      = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(10));
            int offset  = 12;

            for (int i = 0; i < qd; i++)
            {
                ReadName(message, ref offset);
                offset += 4;
            }

            var answers    = ReadRecords(message, ref offset, an);
            var authority  = ReadRecords(message, ref offset, ns);
            var additional = ReadRecords(message, ref offset, ar).Where(r => r.Type != DnsType.OPT).ToList();

            return new DnsAnswer(answers, authority, additional, flags & 0xF, (flags & 0x0020) != 0, (flags & 0x0400) != 0, (flags & 0x0200) != 0, server);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new DnsException("The answer is cut short", ex);
        }
    }

    private static List<DnsRecord> ReadRecords(byte[] m, ref int offset, int count)
    {
        var records = new List<DnsRecord>(count);
        for (int i = 0; i < count; i++)
        {
            string name   = ReadName(m, ref offset);
            var    type   = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(offset));
            ushort cls    = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(offset + 2));
            uint   ttl    = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(offset + 4));
            int    length = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(offset + 8));
            offset += 10;
            if (offset + length > m.Length) throw new DnsException("A record runs past the end of the answer");

            records.Add(new DnsRecord(name, type, cls, ttl, ReadData(m, offset, length, type)));
            offset += length;
        }

        return records;
    }

    // a name, following compression pointers; offset ends after the name as it is written at that place
    private static string ReadName(byte[] m, ref int offset)
    {
        var  sb     = new StringBuilder();
        int  at     = offset;
        bool jumped = false;
        int  guard  = 0;

        while (true)
        {
            if (++guard > 128) throw new DnsException("A name points back at itself");

            int length = m[at];
            if (length == 0)
            {
                at++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                int target = ((length & 0x3F) << 8) | m[at + 1];
                if (!jumped) offset = at + 2;
                jumped = true;
                at     = target;
                continue;
            }

            sb.Append(Encoding.ASCII.GetString(m, at + 1, length).ToLowerInvariant()).Append('.');
            at += length + 1;
        }

        if (!jumped) offset = at;
        return sb.Length == 0 ? "." : sb.ToString();
    }

    private static string ReadData(byte[] m, int start, int length, DnsType type)
    {
        var data = m.AsSpan(start, length);
        int at   = start;
        int end  = start + length;

        switch (type)
        {
            case DnsType.A when length == 4:
                return new IPAddress(data).ToString();
            case DnsType.AAAA when length == 16:
                return new IPAddress(data).ToString();
            case DnsType.NS or DnsType.CNAME or DnsType.PTR:
                return ReadName(m, ref at);
            case DnsType.MX:
            {
                int pref = BinaryPrimitives.ReadUInt16BigEndian(data);
                at += 2;
                return $"{pref} {ReadName(m, ref at)}";
            }
            case DnsType.TXT:
            {
                var parts = new List<string>();
                while (at < end)
                {
                    int n = m[at++];
                    parts.Add("\"" + Encoding.UTF8.GetString(m, at, n).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
                    at += n;
                }

                return string.Join(" ", parts);
            }
            case DnsType.SOA:
            {
                string mname = ReadName(m, ref at);
                string rname = ReadName(m, ref at);
                uint[] t     = Enumerable.Range(0, 5).Select(i => BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(at + i * 4))).ToArray();
                return $"{mname} {rname} {t[0]} {t[1]} {t[2]} {t[3]} {t[4]}";
            }
            case DnsType.SRV:
            {
                int priority = BinaryPrimitives.ReadUInt16BigEndian(data);
                int weight   = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
                int port     = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
                at += 6;
                return $"{priority} {weight} {port} {ReadName(m, ref at)}";
            }
            case DnsType.DS:
                return $"{BinaryPrimitives.ReadUInt16BigEndian(data)} {data[2]} {data[3]} {Convert.ToHexString(data[4..])}";
            case DnsType.DNSKEY:
                return $"{BinaryPrimitives.ReadUInt16BigEndian(data)} {data[2]} {data[3]} {Convert.ToBase64String(data[4..])}";
            case DnsType.RRSIG:
            {
                var    covered = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(data);
                int    alg     = data[2];
                int    labels  = data[3];
                uint   ttl     = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                uint   expires = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
                uint   incepts = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
                int    keyTag  = BinaryPrimitives.ReadUInt16BigEndian(data[16..]);
                at += 18;
                string signer = ReadName(m, ref at);
                string name   = Enum.IsDefined(covered) ? covered.ToString() : "TYPE" + (ushort)covered;
                return $"{name} {alg} {labels} {ttl} {Stamp(expires)} {Stamp(incepts)} {keyTag} {signer} {Convert.ToBase64String(m, at, end - at)}";
            }
            case DnsType.CAA:
            {
                int flags = data[0];
                int n     = data[1];
                return $"{flags} {Encoding.ASCII.GetString(data.Slice(2, n))} \"{Encoding.UTF8.GetString(data[(2 + n)..])}\"";
            }
            default:
                return length == 0 ? "" : "\\# " + length + " " + Convert.ToHexString(data);
        }
    }

    // DNSSEC writes times as YYYYMMDDHHMMSS (UTC)
    private static string Stamp(uint seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyyMMddHHmmss");
}
