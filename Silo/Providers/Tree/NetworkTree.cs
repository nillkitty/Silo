using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Serilog;
using Silo.Connectors.Web;
using Silo.TextEditing;
using Silo.Tools.Traceroute;
using Silo.Ui.Windows;
using Silo.ViewModel;

namespace Silo.Providers.Tree;

/// <summary>
///     An IPv4 or IPv6 based tree in which:
///     <list type="bullet">
///         <item>Each node represents a subnet (CIDR) of the IP space</item>
///         <item>The root node represents the entire network (/0)</item>
///         <item>
///             Intermediary nodes are created lazily/on-demand: a node's children are the subnets of the next step down
///             (IPv4: /8, /16, /24, /28, /32; IPv6: /8, /16, /24, /32, then every nibble down to /64), except that a block
///             the
///             registries name which is larger than that step stands for itself (100.64.0.0/10, 224.0.0.0/4, 2000::/3...)
///         </item>
///         <item>
///             Any node can perform lookups to materialize the following relations:
///             <list type="bullet">
///                 <item>Whois - Registered contact, registrar, etc.</item>
///                 <item>Assigned entity, AS number, scope assigned, registry</item>
///                 <item>Reverse DNS lookup</item>
///                 <item>Reserved Address Block Description + Reference</item>
///             </list>
///         </item>
///     </list>
///     Addresses are <c>ip4://10.0.0.0/8</c> and <c>ip6://[2001:db8::]/32</c>.
/// </summary>
public class NetworkTree : ITreeProvider<NetTreeNode>
{
    /// <summary>
    ///     Gets a reference to the singleton tree instance used to represent all common metadata attached to the IPv4
    ///     address space
    /// </summary>
    public static NetworkTree CanonicalIpv4 { get; } = new(AddressFamily.InterNetwork);

    /// <summary>
    ///     Gets a reference to the singleton tree instance used to represent all common metadata attached to the IPv6
    ///     address space
    /// </summary>
    public static NetworkTree CanonicalIpv6 { get; } = new(AddressFamily.InterNetworkV6);

    /// <summary>The address family of the tree.</summary>
    public AddressFamily Family { get; }

    /// <summary>The node of the whole address space (/0).</summary>
    public NetTreeNode Root { get; }

    /// <summary>
    ///     Opens the way to <paramref name="target" /> in the Explorer: every node from the root down is read and expanded,
    ///     and
    ///     the last one, the node that is <paramref name="target" /> or the smallest that holds it, is selected.
    /// </summary>
    /// <returns>That node.</returns>
    public async Task<NetTreeNode> RevealAsync(IPSubnet target, CancellationToken cancel = default)
    {
        var node = Root;
        while (true)
        {
            var shown = node.GetNode();
            await shown.LoadChildrenAsync(cancel);

            var next = node.Children.FirstOrDefault(c => c.Subnet.Covers(target));
            if (next is null) break;

            node = next;
            if (node.Subnet.PrefixLength >= target.PrefixLength) break;
        }

        AddressSpaces.Reveal(node);
        return node;
    }

    /// <summary>
    ///     Shows <paramref name="target" /> in the Explorer of the active Silo, putting the IPv4 or IPv6 address space there
    ///     first when it is not (and keeping that choice in the Silo).
    /// </summary>
    public static async Task ShowAsync(IPSubnet target, CancellationToken cancel = default)
    {
        var silo  = App.Instance?.ActiveSilo ?? throw new InvalidOperationException("Open a Silo first");
        var space = target.IsV6() ? AddressSpace.Ipv6 : AddressSpace.Ipv4;
        if (!AddressSpaces.IsShown(silo, space)) AddressSpaces.Show(silo, space, true);

        var node = await (target.IsV6() ? CanonicalIpv6 : CanonicalIpv4).RevealAsync(target, cancel);
        App.Status(node.Subnet.SameAs(target) ? $"Found {target.Cidr()}" : $"{target.Cidr()} is in {node.Subnet.Cidr()}");
    }

    /// <summary>
    ///     Forgets the children worked out so far (after the registries were read again) and reads the root's again; the rest
    ///     of the tree is built anew as it is opened.
    /// </summary>
    public Task ReloadAsync()
    {
        Root.ResetChildren();
        return Root.GetNode().ReloadChildrenAsync();
    }

