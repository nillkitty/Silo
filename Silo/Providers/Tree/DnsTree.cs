using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Windows;
using Serilog;
using Silo.Connectors.Web;
using Silo.Contracts.Core;
using Silo.Contracts.UI;
using Silo.Model;
using Silo.TextEditing;
using Silo.Tools.Traceroute;
using Silo.Ui.Windows;
using Silo.ViewModel;

namespace Silo.Providers.Tree;

/// <summary>What a node of the domain name tree is (the kinds the tree is drawn with).</summary>
public enum DnsNodeKind
{
    /// <summary>The root of the name space, ".".</summary>
    Root,

    /// <summary>A name that is set aside and is not an ordinary top-level domain (.test, .local, .onion...).</summary>
    Special,

    /// <summary>A top-level domain (.com, .net, .nl...), one of the zones the root delegates.</summary>
    Tld,

    /// <summary>A name below a top-level domain that is not the start of a zone of its own.</summary>
    Zone,

    /// <summary>A name with name servers of its own: the start of a delegated zone (it has NS records).</summary>
    Delegated
}

/// <summary>The kinds of resource record the tree puts together under a name.</summary>
public enum DnsGroupKind
{
    Host,
    Alias,
    Mail,
    NameServers,
    Soa,
    Text,
    Service,
    DelegationSigner,
    PublicKey,
    Signature,
    Pointer,
    Other
}

/// <summary>The icon keys of the domain name tree (see <see cref="NetworkIcons" /> for the address trees).</summary>
public static class DnsIcons
{
    public const string Root             = "Icon038Dns";
    public const string Special          = "IconStreetSign";
    public const string Tld              = "IconDomain1";
    public const string Zone             = "IconNameTag";
    public const string Delegated        = "Icon025Server";
    public const string Host             = "Icon026Internet";
    public const string Alias            = "IconLabel";
    public const string Mail             = "IconMexico";
    public const string NameServers      = "Icon025Server";
    public const string Soa              = "Icon068CreditCard";
    public const string Text             = "Icon119Signature";
    public const string Service          = "Icon012Server";
    public const string DelegationSigner = "Icon051DomainRegistration";
    public const string PublicKey        = "IconKey";
    public const string Signature        = "Icon114IdCard";
    public const string Pointer          = "IconIpAddress";
    public const string Other            = "IconWebsite";

    public static string For(DnsNodeKind kind) => kind switch
                                                   {
                                                       DnsNodeKind.Root      => Root,
                                                       DnsNodeKind.Special   => Special,
                                                       DnsNodeKind.Tld       => Tld,
                                                       DnsNodeKind.Delegated => Delegated,
                                                       _                     => Zone
                                                   };

    public static string For(DnsGroupKind kind) => kind switch
                                                   {
                                                       DnsGroupKind.Host             => Host,
                                                       DnsGroupKind.Alias            => Alias,
                                                       DnsGroupKind.Mail             => Mail,
                                                       DnsGroupKind.NameServers      => NameServers,
                                                       DnsGroupKind.Soa              => Soa,
                                                       DnsGroupKind.Text             => Text,
                                                       DnsGroupKind.Service          => Service,
                                                       DnsGroupKind.DelegationSigner => DelegationSigner,
                                                       DnsGroupKind.PublicKey        => PublicKey,
                                                       DnsGroupKind.Signature        => Signature,
                                                       DnsGroupKind.Pointer          => Pointer,
                                                       _                             => Other
                                                   };
}

/// <summary>A name that is set aside, and who says so.</summary>
public sealed record SpecialName(string Name, string Rfc, string Purpose);

/// <summary>Domain names: how they are written, and how they relate to addresses.</summary>
public static class DnsNames
{
    /// <summary>The names that are set aside at the top of the name space (RFC 6761 and later).</summary>
    public static IReadOnlyList<SpecialName> Reserved { get; } =
    [
        new("alt.", "RFC 9476", "Names of alternative name systems; the global DNS never answers for them"),
        new("example.", "RFC 6761", "For examples in documentation; never delegated"),
        new("internal.", "ICANN, 2024", "Reserved for private use inside a network; never delegated"),
        new("invalid.", "RFC 6761", "Guaranteed not to exist: what to put where a name must be invalid"),
        new("local.", "RFC 6762", "Multicast DNS: names found on the local link, not by asking a server"),
        new("localhost.", "RFC 6761", "This host: always the loopback address"),
        new("onion.", "RFC 7686", "Tor hidden services; resolved by Tor, not by DNS"),
        new("test.", "RFC 6761", "For testing; never delegated")
    ];

    /// <summary>The name as it is kept: lower case, IDN labels in punycode, ending with the root dot ("." for the root).</summary>
    /// <exception cref="ArgumentException">The text is not a domain name.</exception>
    public static string Normalize(string? name)
    {
        if (!TryNormalize(name, out string normalized))
            throw new ArgumentException($"'{name}' is not a domain name", nameof(name));

        return normalized;
    }

