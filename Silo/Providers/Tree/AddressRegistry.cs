using System.Globalization;
using System.Net;
using System.Net.Http;
using Serilog;

namespace Silo.Providers.Tree;

/// <summary>A block set aside for a special purpose (the IANA special-purpose address registries, RFC 6890 and later).</summary>
/// <param name="Subnet">The block.</param>
/// <param name="Name">What it is for ("Private-Use").</param>
/// <param name="Rfc">The document that says so ("RFC 1918"), or "".</param>
/// <param name="Source">
///     Whether a packet may have an address of the block as its source; null when the registry says
///     "N/A".
/// </param>
/// <param name="Destination">Whether a packet may be sent to the block.</param>
/// <param name="Forwardable">Whether a router may pass such packets on.</param>
/// <param name="Global">Whether the addresses are reachable from the whole Internet.</param>
/// <param name="ReservedByProtocol">Whether the block is part of the protocol itself (loopback, link-local...).</param>
/// <param name="Trivia">An extra sentence worth knowing.</param>
public sealed record SpecialBlock(
    IPSubnet Subnet,
    string   Name,
    string   Rfc,
    bool?    Source,
    bool?    Destination,
    bool?    Forwardable,
    bool?    Global,
    bool?    ReservedByProtocol,
    string?  Trivia = null)
{
    /// <summary>Where the RFC is published, or null.</summary>
    public Uri? RfcUrl => Rfc.StartsWith("RFC ", StringComparison.OrdinalIgnoreCase) && int.TryParse(Rfc[4..].Trim(), out int n)
                              ? new Uri($"https://www.rfc-editor.org/rfc/rfc{n}")
                              : null;
}

/// <summary>One line of an IANA address space registry: who a block was handed to.</summary>
/// <param name="Subnet">The block.</param>
/// <param name="Designation">Who it went to ("APNIC", "Administered by ARIN", "Apple Inc.", "Reserved by IETF").</param>
/// <param name="Status">ALLOCATED, LEGACY, RESERVED or UNALLOCATED.</param>
/// <param name="Date">When, as the registry writes it ("1993-05", "2006-10-03").</param>
/// <param name="Whois">The whois server of the registry that holds the details, or null.</param>
/// <param name="Rdap">The RDAP service of that registry, or null.</param>
/// <param name="Note">The registry's note, or null.</param>
public sealed record RegistryEntry(
    IPSubnet Subnet,
    string   Designation,
    string   Status,
    string?  Date,
    string?  Whois,
    string?  Rdap,
    string?  Note)
{
    /// <summary>The regional registry the designation names (APNIC, ARIN, AFRINIC, LACNIC, RIPE NCC), or null.</summary>
    public string? Rir => AddressRegistry.RirOf(Designation);
}

/// <summary>
///     What is known about blocks of the address space without asking anyone: the special-purpose blocks (built in), and
///     the
///     IANA registries of who the rest went to (downloaded once, kept under the local application data folder and renewed
///     weekly; without them -- offline, say -- the special blocks still describe the well-known ranges).
/// </summary>
public static class AddressRegistry
{
    private const string Ipv4SpaceUrl = "https://www.iana.org/assignments/ipv4-address-space/ipv4-address-space.csv";
    private const string Ipv6SpaceUrl = "https://www.iana.org/assignments/ipv6-address-space/ipv6-address-space.csv";

    private const string Ipv6UnicastUrl =
        "https://www.iana.org/assignments/ipv6-unicast-address-assignments/ipv6-unicast-address-assignments.csv";

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private static readonly object   Gate   = new();

    private static readonly HttpClient Http = CreateClient();

    private static readonly string[] Rirs = ["AFRINIC", "APNIC", "ARIN", "LACNIC", "RIPE"];

    private static Task? _loading;

    /// <summary>The local folder where the downloaded registries are kept.</summary>
    public static string CacheFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Silo", "iana");

    /// <summary>
    ///     The IANA registry lines read so far (empty until <see cref="EnsureLoadedAsync" /> has finished, or when they
    ///     cannot be had).
    /// </summary>
    public static IReadOnlyList<RegistryEntry> Entries { get; private set; } = [];

    /// <summary>The special-purpose blocks, IPv4 and IPv6.</summary>
    public static IReadOnlyList<SpecialBlock> SpecialBlocks { get; } = BuildSpecialBlocks();

    /// <summary>Raised when new registry lines were read.</summary>
    public static event EventHandler? Loaded;