    public NetworkTree(AddressFamily addrFamily)
    {
        switch (addrFamily)
        {
            case AddressFamily.InterNetwork:
                Root = Net4Node.CreateRoot(this);
                break;
            case AddressFamily.InterNetworkV6:
                Root = Net6Node.CreateRoot(this);
                break;
            default:
                throw new NotSupportedException("Only address families supported:  InterNetwork, InterNetworkV6");
        }

        Family = addrFamily;
    }

    public NetTreeNode? GetRoot(Uri uri)
    {
        switch (uri.Required().Scheme.ToLowerInvariant())
        {
            case "ip4" or "ipv4":
                return CanonicalIpv4.Root;
            case "ip6" or "ipv6":
                return CanonicalIpv6.Root;
            default:
                return null;
        }
    }

    public NetTreeNode? GetParent(NetTreeNode node)
    {
        return node?.Parent;
    }

    /// <summary>
    ///     The children of the node. Those that name registered blocks are only as complete as the IANA registries read
    ///     so far (see <see cref="AddressRegistry.EnsureLoadedAsync" />).
    /// </summary>
    public IEnumerable<NetTreeNode?> GetChildren(NetTreeNode node)
    {
        return node.Required().Children;
    }
}

/// <summary>What a node of the network tree is, for its icon and its words.</summary>
public enum NetBlockKind
{
    /// <summary>The whole address space (/0).</summary>
    Internet,

    /// <summary>A block set aside, reserved or not given out (private, loopback, multicast...).</summary>
    WellKnown,

    /// <summary>A block IANA gave to a regional Internet registry (RIR).</summary>
    Rir,

    /// <summary>A block handed out directly before the registries existed (a company or university /8).</summary>
    Legacy,

    /// <summary>Any other block: an allocation, an assignment, a LAN.</summary>
    Subnet,

    /// <summary>A single address.</summary>
    Host
}

/// <summary>
///     The icon keys of the network tree: the shapes drawn for it in <c>Resources/Paths.xaml</c> and
///     <c>NavIcons.xaml</c>.
/// </summary>
public static class NetworkIcons
{
    public const string Internet  = "Icon094Globe";
    public const string WellKnown = "Modules";
    public const string Rir       = "IconNetwork";
    public const string Legacy    = "Icon081Connect1";
    public const string Subnet    = "Icon077Network1";
    public const string Host      = "Cube";
    public const string Address   = "IconIpAddress";

    public static string For(NetBlockKind kind)
    {
        return kind switch
               {
                   NetBlockKind.Internet  => Internet,
                   NetBlockKind.WellKnown => WellKnown,
                   NetBlockKind.Rir       => Rir,
                   NetBlockKind.Legacy    => Legacy,
                   NetBlockKind.Host      => Host,
                   _                      => Subnet
               };
    }
}

/// <summary>
///     Base class for nodes of NetworkTrees. A node is a subnet; it brings its own Explorer node (<see cref="GetNode" />),
///     its
///     properties (<see cref="GetDetails" />) and its commands (<see cref="GetCommands" />).
/// </summary>
public abstract class NetTreeNode : INodeProvider, INodeDetails, ICommandProvider
{
    private static readonly int[] V4Strides = [8, 16, 24, 28, 32];

    private readonly object _gate = new();

    private List<NetTreeNode>? _children;
    private NetSiloNode?       _node;

    /// <summary>The subnets one step down (see <see cref="NetworkTree" />), in address order; worked out on first use.</summary>
    public IReadOnlyList<NetTreeNode> Children
    {
        get
        {
            lock (_gate)
            {
                return _children ??= BuildChildren();
            }
        }
    }

    /// <summary>True when the node has children (a single address, or an IPv6 /64, has none).</summary>
    public bool HasChildren => NextStride(Subnet) != 0;

    public int IPVersion { get; }