    /// <summary>Reads a name (<c>Example.COM</c>, <c>bücher.example.</c>), or says it is not one.</summary>
    public static bool TryNormalize(string? name, out string normalized)
    {
        normalized = ".";
        if (string.IsNullOrWhiteSpace(name)) return true;

        string text = name.Trim().TrimEnd('.');
        if (text.Length == 0) return true;

        var idn    = new IdnMapping();
        var labels = new List<string>();
        foreach (string raw in text.Split('.'))
        {
            string label;
            try
            {
                label = raw.All(c => c < 128) ? raw : idn.GetAscii(raw);
            }
            catch (ArgumentException)
            {
                return false;
            }

            label = label.ToLowerInvariant();
            if (label.Length is 0 or > 63) return false;
            if (!label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '*')) return false;
            labels.Add(label);
        }

        string joined = string.Join('.', labels) + ".";
        if (joined.Length > 254) return false;

        normalized = joined;
        return true;
    }

    /// <summary>The name one label shorter ("www.example.com." gives "example.com."; a top-level name gives "."); null for the root.</summary>
    public static string? ParentOf(string name)
    {
        if (name == ".") return null;

        int dot = name.IndexOf('.');
        return dot == name.Length - 1 ? "." : name[(dot + 1)..];
    }

    /// <summary>The first label ("www" of "www.example.com."); empty for the root.</summary>
    public static string LabelOf(string name) => name == "." ? "" : name[..name.IndexOf('.')];

    /// <summary>The names from the top-level domain down to <paramref name="name" > ("net.", "durms.net.", "int.durms.net.").</paramref></summary>
    public static IReadOnlyList<string> PathTo(string name)
    {
        var path = new List<string>();
        for (string? n = name; n is not null && n != "."; n = ParentOf(n)) path.Add(n);
        path.Reverse();
        return path;
    }

    /// <summary>The name an address has in the reverse DNS ("4.3.2.1.in-addr.arpa." for 1.2.3.4).</summary>
    public static string ReverseNameOf(IPAddress address) => address.ReverseName() + ".";

    /// <summary>
    ///     The addresses a name in <c>in-addr.arpa</c> or <c>ip6.arpa</c> stands for: "10.in-addr.arpa." is 10.0.0.0/8. False for
    ///     any other name (or one that is not a whole number of octets or nibbles).
    /// </summary>
    public static bool TryReverseZone(string name, out IPSubnet subnet)
    {
        subnet = default!;
        if (!TryNormalize(name, out string n)) return false;

        const string v4 = "in-addr.arpa.", v6 = "ip6.arpa.";
        if (n == v4)
        {
            subnet = new IPSubnet(IPAddress.Any, 0);
            return true;
        }

        if (n == v6)
        {
            subnet = new IPSubnet(IPAddress.IPv6Any, 0);
            return true;
        }

        if (n.EndsWith("." + v4, StringComparison.Ordinal))
        {
            string[] labels = n[..^(v4.Length + 1)].Split('.');
            if (labels.Length > 4) return false;

            byte[] bytes = new byte[4];
            for (int i = 0; i < labels.Length; i++)
            {
                if (!byte.TryParse(labels[labels.Length - 1 - i], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i])) return false;
            }

            subnet = new IPSubnet(new IPAddress(bytes), labels.Length * 8);
            return true;
        }

        if (n.EndsWith("." + v6, StringComparison.Ordinal))
        {
            string[] labels = n[..^(v6.Length + 1)].Split('.');
            if (labels.Length > 32 || labels.Any(l => l.Length != 1 || !Uri.IsHexDigit(l[0]))) return false;

            byte[] bytes = new byte[16];
            for (int i = 0; i < labels.Length; i++)
            {
                int value = Convert.ToInt32(labels[labels.Length - 1 - i], 16);
                bytes[i / 2] |= (byte)(i % 2 == 0 ? value << 4 : value);
            }

            subnet = new IPSubnet(new IPAddress(bytes), labels.Length * 4);
            return true;
        }

        return false;
    }
}

/// <summary>
///     The domain name space as a tree, the way <see cref="NetworkTree" /> is the address space: the root "." holds the
///     top-level domains (the list IANA publishes) and the names that are set aside; below them, the names a person goes to
///     (<see cref="RevealAsync" />, Go to Name) each hold what DNS says about them, as resource records put in groups -- hosts
///     (A, AAAA), aliases (CNAME), mail (MX), name servers (NS), SOA, text (TXT), services (SRV), and the DNSSEC records (DS,
///     DNSKEY, RRSIG). DNS cannot list the names below a name, so the tree only has the ones that were asked for.
/// </summary>
/// <remarks>Addresses are <c>dns:example.com.</c>; the root is <c>dns:.</c>.</remarks>
public sealed class DnsTree : ITreeProvider<DnsNode>
{
    private const string TldListUrl = "https://data.iana.org/TLD/tlds-alpha-by-domain.txt";

    private readonly object _gate = new();

    private Task<IReadOnlyList<string>>? _tlds;

    /// <summary>The one tree (what the Explorer shows is shared between the Silos).</summary>
    public static DnsTree Canonical { get; } = new();

    /// <summary>The node of the root zone, ".".</summary>
    public DnsNode Root { get; }

    public DnsTree()
    {
        Root = new DnsNode(this, null, ".", DnsNodeKind.Root);
    }

    public DnsNode? GetRoot(Uri uri)
    {
        return uri.Required().Scheme.ToLowerInvariant() is "dns" or "ns" ? Root : null;
    }

    public DnsNode? GetParent(DnsNode node)
    {
        return node?.Parent;
    }

