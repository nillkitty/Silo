using System.Buffers.Binary;
using System.Net;
using Silo.Model;
using Silo.Providers.Tree;
using Telefrag.Common;

namespace Silo.Tests.Providers;

/// <summary>The DNS client's reading of answers, the names, and the shape of the domain name tree (nothing here asks a server).</summary>
public class DnsTreeTests
{
    // a message built by hand: header, one question (example.com A), then the answers given
    private static byte[] Message(int flags, params byte[][] answers)
    {
        var bytes = new List<byte>();
        void U16(int v) => bytes.AddRange([(byte)(v >> 8), (byte)v]);

        U16(0x1234);
        U16(flags);
        U16(1);
        U16(answers.Length);
        U16(0);
        U16(0);
        bytes.AddRange([7, .. "example"u8, 3, .. "com"u8, 0]);
        U16(1);
        U16(1);
        foreach (var answer in answers) bytes.AddRange(answer);
        return [.. bytes];
    }

    // an answer owned by the name in the question (a compression pointer to offset 12)
    private static byte[] Record(int type, byte[] data, uint ttl = 300)
    {
        var bytes = new List<byte> { 0xC0, 0x0C, (byte)(type >> 8), (byte)type, 0, 1 };
        bytes.AddRange(BitConverter.GetBytes(ttl).Reverse());
        bytes.AddRange([(byte)(data.Length >> 8), (byte)data.Length]);
        bytes.AddRange(data);
        return [.. bytes];
    }

    [Test]
    public void A_query_asks_one_question_with_recursion_and_the_DNSSEC_flag()
    {
        byte[] q = DnsClient.BuildQuery("Example.com.", DnsType.MX, 0xBEEF);

        Assert.That(BinaryPrimitives.ReadUInt16BigEndian(q),     Is.EqualTo(0xBEEF));
        Assert.That(BinaryPrimitives.ReadUInt16BigEndian(q[2..]), Is.EqualTo(0x0100), "recursion desired");
        Assert.That(BinaryPrimitives.ReadUInt16BigEndian(q[4..]), Is.EqualTo(1), "one question");
        Assert.That(BinaryPrimitives.ReadUInt16BigEndian(q[10..]), Is.EqualTo(1), "one additional record: EDNS0");
        Assert.That(q[12],                                        Is.EqualTo(7));
        Assert.That(System.Text.Encoding.ASCII.GetString(q, 13, 7), Is.EqualTo("Example"));

        // the OPT record at the end: the root, type 41, a UDP size, and the DO bit
        Assert.That(BinaryPrimitives.ReadUInt16BigEndian(q[^10..]), Is.EqualTo(41));
        Assert.That(BinaryPrimitives.ReadUInt16BigEndian(q[^4..]),  Is.EqualTo(0x8000), "DNSSEC OK");
    }

    [Test]
    public void An_answer_with_compressed_names_is_read()
    {
        byte[] mx = [0, 10, 4, .. "mail"u8, 0xC0, 0x0C];
        var answer = DnsClient.ParseResponse(Message(0x81A0, Record(1, [93, 184, 216, 34]), Record(15, mx)));

        Assert.That(answer.ResponseCode, Is.EqualTo(0));
        Assert.That(answer.Authenticated, Is.True, "the AD flag");
        Assert.That(answer.Answers.Select(r => (r.Name, r.TypeName, r.Data)),
                    Is.EqualTo(new[] { ("example.com.", "A", "93.184.216.34"), ("example.com.", "MX", "10 mail.example.com.") }));
        Assert.That(answer.Answers[0].Ttl, Is.EqualTo(300));
    }