    /// <summary>The kind of block the node is.</summary>
    public NetBlockKind Kind
    {
        get
        {
            if (Subnet.PrefixLength == 0) return NetBlockKind.Internet;
            if (Special is { } special && special.Subnet.PrefixLength == Subnet.PrefixLength) return NetBlockKind.WellKnown;

            if (Registry is { } entry && entry.Subnet.PrefixLength == Subnet.PrefixLength)
            {
                if (entry.Status.Equals("RESERVED",    StringComparison.OrdinalIgnoreCase) ||
                    entry.Status.Equals("UNALLOCATED", StringComparison.OrdinalIgnoreCase))
                    return NetBlockKind.WellKnown;
                if (entry.Status.Equals("LEGACY", StringComparison.OrdinalIgnoreCase)) return NetBlockKind.Legacy;
                if (entry.Rir is not null) return NetBlockKind.Rir;
            }

            return Subnet.PrefixLength == Subnet.MaxPrefixLength ? NetBlockKind.Host : NetBlockKind.Subnet;
        }
    }

    /// <summary>A few words on what the block is, or null when there is nothing to say beyond its address.</summary>
    public string? Label
    {
        get
        {
            if (Subnet.PrefixLength == 0) return Subnet.IsV6() ? "The Internet (IPv6)" : "The Internet (IPv4)";
            if (Special is { } special && special.Subnet.PrefixLength == Subnet.PrefixLength) return special.Name;
            if (Registry is { } entry && entry.Subnet.PrefixLength == Subnet.PrefixLength && entry.Designation.Length > 0)
                return entry.Designation;

            return null;
        }
    }

    public NetTreeNode? Parent { get; }

    /// <summary>
    ///     The most specific IANA registry line that holds the subnet (or is it), or null; null as well until the
    ///     registries are read.
    /// </summary>
    public RegistryEntry? Registry => AddressRegistry.EntryFor(Subnet);

    /// <summary>The most specific special-purpose block that holds the subnet (or is it), or null.</summary>
    public SpecialBlock? Special => AddressRegistry.SpecialFor(Subnet);

    public IPSubnet Subnet { get; }

    public int TotalBits => Subnet.MaxPrefixLength;

    public NetworkTree Tree { get; }

    /// <inheritdoc />
    public IconRef? Icon => IconRef.FromKey(NetworkIcons.For(Kind));

    /// <summary>The node's text in the Explorer: the CIDR, and what the block is when that is known.</summary>
    public string Name => Label is { } label ? $"{Subnet.Cidr()}  {label}" : Subnet.Cidr();

    /// <summary>What the block is, in words.</summary>
    public string TypeName
    {
        get
        {
            switch (Kind)
            {
                case NetBlockKind.Internet:
                    return "Address space";
                case NetBlockKind.WellKnown:
                    return Label is { } label ? $"Reserved block ({label})" : "Reserved block";
                case NetBlockKind.Rir:
                    return $"RIR block ({Registry?.Rir})";
                case NetBlockKind.Legacy:
                    return "Legacy allocation";
                case NetBlockKind.Host:
                    return "Address";
                default:
                    return $"Subnet (/{Subnet.PrefixLength})";
            }
        }
    }

    /// <inheritdoc />
    public Uri Uri => Subnet.ToUri();

    /// <inheritdoc />
    public object? Data => Subnet;

    /// <summary>The Explorer node of this subnet (made once).</summary>
    public SiloNode GetNode()
    {
        lock (_gate)
        {
            return _node ??= new NetSiloNode(this);
        }
    }

    /// <summary>Forgets the children (they are worked out again when asked for).</summary>
    public void ResetChildren()
    {
        lock (_gate)
        {
            _children = null;
        }
    }

    /// <summary>Reads the registries if that is not done yet, then makes the Explorer nodes of the children.</summary>
    internal async Task<IReadOnlyList<SiloNode>> LoadChildNodesAsync(CancellationToken cancel)
    {
        await AddressRegistry.EnsureLoadedAsync().WaitAsync(cancel);
        return await Task.Run(() => (IReadOnlyList<SiloNode>)Children.Select(c => c.GetNode()).ToList(), cancel);
    }

    protected abstract NetTreeNode CreateChild(IPSubnet subnet);

    // the subnets of the next step down, where a block the registries name that is larger than the step stands for itself
    private List<NetTreeNode> BuildChildren()
    {
        var list   = new List<NetTreeNode>();
        int stride = NextStride(Subnet);
        if (stride == 0) return list;

        var larger = new List<IPSubnet>();
        foreach (var known in AddressRegistry.KnownWithin(Subnet)
                                             .Where(k => k.PrefixLength < stride)
                                             .OrderBy(k => k.PrefixLength)
                                             .ThenBy(k => k.Network.ToNumber()))
            if (!larger.Any(l => l.Covers(known)))
                larger.Add(known);

        IPSubnet? added = null;
        foreach (var cell in Subnet.Subdivide(stride))
        {
            var cover = larger.FirstOrDefault(l => l.Covers(cell));
            if (cover is null)
            {
                list.Add(CreateChild(cell));
            }
            else if (!cover.SameAs(added))
            {
                list.Add(CreateChild(cover));
                added = cover;
            }
        }

        return list;
    }