    /// <summary>The names below the node that are known: the top-level domains and special names for the root, else those that were asked for.</summary>
    public IEnumerable<DnsNode?> GetChildren(DnsNode node)
    {
        return node.Required().NameChildren;
    }

    /// <summary>The top-level domains IANA lists, lower case with the root dot ("com."), read once and kept for a week; empty when it cannot be had.</summary>
    public Task<IReadOnlyList<string>> TopLevelDomainsAsync()
    {
        lock (_gate)
        {
            return _tlds ??= ReadTldsAsync();
        }
    }

    private static async Task<IReadOnlyList<string>> ReadTldsAsync()
    {
        try
        {
            string? text = await AddressRegistry.GetCachedTextAsync(TldListUrl, "tlds-alpha-by-domain.txt", false);
            return ParseTlds(text ?? "");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            Log.Warning(ex, "The list of top-level domains could not be read");
            return [];
        }
    }

    /// <summary>The names in IANA's list (one per line, comments start with #).</summary>
    public static IReadOnlyList<string> ParseTlds(string text)
    {
        return text.Split('\n')
                   .Select(l => l.Trim())
                   .Where(l => l.Length > 0 && l[0] != '#')
                   .Select(l => l.ToLowerInvariant() + ".")
                   .Distinct()
                   .ToList();
    }

    /// <summary>
    ///     Opens the way to <paramref name="name" > in the Explorer: every name from the top-level domain down is added where it
    ///     is missing and read, and the node of <paramref name="name" /> is selected. A name is a delegated zone when DNS says it
    ///     has name servers of its own.</paramref>
    /// </summary>
    /// <exception cref="ArgumentException">The text is not a domain name.</exception>
    public async Task<DnsNode> RevealAsync(string name, CancellationToken cancel = default)
    {
        name = DnsNames.Normalize(name);

        var node = Root;
        foreach (string step in DnsNames.PathTo(name))
        {
            await node.GetNode().LoadChildrenAsync(cancel);
            node = await node.GetOrAddChildAsync(step, cancel);
        }

        // what the last name says is read now, so there is something to see
        await node.GetNode().LoadChildrenAsync(cancel);
        AddressSpaces.Reveal(node);
        return node;
    }

    /// <summary>The node of a name that is known, or null (the names are added with <see cref="RevealAsync" />).</summary>
    public DnsNode? Find(string name)
    {
        var node = Root;
        foreach (string step in DnsNames.PathTo(DnsNames.Normalize(name)))
        {
            node = node.NameChildren.FirstOrDefault(c => c.Name == step);
            if (node is null) return null;
        }

        return node;
    }
}

/// <summary>A name of the domain name tree.</summary>
public sealed class DnsNode : INodeProvider, INodeDetails, ICommandProvider
{
    // the names that DNS is asked about, besides the ones a name's own label asks for
    private static readonly DnsType[] BaseTypes = [DnsType.A, DnsType.AAAA, DnsType.CNAME, DnsType.MX, DnsType.NS, DnsType.TXT, DnsType.SOA, DnsType.DS, DnsType.DNSKEY];

    private readonly object        _gate = new();
    private readonly List<DnsNode> _names = [];

    private List<DnsNode>?   _topLevel;
    private TreeSiloNode?    _node;
    private DnsLookup?       _lookup;

    internal DnsNode(DnsTree tree, DnsNode? parent, string name, DnsNodeKind kind, SpecialName? special = null)
    {
        Tree    = tree;
        Parent  = parent;
        Name    = name;
        Kind    = kind;
        Special = special;
    }

    public DnsTree     Tree    { get; }
    public DnsNode?    Parent  { get; }
    public DnsNodeKind Kind    { get; }
    public SpecialName? Special { get; }

    /// <summary>The name, lower case with the root dot ("example.com.").</summary>
    public string Name { get; }

    /// <summary>The first label ("example" of "example.com.").</summary>
    public string Label => DnsNames.LabelOf(Name);

    /// <summary>What the last reading of the name's records said (null until it is read).</summary>
    public DnsLookup? Lookup => _lookup;

    /// <summary>The names below this one that are known: for the root the top-level domains (once read) and the special names, else the ones asked for.</summary>
    public IReadOnlyList<DnsNode> NameChildren
    {
        get
        {
            lock (_gate)
            {
                var list = new List<DnsNode>();
                if (_topLevel is not null) list.AddRange(_topLevel);
                list.AddRange(_names);
                return list;
            }
        }
    }

    // ---- INodeProvider ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public object? Data => Name;

    /// <summary>The Explorer node of this name (made once).</summary>
    public SiloNode GetNode()
    {
        lock (_gate)
        {
            return _node ??= new TreeSiloNode(this, Display, Uri, Icon, TypeName, LoadChildNodesAsync);
        }
    }

    SiloNode? INodeProvider.GetNode() => GetNode();

    // ---- INodeDetails ----------------------------------------------------------------------------------------------

    /// <summary>What the node is called in the Explorer: the name; the root is "." with a word to say what it is.</summary>
    public string Display => Kind == DnsNodeKind.Root ? ". (DNS root)" : Name;

    /// <inheritdoc />
    public Uri Uri => new("dns:" + Name);

    /// <inheritdoc />
    public IconRef? Icon => IconRef.FromKey(DnsIcons.For(Kind));

    /// <inheritdoc />
    string INodeDetails.Name => Display;

