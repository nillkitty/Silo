using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using Silo.Connectors.File;
using Silo.ExternalTools;
using Silo.Providers.Tree;
using Run = Silo.ExternalTools.ExternalToolRun;

namespace Silo.Tests.ExternalTools;

public class ExternalToolTests
{
    private static readonly Type[] TargetTypes = [typeof(IPAddress), typeof(DomainName), typeof(FileInfo)];

    private static IReadOnlyList<ExternalTool> ToolsOf(Type type) => Silo.ExternalTools.ExternalTools.All.Where(t => t.TargetType == type).ToList();

    private static ExternalTool Tool<T>(string id) => ToolsOf(typeof(T)).Single(t => t.Id == id);

    [Test]
    public void The_built_in_tools_are_registered_for_their_target_types()
    {
        Assert.That(ToolsOf(typeof(IPAddress)).Select(t => t.Id), Is.SupersetOf(new[]
                                                                                 {
                                                                                     "ping", "nbtstat", "tracert", "arp", "nslookup.server", "nslookup.target", "ssh",
                                                                                     "geoip", "whois", "arin", "browse", "rdp", "nmap", "sessions", "firewall.log",
                                                                                     "security.log"
                                                                                 }));
        Assert.That(ToolsOf(typeof(DomainName)).Select(t => t.Id), Is.SupersetOf(new[]
                                                                                  {
                                                                                      "whois", "archive", "browse", "ping", "tracert", "nslookup.target", "nslookup.server",
                                                                                      "ssh", "rdp", "nmap", "security.log", "adsi", "smb"
                                                                                  }));
        Assert.That(ToolsOf(typeof(FileInfo)).Select(t => t.Id), Is.EquivalentTo(new[] { "hash.md5", "hash.crc32", "hash.sha1", "hash.sha256", "hash.sha512" }));
    }

    [Test]
    public void No_two_tools_of_a_target_type_share_an_icon_and_none_shares_its_sections()
    {
        string[] sections = [ExternalToolSection.Terminal.IconKey, ExternalToolSection.Browser.IconKey, ExternalToolSection.Other.IconKey, ExternalToolSection.CopyHash.IconKey];
        Assert.That(sections, Is.Unique);

        foreach (var type in TargetTypes)
        {
            var icons = ToolsOf(type).Select(t => t.IconKey).ToList();
            Assert.That(icons, Is.Unique, type.Name);
            Assert.That(icons.Intersect(sections), Is.Empty, type.Name);
        }
    }