    private static int NextStride(IPSubnet subnet)
    {
        int prefix = subnet.PrefixLength;
        if (!subnet.IsV6()) return V4Strides.FirstOrDefault(s => s > prefix);
        if (prefix >= 64) return 0;
        if (prefix < 32) return (prefix / 8 + 1) * 8;
        return (prefix / 4 + 1) * 4;
    }

    protected NetTreeNode(NetworkTree tree, NetTreeNode? parent, IPSubnet subnet, int ipVersion)
    {
        if (ipVersion is not (4 or 6))
            throw new NotSupportedException("Invalid IPVersion value");

        bool v6 = subnet.IsV6();
        if (v6 != (ipVersion == 6))
            throw new InvalidOperationException("The address specified does not match the expected address family.");

        Tree      = tree;
        Parent    = parent;
        Subnet    = subnet.Normalized();
        IPVersion = ipVersion;
    }

    SiloNode? INodeProvider.GetNode()
    {
        return GetNode();
    }

    #region Properties

    /// <inheritdoc />
    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        bool v6 = Subnet.IsV6();
        yield return ("Network", Subnet.Cidr());
        yield return ("Address family", v6 ? "IPv6" : "IPv4");
        yield return ("Kind", TypeName);
        if (Label is { } label) yield return ("Description", label);

        yield return ("First address", Subnet.First().ToString());
        yield return ("Last address", Subnet.Last().ToString());
        if (Subnet.SubnetMask is { } mask) yield return ("Netmask", mask.ToString());
        if (Subnet.Wildcard() is { } wildcard) yield return ("Wildcard mask", wildcard.ToString());
        yield return ("Addresses", Count(Subnet.Size()));
        if (!v6) yield return ("Usable hosts", Usable());

        yield return ("Share of the address space", Share());
        if (Parent is not null) yield return ("Parent block", Parent.Subnet.Cidr());

        if (!v6 && Subnet.PrefixLength > 0)
            yield return ("Classful range (historic)", Classful(Subnet.Network.GetAddressBytes()[0]));

        // what is set aside
        var special = Special;
        if (special is not null)
        {
            bool exact = special.Subnet.PrefixLength == Subnet.PrefixLength;
            yield return (exact ? "Special purpose" : "Inside special purpose block", $"{special.Name} ({special.Subnet.Cidr()})");
            if (special.Rfc.Length > 0) yield return ("Defined by", special.Rfc + (special.RfcUrl is { } url ? "  " + url : ""));
            yield return ("Valid as a source", Flag(special.Source));
            yield return ("Valid as a destination", Flag(special.Destination));
            yield return ("Forwardable", Flag(special.Forwardable));
            yield return ("Globally reachable", Flag(special.Global));
            yield return ("Reserved by protocol", Flag(special.ReservedByProtocol));
        }

        // who it went to
        if (Registry is { } entry)
        {
            bool exact = entry.Subnet.PrefixLength == Subnet.PrefixLength;
            yield return (exact ? "IANA designation" : $"IANA designation (of {entry.Subnet.Cidr()})", entry.Designation);
            if (entry.Status.Length > 0) yield return ("IANA status", entry.Status);
            if (entry.Rir is { } rir) yield return ("Regional registry", rir);
            if (entry.Date is not null) yield return ("Date", entry.Date);
            if (entry.Whois is not null) yield return ("WHOIS server", entry.Whois);
            if (entry.Rdap is not null) yield return ("RDAP service", entry.Rdap);
            if (entry.Note is not null) yield return ("Registry note", entry.Note);
        }
        else if (AddressRegistry.Entries.Count == 0)
        {
            yield return ("IANA registry", "not read yet (expand the tree, or choose Update IANA Registry Data)");
        }

        // reverse DNS
        string zone = Subnet.ReverseZone(out bool aligned);
        yield return ("Reverse DNS zone", aligned ? zone : zone + "  (holds this block, which is not a zone of its own)");