    /// <summary>What the name is, in words.</summary>
    public string TypeName => Kind switch
                              {
                                  DnsNodeKind.Root      => "DNS root",
                                  DnsNodeKind.Special   => "Name: Special/Reserved",
                                  DnsNodeKind.Tld       => "Name: TLD Zone",
                                  DnsNodeKind.Delegated => "Name: Delegated Zone (NS)",
                                  _                     => "Name: Zone"
                              };

    /// <inheritdoc />
    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        yield return ("Name", Name);
        yield return ("Kind", TypeName);
        if (Parent is not null) yield return ("Parent", Parent.Name);
        if (Kind != DnsNodeKind.Root) yield return ("Labels", DnsNames.PathTo(Name).Count.ToString());

        if (Special is { } special)
        {
            yield return ("Set aside by", special.Rfc);
            yield return ("Purpose", special.Purpose);
        }

        if (Kind == DnsNodeKind.Root)
            yield return ("Top-level domains", _topLevel is null ? "not read yet (expand the node)" : _topLevel.Count.ToString("N0"));

        if (DnsNames.TryReverseZone(Name, out var block))
        {
            yield return ("Address block", block.Cidr());
            yield return ("Reverse DNS zone of", block.Cidr() + (block.IsV6() ? " (IPv6)" : " (IPv4)"));
        }

        if (Name is "example.com." or "example.net." or "example.org.")
            yield return ("Did you know", "Reserved for examples by RFC 2606: it is safe to use in documentation.");

        if (Kind == DnsNodeKind.Delegated) yield return ("Delegation", "has name servers of its own (NS records)");