    [Test]
    public void The_same_tool_keeps_its_icon_for_every_target_type()
    {
        var byId = Silo.ExternalTools.ExternalTools.All.Where(t => t.TargetType != typeof(FileInfo) && t.Id != "whois").GroupBy(t => t.Id);
        foreach (var group in byId)
            Assert.That(group.Select(t => t.IconKey).Distinct().Count(), Is.EqualTo(1), group.Key);
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void Every_icon_is_in_the_apps_icon_set()
    {
        // the dictionaries App.xaml merges, read without making an Application (one made on a test thread would own the
        // dispatcher the other tests' work is sent to); the Application type registers the pack:// scheme they are read through
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
        var icons = new ResourceDictionary();
        foreach (string dictionary in new[] { "Paths", "NavIcons", "DnsIcons", "ExternalToolIcons" })
            icons.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Silo;component/Resources/{dictionary}.xaml") });

        var keys = Silo.ExternalTools.ExternalTools.All.Select(t => t.IconKey)
                       .Concat([ExternalToolSection.Terminal.IconKey, ExternalToolSection.Browser.IconKey, ExternalToolSection.Other.IconKey, ExternalToolSection.CopyHash.IconKey]);
        Assert.That(keys, Has.All.Matches<string>(k => icons[k] is System.Windows.Media.Geometry));
    }

    [Test]
    public void A_tool_says_what_it_runs_for_a_target()
    {
        var address = IPAddress.Parse("8.8.8.8");

        Assert.That(Tool<IPAddress>("ping").Run(address), Is.EqualTo(new Run.Terminal("ping 8.8.8.8", "ping 8.8.8.8")));
        Assert.That(Tool<IPAddress>("nslookup.server").Run(address), Is.EqualTo(new Run.Terminal("nslookup @8.8.8.8", "nslookup - 8.8.8.8")));
        Assert.That(Tool<IPAddress>("arin").Run(address), Is.EqualTo(new Run.Browser(new Uri("https://search.arin.net/rdap/?query=8.8.8.8"))));
        Assert.That(Tool<IPAddress>("rdp").Run(IPAddress.Parse("fe80::1%12")), Is.EqualTo(new Run.Program("mstsc.exe", "/v:[fe80::1]")));
        Assert.That(Tool<IPAddress>("security.log").Run(address), Is.InstanceOf<Run.PowerShell>().With.Property("Elevated").True);

        var name = DomainName.From("www.example.com.")!;
        Assert.That(Tool<DomainName>("archive").Run(name), Is.EqualTo(new Run.Browser(new Uri("https://web.archive.org/web/*/www.example.com"))));
        Assert.That(Tool<DomainName>("smb").Run(name), Is.EqualTo(new Run.Terminal("shares www.example.com", @"net view \\www.example.com /all")));
    }

    [Test]
    public void Tools_that_do_not_fit_a_target_are_not_offered()
    {
        var tools = Silo.ExternalTools.ExternalTools.For(IPAddress.Parse("fe80::1")).Select(t => t.Id).ToList();

        Assert.That(tools, Does.Not.Contain("nbtstat"), "NetBIOS is IPv4 only");
        Assert.That(tools, Does.Not.Contain("geoip"),   "a link-local address has no location");
        Assert.That(Silo.ExternalTools.ExternalTools.For(IPAddress.Parse("192.168.1.1")).Select(t => t.Id), Does.Not.Contain("whois"));
        Assert.That(Silo.ExternalTools.ExternalTools.For(IPAddress.Parse("1.1.1.1")).Select(t => t.Id), Does.Contain("whois").And.Contain("nbtstat"));
        Assert.That(Silo.ExternalTools.ExternalTools.For(new FileInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))), Is.Empty,
                    "no hashes of a file that is not there");
    }

    [Test]
    public void Domain_names_are_only_what_can_go_on_a_command_line()
    {
        Assert.That(DomainName.From("WWW.Example.COM.")?.Value, Is.EqualTo("www.example.com"));
        Assert.That(DomainName.From("_ldap._tcp.dc"), Is.Not.Null);
        Assert.That(DomainName.From("host & calc"),   Is.Null);
        Assert.That(DomainName.From("-bad.com"),      Is.Null);
        Assert.That(DomainName.From("a..b"),          Is.Null);
        Assert.That(DomainName.From("10.0.0.1"),      Is.Null);
        Assert.That(DomainName.From("."),             Is.Null);
    }

    [Test]
    public void Nodes_stand_for_the_targets_in_them()
    {
        string path = Path.GetTempFileName();
        try
        {
            Assert.That(Silo.ExternalTools.ExternalTools.TargetsOf(FileConnection.FromPath(new FileConnector(), path)).Single(), Is.InstanceOf<FileInfo>().With.Property("FullName").EqualTo(path));
            Assert.That(Silo.ExternalTools.ExternalTools.TargetsOf(FileConnection.FromPath(new FileConnector(), Path.GetTempPath())), Is.Empty, "a folder has no hash");
        }
        finally
        {
            File.Delete(path);
        }

        Assert.That(Silo.ExternalTools.ExternalTools.TargetsOf(IPAddress.Loopback), Is.EqualTo(new object[] { IPAddress.Loopback }));
        Assert.That(BuiltInExternalTools.RecordTarget(new DnsRecord("example.com.", DnsType.A, 1, 60, "93.184.216.34")), Is.EqualTo(IPAddress.Parse("93.184.216.34")));
        Assert.That(BuiltInExternalTools.RecordTarget(new DnsRecord("example.com.", DnsType.MX, 1, 60, "10 mail.example.com.")), Is.EqualTo(DomainName.From("mail.example.com")));
        Assert.That(BuiltInExternalTools.RecordTarget(new DnsRecord("example.com.", DnsType.TXT, 1, 60, "v=spf1 -all")), Is.Null);
    }

    [Test]
    public void The_menu_has_the_featured_tools_then_a_submenu_per_section()
    {
        var commands = ExternalToolActions.CommandsFor(IPAddress.Parse("8.8.8.8")).ToList();

        Assert.That(commands[0].Header, Is.EqualTo("_Ping in Terminal"));
        Assert.That(commands.Skip(1).Select(c => c.Header), Is.EqualTo(new[] { "In _Terminal", "In _Browser", "_Other Tools" }));
        Assert.That(commands[1].Children!.Select(c => c.Id), Does.Contain("external.ipaddress.ping"));
    }

    [Test]
    public void A_tool_registered_later_replaces_the_built_in_one_of_its_id()
    {
        var builtIn = Tool<DomainName>("archive");
        try
        {
            Silo.ExternalTools.ExternalTools.Register<DomainName>("archive", "_archive.today", builtIn.IconKey, ExternalToolSection.Browser, "archive.today",
                                                                 n => new Run.Browser(new Uri($"https://archive.today/{n}")));
            Assert.That(Tool<DomainName>("archive").Header, Is.EqualTo("_archive.today"));
        }
        finally
        {
            Silo.ExternalTools.ExternalTools.Register(builtIn);
        }
    }

    [Test]
    public async Task Hashes_are_the_standard_ones()
    {
        byte[] data = Encoding.ASCII.GetBytes("123456789");

        Assert.That(await FileHashes.ComputeAsync(new MemoryStream(data), FileHashKind.Crc32), Is.EqualTo("cbf43926"));
        Assert.That(await FileHashes.ComputeAsync(new MemoryStream(data), FileHashKind.Md5),   Is.EqualTo("25f9e794323b453885f5181f1b624d0b"));
        Assert.That(await FileHashes.ComputeAsync(new MemoryStream(data), FileHashKind.Sha256),
                    Is.EqualTo("15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225"));
    }
}
