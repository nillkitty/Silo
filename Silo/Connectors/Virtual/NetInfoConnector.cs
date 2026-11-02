using System.Net;
using Silo.Contracts.Core;
using Silo.Contracts.UI;
using Silo.Providers.Data.Source;
using Silo.Providers.Network;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors.Virtual;

/// <summary>
///     The machine's network configuration and state as a connectionless tree of data sources: host config, wifi profiles and
///     adapters; addresses, interfaces (each with its addresses and routes) and routes; and TCP/UDP sockets joined with the
///     process that owns them. Every node's <see cref="SiloNode.DataContext" /> is an <see cref="IFlatDataProvider" /> whose
///     data sources are <see cref="ITabularDataSource" />s that read the live system each time their rows are asked for.
/// </summary>
[Transient(typeof(IConnector))]
public class NetInfoConnector : VirtualRoot<NetInfoModel?, ConnectionlessState>, INodeProvider<NetInfoModel>
{
    private readonly INetworkInfoReader _reader;

    public NetInfoModel? Data { get; private set; }

    public NetInfoConnector() : this(null)
    {
    }

    public NetInfoConnector(INetworkInfoReader? reader)
    {
        _reader = reader ?? new SystemNetworkInfoReader();
        Data    = new NetInfoModel(_reader);
    }

    protected override IConnection OnCreateConnection(ConnectionRequest crq)
    {
        return this;
    }

    public SiloNode? GetNode()
    {
        return Node ??= Data!.BuildTree();
    }
}

/// <summary>The icons of the network nodes (keys in <c>Resources/NavIcons.xaml</c>).</summary>
internal static class NetInfoIcons
{
    public static readonly Uri Network = new("Network", UriKind.Relative);
    public static readonly Uri Server  = new("Server", UriKind.Relative);
    public static readonly Uri Plug    = new("Plug", UriKind.Relative);
    public static readonly Uri Table   = new("TbTable", UriKind.Relative);
}

/// <summary>Builds the network tree and its tables from an <see cref="INetworkInfoReader" />.</summary>
public class NetInfoModel(INetworkInfoReader reader)
{
    public INetworkInfoReader Reader { get; } = reader;

    // ---- tables ----------------------------------------------------------------------------------------------

    public NetTable HostConfig(IDataProvider? owner = null)
    {
        return new NetTable(owner, "Host Network Config", "host", ["Setting", "Value"], () =>
        {
            var h = Reader.GetHostConfig();
            return new IReadOnlyList<string>[]
            {
                ["Host Name", h.HostName],
                ["Domain Name", h.DomainName],
                ["Node Type", h.NodeType],
                ["DHCP Scope", h.DhcpScopeName ?? ""],
                ["WINS Proxy", Yes(h.IsWinsProxy)],
                ["DNS Servers", Join(h.DnsServers)],
                ["DNS Suffixes", string.Join(", ", h.DnsSuffixes)]
            };
        });
    }

    public NetTable Profiles(IDataProvider? owner = null, string? profileName = null)
    {
        return new NetTable(owner, profileName ?? "Wifi Profiles", profileName is null ? "wifi" : $"wifi/{Uri.EscapeDataString(profileName)}",
                            ["Profile", "SSID", "Authentication", "Encryption", "Mode", "Hidden", "All Users", "Interface"],
                            () => Reader.GetWifiProfiles()
                                        .Where(p => profileName is null || p.Name == profileName)
                                        .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                                        .Select(p => (IReadOnlyList<string>)[p.Name, p.Ssid, p.Authentication, p.Encryption, p.ConnectionMode,
                                                                             Yes(p.Hidden), Yes(p.AllUsers), p.Interface]));
    }

    public NetTable Adapters(IDataProvider? owner = null)
    {
        return new NetTable(owner, "Adapters", "adapters",
                            ["Name", "Description", "Enabled", "Status", "Type", "MAC", "MAC Override", "Speed", "Driver", "Version", "Provider", "Id"],
                            () => Reader.GetAdapters()
                                        .Select(a => (IReadOnlyList<string>)[a.Name, a.Description, Yes(a.Present), a.Status, a.Type, a.Mac ?? "",
                                                                             a.MacOverride ?? "", Speed(a.Speed), a.Driver ?? "", a.DriverVersion ?? "",
                                                                             a.Provider ?? "", a.Id]));
    }