        if (_lookup is { } lookup)
        {
            yield return ("Last lookup", lookup.Summary);
            foreach (var (label, value) in lookup.Details) yield return (label, value);
        }
    }

    // ---- reading the names ---------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<SiloNode>> LoadChildNodesAsync(CancellationToken cancel)
    {
        var nodes = new List<SiloNode>();

        if (Kind == DnsNodeKind.Root)
        {
            var tlds = await Tree.TopLevelDomainsAsync();
            lock (_gate)
            {
                _topLevel ??= BuildTopLevel(tlds);
            }

            if (tlds.Count == 0) nodes.Add(Info("The list of top-level domains could not be read (are you online?)"));
        }
        else
        {
            var lookup = await ReadRecordsAsync(cancel);
            nodes.AddRange(lookup.Groups.Select(g => g.GetNode()));
            if (lookup.Groups.Count == 0) nodes.Add(Info(lookup.Summary));
        }

        foreach (var child in NameChildren) nodes.Add(child.GetNode());
        return nodes;
    }

    private static SiloNode Info(string text)
    {
        return new SiloNode(text, text, null, null, null);
    }

    private List<DnsNode> BuildTopLevel(IReadOnlyList<string> tlds)
    {
        var list = new List<DnsNode>();
        foreach (var special in DnsNames.Reserved) list.Add(new DnsNode(Tree, this, special.Name, DnsNodeKind.Special, special));
        foreach (string tld in tlds)
            if (!list.Any(n => n.Name == tld))
                list.Add(new DnsNode(Tree, this, tld, DnsNodeKind.Tld));

        return list.OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The types of record DNS is asked about for this name.</summary>
    public IReadOnlyList<DnsType> TypesToAsk()
    {
        var types = new List<DnsType>(BaseTypes);
        if (Label.StartsWith('_')) types.Add(DnsType.SRV);
        if (Name.EndsWith(".arpa.", StringComparison.Ordinal)) types.Add(DnsType.PTR);
        return types;
    }

    /// <summary>Asks DNS for the records of this name and puts them in groups. A name no resolver could be asked about throws.</summary>
    public async Task<DnsLookup> ReadRecordsAsync(CancellationToken cancel = default)
    {
        var types   = TypesToAsk();
        var answers = await Task.WhenAll(types.Select(t => AskAsync(t, cancel)));

        if (answers.All(a => a is null))
            throw new DnsException($"No name server could be asked about {Name} (are you online?)");

        var lookup = DnsLookup.From(this, answers.Where(a => a is not null).Select(a => a!).ToList());
        lock (_gate)
        {
            _lookup = lookup;
        }

        return lookup;
    }

    private async Task<DnsAnswer?> AskAsync(DnsType type, CancellationToken cancel)
    {
        try
        {
            return await DnsClient.QueryAsync(Name, type, null, cancel);
        }
        catch (DnsException ex)
        {
            Log.Information("DNS {Type} {Name}: {Message}", type, Name, ex.Message);
            return null;
        }
    }

    // ---- adding names --------------------------------------------------------------------------------------------

    /// <summary>
    ///     The node of the child <paramref name="childName" /> (a full name, one label longer than this one), added when it is
    ///     missing. DNS is asked whether it is the start of a zone (it has NS records) to tell what kind of name it is.
    /// </summary>
    public async Task<DnsNode> GetOrAddChildAsync(string childName, CancellationToken cancel = default)
    {
        lock (_gate)
        {
            if (NameChildren.FirstOrDefault(c => c.Name == childName) is { } existing) return existing;
        }

        var kind = DnsNodeKind.Zone;
        if (Kind != DnsNodeKind.Root)
        {
            try
            {
                var answer = await DnsClient.QueryAsync(childName, DnsType.NS, null, cancel);
                if (answer.Answers.Any(r => r.Type == DnsType.NS && r.Name == childName)) kind = DnsNodeKind.Delegated;
            }
            catch (DnsException)
            {
                // unknown: an ordinary name
            }
        }
        else
            kind = DnsNodeKind.Tld;

        lock (_gate)
        {
            if (NameChildren.FirstOrDefault(c => c.Name == childName) is { } raced) return raced;

            var child = new DnsNode(Tree, this, childName, kind);
            _names.Add(child);
            _names.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            // a node that is shown already gets the new name at once
            if (_node is { ChildrenLoaded: true }) _node.Children.Add(child.GetNode());
            return child;
        }
    }

    // ---- commands ------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public IEnumerable<ProvidedCommand> GetCommands()
    {
        const string NamesGroup = "Names";
        const string LookupGroup = "Lookup";
        const string CopyGroup  = "Copy";

        yield return ProvidedCommand.From("dns.goto", "_Go to Name...", () => TreeActions.Run(GoToAsync, "Go to name"), "Search",
                                          toolTip: "Open the tree down to a name (www.example.com)", group: NamesGroup);

        if (Kind != DnsNodeKind.Root)
            yield return ProvidedCommand.From("dns.sub", "Add _Subdomain...", () => TreeActions.Run(AddSubdomainAsync, "Add subdomain"), "IconNameTag",
                                              toolTip: "DNS cannot list the names below a name: add one you know (www, mail, _sip._tcp)", group: NamesGroup);

        if (Kind == DnsNodeKind.Root)
            yield return ProvidedCommand.From("dns.remove", "Remove from E_xplorer", () => TreeActions.RemoveFromExplorer(this, AddressSpace.Dns),
                                              null, toolTip: "Take the domain name space out of the Explorer", group: NamesGroup);

        if (DnsNames.TryReverseZone(Name, out var block))
            yield return ProvidedCommand.From("dns.address", "Show in _IP Address Space", () => TreeActions.Run(() => NetworkTree.ShowAsync(block), "Show in IP address space"),
                                              NetworkIcons.Subnet, toolTip: "Open " + block.Cidr() + " in the address tree", group: NamesGroup);

        if (Kind != DnsNodeKind.Root)
        {
            yield return ProvidedCommand.From("dns.rdap", "_RDAP Domain Lookup", () => TreeActions.Run(RdapAsync, "RDAP domain lookup"), "Search",
                                              toolTip: "Ask the registry who holds the domain", group: LookupGroup);
            yield return ProvidedCommand.From("dns.trace", "Visual _Traceroute", Trace, "Network", toolTip: "Trace the route to " + Name, group: LookupGroup);
        }

        if (Kind == DnsNodeKind.Tld)
            yield return ProvidedCommand.From("dns.iana", "IANA _Root Zone Database Entry", () => _ = Browser.LaunchAsync(new Uri($"https://www.iana.org/domains/root/db/{Label}.html")),
                                              NetworkIcons.Internet, toolTip: "Who runs ." + Label, group: LookupGroup);

        if (Kind is DnsNodeKind.Zone or DnsNodeKind.Delegated)
            yield return ProvidedCommand.From("dns.web", "_Open in Browser", () => _ = Browser.LaunchAsync(new Uri("https://" + Name.TrimEnd('.') + "/")),
                                              NetworkIcons.Internet, toolTip: "https://" + Name.TrimEnd('.') + "/", group: LookupGroup);

        yield return ProvidedCommand.From("dns.copy", "Copy _Name", () => Clipboard.SetText(Name), "TbCopy", group: CopyGroup);
    }

    private async Task GoToAsync()
    {
        string? text = PromptDialog.Ask("Go to Name", "A domain name (www.example.com):", "Go");
        if (text is null) return;

        if (!DnsNames.TryNormalize(text, out string name))
        {
            App.Status($"'{text}' is not a domain name");
            return;
        }

        App.Status($"Opening {name}...");
        var node = await Tree.RevealAsync(name);
        App.Status(node.Lookup is { } lookup ? $"{node.Name}: {lookup.Summary}" : $"Found {node.Name}");
    }

    private async Task AddSubdomainAsync()
    {
        string? text = PromptDialog.Ask("Add Subdomain", $"A name below {Name}  (one label, or more):", "Add");
        if (text is null) return;

        string full = DnsNames.Normalize(text.TrimEnd('.') + "." + Name);
        await Tree.RevealAsync(full);
    }

    private async Task RdapAsync()
    {
        App.Status($"Asking the registry about {Name}...");
        var (url, json) = await NetworkLookups.RdapDomainAsync(Name);
        TextEditors.OpenText("dns:rdap:" + Name, "RDAP " + Name, url.AbsoluteUri, json, "rdap-" + Name.TrimEnd('.') + ".json", "application/json");
        App.Status($"RDAP answer for {Name} from {url.Host}");
    }

    private void Trace()
    {
        var tool = App.Resolve<IEnumerable<ITool>>()?.OfType<VisualTracerouteTool>().FirstOrDefault();
        if (tool is null)
            App.Status("The Visual Traceroute tool is not available");
        else
            tool.Trace(Name.TrimEnd('.'));
    }
}

