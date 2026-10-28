using Telefrag.Common;
using Silo.Providers.Tree;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Silo.DbModel.Meta;
using Silo.Providers.Data;
using Silo.Providers.Definitions;

namespace Silo.Tests.Definitions;

[TestFixture]
public class SyntaxParserTests
{
    /// <summary>SecurityIdentifier is a Windows-only type: on other systems (a build agent on Linux) its parser cannot run.</summary>
    private static bool Unavailable(string key)
    {
        return key == "securityidentifier" && !OperatingSystem.IsWindows();
    }

    private static object? Ok(string key, string text)
    {
        var p = SyntaxParsers.Find(key)!;
        Assert.That(p.TryParse(text, out object? value, out string? error), Is.True, $"{key}: '{text}' -> {error}");
        return value;
    }

    private static void Bad(string key, string? text)
    {
        var p = SyntaxParsers.Find(key)!;
        Assert.That(p.TryParse(text, out _, out string? error), Is.False, $"{key}: '{text}' should be invalid");
        Assert.That(string.IsNullOrEmpty(error),                Is.False, $"{key}: an invalid value needs a message");
    }

    private static string Canon(string key, string text)
    {
        var p = SyntaxParsers.Find(key)!;
        return p.Format(Ok(key, text)!);
    }

    [Test]
    public void Every_parser_has_a_unique_key_a_description_and_a_valid_example()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in SyntaxParsers.All)
        {
            Assert.That(keys.Add(p.Key), Is.True, p.Key);
            if (Unavailable(p.Key)) continue;
            Assert.That(p.Key,                                               Is.EqualTo(p.Key.ToLowerInvariant()));
            Assert.That(string.IsNullOrWhiteSpace(p.Description),            Is.False, p.Key);
            Assert.That(p.TryParse(p.Example, out object? v, out string? e), Is.True,  $"{p.Key} example: {e}");
            Assert.That(p.ValueType.IsInstanceOfType(v),                     Is.True,  p.Key);
            Assert.That(p.TryParse(p.Format(v!), out _, out _),              Is.True,  $"{p.Key} canonical form must read back");
        }
    }

    [Test]
    public void Timestamp()
    {
        Assert.That(((DateTimeOffset)Ok("timestamp", "2026-10-16T14:30:00Z")!).UtcDateTime,
                    Is.EqualTo(new DateTime(2026, 10, 16, 14, 30, 0, DateTimeKind.Utc)));
        Assert.That(Canon("timestamp", "2026-10-16T14:30:00Z"),          Is.EqualTo("2026-10-16T14:30:00Z"));
        Assert.That(Canon("timestamp", "2026-10-16T14:30:00.5+02:00"),   Is.EqualTo("2026-10-16T14:30:00.5+02:00"));
        Assert.That(Canon("timestamp", "2026-10-16 14:30"),              Is.EqualTo("2026-10-16T14:30:00Z"));
        Assert.That(Canon("timestamp", "2026-10-16"),                    Is.EqualTo("2026-10-16T00:00:00Z"));
        Assert.That(Canon("timestamp", "Thu, 15 Oct 2026 10:00:00 GMT"), Is.EqualTo("2026-10-15T10:00:00Z"));
        Bad("timestamp", "yesterday");
        Bad("timestamp", "Oct 5");
        Bad("timestamp", "2026-13-40");
        Bad("timestamp", "");
        Bad("timestamp", null);
    }

    [Test]
    public void Duration()
    {
        Assert.That(Ok("duration", "PT1H30M"),    Is.EqualTo(TimeSpan.FromMinutes(90)));
        Assert.That(Ok("duration", "P1DT2H"),     Is.EqualTo(TimeSpan.FromHours(26)));
        Assert.That(Ok("duration", "P2W"),        Is.EqualTo(TimeSpan.FromDays(14)));
        Assert.That(Ok("duration", "PT0.5S"),     Is.EqualTo(TimeSpan.FromMilliseconds(500)));
        Assert.That(Ok("duration", "-PT1M"),      Is.EqualTo(TimeSpan.FromMinutes(-1)));
        Assert.That(Ok("duration", "1:30:00"),    Is.EqualTo(TimeSpan.FromMinutes(90)));
        Assert.That(Ok("duration", "1.02:03:04"), Is.EqualTo(new TimeSpan(1, 2, 3, 4)));
        Assert.That(Canon("duration", "PT1H30M"), Is.EqualTo("01:30:00"));
        Bad("duration", "P");
        Bad("duration", "PT");
        Bad("duration", "P1DT");
        Bad("duration", "P1Y");
        Bad("duration", "P1M");
        Bad("duration", "5");
        Bad("duration", "soon");
        Bad("duration", "");
    }

    [Test]
    public void Guid_regex_wildcard()
    {
        Assert.That(Canon("guid", "{3F2504E0-4F89-11D3-9A0C-0305E82C3301}"), Is.EqualTo("3f2504e0-4f89-11d3-9a0c-0305e82c3301"));
        Bad("guid", "3f2504e0");

        Assert.That(((Regex)Ok("regex", @"^\d+$")!).IsMatch("123"), Is.True);
        Bad("regex", "(unclosed");
        Bad("regex", "[");

        var w = (Wildcard)Ok("wildcard", "*.LOG")!;
        Assert.That(w.IsMatch("a.log"),                                           Is.True);
        Assert.That(w.IsMatch("a.logx"),                                          Is.False);
        Assert.That(((Wildcard)Ok("wildcard", "file?.t*")!).IsMatch("file1.txt"), Is.True);
        Assert.That(((Wildcard)Ok("wildcard", "a.b")!).IsMatch("axb"),            Is.False); // the dot is a dot
        Bad("wildcard", "");
    }

    [Test]
    public void IPAddress_and_subnet()
    {
        Assert.That(Canon("ipaddress", " 192.168.1.10 "),     Is.EqualTo("192.168.1.10"));
        Assert.That(Canon("ipaddress", "FE80:0:0:0:0:0:0:1"), Is.EqualTo("fe80::1"));
        Assert.That(Canon("ipaddress", "::1"),                Is.EqualTo("::1"));
        Bad("ipaddress", "1");
        Bad("ipaddress", "1.2.3");
        Bad("ipaddress", "256.1.1.1");
        Bad("ipaddress", "1.2.3.4/8");
        Bad("ipaddress", "[::1]");
        Bad("ipaddress", "host");

        var s = (IPSubnet)Ok("ipsubnet", "10.1.2.3/8")!;
        Assert.That(s.Network.ToString(),                                                          Is.EqualTo("10.0.0.0"));
        Assert.That(s.Contains(IPAddress.Parse("10.255.0.1")),                                     Is.True);
        Assert.That(s.Contains(IPAddress.Parse("11.0.0.1")),                                       Is.False);
        Assert.That(s.Contains(IPAddress.Parse("::1")),                                            Is.False);
        Assert.That(Canon("ipsubnet", "192.168.0.0/16"),                                           Is.EqualTo("192.168.0.0/16"));
        Assert.That(((IPSubnet)Ok("ipsubnet", "fe80::/10")!).PrefixLength,                         Is.EqualTo(10));
        Assert.That(((IPSubnet)Ok("ipsubnet", "0.0.0.0/0")!).Contains(IPAddress.Parse("8.8.8.8")), Is.True);
        Bad("ipsubnet", "10.0.0.0");
        Bad("ipsubnet", "10.0.0.0/");
        Bad("ipsubnet", "10.0.0.0/33");
        Bad("ipsubnet", "::/129");
        Bad("ipsubnet", "/8");
        Bad("ipsubnet", "10.0.0.0/-1");
    }

    [Test]
    public void Version_oid_sid()
    {
        Assert.That(Canon("version", "1.2.3.4"), Is.EqualTo("1.2.3.4"));
        Assert.That(Canon("version", "1.2"),     Is.EqualTo("1.2"));
        Bad("version", "1");
        Bad("version", "1.2.3.4.5");
        Bad("version", "a.b");

        Assert.That(Canon("oid", "1.3.6.1.4.1.311"), Is.EqualTo("1.3.6.1.4.1.311"));
        Assert.That(Canon("oid", "2.999.1"),         Is.EqualTo("2.999.1"));
        Bad("oid", "1");
        Bad("oid", "3.1");
        Bad("oid", "1.40");
        Bad("oid", "1.02.3");
        Bad("oid", "1..2");
        Bad("oid", "1.2.");

        if (Unavailable("securityidentifier")) return;
        Assert.That(Canon("securityidentifier", "s-1-5-32-544"), Is.EqualTo("S-1-5-32-544"));
        Bad("securityidentifier", "S-1");
        Bad("securityidentifier", "S-2-5-32-544");
        Bad("securityidentifier", "BA");
    }

    [Test]
    public void Json_syntaxes()
    {
        Assert.That(Ok("jobj", " { \"a\" : 1 } "),    Is.InstanceOf<JObject>());
        Assert.That(Canon("jobj", " { \"a\" : 1 } "), Is.EqualTo("{\"a\":1}"));
        Bad("jobj", "[1]");
        Bad("jobj", "{'a':1}");
        Bad("jobj", "{a:1}");
        Bad("jobj", "{\"a\":1} extra");
        Bad("jobj", "");

        Assert.That(Canon("jarr", "[1, 2,3]"), Is.EqualTo("[1,2,3]"));
        Bad("jarr", "{}");

        Assert.That(Canon("jtok", "\"x\""), Is.EqualTo("\"x\""));
        Assert.That(Canon("jtok", " 12 "),  Is.EqualTo("12"));
        Assert.That(Canon("jtok", "null"),  Is.EqualTo("null"));
        Assert.That(Canon("jtok", "true"),  Is.EqualTo("true"));
        Bad("jtok", "undefined");
        Bad("jtok", "");
    }

    [Test]
    public void Base64_html()
    {
        Assert.That(Encoding.UTF8.GetString((byte[])Ok("base64", "SGVsbG8sIFNpbG8h")!), Is.EqualTo("Hello, Silo!"));
        Assert.That(Canon("base64", "SGVs bG8="),                                       Is.EqualTo("SGVsbG8="));
        Assert.That(((byte[])Ok("base64", "")!).Length,                                 Is.EqualTo(0));
        Bad("base64", "SGVsbG8");
        Bad("base64", "SGV$bG8=");

        Assert.That(Canon("html", "<p>x</p>"), Is.EqualTo("<p>x</p>"));
        Assert.That(Canon("html", ""),         Is.EqualTo(""));
    }

    [Test]
    public void Csv_and_tsv()
    {
        var rows = (string[][])Ok("csv", "a,b\r\n\"x,y\",\"say \"\"hi\"\"\"\n")!;
        Assert.That(rows.Length,                                         Is.EqualTo(2));
        Assert.That(rows[1][0],                                          Is.EqualTo("x,y"));
        Assert.That(rows[1][1],                                          Is.EqualTo("say \"hi\""));
        Assert.That(Canon("csv", "a,b\n\"x,y\",\"say \"\"hi\"\"\""),     Is.EqualTo("a,b\r\n\"x,y\",\"say \"\"hi\"\"\""));
        Assert.That(((string[][])Ok("csv", "")!).Length,                 Is.EqualTo(0));
        Assert.That(((string[][])Ok("csv", "a,,c")!)[0].Length,          Is.EqualTo(3));
        Assert.That(((string[][])Ok("csv", "\"multi\nline\",2")!)[0][0], Is.EqualTo("multi\nline"));
        Bad("csv", "a,\"b");
        Bad("csv", "a,\"b\"c");
        Bad("csv", "a,b\"c\"");

        Assert.That(((string[][])Ok("tsv", "a\tb\nc\td")!)[1][1], Is.EqualTo("d"));
        Assert.That(Canon("tsv", "a\t\"b\tc\""),                  Is.EqualTo("a\t\"b\tc\""));
        Assert.That(((string[][])Ok("tsv", "a,b\tc")!)[0][0],     Is.EqualTo("a,b"));
    }

    [Test]
    public void Cli_arguments_and_commands()
    {
        Assert.That((string[])Ok("cliargs", "--input \"my file.txt\" -v")!, Is.EqualTo(new[] { "--input", "my file.txt", "-v" }));
        Assert.That((string[])Ok("cliargs", "")!,                           Is.EqualTo(Array.Empty<string>()));
        Assert.That((string[])Ok("cliargs", "a \"\" b")!,                   Is.EqualTo(new[] { "a", "", "b" }));
        Assert.That((string[])Ok("cliargs", "\"a\\\"b\"")!,                 Is.EqualTo(new[] { "a\"b" }));
        Assert.That((string[])Ok("cliargs", "\"C:\\dir\\\\\" x")!,          Is.EqualTo(new[] { "C:\\dir\\", "x" }));
        Assert.That((string[])Ok("cliargs", "C:\\dir\\file.txt")!,          Is.EqualTo(new[] { "C:\\dir\\file.txt" }));
        Assert.That((string[])Ok("cliargs", "\"a\"\"b\"")!,                 Is.EqualTo(new[] { "a\"b" }));
        Bad("cliargs", "\"open");

        var cmd = (CliCommand)Ok("clicommand", "git commit -m \"first commit\"")!;
        Assert.That(cmd.Executable, Is.EqualTo("git"));
        Assert.That(cmd.Arguments, Is.EqualTo(new[] { "commit", "-m", "first commit" }));
        Assert.That(Canon("clicommand", "git   commit -m \"first commit\""), Is.EqualTo("git commit -m \"first commit\""));
        Assert.That(((CliCommand)Ok("clicommand", "\"C:\\Program Files\\x.exe\" /a")!).Executable, Is.EqualTo("C:\\Program Files\\x.exe"));
        Bad("clicommand", "");
        Bad("clicommand", "   ");
        Bad("clicommand", "\"\"");
    }

    [Test]
    public void Quote_and_split_round_trip()
    {
        string[][] samples =
        [
            ["plain"], ["with space", "x"], ["", "y"], ["quote\"inside"], ["trailing\\"], ["trailing space \\"],
            ["a\\\\\"b"], ["tab\there"], ["C:\\Program Files\\App\\"]
        ];
        foreach (var args in samples)
        {
            string line = CliText.Join(args);
            Assert.That(CliText.TrySplit(line, out string[] back, out _), Is.True,          line);
            Assert.That(back,                                             Is.EqualTo(args), line);
        }
    }
}