    /// <summary>The regional registry named in a designation ("Administered by RIPE NCC" gives "RIPE NCC"), or null.</summary>
    public static string? RirOf(string? designation)
    {
        if (string.IsNullOrEmpty(designation)) return null;

        foreach (string rir in Rirs)
            if (designation.Contains(rir, StringComparison.OrdinalIgnoreCase))
                return rir == "RIPE" ? "RIPE NCC" : rir;

        return null;
    }

    /// <summary>
    ///     Reads the registries once (from the local copy while it is fresh, else from IANA). Never fails: what cannot be
    ///     read is left out.
    /// </summary>
    public static Task EnsureLoadedAsync()
    {
        lock (Gate)
        {
            return _loading ??= LoadAsync(false);
        }
    }

    /// <summary>Downloads the registries again, whatever the age of the local copy.</summary>
    public static Task RefreshAsync()
    {
        lock (Gate)
        {
            return _loading = LoadAsync(true);
        }
    }

    /// <summary>The most specific special block that holds <paramref name="subnet" /> (or is it), or null.</summary>
    public static SpecialBlock? SpecialFor(IPSubnet subnet)
    {
        SpecialBlock? best = null;
        foreach (var block in SpecialBlocks)
            if (block.Subnet.Covers(subnet) && (best is null || block.Subnet.PrefixLength > best.Subnet.PrefixLength))
                best = block;

        return best;
    }

    /// <summary>The most specific registry line that holds <paramref name="subnet" /> (or is it), or null.</summary>
    public static RegistryEntry? EntryFor(IPSubnet subnet)
    {
        RegistryEntry? best = null;
        foreach (var entry in Entries)
            if (entry.Subnet.Covers(subnet) && (best is null || entry.Subnet.PrefixLength > best.Subnet.PrefixLength))
                best = entry;

        return best;
    }

    /// <summary>
    ///     The blocks the registries and the special-purpose list name that lie inside <paramref name="subnet" />, not
    ///     counting
    ///     it itself, as subnets.
    /// </summary>
    public static IEnumerable<IPSubnet> KnownWithin(IPSubnet subnet)
    {
        foreach (var block in SpecialBlocks)
            if (subnet.StrictlyCovers(block.Subnet))
                yield return block.Subnet;

        foreach (var entry in Entries)
            if (subnet.StrictlyCovers(entry.Subnet))
                yield return entry.Subnet;
    }

    /// <summary>
    ///     The text of a cached download: the local copy while it is fresh (or <paramref name="force" /> is false and the
    ///     network is down), else a new download.
    /// </summary>
    internal static async Task<string?> GetCachedTextAsync(string url, string fileName, bool force, CancellationToken cancel = default)
    {
        string path  = Path.Combine(CacheFolder, fileName);
        bool   have  = File.Exists(path);
        bool   fresh = have && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MaxAge;

        if (fresh && !force) return await File.ReadAllTextAsync(path, cancel);

        try
        {
            string text = await Http.GetStringAsync(url, cancel);
            Directory.CreateDirectory(CacheFolder);
            await File.WriteAllTextAsync(path, text, cancel);
            return text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Log.Information("IANA {File} could not be downloaded ({Message}); using the local copy, if any", fileName, ex.Message);
            return have ? await File.ReadAllTextAsync(path, cancel) : null;
        }
    }

    private static async Task LoadAsync(bool force)
    {
        var entries = new List<RegistryEntry>();
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var       v4     = GetCachedTextAsync(Ipv4SpaceUrl,   "ipv4-address-space.csv",               force, cancel.Token);
            var       v6     = GetCachedTextAsync(Ipv6SpaceUrl,   "ipv6-address-space.csv",               force, cancel.Token);
            var       v6u    = GetCachedTextAsync(Ipv6UnicastUrl, "ipv6-unicast-address-assignments.csv", force, cancel.Token);

            foreach (string? text in await Task.WhenAll(v4, v6, v6u))
                if (text is not null)
                    entries.AddRange(ParseRegistry(text));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The IANA address registries could not be read");
        }

