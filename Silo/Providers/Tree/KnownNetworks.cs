using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;

namespace Silo.Providers.Tree;

/// <summary>
///     The networks something has told us exist, as opposed to every CIDR that could: what the network tree shows by default. The IANA
///     registries and the special-purpose list are always counted (see <see cref="AddressRegistry" />); this adds the networks of this
///     computer's interfaces (static or DHCP addresses, router advertisements) and anything a provider registers with
///     <see cref="Add" /> (a route, a WHOIS answer, a scan).
/// </summary>
public static class KnownNetworks
{
    private static readonly object Gate = new();

    private static readonly Dictionary<(string Network, int Prefix), (IPSubnet Subnet, HashSet<string> Sources)> Registered = [];

    private static List<(IPSubnet Subnet, string Source)>? _interfaces;

    private static int _version;

    /// <summary>Counts the changes to the set (a tree node reads its children again when this is not what it built them under).</summary>
    public static int Version => Volatile.Read(ref _version);

    /// <summary>The set of known networks changed (the tree reads its children again).</summary>
    public static event EventHandler? Changed;

    /// <summary>Says that <paramref name="subnet" /> exists, and where that is known from ("route", "WHOIS", "static assignment"...).</summary>
    public static void Add(IPSubnet subnet, string source)
    {
        var normal = subnet.Normalized();
        lock (Gate)
        {
            var key = (normal.Network.ToString(), normal.PrefixLength);
            if (!Registered.TryGetValue(key, out var entry)) Registered[key] = entry = (normal, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            entry.Sources.Add(source);
        }

        Interlocked.Increment(ref _version);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Forgets what was registered under <paramref name="source" />.</summary>
    public static void RemoveSource(string source)
    {
        lock (Gate)
        {
            foreach (var key in Registered.Keys.ToList())
            {
                Registered[key].Sources.Remove(source);
                if (Registered[key].Sources.Count == 0) Registered.Remove(key);
            }
        }

        Interlocked.Increment(ref _version);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Reads this computer's interfaces again.</summary>
    public static void Refresh()
    {
        lock (Gate)
        {
            _interfaces = null;
        }

        Interlocked.Increment(ref _version);
    }

    /// <summary>Every known network strictly inside <paramref name="inside" />, once each (registries, special blocks, interfaces, registered).</summary>
    public static IReadOnlyList<IPSubnet> Within(IPSubnet inside)
    {
        var seen = new HashSet<(string, int)>();
        var list = new List<IPSubnet>();

        void Take(IPSubnet subnet)
        {
            if (subnet.NetworkAddress.AddressFamily == inside.NetworkAddress.AddressFamily && inside.StrictlyCovers(subnet) && seen.Add((subnet.Network.ToString(), subnet.PrefixLength))) list.Add(subnet);
        }

        foreach (var subnet in AddressRegistry.KnownWithin(inside)) Take(subnet);

        lock (Gate)
        {
            foreach (var (subnet, _) in Interfaces()) Take(subnet);
            foreach (var (subnet, _) in Registered.Values) Take(subnet);
        }

        return list;
    }

    /// <summary>Where a network is known from, apart from the IANA lists (empty when only they name it).</summary>
    public static IReadOnlyList<string> SourcesOf(IPSubnet subnet)
    {
        var sources = new List<string>();
        lock (Gate)
        {
            foreach (var (known, source) in Interfaces())
                if (known.SameAs(subnet) && !sources.Contains(source))
                    sources.Add(source);

            if (Registered.TryGetValue((subnet.Network.ToString(), subnet.PrefixLength), out var entry))
                sources.AddRange(entry.Sources.Where(s => !sources.Contains(s)));
        }

        return sources;
    }

    // called under the gate
    private static List<(IPSubnet Subnet, string Source)> Interfaces()
    {
        if (_interfaces is not null) return _interfaces;

        var found = new List<(IPSubnet, string)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            {
                var props = nic.GetIPProperties();
                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) continue;

                    string origin = unicast.PrefixOrigin switch
                                    {
                                        PrefixOrigin.Dhcp                  => "DHCP",
                                        PrefixOrigin.RouterAdvertisement   => "router advertisement",
                                        PrefixOrigin.Manual                => "static assignment",
                                        _                                  => "interface"
                                    };
                    string source = $"{origin} ({nic.Name})";
                    int    max    = unicast.Address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                    int    prefix = Math.Clamp(unicast.PrefixLength, 0, max);

                    if (prefix > 0 && prefix < max) found.Add((unicast.Address.SubnetOf(prefix), source));
                    found.Add((unicast.Address.SubnetOf(max), source));
                }

                foreach (var gateway in props.GatewayAddresses)
                    found.Add((gateway.Address.SubnetOf(gateway.Address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128), $"gateway ({nic.Name})"));
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            Log.Information(ex, "The network interfaces could not be read");
        }

        return _interfaces = found;
    }
}