    private static readonly string[] AddressColumns = ["Interface", "Address", "Prefix", "CIDR", "Family", "Origin"];

    /// <param name="cidr">When given, only that address (<c>192.168.1.19/24</c>).</param>
    public NetTable Addresses(IDataProvider? owner = null, string? interfaceName = null, string? cidr = null)
    {
        var path = cidr is not null ? $"addresses/{Uri.EscapeDataString(cidr)}"
                   : interfaceName is null ? "addresses"
                   : $"interfaces/{Uri.EscapeDataString(interfaceName)}/addresses";
        return new NetTable(owner, cidr ?? "Addresses", path, AddressColumns,
                            () => Reader.GetInterfaces()
                                        .Where(i => interfaceName is null || i.Name == interfaceName)
                                        .SelectMany(i => i.Addresses)
                                        .Where(a => cidr is null || a.Cidr == cidr)
                                        .Select(a => (IReadOnlyList<string>)[a.Interface, a.Address.ToString(), a.PrefixLength.ToString(), a.Cidr,
                                                                             a.Family, a.Origin]));
    }

    private static readonly string[] InterfaceColumns =
    [
        "Name", "Description", "Status", "Type", "MAC", "Speed", "MTU", "DHCP", "Addresses", "Gateways", "DNS Servers", "DNS Suffix",
        "DNS Enabled", "Dynamic DNS", "Index"
    ];

    private static IReadOnlyList<string> InterfaceRow(NetInterface i)
    {
        return [i.Name, i.Description, i.Status.ToString(), i.Type.ToString(), i.Mac, Speed(i.Speed), i.Mtu.ToString(), Yes(i.DhcpEnabled),
                string.Join(", ", i.Addresses.Select(a => a.Cidr)), Join(i.Gateways), Join(i.DnsServers), i.DnsSuffix ?? "", Yes(i.DnsEnabled),
                Yes(i.DynamicDns), i.Index.ToString()];
    }

    /// <param name="addressedOnly">True to leave out the interfaces that have no unicast address.</param>
    public NetTable Interfaces(IDataProvider? owner = null, string? interfaceName = null, bool addressedOnly = false)
    {
        return new NetTable(owner, interfaceName ?? "Interfaces", interfaceName is null ? "interfaces" : $"interfaces/{Uri.EscapeDataString(interfaceName)}",
                            InterfaceColumns,
                            () => Reader.GetInterfaces()
                                        .Where(i => interfaceName is null || i.Name == interfaceName)
                                        .Where(i => !addressedOnly || i.Addresses.Count > 0)
                                        .Select(InterfaceRow));
    }

    /// <summary>
    ///     The address that stands for an interface: its most specific (longest prefix) IPv4 address, else the same among its IPv6 ones
    ///     (a routable address before a link-local one). Null when it has none.
    /// </summary>
    public static NetAddress? PrimaryAddress(NetInterface i)
    {
        var pool = i.Addresses.Where(a => a.Family == "IPv4").ToList();
        if (pool.Count == 0) pool = i.Addresses.ToList();

        return pool.OrderBy(a => a.Address.IsIPv6LinkLocal ? 1 : 0).ThenByDescending(a => a.PrefixLength).FirstOrDefault();
    }

    private static readonly string[] RouteColumns = ["Destination", "Next Hop", "Interface", "Metric", "Protocol", "Family", "Age (s)"];

    private static IReadOnlyList<string> RouteRow(NetRoute r)
    {
        return [r.Destination, r.NextHop.Equals(IPAddress.Any) || r.NextHop.Equals(IPAddress.IPv6Any) ? "On-link" : r.NextHop.ToString(), r.Interface,
                r.Metric.ToString(), r.Protocol, r.Family, r.AgeSeconds.ToString()];
    }

