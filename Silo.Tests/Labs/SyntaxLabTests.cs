using Silo.Providers.Data;
using Silo.DbModel.Meta;
using Silo.Labs.Syntaxes;

namespace Silo.Tests.Labs;

public class SyntaxLabTests
{
    private static SyntaxDraft Pattern(string name, string pattern, string? example = null)
    {
        return new SyntaxDraft { Name = name, Kind = SyntaxKind.Pattern, Pattern = pattern, Example = example };
    }

    private static SyntaxDraft Parser(string name, string parser)
    {
        return new SyntaxDraft { Name = name, Kind = SyntaxKind.Parser, Parser = parser };
    }

    [Test]
    public void A_pattern_syntax_has_to_match_the_whole_text()
    {
        var build = SyntaxTrial.Build(Pattern("Zip", @"\d{5}"));

        Assert.That(build.IsBuilt, Is.True);
        Assert.That(build.Problems, Is.Empty);
        Assert.That(SyntaxTrial.Run(build.Syntax!, "90210").IsValid, Is.True);
        Assert.That(SyntaxTrial.Run(build.Syntax!, "902101").IsValid, Is.False);
        Assert.That(SyntaxTrial.Run(build.Syntax!, "x90210").IsValid, Is.False);
    }

    [Test]
    public void A_parser_syntax_makes_a_value_and_writes_it_back()
    {
        var build = SyntaxTrial.Build(Parser("Address", "ipaddress"));

        var ok = SyntaxTrial.Run(build.Syntax!, "192.168.1.10");

        Assert.That(ok.IsValid, Is.True);
        Assert.That(ok.TypeName, Is.EqualTo("IPAddress"));
        Assert.That(ok.Canonical, Is.EqualTo("192.168.1.10"));
        Assert.That(ok.RoundTrips, Is.True);
        Assert.That(SyntaxTrial.Run(build.Syntax!, "300.1.1.1").IsValid, Is.False);
        Assert.That(SyntaxTrial.Run(build.Syntax!, "300.1.1.1").Error, Is.Not.Null);
    }

    [Test]
    public void A_syntax_that_cannot_be_built_says_why()
    {
        Assert.That(SyntaxTrial.Build(Pattern("Bad", "a(b")).Problems[0], Does.Contain("not a valid regular expression"));
        Assert.That(SyntaxTrial.Build(Pattern("Empty", "")).Problems[0], Does.Contain("pattern is required"));
        Assert.That(SyntaxTrial.Build(Parser("NoParser", "")).Problems[0], Does.Contain("Choose the parser"));
        Assert.That(SyntaxTrial.Build(Parser("Unknown", "nope")).Problems[0], Does.Contain("no built-in parser"));
        Assert.That(SyntaxTrial.Build(Pattern("Bad", "a(b")).IsBuilt, Is.False);
    }

    [Test]
    public void The_name_has_to_be_free_and_the_example_has_to_be_valid()
    {
        var other = SyntaxRecord.ForPattern("Zip", @"\d{5}");

        var taken = SyntaxTrial.Build(Pattern("zip", @"\d{4}"), [other]);
        Assert.That(taken.IsBuilt, Is.True);
        Assert.That(taken.Problems.Any(p => p.Contains("already used")), Is.True);

        var badExample = SyntaxTrial.Build(Pattern("Code", @"\d{4}", "abc"));
        Assert.That(badExample.Problems.Any(p => p.Contains("example is not valid")), Is.True);

        var fine = SyntaxTrial.Build(Pattern("Code", @"\d{4}", "1234"));
        Assert.That(fine.Problems, Is.Empty);
    }

    [Test]
    public void A_syntax_does_not_clash_with_itself()
    {
        var saved = new SyntaxRecord(7, "Zip", null, null, SyntaxKind.Pattern, null, @"\d{5}", null);
        var draft = SyntaxDraft.From(saved);

        Assert.That(SyntaxTrial.Build(draft, [saved]).Problems, Is.Empty);
    }