/// <summary>What DNS said about a name: the records, in groups, and how the question went.</summary>
public sealed class DnsLookup
{
    private DnsLookup(DnsNode owner, IReadOnlyList<DnsRecordGroup> groups, string summary, IReadOnlyList<(string, string)> details)
    {
        Owner   = owner;
        Groups  = groups;
        Summary = summary;
        Details = details;
    }

    public DnsNode                          Owner   { get; }
    public IReadOnlyList<DnsRecordGroup>    Groups  { get; }

    /// <summary>One line: "7 records in 4 groups; no such name"...</summary>
    public string Summary { get; }

    /// <summary>Label/value rows for the Properties pane.</summary>
    public IReadOnlyList<(string Name, string Value)> Details { get; }

    /// <summary>Puts the answers to the questions about a name together.</summary>
    public static DnsLookup From(DnsNode owner, IReadOnlyList<DnsAnswer> answers)
    {
        var records = answers.SelectMany(a => a.Answers)
                             .GroupBy(r => r.ToString())
                             .Select(g => g.First())
                             .ToList();

        var groups = DnsRecordGroup.Build(owner, records);

        bool nameMissing = answers.Any(a => a.ResponseCode == 3);
        bool authentic   = answers.Any(a => a.Authenticated && a.Answers.Count > 0);
        bool refused     = answers.All(a => a.ResponseCode != 0 && a.ResponseCode != 3);

        string summary = records.Count > 0
                             ? $"{records.Count} record{(records.Count == 1 ? "" : "s")} in {groups.Count} group{(groups.Count == 1 ? "" : "s")}"
                             : nameMissing ? "No such name (NXDOMAIN)"
                             : refused     ? "The name servers refused or failed (" + answers[0].Status + ")"
                                             : "No records of the kinds asked for";

        var details = new List<(string, string)>
                      {
                          ("Records", records.Count.ToString()),
                          ("DNSSEC", authentic ? "the resolver checked the signatures (AD)" : records.Any(r => r.Type == DnsType.RRSIG) ? "signed; not checked by the resolver" : "not signed, or not shown")
                      };
        if (answers.Select(a => a.Server).FirstOrDefault(s => s is not null) is { } server) details.Add(("Resolver", server.ToString()));
        if (nameMissing) details.Add(("Status", "NXDOMAIN: the name does not exist"));

        return new DnsLookup(owner, groups, summary, details);
    }
}

/// <summary>The records of one kind at a name (all its hosts, all its mail servers...): a node of the tree that holds one node per record.</summary>
public sealed class DnsRecordGroup : INodeProvider, INodeDetails, ICommandProvider
{
    private TreeSiloNode? _node;

    internal DnsRecordGroup(DnsNode owner, DnsGroupKind kind, IReadOnlyList<DnsRecord> records)
    {
        Owner   = owner;
        Kind    = kind;
        Records = records;
    }

    public DnsNode                  Owner   { get; }
    public DnsGroupKind             Kind    { get; }
    public IReadOnlyList<DnsRecord> Records { get; }

    /// <summary>The group's title: "Host (A, AAAA)", "Mail Exchanger (MX)"...</summary>
    public string Title => TitleOf(Kind);

    /// <summary>The title of a kind of group.</summary>
    public static string TitleOf(DnsGroupKind kind) => kind switch
                                                       {
                                                           DnsGroupKind.Host             => "Host (A, AAAA)",
                                                           DnsGroupKind.Alias            => "Alias (CNAME)",
                                                           DnsGroupKind.Mail             => "Mail Exchanger (MX)",
                                                           DnsGroupKind.NameServers      => "Name Servers (NS)",
                                                           DnsGroupKind.Soa              => "SOA",
                                                           DnsGroupKind.Text             => "Text (TXT)",
                                                           DnsGroupKind.Service          => "Service Locator (SRV)",
                                                           DnsGroupKind.DelegationSigner => "Delegation Signer (DS)",
                                                           DnsGroupKind.PublicKey        => "Public Key (DNSKEY)",
                                                           DnsGroupKind.Signature        => "Signature (RRSIG)",
                                                           DnsGroupKind.Pointer          => "Pointer (PTR)",
                                                           _                             => "Combination / Other"
                                                       };

    /// <summary>The kind of group a record belongs in.</summary>
    public static DnsGroupKind KindOf(DnsType type) => type switch
                                                       {
                                                           DnsType.A or DnsType.AAAA => DnsGroupKind.Host,
                                                           DnsType.CNAME             => DnsGroupKind.Alias,
                                                           DnsType.MX                => DnsGroupKind.Mail,
                                                           DnsType.NS                => DnsGroupKind.NameServers,
                                                           DnsType.SOA               => DnsGroupKind.Soa,
                                                           DnsType.TXT               => DnsGroupKind.Text,
                                                           DnsType.SRV               => DnsGroupKind.Service,
                                                           DnsType.DS                => DnsGroupKind.DelegationSigner,
                                                           DnsType.DNSKEY            => DnsGroupKind.PublicKey,
                                                           DnsType.RRSIG             => DnsGroupKind.Signature,
                                                           DnsType.PTR               => DnsGroupKind.Pointer,
                                                           _                         => DnsGroupKind.Other
                                                       };

    /// <summary>Puts records in groups, in the order the groups are listed (hosts first, the DNSSEC records last).</summary>
    public static IReadOnlyList<DnsRecordGroup> Build(DnsNode owner, IEnumerable<DnsRecord> records)
    {
        return records.GroupBy(r => KindOf(r.Type))
                      .OrderBy(g => g.Key)
                      .Select(g => new DnsRecordGroup(owner, g.Key, g.OrderBy(r => r.Type).ThenBy(r => r.Data, StringComparer.Ordinal).ToList()))
                      .ToList();
    }