    /// <param name="order">"destination" (most specific prefix first, within an address) or "interface".</param>
    public NetTable Routes(IDataProvider? owner = null, string order = "destination", string? interfaceName = null)
    {
        var name = interfaceName is null ? order == "interface" ? "Routes by Interface" : "Routes by Destination" : "Routes";
        var path = interfaceName is null ? $"routes/by-{order}" : $"interfaces/{Uri.EscapeDataString(interfaceName)}/routes";
        return new NetTable(owner, name, path, RouteColumns, () =>
        {
            var routes = Reader.GetRoutes().Where(r => interfaceName is null || r.Interface == interfaceName);
            routes = order == "interface"
                         ? routes.OrderBy(r => r.Interface, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Family).ThenBy(r => r.Metric)
                         : routes.OrderBy(r => r.Family).ThenBy(r => r.Prefix.GetAddressBytes(), ByteComparer.Instance).ThenBy(r => r.PrefixLength)
                                 .ThenBy(r => r.Metric);
            return routes.Select(RouteRow);
        });
    }

    private static readonly string[] SocketColumns =
        ["Process", "PID", "Protocol", "Local Address", "Local Port", "Remote Address", "Remote Port", "State", "User", "Path", "Started"];

    private static IReadOnlyList<string> SocketRow(PortOwner s)
    {
        return [s.Process?.Name ?? (s.ProcessId == 0 ? "System" : "?"), s.ProcessId.ToString(), s.Protocol.ToString().ToUpperInvariant(),
                s.Local.Address.ToString(), s.Local.Port.ToString(), s.Remote?.Address.ToString() ?? "", s.Remote?.Port.ToString() ?? "",
                s.State?.ToString() ?? "", s.Process?.UserName ?? "", s.Process?.ExecutablePath ?? "",
                s.Process?.StartedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? ""];
    }

    /// <param name="tcp">True for TCP connections and listeners, false for UDP sessions.</param>
    /// <param name="grouping">"application", "destination" or "port".</param>
    public NetTable Sockets(IDataProvider? owner, bool tcp, string grouping)
    {
        var kind  = tcp ? "TCP" : "UDP";
        var title = grouping switch { "application" => "By Application", "destination" => "By Destination", _ => "By Port/Protocol" };
        return new NetTable(owner, $"{kind} {title}", $"{kind.ToLowerInvariant()}/by-{grouping}", SocketColumns, () =>
        {
            var sockets = Reader.GetSockets().Where(s => (s.Protocol == PortProtocol.Tcp) == tcp);
            sockets = grouping switch
            {
                "application" => sockets.OrderBy(s => s.Process?.Name ?? "", StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.ProcessId)
                                        .ThenBy(s => s.Local.Port),
                "destination" => sockets.OrderBy(s => s.Remote is null ? 1 : 0).ThenBy(s => s.Remote?.Address.GetAddressBytes(), ByteComparer.Instance)
                                        .ThenBy(s => s.Remote?.Port ?? 0),
                _             => sockets.OrderBy(s => s.Protocol).ThenBy(s => s.Local.Port).ThenBy(s => s.ProcessId)
            };
            return sockets.Select(SocketRow);
        });
    }

    // ---- tree ------------------------------------------------------------------------------------------------

    private static SiloNode Leaf(NetProvider provider, string text, string typeName, Uri icon)
    {
        return new NetNode(provider, text, provider.GetDataSources().FirstOrDefault()?.DataSourceUri, icon, typeName);
    }

    private static SiloNode Folder(object context, string text, Uri icon, params SiloNode[] children)
    {
        var node = new NetNode(context, text, null, icon, "Network");
        foreach (var c in children) node.Children.Add(c);
        return node;
    }