    [Test]
    public void Text_and_SOA_and_service_records_are_written_as_in_a_zone_file()
    {
        byte[] txt = [3, .. "v=a"u8, 5, .. "b\"c d"u8];
        byte[] soa = [2, .. "ns"u8, 0xC0, 0x0C, 4, .. "host"u8, 0xC0, 0x0C, 0, 0, 0, 7, 0, 0, 0x1C, 0x20, 0, 0, 0x0E, 0x10, 0, 0x12, 0x75, 0, 0, 0, 1, 0x2C];
        byte[] srv = [0, 1, 0, 2, 0x13, 0xC4, 3, .. "sip"u8, 0xC0, 0x0C];
        var answer = DnsClient.ParseResponse(Message(0x8180, Record(16, txt), Record(6, soa), Record(33, srv)));

        Assert.That(answer.Answers[0].Data, Is.EqualTo("\"v=a\" \"b\\\"c d\""));
        Assert.That(answer.Answers[1].Data, Is.EqualTo("ns.example.com. host.example.com. 7 7200 3600 1209600 300"));
        Assert.That(answer.Answers[2].Data, Is.EqualTo("1 2 5060 sip.example.com."));
    }

    [Test]
    public void A_missing_name_is_an_answer_without_records()
    {
        var answer = DnsClient.ParseResponse(Message(0x8183));

        Assert.That(answer.ResponseCode, Is.EqualTo(3));
        Assert.That(answer.Status,       Is.EqualTo("NXDOMAIN"));
        Assert.That(answer.Answers,      Is.Empty);
    }

    [Test]
    public void An_answer_that_is_cut_short_or_loops_is_refused()
    {
        Assert.Throws<DnsException>(() => DnsClient.ParseResponse([1, 2, 3]));

        byte[] message = Message(0x8180, Record(1, [1, 2, 3, 4]));
        Assert.Throws<DnsException>(() => DnsClient.ParseResponse(message[..^3]));

        // a name that points at itself
        byte[] loop = Message(0x8180, [0xC0, 0x1E, 0, 1, 0, 1, 0, 0, 0, 1, 0, 0]);
        Assert.Throws<DnsException>(() => DnsClient.ParseResponse([.. loop[..30], 0xC0, 0x1E, .. loop[32..]]));
    }

    [TestCase("Example.COM", "example.com.")]
    [TestCase("example.com.", "example.com.")]
    [TestCase(".", ".")]
    [TestCase("", ".")]
    [TestCase("bücher.example", "xn--bcher-kva.example.")]
    [TestCase("_sip._tcp.example.com", "_sip._tcp.example.com.")]
    public void Names_are_written_one_way(string name, string normalized)
    {
        Assert.That(DnsNames.Normalize(name), Is.EqualTo(normalized));
    }

    [TestCase("a..b")]
    [TestCase("bad name.com")]
    [TestCase("this-label-is-much-too-long-for-the-domain-name-system-which-allows-sixty-three-characters.com")]
    public void Text_that_is_no_name_is_refused(string name)
    {
        Assert.That(DnsNames.TryNormalize(name, out _), Is.False);
        Assert.Throws<ArgumentException>(() => DnsNames.Normalize(name));
    }

    [Test]
    public void The_way_to_a_name_goes_from_its_top_level_domain_down()
    {
        Assert.That(DnsNames.PathTo("dc.int.durms.net."), Is.EqualTo(new[] { "net.", "durms.net.", "int.durms.net.", "dc.int.durms.net." }));
        Assert.That(DnsNames.PathTo("."),                 Is.Empty);
        Assert.That(DnsNames.ParentOf("durms.net."),      Is.EqualTo("net."));
        Assert.That(DnsNames.ParentOf("net."),            Is.EqualTo("."));
        Assert.That(DnsNames.ParentOf("."),               Is.Null);
        Assert.That(DnsNames.LabelOf("www.example.com."), Is.EqualTo("www"));
    }

    [TestCase("10.in-addr.arpa.",           "10.0.0.0/8")]
    [TestCase("64.129.10.in-addr.arpa.",     "10.129.64.0/24")]
    [TestCase("4.3.2.1.in-addr.arpa.",       "1.2.3.4/32")]
    [TestCase("in-addr.arpa.",               "0.0.0.0/0")]
    [TestCase("8.b.d.0.1.0.0.2.ip6.arpa.",   "2001:db8::/32")]
    [TestCase("ip6.arpa.",                   "::/0")]
    public void A_reverse_zone_is_the_block_it_holds_the_names_of(string zone, string cidr)
    {
        Assert.That(DnsNames.TryReverseZone(zone, out var block), Is.True);
        Assert.That(block.Cidr(), Is.EqualTo(cidr));

        // and back: the zone of the block is the name it came from
        Assert.That(block.ReverseZone(out bool exact) + ".", Is.EqualTo(zone));
        Assert.That(exact, Is.True);
    }

