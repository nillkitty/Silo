using Silo.Providers.Tree;
using Telefrag.Common;

namespace Silo.Tests.Providers;

/// <summary>
///     The network tree: subnet arithmetic, which blocks a node's children are, and what the nodes say about
///     themselves.
/// </summary>
public class NetworkTreeTests
{
    private static IPSubnet Net(string text)
    {
        Assert.That(SubnetMath.TryParse(text, out var subnet), Is.True, text);
        return subnet;
    }

    private static NetTreeNode Child(NetTreeNode node, string cidr)
    {
        var found = node.Children.FirstOrDefault(c => c.Subnet.Cidr() == cidr);
        Assert.That(found, Is.Not.Null, $"{cidr} under {node.Subnet.Cidr()}");
        return found!;
    }

    [TestCase("10.0.0.0/8",    "10.in-addr.arpa",          true)]
    [TestCase("10.0.0.0/10",   "10.in-addr.arpa",          false)]
    [TestCase("192.0.2.1/32",  "1.2.0.192.in-addr.arpa",   true)]
    [TestCase("0.0.0.0/0",     "in-addr.arpa",             true)]
    [TestCase("2001:db8::/32", "8.b.d.0.1.0.0.2.ip6.arpa", true)]
    [TestCase("2001:db8::/33", "8.b.d.0.1.0.0.2.ip6.arpa", false)]
    public void The_reverse_zone_is_cut_at_an_octet_or_a_nibble(string subnet, string zone, bool exact)
    {
        Assert.That(Net(subnet).ReverseZone(out bool aligned), Is.EqualTo(zone));
        Assert.That(aligned,                                   Is.EqualTo(exact));
    }

    [Test]
    public void A_subnet_knows_its_first_and_last_address_and_its_size()
    {
        var ten = Net("10.0.0.0/8");
        Assert.That(ten.First().ToString(),     Is.EqualTo("10.0.0.0"));
        Assert.That(ten.Last().ToString(),      Is.EqualTo("10.255.255.255"));
        Assert.That(ten.Size().ToString(),      Is.EqualTo("16777216"));
        Assert.That(ten.SubnetMask!.ToString(), Is.EqualTo("255.0.0.0"));
        Assert.That(ten.Wildcard()!.ToString(), Is.EqualTo("0.255.255.255"));

        var docs = Net("2001:db8::/32");
        Assert.That(docs.Last().ToString(), Is.EqualTo("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff"));
        Assert.That(docs.SubnetMask.ToString(), Is.EqualTo("ffff:ffff::"));
        Assert.That(docs.Netmask(),              Is.Null, "the netmask is an IPv4 notion");
    }

    [Test]
    public void Subnets_are_read_from_text_and_addresses()
    {
        Assert.That(Net("10.1.2.3/8").Cidr(),                  Is.EqualTo("10.0.0.0/8"), "host bits are cleared");
        Assert.That(Net("8.8.8.8").Cidr(),                     Is.EqualTo("8.8.8.8/32"), "a bare address is a single address");
        Assert.That(Net("::1").Cidr(),                         Is.EqualTo("::1/128"));
        Assert.That(SubnetMath.TryParse("10.0.0.0/33", out _), Is.False);
        Assert.That(SubnetMath.TryParse("nonsense",    out _), Is.False);

        foreach (string text in new[] { "10.129.64.0/11", "0.0.0.0/0", "2001:db8::/32", "::/0" })
        {
            var subnet = Net(text);
            Assert.That(SubnetMath.TryParse(subnet.ToUri(), out var back), Is.True,            text);
            Assert.That(back.Cidr(), Is.EqualTo(subnet.Cidr()), text);
        }
    }

    [Test]
    public void A_block_covers_what_is_inside_it()
    {
        Assert.That(Net("10.0.0.0/8").Covers(Net("10.129.64.0/11")),     Is.True);
        Assert.That(Net("10.0.0.0/8").Covers(Net("10.0.0.0/8")),         Is.True);
        Assert.That(Net("10.0.0.0/8").StrictlyCovers(Net("10.0.0.0/8")), Is.False);
        Assert.That(Net("10.0.0.0/8").Covers(Net("11.0.0.0/8")),         Is.False);
        Assert.That(Net("10.129.64.0/11").Covers(Net("10.0.0.0/8")),     Is.False);
    }

    [Test]
    public void The_Ipv4_root_has_a_block_per_slash_8_and_the_larger_reserved_blocks_stand_for_themselves()
    {
        var root = NetworkTree.CanonicalIpv4.Root;

        // 256 /8s, less the 16 each that 224.0.0.0/4 (multicast) and 240.0.0.0/4 (reserved) take up, plus those two
        Assert.That(root.Children.Count, Is.EqualTo(256 - 32 + 2));
        Assert.That(root.Children.Select(c => c.Subnet.Cidr()),
                    Does.Contain("224.0.0.0/4").And.Contain("240.0.0.0/4").And.Contain("10.0.0.0/8"));
        Assert.That(root.Children.Select(c => c.Subnet.Cidr()), Does.Not.Contain("225.0.0.0/8"));

        var ordered = root.Children.Select(c => c.Subnet.Network.ToNumber()).ToList();
        Assert.That(ordered, Is.Ordered, "in address order");
    }