    /// <summary>A folder whose data provider holds the tables of all its leaves, so the folder itself can be browsed as data.</summary>
    private static SiloNode Group(string text, Uri icon, params (string Text, Func<IDataProvider, NetTable> Make)[] leaves)
    {
        var all = new NetProvider(text, leaves.Select(l => l.Make).ToArray());
        var kids = leaves.Select(l => Leaf(new NetProvider(l.Text, [l.Make]), l.Text, "Data Source", NetInfoIcons.Table)).ToArray();
        return Folder(all, text, icon, kids);
    }

    /// <summary>A node that is a data source itself and whose children are read when it is first expanded.</summary>
    private static SiloNode Branch(NetProvider provider, string text, Uri icon, string typeName,
                                   Func<IReadOnlyList<SiloNode>> children)
    {
        return new NetNode(provider, text, provider.GetDataSources().FirstOrDefault()?.DataSourceUri, icon, typeName,
                           cancel => Task.Run(children, cancel));
    }

    public SiloNode BuildTree()
    {
        var host = Leaf(new NetItem("Host Network Config", "Host Network Config", NetInfoIcons.Table, [p => HostConfig(p)], HostDetails),
                        "Host Network Config", "Data Source", NetInfoIcons.Table);

        var profiles = Branch(new NetItem("Profiles", "Wifi Profiles", NetInfoIcons.Table, [p => Profiles(p)], null, WifiCommands),
                              "Profiles", NetInfoIcons.Table, "Data Source",
                              () => Reader.GetWifiProfiles().OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(ProfileNode).ToList());

        var adapters = Branch(new NetProvider("Adapters", [p => Adapters(p)]), "Adapters", NetInfoIcons.Plug, "Data Source",
                              () => Reader.GetInterfaces().Select(i => InterfaceNode(i, i.Name)).ToList());

        var config = Folder(new NetProvider("Network Configuration", [p => HostConfig(p), p => Profiles(p), p => Adapters(p)]), "Network Configuration",
                            NetInfoIcons.Server, host, profiles, adapters);

        var addresses = Branch(new NetProvider("Addresses", [p => Addresses(p)]), "Addresses", NetInfoIcons.Table, "Data Source",
                               () => Reader.GetInterfaces().SelectMany(i => i.Addresses)
                                           .OrderBy(a => a.Family).ThenBy(a => a.Address.GetAddressBytes(), ByteComparer.Instance)
                                           .Select(AddressNode).ToList());

        var interfaces = Branch(new NetProvider("Interfaces", [p => Interfaces(p, null, true)]), "Interfaces", NetInfoIcons.Plug, "Data Source",
                                () => Reader.GetInterfaces().Where(i => i.Addresses.Count > 0)
                                            .Select(i => InterfaceNode(i, $"{PrimaryAddress(i)!.Cidr} {i.Name}")).ToList());

        var routes = Group("Routes", NetInfoIcons.Table,
                           ("By Destination", p => Routes(p, "destination")),
                           ("By Interface", p => Routes(p, "interface")));

        var state = Folder(new NetProvider("Network State", [p => Addresses(p), p => Interfaces(p, null, true), p => Routes(p)]), "Network State",
                           NetInfoIcons.Plug, addresses, interfaces, routes);

        var tcp = Group("TCP Connections", NetInfoIcons.Plug,
                        ("By Application", p => Sockets(p, true, "application")),
                        ("By Destination", p => Sockets(p, true, "destination")),
                        ("By Port/Protocol", p => Sockets(p, true, "port")));
        var udp = Group("UDP Connections", NetInfoIcons.Plug,
                        ("By Application", p => Sockets(p, false, "application")),
                        ("By Destination", p => Sockets(p, false, "destination")),
                        ("By Port/Protocol", p => Sockets(p, false, "port")));

        var root = Folder(this, "Network", NetInfoIcons.Network, config, state, tcp, udp);
        root.IsExpanded = true;
        return root;
    }