[TestFixture]
public class SeedAndRuleTests
{
    private static readonly string[] Wanted =
    [
        "Timestamp", "Duration", "Guid", "Regex", "Wildcard", "IPAddress", "IPSubnet", "Version",
        "Oid", "SecurityIdentifier", "JObject", "JArray", "JToken",
        "Base64", "Html", "Csv", "Tsv", "CliArgs", "CliCommand"
    ];

    [Test]
    public void The_seeds_are_the_nineteen_asked_for()
    {
        Assert.That(SyntaxSeeds.Defaults.Select(s => s.Name).ToArray(),          Is.EqualTo(Wanted));
        Assert.That(SyntaxSeeds.Defaults.Single(s => s.Name == "JObject").Alias, Is.EqualTo("jobj"));
        Assert.That(SyntaxSeeds.Defaults.Single(s => s.Name == "JArray").Alias,  Is.EqualTo("jarr"));
        Assert.That(SyntaxSeeds.Defaults.Single(s => s.Name == "JToken").Alias,  Is.EqualTo("jtok"));
    }

    [Test]
    public void Every_seed_is_a_valid_seeded_parser_syntax_among_the_others()
    {
        var seen = new List<SyntaxRecord>();
        foreach (var seed in SyntaxSeeds.Defaults)
        {
            Assert.That(seed.IsSeeded, Is.True,                       seed.Name);
            Assert.That(seed.Kind,     Is.EqualTo(SyntaxKind.Parser), seed.Name);
            var checkedSeed = seed.Parser == "securityidentifier" && !OperatingSystem.IsWindows()
                                  ? seed with { Example = null }
                                  : seed; // the SID parser is Windows-only
            var problems = SyntaxRules.Validate(checkedSeed, seen);
            Assert.That(problems, Is.Empty, seed.Name + ": " + string.Join("; ", problems));
            seen.Add(seed);
        }
    }