    // ---- INodeProvider / INodeDetails ------------------------------------------------------------------------------

    public object? Data => Records;

    public SiloNode GetNode()
    {
        lock (this)
        {
            return _node ??= new TreeSiloNode(this, $"{Title}  ({Records.Count})", Uri, Icon, "Resource records",
                                              _ => Task.FromResult<IReadOnlyList<SiloNode>>(Records.Select((r, i) => new DnsRecordLeaf(this, r, i).GetNode()).ToList()), 1);
        }
    }

    SiloNode? INodeProvider.GetNode() => GetNode();

    public string Name => Title;

    public string TypeName => "Resource records";

    public Uri Uri => new($"dns:{Owner.Name}?group={Kind}");

    public IconRef? Icon => IconRef.FromKey(DnsIcons.For(Kind));

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        yield return ("Owner", Owner.Name);
        yield return ("Group", Title);
        yield return ("Records", Records.Count.ToString());
        yield return ("Types", string.Join(", ", Records.Select(r => r.TypeName).Distinct()));
        if (Records.Count > 0) yield return ("Shortest TTL", DnsRecordLeaf.Span(Records.Min(r => r.Ttl)));
    }

    public IEnumerable<ProvidedCommand> GetCommands()
    {
        yield return ProvidedCommand.From("dns.group.copy", "Copy _Records", () => Clipboard.SetText(string.Join(Environment.NewLine, Records)), "TbCopy",
                                          group: "Copy");
    }
}

/// <summary>One resource record: a leaf of the tree.</summary>
public sealed class DnsRecordLeaf : INodeProvider, INodeDetails, ICommandProvider
{
    private TreeSiloNode? _node;

    internal DnsRecordLeaf(DnsRecordGroup group, DnsRecord record, int index)
    {
        Group  = group;
        Record = record;
        Index  = index;
    }

    public DnsRecordGroup Group  { get; }
    public DnsRecord      Record { get; }
    public int            Index  { get; }

    public object? Data => Record;

    public SiloNode GetNode()
    {
        lock (this)
        {
            return _node ??= new TreeSiloNode(this, Name, Uri, Icon, "Resource record", null);
        }
    }

    SiloNode? INodeProvider.GetNode() => GetNode();

    /// <summary>The record's text in the tree: its data, with its owner when that is not the name the group is of (the target of an alias).</summary>
    public string Name
    {
        get
        {
            string data = Record.Type == DnsType.RRSIG ? $"{Record.Data.Split(' ')[0]}  (signature)" : Record.Data;
            if (data.Length > 120) data = data[..117] + "...";
            return Record.Name == Group.Owner.Name ? $"{Record.TypeName}  {data}" : $"{Record.Name}  {Record.TypeName}  {data}";
        }
    }

    public string TypeName => "Resource record";

    public Uri Uri => new($"dns:{Group.Owner.Name}?type={Record.TypeName}&n={Index}");

    public IconRef? Icon => IconRef.FromKey(DnsIcons.For(Group.Kind));

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        var r = Record;
        yield return ("Owner", r.Name);
        yield return ("Type", r.TypeName);
        yield return ("Class", r.Class == 1 ? "IN" : r.Class.ToString());
        yield return ("TTL", $"{r.Ttl} s  ({Span(r.Ttl)})");
        yield return ("Data", r.Data);

        string[] f = r.Data.Split(' ');
        switch (r.Type)
        {
            case DnsType.MX when f.Length == 2:
                yield return ("Preference", f[0]);
                yield return ("Exchange", f[1]);
                break;
            case DnsType.SRV when f.Length == 4:
                yield return ("Priority", f[0]);
                yield return ("Weight", f[1]);
                yield return ("Port", f[2]);
                yield return ("Target", f[3]);
                break;
            case DnsType.SOA when f.Length == 7:
                yield return ("Primary server", f[0]);
                yield return ("Responsible mailbox", f[1]);
                yield return ("Serial", f[2]);
                yield return ("Refresh", Span(uint.Parse(f[3])));
                yield return ("Retry", Span(uint.Parse(f[4])));
                yield return ("Expire", Span(uint.Parse(f[5])));
                yield return ("Negative-answer TTL", Span(uint.Parse(f[6])));
                break;
            case DnsType.DNSKEY when f.Length == 4:
                yield return ("Role", f[0] switch { "257" => "key signing key (KSK)", "256" => "zone signing key (ZSK)", var other => "flags " + other });
                yield return ("Algorithm", AlgorithmName(int.Parse(f[2])));
                yield return ("Key tag", KeyTag(r).ToString());
                break;
            case DnsType.DS when f.Length == 4:
                yield return ("Key tag", f[0]);
                yield return ("Algorithm", AlgorithmName(int.Parse(f[1])));
                yield return ("Digest type", f[2] switch { "1" => "SHA-1", "2" => "SHA-256", "3" => "GOST R 34.11-94", "4" => "SHA-384", var other => other });
                yield return ("Digest", f[3]);
                break;
            case DnsType.RRSIG when f.Length >= 9:
                yield return ("Covers", f[0]);
                yield return ("Algorithm", AlgorithmName(int.Parse(f[1])));
                yield return ("Signature expires", f[4] + Validity(f[4]));
                yield return ("Signature starts", f[5]);
                yield return ("Key tag", f[6]);
                yield return ("Signer", f[7]);
                break;
        }