    /// <summary>An interface: its own row, its addresses (CIDR list) and its routes.</summary>
    private SiloNode InterfaceNode(NetInterface i, string text)
    {
        var name = i.Name;
        var self = new NetProvider(name, [p => Interfaces(p, name), p => Addresses(p, name), p => Routes(p, "destination", name)]);
        return Folder(self, text, NetInfoIcons.Plug,
                      Leaf(new NetProvider(name, [p => Interfaces(p, name)]), "Interface", "Data Source", NetInfoIcons.Table),
                      Leaf(new NetProvider(name, [p => Addresses(p, name)]), "Addresses", "Data Source", NetInfoIcons.Table),
                      Leaf(new NetProvider(name, [p => Routes(p, "destination", name)]), "Routes", "Data Source", NetInfoIcons.Table));
    }

    private SiloNode AddressNode(NetAddress a)
    {
        var item = new NetItem(a.Cidr, "Unicast Address", NetInfoIcons.Table, [p => Addresses(p, null, a.Cidr)], () => AddressDetails(a));
        return Leaf(item, a.Cidr, "Data Source", NetInfoIcons.Table);
    }

    private SiloNode ProfileNode(WifiProfile w)
    {
        var item = new NetItem(w.Name, "Wifi Profile", NetInfoIcons.Network, [p => Profiles(p, w.Name)], () => ProfileDetails(w), WifiCommands);
        return Leaf(item, w.Name, "Wifi Profile", NetInfoIcons.Network);
    }

    // ---- properties and commands -----------------------------------------------------------------------------

    /// <summary>The host-wide parameters <c>netsh</c> shows (IP, IPv6 privacy and randomisation, TCP stack, offload), after the host's names.</summary>
    private IEnumerable<(string, string)> HostDetails()
    {
        var h = Reader.GetHostConfig();
        yield return ("Host Name", h.HostName);
        yield return ("Domain Name", h.DomainName);

        var settings = Reader.GetHostSettings();
        if (settings.Count == 0) yield return ("Stack settings", "Not available on this machine");

        foreach (var s in settings) yield return ($"{s.Group}: {s.Name}", s.Value);
    }

    private static IEnumerable<(string, string)> ProfileDetails(WifiProfile w)
    {
        yield return ("SSID", w.Ssid);
        yield return ("Authentication", w.Authentication);
        yield return ("Encryption", w.Encryption);
        yield return ("Connection Mode", w.ConnectionMode);
        yield return ("Hidden", Yes(w.Hidden));
        yield return ("All Users", Yes(w.AllUsers));
        yield return ("Interface", w.Interface);
    }

    private static IEnumerable<(string, string)> AddressDetails(NetAddress a)
    {
        yield return ("Address", a.Address.ToString());
        yield return ("Prefix Length", a.PrefixLength.ToString());
        yield return ("Network", $"{NetworkOf(a.Address, a.PrefixLength)}/{a.PrefixLength}");
        if (a.Family == "IPv4") yield return ("Subnet Mask", MaskOf(a.PrefixLength));
        yield return ("Family", a.Family);
        yield return ("Origin", a.Origin);
        yield return ("Interface", a.Interface);
    }

    internal static IPAddress NetworkOf(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefixLength - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }

        return new IPAddress(bytes);
    }

    internal static string MaskOf(int prefixLength)
    {
        var mask = prefixLength <= 0 ? 0u : uint.MaxValue << (32 - Math.Min(prefixLength, 32));
        return new IPAddress([(byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask]).ToString();
    }

    private static IEnumerable<ProvidedCommand> WifiCommands()
    {
        yield return ProvidedCommand.From("net.wifi.open", "Open in Windows _Settings", () => OpenInWindows("ms-settings:network-wifisettings"), "Network",
                                          toolTip: "Manage known networks in Windows Settings", group: "Windows");
    }

    /// <summary>Opens a Windows UI (a <c>ms-settings:</c> page, say) in its own application.</summary>
    private static void OpenInWindows(string target)
    {
        try
        {
            global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is global::System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Serilog.Log.Warning(ex, "Opening {Target} failed", target);
        }
    }

    // ---- formatting ------------------------------------------------------------------------------------------

    private static string Yes(bool b)
    {
        return b ? "Yes" : "No";
    }

    private static string Join(IEnumerable<IPAddress> addresses)
    {
        return string.Join(", ", addresses);
    }

    internal static string Speed(long? bitsPerSecond)
    {
        return bitsPerSecond switch
        {
            null or <= 0         => "",
            >= 1_000_000_000     => $"{bitsPerSecond / 1_000_000_000.0:0.##} Gbps",
            >= 1_000_000         => $"{bitsPerSecond / 1_000_000.0:0.##} Mbps",
            _                    => $"{bitsPerSecond / 1000.0:0.##} Kbps"
        };
    }

    private sealed class ByteComparer : IComparer<byte[]?>
    {
        public static readonly ByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (x is null || y is null) return (x is null ? 0 : 1) - (y is null ? 0 : 1);
            return x.AsSpan().SequenceCompareTo(y);
        }
    }
}