        foreach (string line in Trivia()) yield return ("Did you know", line);
    }

    private IEnumerable<string> Trivia()
    {
        if (Special?.Trivia is { } trivia && Special.Subnet.PrefixLength == Subnet.PrefixLength) yield return trivia;

        int p = Subnet.PrefixLength;
        if (!Subnet.IsV6())
            switch (p)
            {
                case 24:
                    yield return
                        "A /24 holds 254 hosts: the size of a classic Class C network, and the smallest block most providers will route on the Internet.";
                    break;
                case 30:
                    yield return "A /30 holds the two hosts of a point-to-point link (and the network and broadcast addresses)."; break;
                case 31: yield return "A /31 is a point-to-point link with no network or broadcast address (RFC 3021)."; break;
                case 32: yield return "A /32 is a single host."; break;
            }
        else
            switch (p)
            {
                case 32: yield return "A /32 is the usual allocation to a provider (a LIR) from its regional registry."; break;
                case 48: yield return "A /48 is the usual assignment to a site: 65,536 /64 subnets."; break;
                case 56: yield return "A /56 is a common assignment to a home: 256 /64 subnets."; break;
                case 64:
                    yield return "A /64 is one subnet: the size stateless address autoconfiguration needs, with 2^64 addresses."; break;
                case 128: yield return "A /128 is a single host."; break;
            }
    }

    private string Usable()
    {
        int p = Subnet.PrefixLength;
        return p switch
               {
                   32 => "1 (the address itself)",
                   31 => "2 (point-to-point, RFC 3021)",
                   _  => (Subnet.Size() - 2).ToString("N0") + "  (without the network and broadcast addresses)"
               };
    }

    private string Share()
    {
        // 1 in 2^p of the whole space
        if (Subnet.PrefixLength == 0) return "all of it";

        double percent = 100 * Math.Pow(2, -Subnet.PrefixLength);
        string text    = percent >= 0.0001 ? percent.ToString("0.####") : percent.ToString("0.###e+0");
        return $"1 in 2^{Subnet.PrefixLength}  ({text}%)";
    }

    private static string Count(BigInteger size)
    {
        int bits = (int)BigInteger.Log(size, 2);
        return size > 1_000_000_000_000 ? $"2^{bits}  ({size:N0})" : size.ToString("N0");
    }

    private static string Classful(byte first)
    {
        return first switch
               {
                   < 128 => "Class A",
                   < 192 => "Class B",
                   < 224 => "Class C",
                   < 240 => "Class D (multicast)",
                   _     => "Class E (reserved)"
               };
    }

    private static string Flag(bool? value)
    {
        return value switch { true => "Yes", false => "No", _ => "n/a" };
    }

    #endregion

    #region Commands

    /// <inheritdoc />
    public IEnumerable<ProvidedCommand> GetCommands()
    {
        const string RegistryGroup = "Registry";
        const string DnsGroup      = "Reverse DNS";
        const string NetworkGroup  = "Network";
        const string CopyGroup     = "Copy";
        const string SpaceGroup    = "Address Space";

        string cidr = Subnet.Cidr();

        // the whole space: where to go in it, and the data behind it
        if (Parent is null)
        {
            yield return ProvidedCommand.From("net.goto", "_Go to Address...", () => Run(GoToAsync, "Go to address"), "Search",
                                              toolTip: "Open the tree down to an address or subnet (10.129.64.0/11, 2001:db8::1)",
                                              group: SpaceGroup);
            yield return ProvidedCommand.From("net.update", "_Update IANA Registry Data",
                                              () => Run(UpdateAsync, "Update IANA registry data"), "Refresh",
                                              toolTip: "Download the IANA address registries again (they are kept for a week)",
                                              group: SpaceGroup);
            yield return ProvidedCommand.From("net.remove", "Remove from E_xplorer", RemoveFromExplorer,
                                              toolTip: "Take the address space out of the Explorer", group: SpaceGroup);
        }

        // who holds it
        yield return ProvidedCommand.From("net.rdap", "_RDAP Lookup", () => Run(RdapAsync, "RDAP lookup"), "Search",
                                          toolTip: "Ask the regional registry who holds this block (RDAP, the successor of WHOIS)",
                                          group: RegistryGroup);
        yield return ProvidedCommand.From("net.whois", "_WHOIS Lookup", () => Run(WhoisAsync, "WHOIS lookup"), "Search",
                                          toolTip: "Ask whois.iana.org, then the registry it points to", group: RegistryGroup);
        foreach (var (name, _, url) in NetworkLookups.Links(Subnet))
        {
            var target = url;
            yield return ProvidedCommand.From("net.link." + target.Host + target.AbsolutePath, name, () => _ = Browser.LaunchAsync(target),
                                              NetworkIcons.Internet, toolTip: target.AbsoluteUri, group: RegistryGroup);
        }

        if (Special?.RfcUrl is { } rfc)
            yield return ProvidedCommand.From("net.rfc", $"Read {Special.Rfc}", () => _ = Browser.LaunchAsync(rfc), "Document",
                                              toolTip: rfc.AbsoluteUri, group: RegistryGroup);

        // names
        string zone = Subnet.ReverseZone(out _);
        if (Kind == NetBlockKind.Host)
            yield return ProvidedCommand.From("net.ptr", "Look up _PTR (Reverse DNS)", () => Run(PtrAsync, "Reverse DNS lookup"),
                                              NetworkIcons.Address, toolTip: "Ask DNS which name the address has", group: DnsGroup);

        yield return ProvidedCommand.From("net.zone.show", "Show Zone in _DNS Tree", () => Run(ShowZoneAsync, "Show zone in DNS tree"), DnsIcons.Zone,
                                          toolTip: "Open " + zone + " in the domain name tree", group: DnsGroup);
        yield return ProvidedCommand.From("net.zone.ns", "Look up the Zone's _NS Records", () => Run(() => ZoneAsync("NS"), "NS lookup"),
                                          "Search", toolTip: "Which name servers answer for " + zone, group: DnsGroup);
        yield return ProvidedCommand.From("net.zone.soa", "Look up the Zone's S_OA Record", () => Run(() => ZoneAsync("SOA"), "SOA lookup"),
                                          "Search", toolTip: "The start of authority of " + zone, group: DnsGroup);

        // the route to it
        if (Kind != NetBlockKind.Internet)
            yield return ProvidedCommand.From("net.trace", "Visual _Traceroute", Trace, "Network",
                                              toolTip: "Trace the route to " + TraceTarget(), group: NetworkGroup);

        // copy
        yield return ProvidedCommand.From("net.copy.cidr", "Copy _CIDR", () => Clipboard.SetText(cidr), "TbCopy", group: CopyGroup);
        yield return ProvidedCommand.From("net.copy.range", "Copy _Range", () => Clipboard.SetText($"{Subnet.First()} - {Subnet.Last()}"),
                                          "TbCopy", group: CopyGroup);
        yield return ProvidedCommand.From("net.copy.zone", "Copy Reverse _Zone Name", () => Clipboard.SetText(zone), "TbCopy",
                                          group: CopyGroup);
    }

    private IPAddress TraceTarget()
    {
        // the first host of a subnet; a single address is its own target
        var first = Subnet.First();
        if (Subnet.PrefixLength >= Subnet.MaxPrefixLength - 1) return first;
        return SubnetMath.FromNumber(first.ToNumber() + 1, first.AddressFamily);
    }

    private void Trace()
    {
        var tool = App.Resolve<IEnumerable<ITool>>()?.OfType<VisualTracerouteTool>().FirstOrDefault();
        if (tool is null)
        {
            App.Status("The Visual Traceroute tool is not available");
            return;
        }

        tool.Trace(TraceTarget().ToString());
    }

    private async Task RdapAsync()
    {
        App.Status($"Asking the registry about {Subnet.Cidr()}...");
        var (url, json) = await NetworkLookups.RdapAsync(Subnet);
        TextEditors.OpenText("net:rdap:" + Subnet.Cidr(), "RDAP " + Subnet.Cidr(), url.AbsoluteUri, json,
                             "rdap-"     + Subnet.Network         + ".json", "application/json");
        App.Status($"RDAP answer for {Subnet.Cidr()} from {url.Host}");
    }

    private async Task WhoisAsync()
    {
        App.Status($"Asking whois.iana.org about {Subnet.Network}...");
        string text = await NetworkLookups.WhoisAsync(Subnet);
        TextEditors.OpenText("net:whois:" + Subnet.Cidr(), "WHOIS " + Subnet.Cidr(), "whois://whois.iana.org/" + Subnet.Network, text,
                             "whois-"     + Subnet.Network          + ".txt", "text/plain");
        App.Status($"WHOIS answer for {Subnet.Network}");
    }

    private async Task PtrAsync()
    {
        var address = Subnet.Network;
        App.Status($"Looking up the name of {address}...");
        try
        {
            string[] names = await NetworkLookups.ReverseLookupAsync(address);
            App.Status($"{address} is {string.Join(", ", names)}");
        }
        catch (SocketException ex)
        {
            App.Status($"{address} has no reverse DNS name ({ex.Message})");
        }
    }

    private async Task ZoneAsync(string type)
    {
        string zone = Subnet.ReverseZone(out _);
        App.Status($"Asking for the {type} of {zone}...");
        string text = await NetworkLookups.NsLookupAsync(zone, type);
        TextEditors.OpenText($"net:{type}:{zone}", $"{type} {zone}", "dns://" + zone, text, null, "text/plain");
    }

    private async Task ShowZoneAsync()
    {
        var silo = App.Instance?.ActiveSilo ?? throw new InvalidOperationException("Open a Silo first");
        if (!AddressSpaces.IsShown(silo, AddressSpace.Dns)) AddressSpaces.Show(silo, AddressSpace.Dns, true);

        App.Status($"Opening {Subnet.ReverseZone(out _)}...");
        await DnsTree.Canonical.RevealAsync(Subnet.ReverseZone(out _));
    }

    private async Task GoToAsync()
    {
        string? text = PromptDialog.Ask("Go to Address", "An address or a subnet (10.129.64.0/11, 2001:db8::1):", "Go");
        if (text is null) return;

        if (!SubnetMath.TryParse(text, out var target) || target.IsV6() != Subnet.IsV6())
        {
            App.Status($"'{text}' is not {(Subnet.IsV6() ? "an IPv6" : "an IPv4")} address or subnet");
            return;
        }

        await NetworkTree.ShowAsync(target);
    }

    private async Task UpdateAsync()
    {
        App.Status("Downloading the IANA address registries...");
        await AddressRegistry.RefreshAsync();
        await Tree.ReloadAsync();
        App.Status($"IANA address registries read: {AddressRegistry.Entries.Count:N0} lines");
    }

    private void RemoveFromExplorer()
    {
        TreeActions.RemoveFromExplorer(this, Subnet.IsV6() ? AddressSpace.Ipv6 : AddressSpace.Ipv4);
    }

    // a command's work runs on the UI thread and goes on after the first await; a failure is said in the status bar, not thrown
    private static async void Run(Func<Task> work, string what)
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

    #endregion
}