    [Test]
    public void A_block_the_size_of_no_step_appears_where_it_starts()
    {
        var root = NetworkTree.CanonicalIpv4.Root;

        Assert.That(Child(Child(root, "100.0.0.0/8"), "100.64.0.0/10").Kind, Is.EqualTo(NetBlockKind.WellKnown));
        Assert.That(Child(root, "100.0.0.0/8").Children.Count, Is.EqualTo(256 - 64 + 1), "the 64 /16s of the shared space are one block");
        Assert.That(Child(root, "172.0.0.0/8").Children.Count, Is.EqualTo(256 - 16 + 1));
        Assert.That(Child(root, "198.0.0.0/8").Children.Select(c => c.Subnet.Cidr()), Does.Contain("198.18.0.0/15"));
    }

    [Test]
    public void The_tree_goes_down_to_single_addresses()
    {
        var node = NetworkTree.CanonicalIpv4.Root;
        foreach (string cidr in new[] { "192.0.0.0/8", "192.168.0.0/16", "192.168.1.0/24", "192.168.1.0/28", "192.168.1.5/32" })
            node = Child(node, cidr);

        Assert.That(node.Kind,                  Is.EqualTo(NetBlockKind.Host));
        Assert.That(node.HasChildren,           Is.False);
        Assert.That(node.Parent!.Subnet.Cidr(), Is.EqualTo("192.168.1.0/28"));
        Assert.That(node.Uri.AbsoluteUri,       Is.EqualTo("ip4://192.168.1.5/32"));
    }

    [Test]
    public void The_Ipv6_root_has_a_block_per_slash_8_with_unique_local_space_as_one_slash_7()
    {
        var root = NetworkTree.CanonicalIpv6.Root;

        Assert.That(root.Subnet.Cidr(), Is.EqualTo("::/0"));
        Assert.That(root.Children.Select(c => c.Subnet.Cidr()),
                    Does.Contain("fc00::/7").And.Contain("ff00::/8").And.Not.Contain("fd00::/8"));
        Assert.That(root.Children.Count,          Is.EqualTo(256 - 2 + 1));
        Assert.That(Child(root, "ff00::/8").Kind, Is.EqualTo(NetBlockKind.WellKnown));
    }

    [Test]
    public void Ipv6_goes_down_a_nibble_at_a_time_below_a_slash_32_and_stops_at_a_slash_64()
    {
        var node = NetworkTree.CanonicalIpv6.Root;
        foreach (string cidr in new[] { "2000::/8", "2001::/16", "2001:d00::/24", "2001:db8::/32", "2001:db8::/36", "2001:db8::/40" })
            node = Child(node, cidr);

        Assert.That(node.Children.Count, Is.EqualTo(16));

        var lan = node;
        foreach (string cidr in new[]
                                {
                                    "2001:db8::/44", "2001:db8::/48", "2001:db8::/52", "2001:db8::/56", "2001:db8::/60",
                                    "2001:db8::/64"
                                })
            lan = Child(lan, cidr);

        Assert.That(lan.HasChildren, Is.False);
    }

    [Test]
    public void Nodes_say_what_they_are()
    {
        var v4 = NetworkTree.CanonicalIpv4.Root;
        Assert.That(v4.Kind,      Is.EqualTo(NetBlockKind.Internet));
        Assert.That(v4.Icon!.Key, Is.EqualTo(NetworkIcons.Internet));

        var ten = Child(v4, "10.0.0.0/8");
        Assert.That(ten.Kind,         Is.EqualTo(NetBlockKind.WellKnown));
        Assert.That(ten.Icon!.Key,    Is.EqualTo(NetworkIcons.WellKnown));
        Assert.That(ten.Name,         Does.StartWith("10.0.0.0/8").And.Contain("Private-Use"));
        Assert.That(ten.Special!.Rfc, Is.EqualTo("RFC 1918"));

        var inside = Child(ten, "10.129.0.0/16");
        Assert.That(inside.Kind,                             Is.EqualTo(NetBlockKind.Subnet));
        Assert.That(inside.Special!.Subnet.Cidr(),           Is.EqualTo("10.0.0.0/8"), "it is inside a private block");
        Assert.That(inside.GetDetails().Select(d => d.Name), Does.Contain("Inside special purpose block"));
    }

