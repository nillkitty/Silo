using System.Net;
using System.Net.NetworkInformation;
using Silo.Connectors.Virtual;
using Silo.Providers.Data.Source;
using Silo.Providers.Network;

namespace Silo.Tests.Connectors;

public class NetInfoTests
{
    private sealed class FakeReader : INetworkInfoReader
    {
        public IReadOnlyList<NetInterface> GetInterfaces()
        {
            var a = new NetAddress("Ethernet", IPAddress.Parse("10.0.0.5"), 24, "Dhcp", "IPv4");
            return [new NetInterface("Ethernet", "Intel", "{1}", 4, NetworkInterfaceType.Ethernet, OperationalStatus.Up, "AA-BB-CC-DD-EE-FF",
                                     1_000_000_000, 1500, true, true, true, "corp.local", [IPAddress.Parse("10.0.0.1")],
                                     [IPAddress.Parse("10.0.0.1")], [a])];
        }

        public IReadOnlyList<NetRoute> GetRoutes()
        {
            return
            [
                new NetRoute(4, "Ethernet", IPAddress.Parse("10.0.0.0"), 24, IPAddress.Any, 281, "Local", "IPv4", 5),
                new NetRoute(4, "Ethernet", IPAddress.Any, 0, IPAddress.Parse("10.0.0.1"), 25, "Network Management", "IPv4", 9),
                new NetRoute(1, "Loopback", IPAddress.Parse("127.0.0.0"), 8, IPAddress.Any, 331, "Local", "IPv4", 9)
            ];
        }

        public IReadOnlyList<AdapterConfig> GetAdapters()
        {
            return [new AdapterConfig("Ethernet", "Intel", "{1}", true, "Up", "Ethernet", "AA-BB-CC-DD-EE-FF", "AA-BB-CC-DD-EE-FF", null, null, null, 1_000_000_000)];
        }

        public IReadOnlyList<WifiProfile> GetWifiProfiles()
        {
            return [new WifiProfile("Wi-Fi", "Home", "HomeNet", "WPA2PSK", "AES", "auto", false, true)];
        }

        public HostNetworkConfig GetHostConfig()
        {
            return new HostNetworkConfig("box", "corp.local", "Hybrid", null, false, [IPAddress.Parse("10.0.0.1")], ["corp.local"]);
        }

        public IReadOnlyList<PortOwner> GetSockets()
        {
            var zed   = new LocalProcessInfo(20, "zed", null, null, null, null);
            var alpha = new LocalProcessInfo(10, "alpha", @"C:\alpha.exe", @"X\u", null, null);
            return
            [
                new PortOwner(PortProtocol.Tcp, new IPEndPoint(IPAddress.Loopback, 80), new IPEndPoint(IPAddress.Parse("9.9.9.9"), 443), TcpState.Established, 20, zed),
                new PortOwner(PortProtocol.Tcp, new IPEndPoint(IPAddress.Any, 22), null, TcpState.Listen, 10, alpha),
                new PortOwner(PortProtocol.Udp, new IPEndPoint(IPAddress.Any, 53), null, null, 10, alpha)
            ];
        }
    }

    private static NetInfoModel Model => new(new FakeReader());

    private static List<IReadOnlyList<string>> Rows(NetTable t)
    {
        return t.GetRows().ToList();
    }

    [Test]
    public void Tcp_by_application_orders_by_process_and_leaves_out_udp()
    {
        var rows = Rows(Model.Sockets(null, true, "application"));

        Assert.That(rows.Select(r => r[0]), Is.EqualTo(new[] { "alpha", "zed" }));
        Assert.That(rows[0][ModelIndex("Path")], Is.EqualTo(@"C:\alpha.exe"));
    }

    [Test]
    public void Tcp_by_destination_puts_listeners_last()
    {
        var rows = Rows(Model.Sockets(null, true, "destination"));

        Assert.That(rows.Select(r => r[ModelIndex("State")]), Is.EqualTo(new[] { "Established", "Listen" }));
    }