    [TestCase("example.com.")]
    [TestCase("300.in-addr.arpa.")]
    [TestCase("1.2.3.4.5.in-addr.arpa.")]
    [TestCase("gg.ip6.arpa.")]
    public void Other_names_are_not_reverse_zones(string name)
    {
        Assert.That(DnsNames.TryReverseZone(name, out _), Is.False);
    }

    [Test]
    public void The_root_is_the_top_of_the_tree_and_a_top_level_name_goes_under_it_without_asking_anyone()
    {
        var tree = new DnsTree();
        Assert.That(tree.Root.Kind, Is.EqualTo(DnsNodeKind.Root));
        Assert.That(tree.Root.Uri.AbsoluteUri, Is.EqualTo("dns:."));
        Assert.That(tree.GetRoot(new Uri("ns:example.com.")), Is.SameAs(tree.Root));
        Assert.That(tree.GetRoot(new Uri("ip4://10.0.0.0/8")), Is.Null);

        var net = tree.Root.GetOrAddChildAsync("net.").GetAwaiter().GetResult();
        Assert.That(net.Kind,         Is.EqualTo(DnsNodeKind.Tld));
        Assert.That(net.Parent,       Is.SameAs(tree.Root));
        Assert.That(net.Icon!.Key,    Is.EqualTo(DnsIcons.Tld));
        Assert.That(net.TypeName,     Is.EqualTo("Name: TLD Zone"));
        Assert.That(tree.Find("net."), Is.SameAs(net));
        Assert.That(tree.Root.GetOrAddChildAsync("net.").GetAwaiter().GetResult(), Is.SameAs(net), "once");
    }

    [Test]
    public void The_top_level_domains_are_read_from_IANAs_list()
    {
        const string list = "# Version 2024102701, Last Updated Sun Oct 27 07:07:01 2024 UTC\nAAA\nAARP\nCOM\nXN--P1AI\ncom\n";
        Assert.That(DnsTree.ParseTlds(list), Is.EqualTo(new[] { "aaa.", "aarp.", "com.", "xn--p1ai." }));
    }

    [Test]
    public void Records_are_put_in_groups_the_way_the_tree_lists_them()
    {
        var owner = DnsTree.Canonical.Root;
        var records = new[]
                      {
                          new DnsRecord("example.com.", DnsType.RRSIG, 1, 300, "A 13 2 300 20250101000000 20241201000000 1234 example.com. AAAA"),
                          new DnsRecord("example.com.", DnsType.TXT, 1, 300, "\"hello\""),
                          new DnsRecord("example.com.", DnsType.AAAA, 1, 300, "2001:db8::1"),
                          new DnsRecord("example.com.", DnsType.A, 1, 300, "192.0.2.1"),
                          new DnsRecord("example.com.", DnsType.MX, 1, 300, "10 mail.example.com."),
                          new DnsRecord("example.com.", DnsType.DNSKEY, 1, 3600, "257 3 8 AQI=")
                      };

        var groups = DnsRecordGroup.Build(owner, records);

        Assert.That(groups.Select(g => g.Kind), Is.EqualTo(new[] { DnsGroupKind.Host, DnsGroupKind.Mail, DnsGroupKind.Text, DnsGroupKind.PublicKey, DnsGroupKind.Signature }));
        Assert.That(groups[0].Title, Is.EqualTo("Host (A, AAAA)"));
        Assert.That(groups[0].Records.Select(r => r.Data), Is.EqualTo(new[] { "192.0.2.1", "2001:db8::1" }));
        Assert.That(groups[0].Icon!.Key, Is.EqualTo(DnsIcons.Host));
        Assert.That(groups[1].Icon!.Key, Is.EqualTo(DnsIcons.Mail));
        Assert.That(groups[3].Icon!.Key, Is.EqualTo(DnsIcons.PublicKey));
    }