    [Test]
    public void The_Explorer_node_belongs_to_the_model()
    {
        var node  = NetworkTree.CanonicalIpv4.Root;
        var shown = node.GetNode();

        Assert.That(shown,             Is.SameAs(node.GetNode()), "made once");
        Assert.That(shown.DataContext, Is.SameAs(node));
        Assert.That(shown.Uri,         Is.EqualTo(node.Uri));
        Assert.That(shown.ChildLoader, Is.Not.Null);
        Assert.That(Child(Child(Child(Child(node, "192.0.0.0/8"), "192.168.0.0/16"), "192.168.1.0/24"), "192.168.1.0/28")
                   .Children.Last()
                   .GetNode()
                   .ChildLoader, Is.Null, "a single address has no children");
    }

    [Test]
    public void A_node_offers_lookups_and_a_root_offers_to_be_removed()
    {
        var root = NetworkTree.CanonicalIpv4.Root.GetCommands().Select(c => c.Id).ToList();
        Assert.That(root, Does.Contain("net.rdap").And.Contain("net.whois").And.Contain("net.goto").And.Contain("net.remove"));

        var ten = Child(NetworkTree.CanonicalIpv4.Root, "10.0.0.0/8").GetCommands().Select(c => c.Id).ToList();
        Assert.That(ten, Does.Contain("net.rdap")
                             .And.Contain("net.zone.ns")
                             .And.Contain("net.rfc")
                             .And.Not.Contain("net.remove")
                             .And.Not.Contain("net.ptr"));
    }

    [Test]
    public void The_tree_is_found_from_a_scheme()
    {
        var tree = NetworkTree.CanonicalIpv4;
        Assert.That(tree.GetRoot(new Uri("ip4://10.0.0.0/8")),    Is.SameAs(NetworkTree.CanonicalIpv4.Root));
        Assert.That(tree.GetRoot(new Uri("ipv6://[::]/0")),       Is.SameAs(NetworkTree.CanonicalIpv6.Root));
        Assert.That(tree.GetRoot(new Uri("https://example.com")), Is.Null);
    }

    [Test]
    public void The_IANA_registry_is_read_with_its_quoted_notes()
    {
        const string csv = "Prefix,Designation,Date,WHOIS,RDAP,Status [1],Note\r\n" +
                           "000/8,IANA - Local Identification,1981-09,,,RESERVED,\"\"\"This network\"\", with a comma\r\nand a second line\"\r\n" +
                           "001/8,APNIC,2010-01,whois.apnic.net,https://rdap.apnic.net/,ALLOCATED,\r\n" +
                           "017/8,Apple Inc.,1992-07,whois.arin.net,https://rdap.arin.net/registry,LEGACY,\r\n";

        var entries = AddressRegistry.ParseRegistry(csv).ToList();

        Assert.That(entries.Select(e => e.Subnet.Cidr()), Is.EqualTo(new[] { "0.0.0.0/8", "1.0.0.0/8", "17.0.0.0/8" }));
        Assert.That(entries[0].Note,                      Does.StartWith("\"This network\", with a comma").And.Contain("second line"));
        Assert.That(entries[1].Rir,                       Is.EqualTo("APNIC"));
        Assert.That(entries[1].Whois,                     Is.EqualTo("whois.apnic.net"));
        Assert.That(entries[2].Status,                    Is.EqualTo("LEGACY"));
        Assert.That(entries[2].Rir,                       Is.Null);
    }

    [Test]
    public void The_IPv6_registry_lines_are_read_by_prefix()
    {
        const string csv = "Prefix,Designation,Date,WHOIS,RDAP,Status [1],Note\r\n" +
                           "2001:0200::/23,APNIC,1999-07-01,whois.apnic.net,https://rdap.apnic.net/,ALLOCATED,\r\n";

        var entry = AddressRegistry.ParseRegistry(csv).Single();

        Assert.That(entry.Subnet.Cidr(), Is.EqualTo("2001:200::/23"));
        Assert.That(entry.Rir,           Is.EqualTo("APNIC"));
    }

    [Test]
    public void The_special_purpose_blocks_are_found_for_what_they_hold()
    {
        Assert.That(AddressRegistry.SpecialFor(Net("10.1.2.0/24"))!.Name,  Is.EqualTo("Private-Use"));
        Assert.That(AddressRegistry.SpecialFor(Net("192.0.0.9/32"))!.Name, Does.Contain("Port Control"), "the most specific block wins");
        Assert.That(AddressRegistry.SpecialFor(Net("8.8.8.8/32")),         Is.Null);
        Assert.That(AddressRegistry.SpecialFor(Net("fe80::1/128"))!.Name,  Is.EqualTo("Link-Local Unicast"));
        Assert.That(AddressRegistry.SpecialFor(Net("10.1.2.3/8")),         Is.Not.Null);

        Assert.That(AddressRegistry.SpecialBlocks.All(b => b.Subnet.SameAs(b.Subnet.Normalized())), Is.True,
                    "every block is written as a network address");
        Assert.That(AddressRegistry.SpecialBlocks.Count, Is.GreaterThan(40));
    }
}