        Entries = entries;
        Loaded?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Reads an IANA address-space CSV (the columns are found by their headings, as the three registries differ).</summary>
    public static IEnumerable<RegistryEntry> ParseRegistry(string csv)
    {
        var rows = ParseCsv(csv);
        if (rows.Count < 2) yield break;

        var header = rows[0];

        int Col(string start)
        {
            return header.FindIndex(h => h.StartsWith(start, StringComparison.OrdinalIgnoreCase));
        }

        int prefix      = Col("Prefix"),
            designation = Col("Designation"),
            date        = Col("Date"),
            whois       = Col("WHOIS"),
            rdap        = Col("RDAP"),
            status      = Col("Status"),
            note        = Col("Note");
        if (prefix < 0 || designation < 0) yield break;

        string? At(List<string> row, int column)
        {
            if (column < 0 || column >= row.Count) return null;

            string value = row[column].Trim();
            return value.Length == 0 ? null : value;
        }

        foreach (var row in rows.Skip(1))
        {
            string? text = At(row, prefix);
            if (text is null || !TryParsePrefix(text, out var subnet)) continue;

            yield return new RegistryEntry(subnet, At(row, designation) ?? "", At(row, status) ?? "", At(row, date), At(row, whois),
                                           At(row,                                                            rdap), At(row, note));
        }
    }

    // IANA writes the IPv4 /8s as "001/8" and the IPv6 blocks as "2001:0200::/23"
    private static bool TryParsePrefix(string text, out IPSubnet subnet)
    {
        int slash = text.IndexOf('/');
        if (slash > 0 && !text.Contains('.') && !text.Contains(':') &&
            int.TryParse(text[..slash],       NumberStyles.None, CultureInfo.InvariantCulture, out int octet) && octet is >= 0 and <= 255 &&
            int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int length) && length is >= 0 and <= 32)
        {
            subnet = new IPSubnet(new IPAddress([(byte)octet, 0, 0, 0]), length).Normalized();
            return true;
        }