    [Test]
    public void StoredSyntax_works_as_an_ISyntax_on_a_parser()
    {
        var timestamp = StoredSyntax.Create(SyntaxSeeds.Defaults.Single(s => s.Name == "Timestamp"));
        Assert.That(timestamp.DisplayName,                                                           Is.EqualTo("Timestamp"));
        Assert.That(timestamp.IsValid("2026-10-16T14:30:00Z"),                                       Is.True);
        Assert.That(timestamp.IsValid("never"),                                                      Is.False);
        Assert.That(timestamp.GetValue("2026-10-16T14:30:00Z"),                                      Is.InstanceOf<DateTimeOffset>());
        Assert.That(timestamp.GetString(new DateTimeOffset(2026, 10, 16, 14, 30, 0, TimeSpan.Zero)), Is.EqualTo("2026-10-16T14:30:00Z"));
        Assert.That(timestamp.GetString("2026-10-16 14:30"),                                         Is.EqualTo("2026-10-16T14:30:00Z"));
        Assert.That(timestamp.ValueType,                                                             Is.EqualTo(typeof(DateTimeOffset)));
        Assert.Throws<FormatException>(() => timestamp.GetValue("never"));
        Assert.Throws<ArgumentException>(() => timestamp.GetString(42));
    }

    [Test]
    public void StoredSyntax_works_on_a_pattern()
    {
        var phone = StoredSyntax.Create(SyntaxRecord.ForPattern("Phone", @"\d{3}-\d{4}"));
        Assert.That(phone.IsValid("555-1234"),   Is.True);
        Assert.That(phone.IsValid("555-12345"),  Is.False); // the whole string has to match
        Assert.That(phone.IsValid("x555-1234"),  Is.False);
        Assert.That(phone.IsValid("555-1234\n"), Is.False);
        Assert.That(phone.GetValue("555-1234"),  Is.EqualTo("555-1234"));
        Assert.That(phone.GetString("555-1234"), Is.EqualTo("555-1234"));
        Assert.That(phone.ValueType,             Is.EqualTo(typeof(string)));
        Assert.Throws<FormatException>(() => phone.GetString("nope"));

        var alternatives = StoredSyntax.Create(SyntaxRecord.ForPattern("Choice", "red|green"));
        Assert.That(alternatives.IsValid("red"),      Is.True);
        Assert.That(alternatives.IsValid("redgreen"), Is.False); // the anchors wrap the alternation
    }

