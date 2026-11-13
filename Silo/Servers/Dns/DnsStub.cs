using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Silo.Providers.Network;
using Telefrag.DI;

namespace Silo.Servers.Dns;

/// <summary>A DNS record type the stub can answer with.</summary>
public enum DnsType : ushort
{
    A     = 1,
    Cname = 5,
    Ptr   = 12,
    Mx    = 15,
    Txt   = 16,
    Aaaa  = 28,
    Any   = 255
}

/// <summary>One record of the stub's table.</summary>
public sealed record DnsRecord(string Name, DnsType Type, string Value, int Ttl);

/// <summary>
///     An authoritative DNS server for the names in a table of the Silo: each row is a name and what it is (an IPv4 or IPv6
///     address, or another name it is an alias of). It answers A, AAAA, CNAME, MX, TXT and PTR questions over UDP and TCP; a
///     name that is not in the table gets "no such name", and it never asks anyone else.
/// </summary>
/// <remarks>
///     The table's columns are found by name: <c>Name</c> (or Host), <c>Value</c> (or Address, IP, Data, Target), and,
///     optionally, <c>Type</c> (A, AAAA, CNAME, MX, TXT, PTR; worked out from the value when empty) and <c>TTL</c> (seconds).
///     A table with other column names is read as name, value. A name like <c>*.lab.test</c> answers for every name under
///     <c>lab.test</c> that has no row of its own. Reverse (PTR) questions are also answered from the A and AAAA rows.
/// </remarks>
[Singleton(typeof(IServerService))]
public sealed class DnsStub : IServerService
{
    private const int UdpLimit = 512;

    public string Name        => "dns-stub";
    public string DisplayName => "DNS stub";
    public string Description => "Answers DNS questions for the names in a table of the Silo.";

    public IReadOnlyList<PortProtocol> Protocols { get; } = [PortProtocol.Udp, PortProtocol.Tcp];

    public IReadOnlyList<ServerEndpoint> DefaultEndpoints { get; } = [new(PortProtocol.Udp, 53), new(PortProtocol.Tcp, 53)];

    public IReadOnlyList<ServerSetting> Settings { get; } =
    [
        new("table", "Table", ServerSettingKind.Table, "DNS", "The table of names and addresses (columns Name, Value, and optionally Type and TTL).",
            ["Name", "Type", "Value", "TTL"]),
        new("ttl", "TTL (seconds)", ServerSettingKind.Number, "60", "How long a resolver may keep an answer, for rows without a TTL of their own."),
        new("log", "Log questions", ServerSettingKind.Boolean, "true", "Writes each question and its answer to the server log.")
    ];