/// <summary>
///     The node of the Explorer for a <see cref="NetTreeNode" />: its address, icon and words, and its children as
///     they are asked for.
/// </summary>
public record NetSiloNode : SiloNode
{
    public NetSiloNode(NetTreeNode model) : base(model, model.Name, model.Uri, model.Icon, null)
    {
        TypeName = model.TypeName;
        if (model.HasChildren) ChildLoader = model.LoadChildNodesAsync;
    }
}

/// <summary>
///     A network tree node that represents an IPv4 network
/// </summary>
public sealed class Net4Node : NetTreeNode
{
    public static Net4Node CreateRoot(NetworkTree tree)
    {
        return new Net4Node(tree, null, new IPSubnet(IPAddress.Any, 0));
    }

    protected override NetTreeNode CreateChild(IPSubnet subnet)
    {
        return new Net4Node(Tree, this, subnet);
    }

    public Net4Node(NetworkTree tree, NetTreeNode? parent, IPSubnet subnet) : base(tree, parent, subnet, 4)
    {
    }
}

/// <summary>
///     A network tree node that represents an IPv6 network
/// </summary>
public sealed class Net6Node : NetTreeNode
{
    public static Net6Node CreateRoot(NetworkTree tree)
    {
        return new Net6Node(tree, null, new IPSubnet(IPAddress.IPv6Any, 0));
    }

    protected override NetTreeNode CreateChild(IPSubnet subnet)
    {
        return new Net6Node(Tree, this, subnet);
    }

    public Net6Node(NetworkTree tree, NetTreeNode? parent, IPSubnet subnet) : base(tree, parent, subnet, 6)
    {
    }
}