    [Test]
    public void Samples_are_checked_against_what_they_should_be()
    {
        var build = SyntaxTrial.Build(Pattern("Zip", @"\d{5}"));
        var book  = new SyntaxBook { Valid = "12345\n99999\n1234", Invalid = "abcde\n123456", Try = "00000" };

        var results = SyntaxTrial.RunAll(build.Syntax!, book);

        Assert.That(results.Count, Is.EqualTo(6));
        Assert.That(results.Count(r => r.Passed), Is.EqualTo(5));
        Assert.That(results.First(r => r.Text == "1234").Passed, Is.False);
        Assert.That(results.First(r => r.Text == "abcde").Passed, Is.True);
        Assert.That(results.First(r => r.Text == "00000").Expect, Is.EqualTo(TrialExpect.Nothing));
    }

    [Test]
    public void Escapes_let_a_sample_have_several_lines()
    {
        Assert.That(SyntaxTrial.Unescape(@"a\nb\tc\\d\q"), Is.EqualTo("a\nb\tc\\d\\q"));
        Assert.That(SyntaxTrial.Escape("a\nb\tc\\d"), Is.EqualTo(@"a\nb\tc\\d"));
        Assert.That(SyntaxTrial.Lines("x\\ny\r\n\r\nz", true), Is.EqualTo(new[] { "x\ny", "z" }));
        Assert.That(SyntaxTrial.Lines("x\\ny", false), Is.EqualTo(new[] { "x\\ny" }));
        Assert.That(SyntaxTrial.Lines(null, true), Is.Empty);

        var csv = SyntaxTrial.Build(Parser("Table", "csv"));
        var r   = SyntaxTrial.Run(csv.Syntax!, SyntaxTrial.Unescape("a,b\\nc,d"));
        Assert.That(r.IsValid, Is.True);
        Assert.That(r.Value, Does.Contain("2 row(s)"));
    }

    [Test]
    public void Values_are_described_by_their_kind()
    {
        Assert.That(SyntaxTrial.Describe(null), Is.EqualTo("(null)"));
        Assert.That(SyntaxTrial.Describe(new byte[] { 1, 0xAB }), Is.EqualTo("2 byte(s): 01AB"));
        Assert.That(SyntaxTrial.Describe(new[] { "a", "b" }), Is.EqualTo("\"a\" | \"b\""));
        Assert.That(SyntaxTrial.Describe(TimeSpan.FromMinutes(90)), Is.EqualTo("01:30:00"));
        Assert.That(SyntaxTrial.Describe(Newtonsoft.Json.Linq.JObject.Parse("{ \"a\" : 1 }")), Is.EqualTo("{\"a\":1}"));
        Assert.That(SyntaxTrial.TypeNameOf(new byte[0]), Is.EqualTo("byte[]"));
    }

    [Test]
    public void Every_built_in_parser_accepts_its_own_example_and_round_trips_it()
    {
        foreach (var parser in Silo.Providers.Definitions.SyntaxParsers.All)
        {
            // Windows-only parsers (security identifiers) cannot run elsewhere.
            if (parser.Key == "securityidentifier" && !OperatingSystem.IsWindows())
            {
                continue;
            }

            var build = SyntaxTrial.Build(Parser("P" + parser.Key, parser.Key));
            var r     = SyntaxTrial.Run(build.Syntax!, parser.Example);

            Assert.That(r.IsValid, Is.True, parser.Key + ": " + r.Error);
            Assert.That(r.RoundTrips, Is.True, parser.Key + " does not round trip: " + r.Canonical);
        }
    }

    [Test]
    public void A_draft_and_a_record_convert_both_ways()
    {
        var record = new SyntaxRecord(3, "N", "n", "d", SyntaxKind.Parser, "guid", null, "x", SyntaxFlags.Seeded);

        var back = SyntaxDraft.From(record).ToRecord();

        Assert.That(back, Is.EqualTo(record));
        Assert.That(SyntaxTrial.ToJson(SyntaxDraft.From(record)), Does.Contain("\"Kind\": \"Parser\""));
    }
}