/// <summary>A node of the network tree.</summary>
public record NetNode : SiloNode
{
    public NetNode(object dataContext, string text, Uri? uri, IconRef? icon, string typeName,
                   Func<CancellationToken, Task<IReadOnlyList<SiloNode>>>? loader = null) : base(dataContext, text, uri, icon, null)
    {
        TypeName    = typeName;
        ChildLoader = loader;
    }
}

/// <summary>A flat data provider over a fixed set of <see cref="NetTable" /> makers.</summary>
public class NetProvider : IFlatDataProvider, INodeViewProvider
{
    private readonly List<NetTable> _tables;

    public string? DisplayName { get; }

    public NetProvider(string displayName, IEnumerable<Func<IDataProvider, NetTable>> makers)
    {
        DisplayName = displayName;
        _tables     = makers.Select(m => m(this)).ToList();
    }

    public UIElement? BuildView()
    {
        return _tables.Count == 0 ? null : NetTableView.For(_tables);
    }

    public IEnumerable<DataVerb> GetSupportedVerbs()
    {
        return [];
    }

    public IEnumerable<IDataSource> GetDataSources()
    {
        return _tables;
    }
}

/// <summary>
///     A <see cref="NetProvider" /> that also describes itself: the Explorer shows <paramref name="details" /> as the node's
///     properties and <paramref name="commands" /> in its menu.
/// </summary>
public sealed class NetItem(string displayName, string typeName, Uri icon, IEnumerable<Func<IDataProvider, NetTable>> makers,
                            Func<IEnumerable<(string, string)>>? details, Func<IEnumerable<ProvidedCommand>>? commands = null)
    : NetProvider(displayName, makers), INodeDetails, ICommandProvider
{
    public IconRef? Icon     { get; } = icon;
    public string   Name     => displayName;
    public string   TypeName => typeName;
    public Uri      Uri      => GetDataSources().First().DataSourceUri;

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        foreach (var row in details?.Invoke() ?? []) yield return row;

        yield return ("URI", Uri.OriginalString);
    }

    public IEnumerable<ProvidedCommand> GetCommands()
    {
        return commands?.Invoke() ?? [];
    }
}

/// <summary>A table whose rows are read from the system each time they are asked for.</summary>
public class NetTable : ITabularDataSource
{
    private readonly Func<IEnumerable<IReadOnlyList<string>>> _rows;

    public string                CanonicalName   { get; }
    public Uri                   DataSourceUri   { get; }
    public string?               DefaultAlias    { get; }
    public string?               LocalNamespace  => "Network";
    public string?               NativeNamespace => null;
    public IDataProvider?        Provider        { get; }
    public IReadOnlyList<string> ColumnNames     { get; }
    public Origin                Origin          => Origin.Unknown;

    public NetTable(IDataProvider? provider, string name, string path, IReadOnlyList<string> columns, Func<IEnumerable<IReadOnlyList<string>>> rows)
    {
        Provider      = provider;
        CanonicalName = name;
        DefaultAlias  = path.Split('/')[^1];
        DataSourceUri = new Uri($"silo:network/{path}", UriKind.Absolute);
        ColumnNames   = columns;
        _rows         = rows;
    }

    public IEnumerable<IReadOnlyList<string>> GetRows()
    {
        return _rows();
    }
}