    [Test]
    public void Rules_check_names_aliases_kinds_and_examples()
    {
        SyntaxRecord Make(string name, string? alias = null)
        {
            return SyntaxRecord.ForParser(name, "guid", alias);
        }

        var existing = new[] { Make("Taken", "tk") };

        Assert.That(SyntaxRules.Validate(Make("Fresh"),          existing), Is.Empty);
        Assert.That(SyntaxRules.Validate(Make("taken"),          existing), Has.Count.EqualTo(1));
        Assert.That(SyntaxRules.Validate(Make("TK"),             existing), Has.Count.EqualTo(1));
        Assert.That(SyntaxRules.Validate(Make("Fresh", "TAKEN"), existing), Has.Count.EqualTo(1));
        Assert.That(SyntaxRules.Validate(Make("Fresh", "tk"),    existing), Has.Count.EqualTo(1));
        Assert.That(SyntaxRules.Validate(Make("Fresh", "fresh"), existing), Has.Count.EqualTo(1));
        Assert.That(SyntaxRules.Validate(Make("Fresh", "a b"),   existing), Has.Count.EqualTo(1));
        Assert.That(SyntaxRules.Validate(Make(""),               existing), Has.Count.EqualTo(1));

        Assert.That(SyntaxRules.Validate(SyntaxRecord.ForParser("X", "nosuch"), []), Has.Count.EqualTo(1));
        Assert.That(SyntaxRules.Validate(SyntaxRecord.ForParser("X", ""),       []), Has.Count.EqualTo(1));
        Assert.That(SyntaxRules.Validate(SyntaxRecord.ForPattern("X", ""),      []), Has.Count.EqualTo(1));
        Assert.That(SyntaxRules.Validate(SyntaxRecord.ForPattern("X", "(open"), []), Has.Count.EqualTo(1));

        Assert.That(SyntaxRules.Validate(SyntaxRecord.ForPattern("X", "a+", example: "aaa"),         []), Is.Empty);
        Assert.That(SyntaxRules.Validate(SyntaxRecord.ForPattern("X", "a+", example: "bbb"),         []), Has.Count.EqualTo(1));
        Assert.That(SyntaxRules.Validate(SyntaxRecord.ForParser("X", "guid", example: "not a guid"), []), Has.Count.EqualTo(1));
    }

    [Test]
    public void Normalize_trims_and_lowercases_the_parser_key()
    {
        var n = SyntaxRules.Normalize(new SyntaxRecord(3, " Name ", "  ", " d ", SyntaxKind.Parser, " GUID ", "left over", "",
                                                       SyntaxFlags.Seeded));
        Assert.That(n.Name,        Is.EqualTo("Name"));
        Assert.That(n.Alias,       Is.Null);
        Assert.That(n.Description, Is.EqualTo("d"));
        Assert.That(n.Parser,      Is.EqualTo("guid"));
        Assert.That(n.Pattern,     Is.Null);
        Assert.That(n.Example,     Is.Null);
        Assert.That(n.IsSeeded,    Is.True);
    }
}