    public Task<byte[]?> AnswerAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remote, IServerContext context, CancellationToken cancel)
    {
        return Task.FromResult(Respond(datagram.Span, remote, context, UdpLimit));
    }

    public async Task ServeAsync(IServerConnection connection, IServerContext context, CancellationToken cancel)
    {
        var length = new byte[2];
        while (!cancel.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                if (!await ReadExactly(connection.Stream, length, idle.Token)) return;

                var query = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
                if (!await ReadExactly(connection.Stream, query, idle.Token)) return;

                if (Respond(query, connection.Remote, context, ushort.MaxValue) is not { } reply) return;

                var framed = new byte[reply.Length + 2];
                BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)reply.Length);
                reply.CopyTo(framed, 2);
                await connection.Stream.WriteAsync(framed, cancel);
                await connection.Stream.FlushAsync(cancel);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                return; // idle
            }
        }
    }

    private static async Task<bool> ReadExactly(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancel);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    private byte[]? Respond(ReadOnlySpan<byte> query, IPEndPoint remote, IServerContext context, int limit)
    {
        string table   = context.Setting("table") ?? "DNS";
        int    ttl     = int.TryParse(context.Setting("ttl"), out int t) && t >= 0 ? t : 60;
        var    records = context.Tables.Read(table) is { } rows ? ReadTable(rows, ttl) : null;

        var reply = Answer(query, records, limit, out string summary);
        if (reply is not null && !string.Equals(context.Setting("log"), "false", StringComparison.OrdinalIgnoreCase))
            context.Log($"DNS {remote.Address}: {summary}");
        return reply;
    }

    /// <summary>The records of a table (rows that cannot be read are left out).</summary>
    public static IReadOnlyList<DnsRecord> ReadTable(ServerTable table, int defaultTtl)
    {
        int name  = table.ColumnOf("Name", "Host", "Hostname", "Domain", "FQDN");
        int value = table.ColumnOf("Value", "Address", "IP", "Data", "Target", "Answer");
        int type  = table.ColumnOf("Type", "RecordType", "Record");
        int ttl   = table.ColumnOf("TTL");
        if (name < 0 && value < 0 && table.Columns.Count >= 2) (name, value) = (0, 1);
        if (name < 0 || value < 0) return [];

        var list = new List<DnsRecord>();
        foreach (var row in table.Rows)
        {
            string n = Normalize(Cell(row, name)), v = Cell(row, value).Trim();
            if (n.Length == 0 || v.Length == 0) continue;

            var kind = TypeOf(Cell(row, type), v);
            if (kind is null) continue;
            if (kind is DnsType.A or DnsType.Aaaa &&
                (!IPAddress.TryParse(v, out var ip) || ip.AddressFamily != (kind == DnsType.A ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6))) continue;

            int seconds = int.TryParse(Cell(row, ttl), out int s) && s >= 0 ? s : defaultTtl;
            list.Add(new DnsRecord(n, kind.Value, v, seconds));
        }

        return list;
    }

    private static string Cell(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count ? row[index] ?? "" : "";

    private static string Normalize(string name) => name.Trim().TrimEnd('.').ToLowerInvariant();

    private static DnsType? TypeOf(string type, string value)
    {
        if (string.IsNullOrWhiteSpace(type))
            return IPAddress.TryParse(value, out var ip) ? ip.AddressFamily == AddressFamily.InterNetworkV6 ? DnsType.Aaaa : DnsType.A : DnsType.Cname;

        return Enum.TryParse<DnsType>(type.Trim(), true, out var parsed) && parsed != DnsType.Any ? parsed : null;
    }

    // ---- the protocol -------------------------------------------------------------------------------------------

    /// <summary>
    ///     The response to a DNS query, answered from <paramref name="records" /> (null when the table is missing: every question
    ///     then fails). Null when the query is too broken to answer at all.
    /// </summary>
    public static byte[]? Answer(ReadOnlySpan<byte> query, IReadOnlyList<DnsRecord>? records, int limit, out string summary)
    {
        summary = "unreadable query";
        if (query.Length < 12) return null;

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(query), flags = BinaryPrimitives.ReadUInt16BigEndian(query[2..]);
        if ((flags & 0x8000) != 0) return null; // a response, not a question

        int opcode = (flags >> 11) & 0xF;
        int qdcount = BinaryPrimitives.ReadUInt16BigEndian(query[4..]);
        var writer  = new DnsWriter(id, flags);

        if (opcode != 0)
        {
            summary = $"opcode {opcode} not implemented";
            return writer.Finish(rcode: 4, limit);
        }

        if (qdcount != 1 || !TryReadName(query, 12, out string qname, out int at) || at + 4 > query.Length)
        {
            summary = "malformed question";
            return writer.Finish(rcode: 1, limit);
        }

        var qtype  = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(query[at..]);
        int qclass = BinaryPrimitives.ReadUInt16BigEndian(query[(at + 2)..]);
        writer.Question(qname, (ushort)qtype, (ushort)qclass);
        string asked = $"{qname} {Label(qtype)}";

        if (records is null)
        {
            summary = asked + " -> SERVFAIL (the table is missing)";
            return writer.Finish(rcode: 2, limit);
        }

        if (qclass is not (1 or 255))
        {
            summary = asked + " -> REFUSED (class " + qclass + ")";
            return writer.Finish(rcode: 5, limit);
        }

        var answers = Resolve(records, qname, qtype, out bool exists);
        foreach (var r in answers) writer.Answer(r);
        summary = asked + " -> " + (answers.Count > 0 ? string.Join(", ", answers.Select(a => $"{Label(a.Type)} {a.Value}")) : exists ? "no records of that type" : "NXDOMAIN");
        return writer.Finish(rcode: exists ? 0 : 3, limit);
    }

    private static string Label(DnsType type) => Enum.IsDefined(type) ? type.ToString().ToUpperInvariant() : "TYPE" + (ushort)type;

    /// <summary>What answers <paramref name="name" />: its records of the type (following aliases), and whether the name is known at all.</summary>
    public static IReadOnlyList<DnsRecord> Resolve(IReadOnlyList<DnsRecord> records, string name, DnsType type, out bool exists)
    {
        var answers = new List<DnsRecord>();
        name   = Normalize(name);
        exists = false;

        for (int hops = 0; hops < 8; hops++)
        {
            var own = RecordsOf(records, name);
            if (own.Count == 0 && type == DnsType.Ptr && Reverse(records, name) is { } ptr)
            {
                exists = true;
                answers.Add(ptr);
                return answers;
            }

            if (own.Count == 0) return answers;

            exists = true;
            var matching = own.Where(r => type == DnsType.Any || r.Type == type).ToList();
            if (matching.Count > 0 || type == DnsType.Cname)
            {
                answers.AddRange(matching);
                return answers;
            }

            if (own.FirstOrDefault(r => r.Type == DnsType.Cname) is not { } alias) return answers;

            answers.Add(alias);
            name = Normalize(alias.Value);
        }

        return answers;
    }

    /// <summary>The records of a name, as asked (a wildcard row answers in the asked name).</summary>
    private static List<DnsRecord> RecordsOf(IReadOnlyList<DnsRecord> records, string name)
    {
        var exact = records.Where(r => r.Name == name).ToList();
        if (exact.Count > 0) return exact;

        for (int dot = name.IndexOf('.'); dot >= 0; dot = name.IndexOf('.', dot + 1))
        {
            string wildcard = "*" + name[dot..];
            var    matched  = records.Where(r => r.Name == wildcard).Select(r => r with { Name = name }).ToList();
            if (matched.Count > 0) return matched;
        }

        return [];
    }

    /// <summary>The PTR record for a reverse name (<c>4.3.2.1.in-addr.arpa</c>) made from an A or AAAA row with that address.</summary>
    private static DnsRecord? Reverse(IReadOnlyList<DnsRecord> records, string name)
    {
        if (AddressOfReverse(name) is not { } address) return null;

        var row = records.FirstOrDefault(r => r.Type is DnsType.A or DnsType.Aaaa && !r.Name.StartsWith('*') && IPAddress.TryParse(r.Value, out var ip) && ip.Equals(address));
        return row is null ? null : new DnsRecord(name, DnsType.Ptr, row.Name, row.Ttl);
    }

    private static IPAddress? AddressOfReverse(string name)
    {
        if (name.EndsWith(".in-addr.arpa", StringComparison.Ordinal))
        {
            var parts = name[..^".in-addr.arpa".Length].Split('.');
            return parts.Length == 4 && IPAddress.TryParse(string.Join('.', parts.Reverse()), out var v4) ? v4 : null;
        }

        if (name.EndsWith(".ip6.arpa", StringComparison.Ordinal))
        {
            var nibbles = name[..^".ip6.arpa".Length].Split('.');
            if (nibbles.Length != 32 || nibbles.Any(n => n.Length != 1 || !Uri.IsHexDigit(n[0]))) return null;

            var hex   = string.Concat(nibbles.Reverse());
            var bytes = Convert.FromHexString(hex);
            return new IPAddress(bytes);
        }

        return null;
    }

    /// <summary>Reads a (possibly compressed) name starting at <paramref name="offset" />; <paramref name="next" /> is where the data after it starts.</summary>
    public static bool TryReadName(ReadOnlySpan<byte> message, int offset, out string name, out int next)
    {
        var labels = new List<string>();
        name = "";
        next = -1;
        int at = offset, jumps = 0;
        while (true)
        {
            if (at >= message.Length) return false;

            int len = message[at];
            if ((len & 0xC0) == 0xC0)
            {
                if (at + 1 >= message.Length || ++jumps > 16) return false;
                if (next < 0) next = at + 2;
                at = ((len & 0x3F) << 8) | message[at + 1];
                continue;
            }

            if (len == 0)
            {
                if (next < 0) next = at + 1;
                break;
            }

            if (len > 63 || at + 1 + len > message.Length) return false;

            labels.Add(Encoding.ASCII.GetString(message.Slice(at + 1, len)));
            at += 1 + len;
        }

        name = string.Join('.', labels).ToLowerInvariant();
        return true;
    }

    /// <summary>Builds a response.</summary>
    private sealed class DnsWriter(ushort id, ushort queryFlags)
    {
        private readonly List<byte[]> _answers = [];
        private          byte[]       _question = [];

        public void Question(string name, ushort type, ushort @class)
        {
            var bytes = new List<byte>(Name(name));
            bytes.AddRange([(byte)(type >> 8), (byte)type, (byte)(@class >> 8), (byte)@class]);
            _question = bytes.ToArray();
        }

        public void Answer(DnsRecord r)
        {
            byte[] data;
            switch (r.Type)
            {
                case DnsType.A or DnsType.Aaaa:
                    data = IPAddress.Parse(r.Value).GetAddressBytes();
                    break;
                case DnsType.Cname or DnsType.Ptr:
                    data = Name(r.Value);
                    break;
                case DnsType.Mx:
                {
                    var parts = r.Value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    ushort preference = parts.Length == 2 && ushort.TryParse(parts[0], out var p) ? p : (ushort)10;
                    data = [(byte)(preference >> 8), (byte)preference, ..Name(parts[^1])];
                    break;
                }
                case DnsType.Txt:
                {
                    var text   = Encoding.UTF8.GetBytes(r.Value);
                    var chunks = new List<byte>();
                    for (int i = 0; i < text.Length; i += 255)
                    {
                        int n = Math.Min(255, text.Length - i);
                        chunks.Add((byte)n);
                        chunks.AddRange(text.AsSpan(i, n).ToArray());
                    }

                    data = chunks.ToArray();
                    break;
                }
                default:
                    return;
            }

            var record = new List<byte>(Name(r.Name));
            record.AddRange([(byte)((ushort)r.Type >> 8), (byte)r.Type, 0, 1]);
            record.AddRange([(byte)(r.Ttl >> 24), (byte)(r.Ttl >> 16), (byte)(r.Ttl >> 8), (byte)r.Ttl]);
            record.AddRange([(byte)(data.Length >> 8), (byte)data.Length]);
            record.AddRange(data);
            _answers.Add(record.ToArray());
        }

        public byte[] Finish(int rcode, int limit)
        {
            // QR, the query's opcode and RD, AA; no recursion available
            ushort flags = (ushort)(0x8000 | (queryFlags & 0x7900) | 0x0400 | rcode);
            int    count = _answers.Count;
            int    size  = 12 + _question.Length + _answers.Sum(a => a.Length);
            while (size > limit && count > 0)
            {
                count--;
                size  -= _answers[count].Length;
                flags |= 0x0200; // truncated: ask again over TCP
            }

            var message = new byte[size];
            BinaryPrimitives.WriteUInt16BigEndian(message, id);
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), flags);
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), (ushort)(_question.Length > 0 ? 1 : 0));
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6), (ushort)count);
            int at = 12;
            _question.CopyTo(message, at);
            at += _question.Length;
            for (int i = 0; i < count; i++)
            {
                _answers[i].CopyTo(message, at);
                at += _answers[i].Length;
            }

            return message;
        }

        private static byte[] Name(string name)
        {
            var bytes = new List<byte>();
            foreach (var label in name.Trim().TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var ascii = Encoding.ASCII.GetBytes(label.Length > 63 ? label[..63] : label);
                bytes.Add((byte)ascii.Length);
                bytes.AddRange(ascii);
            }

            bytes.Add(0);
            return bytes.ToArray();
        }
    }
}