        if (Link() is { } target) yield return ("Leads to", target);
    }

    public IEnumerable<ProvidedCommand> GetCommands()
    {
        var r = Record;

        if (r.Type is DnsType.A or DnsType.AAAA && IPAddress.TryParse(r.Data, out var address))
            yield return ProvidedCommand.From("dns.record.ip", "Show in _IP Address Space", () => TreeActions.Run(() => NetworkTree.ShowAsync(address.SubnetOf(address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)), "Show in IP address space"),
                                              NetworkIcons.Host, toolTip: "Open " + address + " in the address tree", group: "Go to");

        if (Link() is { } target)
            yield return ProvidedCommand.From("dns.record.goto", $"Go to _{target}", () => TreeActions.Run(() => DnsTree.Canonical.RevealAsync(target), "Go to name"),
                                              DnsIcons.Zone, toolTip: "Open " + target + " in the name tree", group: "Go to");

        yield return ProvidedCommand.From("dns.record.copy", "Copy _Data", () => Clipboard.SetText(r.Data), "TbCopy", group: "Copy");
        yield return ProvidedCommand.From("dns.record.copyline", "Copy _Record", () => Clipboard.SetText(r.ToString()), "TbCopy", group: "Copy");
    }

    // the name a record points at (an alias, a name server, a mail exchanger, a service target, a pointer), if it points at one
    private string? Link()
    {
        string[] f = Record.Data.Split(' ');
        string? name = Record.Type switch
                       {
                           DnsType.CNAME or DnsType.NS or DnsType.PTR => Record.Data,
                           DnsType.MX when f.Length == 2              => f[1],
                           DnsType.SRV when f.Length == 4             => f[3],
                           _                                          => null
                       };

        return name is not null && name != "." && DnsNames.TryNormalize(name, out string n) ? n : null;
    }

    /// <summary>A number of seconds as a few words ("1 h 30 min").</summary>
    public static string Span(uint seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} d" + (t.Hours > 0 ? $" {t.Hours} h" : "");
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h" + (t.Minutes > 0 ? $" {t.Minutes} min" : "");
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} min" + (t.Seconds > 0 ? $" {t.Seconds} s" : "");
        return $"{seconds} s";
    }

    private static string Validity(string stamp)
    {
        return DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
                   ? when < DateTime.UtcNow ? "  (expired)" : $"  (in {Span((uint)Math.Min(uint.MaxValue, (when - DateTime.UtcNow).TotalSeconds))})"
                   : "";
    }

    /// <summary>The DNSSEC algorithm numbers (RFC 8624).</summary>
    public static string AlgorithmName(int algorithm) => algorithm switch
                                                         {
                                                             3  => "3 DSA/SHA1",
                                                             5  => "5 RSASHA1",
                                                             6  => "6 DSA-NSEC3-SHA1",
                                                             7  => "7 RSASHA1-NSEC3-SHA1",
                                                             8  => "8 RSASHA256",
                                                             10 => "10 RSASHA512",
                                                             13 => "13 ECDSAP256SHA256",
                                                             14 => "14 ECDSAP384SHA384",
                                                             15 => "15 ED25519",
                                                             16 => "16 ED448",
                                                             _  => algorithm.ToString()
                                                         };

    /// <summary>The key tag of a DNSKEY record (RFC 4034, appendix B): what RRSIG and DS records name it by.</summary>
    public static int KeyTag(DnsRecord key)
    {
        string[] f = key.Data.Split(' ');
        if (f.Length != 4) return 0;

        byte[] rdata = [.. BitConverter.GetBytes((ushort)int.Parse(f[0])).Reverse(), byte.Parse(f[1]), byte.Parse(f[2]), .. Convert.FromBase64String(f[3])];
        uint   ac    = 0;
        for (int i = 0; i < rdata.Length; i++) ac += (i & 1) == 0 ? (uint)rdata[i] << 8 : rdata[i];
        ac += (ac >> 16) & 0xFFFF;
        return (int)(ac & 0xFFFF);
    }
}

/// <summary>Work the commands of the trees have in common.</summary>
internal static class TreeActions
{
    /// <summary>
    ///     Runs the work of a command, which started on the UI thread and goes on after its first await; a failure is said in the
    ///     status bar, not thrown.
    /// </summary>
    public static async void Run(Func<Task> work, string what)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            App.Status(what + " was cancelled");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{What} failed", what);
            App.Status($"{what} failed: {ex.Message}");
        }
    }

    /// <summary>Takes a space's root out of the Explorer of every Silo that shows it, and keeps that choice.</summary>
    public static void RemoveFromExplorer(INodeProvider root, AddressSpace space)
    {
        bool removed = false;
        foreach (var silo in OpenSilo.OpenSilos.ToList())
            if (AddressSpaces.IsShown(silo, space))
            {
                AddressSpaces.Show(silo, space, false);
                removed = true;
            }

        removed |= App.Instance?.Nodes.Remove(root) ?? false;
        App.Status(removed ? "Removed from the Explorer" : "It is not in the Explorer");
    }
}