        return SubnetMath.TryParse(text, out subnet);
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var  rows   = new List<List<string>>();
        var  row    = new List<string>();
        var  field  = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Silo");
        return client;
    }

    private static IReadOnlyList<SpecialBlock> BuildSpecialBlocks()
    {
        const bool T = true, F = false;

        SpecialBlock S(string cidr,     string  name, string rfc, bool? source, bool? destination, bool? forwardable, bool? global,
                       bool?  reserved, string? trivia = null)
        {
            SubnetMath.TryParse(cidr, out var subnet);
            return new SpecialBlock(subnet, name, rfc, source, destination, forwardable, global, reserved, trivia);
        }

        return
        [
            // IPv4 (IANA IPv4 Special-Purpose Address Registry)
            S("0.0.0.0/8", "\"This network\"", "RFC 1122", T, F, F, F, T,
              "0.0.0.0 as a source means \"this host, before it has an address\" (a DHCP client's first packet)."),
            S("0.0.0.0/32", "\"This host on this network\"", "RFC 1122", T, F, F, F, T),
            S("10.0.0.0/8", "Private-Use", "RFC 1918", T, T, T, F, F,
              "The largest private block: 16,777,216 addresses. Net 10 was ARPANET's until 1990."),
            S("100.64.0.0/10", "Shared Address Space", "RFC 6598", T, T, T, F, F,
              "For carrier-grade NAT between an ISP and its customers; not for ordinary private networks."),
            S("127.0.0.0/8", "Loopback", "RFC 1122", F, F, F, F, T,
              "Every address loops back to this host, though only 127.0.0.1 (localhost) is in general use: 16.7 million addresses for one."),
            S("169.254.0.0/16", "Link Local", "RFC 3927", T, T, F, F, T,
              "A host that finds no DHCP server picks an address here (APIPA); it is never routed."),
            S("172.16.0.0/12",  "Private-Use",                               "RFC 1918", T, T, T, F, F),
            S("192.0.0.0/24",   "IETF Protocol Assignments",                 "RFC 6890", F, F, F, F, F),
            S("192.0.0.0/29",   "IPv4 Service Continuity Prefix (DS-Lite)",  "RFC 7335", T, T, T, F, F),
            S("192.0.0.8/32",   "IPv4 dummy address",                        "RFC 7600", T, F, F, F, F),
            S("192.0.0.9/32",   "Port Control Protocol Anycast",             "RFC 7723", T, T, T, T, F),
            S("192.0.0.10/32",  "Traversal Using Relays around NAT Anycast", "RFC 8155", T, T, T, T, F),
            S("192.0.0.170/32", "NAT64/DNS64 Discovery",                     "RFC 8880", F, F, F, F, T),
            S("192.0.0.171/32", "NAT64/DNS64 Discovery",                     "RFC 8880", F, F, F, F, T),
            S("192.0.2.0/24", "Documentation (TEST-NET-1)", "RFC 5737", F, F, F, F, F,
              "Set aside for examples; never routed, so an example cannot hit a real host."),
            S("192.31.196.0/24", "AS112-v4",                        "RFC 7535", T,    T,    T,    T,    F),
            S("192.52.193.0/24", "Automatic Multicast Tunneling",   "RFC 7450", T,    T,    T,    T,    F),
            S("192.88.99.0/24",  "Deprecated (6to4 Relay Anycast)", "RFC 7526", null, null, null, null, null),
            S("192.168.0.0/16", "Private-Use", "RFC 1918", T, T, T, F, F,
              "Home routers usually hand out 192.168.0.x or 192.168.1.x."),
            S("192.175.48.0/24", "Direct Delegation AS112 Service", "RFC 7534", T, T, T, T, F),
            S("198.18.0.0/15",   "Benchmarking",                    "RFC 2544", T, T, T, F, F),
            S("198.51.100.0/24", "Documentation (TEST-NET-2)",      "RFC 5737", F, F, F, F, F),
            S("203.0.113.0/24",  "Documentation (TEST-NET-3)",      "RFC 5737", F, F, F, F, F),
            S("224.0.0.0/4", "Multicast", "RFC 5771", null, null, null, null, null,
              "Class D. 224.0.0.1 is every host on the link, 224.0.0.251 is mDNS."),
            S("240.0.0.0/4", "Reserved", "RFC 1112", F, F, F, F, T,
              "Class E: \"reserved for future use\" since 1989, and still unused; most systems refuse to use it."),
            S("255.255.255.255/32", "Limited Broadcast", "RFC 8190", F, T, F, F, T),

            // IPv6 (IANA IPv6 Special-Purpose Address Registry, and the multicast block of RFC 4291)
            S("::/128",  "Unspecified Address", "RFC 4291", T, F, F, F, T),
            S("::1/128", "Loopback Address",    "RFC 4291", F, F, F, F, T),
            S("::ffff:0:0/96", "IPv4-mapped Address", "RFC 4291", F, F, F, F, T,
              "How an IPv6 socket sees an IPv4 peer: ::ffff:192.0.2.1."),
            S("64:ff9b::/96", "IPv4-IPv6 Translation", "RFC 6052", T, T, T, T, F,
              "The well-known NAT64 prefix: 64:ff9b::192.0.2.1 reaches the IPv4 host."),
            S("64:ff9b:1::/48", "IPv4-IPv6 Translation (local use)", "RFC 8215", T, T, T, F, F),
            S("100::/64", "Discard-Only Address Block", "RFC 6666", T, T, T, F, F,
              "Traffic sent here is dropped on purpose (remotely triggered black hole)."),
            S("2001::/23",       "IETF Protocol Assignments",            "RFC 2928", F, F, F, F,    F),
            S("2001::/32",       "TEREDO",                               "RFC 4380", T, T, T, null, F),
            S("2001:2::/48",     "Benchmarking",                         "RFC 5180", T, T, T, F,    F),
            S("2001:3::/32",     "AMT",                                  "RFC 7450", T, T, T, T,    F),
            S("2001:4:112::/48", "AS112-v6",                             "RFC 7535", T, T, T, T,    F),
            S("2001:20::/28",    "ORCHIDv2",                             "RFC 7343", T, T, T, T,    F),
            S("2001:30::/28",    "Drone Remote ID Protocol Entity Tags", "RFC 9374", T, T, T, T,    F),
            S("2001:db8::/32", "Documentation", "RFC 3849", F, F, F, F, F,
              "Set aside for examples; 2001:db8:: is what documentation writes."),
            S("2002::/16", "6to4", "RFC 3056", T, T, T, null, F,
              "Deprecated: the IPv4 address of the tunnel endpoint is in bits 16 to 47."),
            S("2620:4f:8000::/48", "Direct Delegation AS112 Service", "RFC 7534", T, T, T, T, F),
            S("3fff::/20", "Documentation", "RFC 9637", F, F, F, F, F,
              "A larger documentation block, added in 2024 because 2001:db8::/32 was too small for some examples."),
            S("5f00::/16", "Segment Routing (SRv6) SIDs", "RFC 9602", T, T, T, F, F),
            S("fc00::/7", "Unique-Local", "RFC 4193", T, T, T, F, F,
              "The IPv6 private space; in practice fd00::/8 with a random 40-bit Global ID."),
            S("fe80::/10", "Link-Local Unicast", "RFC 4291", T, T, F, F, T,
              "Every IPv6 interface has one; neighbor discovery runs over it."),
            S("ff00::/8", "Multicast", "RFC 4291", null, null, null, null, null,
              "ff02::1 is every node on the link, ff02::2 every router.")
        ];
    }
}