    [Test]
    public void Udp_sessions_have_no_state_or_remote()
    {
        var rows = Rows(Model.Sockets(null, false, "port"));

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0][ModelIndex("State")],          Is.Empty);
        Assert.That(rows[0][ModelIndex("Remote Address")], Is.Empty);
    }

    private static int ModelIndex(string column)
    {
        return Model.Sockets(null, true, "port").ColumnNames.ToList().IndexOf(column);
    }

    [Test]
    public void Routes_by_destination_put_the_default_route_first_and_show_on_link()
    {
        var rows = Rows(Model.Routes(null, "destination"));

        Assert.That(rows.Select(r => r[0]), Is.EqualTo(new[] { "0.0.0.0/0", "10.0.0.0/24", "127.0.0.0/8" }));
        Assert.That(rows[1][1],             Is.EqualTo("On-link"));
    }

    [Test]
    public void Routes_by_interface_group_and_a_single_interface_filters()
    {
        Assert.That(Rows(Model.Routes(null, "interface")).Select(r => r[2]), Is.EqualTo(new[] { "Ethernet", "Ethernet", "Loopback" }));
        Assert.That(Rows(Model.Routes(null, "destination", "Loopback")),     Has.Count.EqualTo(1));
    }

    [Test]
    public void An_interface_carries_its_dns_settings()
    {
        var table = Model.Interfaces();
        var row   = Rows(table).Single();

        Assert.That(row[table.ColumnNames.ToList().IndexOf("DNS Servers")], Is.EqualTo("10.0.0.1"));
        Assert.That(row[table.ColumnNames.ToList().IndexOf("DNS Suffix")],  Is.EqualTo("corp.local"));
        Assert.That(row[table.ColumnNames.ToList().IndexOf("Speed")],       Is.EqualTo("1 Gbps"));
    }

    [Test]
    public void Adapters_show_the_mac_override_and_host_config_the_dns_servers()
    {
        Assert.That(Rows(Model.Adapters()).Single(), Does.Contain("AA-BB-CC-DD-EE-FF"));
        Assert.That(Rows(Model.HostConfig()).Single(r => r[0] == "DNS Servers")[1], Is.EqualTo("10.0.0.1"));
    }

    [Test]
    public async Task The_tree_has_the_four_branches_and_one_node_per_interface()
    {
        var root = Model.BuildTree();

        Assert.That(root.Children.Select(c => c.Text), Is.EqualTo(new[] { "Network Configuration", "Network State", "TCP Connections", "UDP Sessions" }));

        var interfaces = root.Children[1].Children.Single(c => c.Text == "Interfaces");
        await interfaces.LoadChildrenAsync();
        var eth = interfaces.Children.Single();
        Assert.That(eth.Children.Select(c => c.Text), Is.EqualTo(new[] { "Interface", "Addresses", "Routes" }));
        Assert.That(eth.DataContext,                  Is.InstanceOf<IFlatDataProvider>());
    }

    [Test]
    public void A_wifi_profile_is_read_from_its_xml_without_the_key()
    {
        const string xml = """
                           <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1"><name>Home</name>
                           <SSIDConfig><SSID><name>HomeNet</name></SSID><nonBroadcast>true</nonBroadcast></SSIDConfig>
                           <connectionMode>auto</connectionMode>
                           <MSM><security><authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption></authEncryption></security></MSM>
                           </WLANProfile>
                           """;

        var p = SystemNetworkInfoReader.ParseProfile("Wi-Fi", "Home", xml, true);

        Assert.That((p.Ssid, p.Authentication, p.Encryption, p.ConnectionMode, p.Hidden), Is.EqualTo(("HomeNet", "WPA2PSK", "AES", "auto", true)));
    }

    [Test]
    public void A_mac_override_is_normalised()
    {
        Assert.That(SystemNetworkInfoReader.NormalizeMac("aabbccddeeff"), Is.EqualTo("AA-BB-CC-DD-EE-FF"));
        Assert.That(SystemNetworkInfoReader.NormalizeMac("zz"),           Is.Null);
        Assert.That(SystemNetworkInfoReader.NormalizeMac(null),           Is.Null);
    }

    [Test]
    [Platform("Win")]
    public void The_system_reader_reads_this_machine()
    {
        var reader = new SystemNetworkInfoReader();

        Assert.That(reader.GetInterfaces(), Is.Not.Empty);
        Assert.That(reader.GetRoutes(),     Is.Not.Empty);
        Assert.That(reader.GetAdapters(),   Is.Not.Empty);
        Assert.That(reader.GetSockets(),    Is.Not.Empty);
        Assert.DoesNotThrow(() => reader.GetWifiProfiles());
        Assert.That(reader.GetHostConfig().HostName, Is.Not.Empty);
    }
}