    [Test]
    public void A_record_says_what_its_fields_mean_and_where_it_leads()
    {
        var owner = DnsTree.Canonical.Root;
        var group = DnsRecordGroup.Build(owner, [new DnsRecord("example.com.", DnsType.MX, 1, 3600, "10 mail.example.com.")]).Single();
        var leaf  = (DnsRecordLeaf)group.GetNode().ChildLoader!(CancellationToken.None).GetAwaiter().GetResult().Single().DataContext;

        var details = leaf.GetDetails().ToDictionary(d => d.Name, d => d.Value);
        Assert.That(details["Preference"], Is.EqualTo("10"));
        Assert.That(details["Exchange"],   Is.EqualTo("mail.example.com."));
        Assert.That(details["TTL"],        Does.StartWith("3600 s").And.Contain("1 h"));
        Assert.That(details["Leads to"],   Is.EqualTo("mail.example.com."));
        Assert.That(leaf.GetCommands().Select(c => c.Id), Does.Contain("dns.record.goto").And.Contain("dns.record.copy"));

        var host = DnsRecordGroup.Build(owner, [new DnsRecord("example.com.", DnsType.A, 1, 60, "192.0.2.1")]).Single();
        var hostLeaf = (DnsRecordLeaf)host.GetNode().ChildLoader!(CancellationToken.None).GetAwaiter().GetResult().Single().DataContext;
        Assert.That(hostLeaf.GetCommands().Select(c => c.Id), Does.Contain("dns.record.ip"), "an address can be opened in the address tree");
    }

    [Test]
    public void A_DNSKEY_has_the_key_tag_RFC_4034_describes()
    {
        // flags 0x0101, protocol 3, algorithm 8, key 01 02: the sum of the 16-bit words of 01 01 03 08 01 02
        var key = new DnsRecord("example.com.", DnsType.DNSKEY, 1, 3600, "257 3 8 AQI=");

        Assert.That(DnsRecordLeaf.KeyTag(key), Is.EqualTo(0x050B));
        Assert.That(DnsRecordLeaf.AlgorithmName(13), Is.EqualTo("13 ECDSAP256SHA256"));
        Assert.That(DnsRecordLeaf.Span(5400),        Is.EqualTo("1 h 30 min"));
    }

    [Test]
    public void The_commands_of_a_name_depend_on_what_it_is()
    {
        var tree = new DnsTree();
        var root = tree.Root.GetCommands().Select(c => c.Id).ToList();
        Assert.That(root, Does.Contain("dns.goto").And.Contain("dns.remove").And.Not.Contain("dns.sub").And.Not.Contain("dns.rdap"));

        var com = tree.Root.GetOrAddChildAsync("com.").GetAwaiter().GetResult().GetCommands().Select(c => c.Id).ToList();
        Assert.That(com, Does.Contain("dns.goto").And.Contain("dns.sub").And.Contain("dns.rdap").And.Contain("dns.iana").And.Contain("dns.copy").And.Not.Contain("dns.remove"));
    }

    [Test]
    public void The_trees_are_kept_in_the_Silo_as_preferences()
    {
        var keys = PreferenceDefinition.For(typeof(OpenSilo)).Select(d => d.Key).ToList();

        foreach (var space in Enum.GetValues<AddressSpace>())
            Assert.That(keys, Does.Contain(AddressSpaces.KeyOf(space)), space.ToString());

        Assert.That(AddressSpaces.RootOf(AddressSpace.Ipv4), Is.SameAs(NetworkTree.CanonicalIpv4.Root));
        Assert.That(AddressSpaces.RootOf(AddressSpace.Ipv6), Is.SameAs(NetworkTree.CanonicalIpv6.Root));
        Assert.That(AddressSpaces.RootOf(AddressSpace.Dns),  Is.SameAs(DnsTree.Canonical.Root));
    }
}
