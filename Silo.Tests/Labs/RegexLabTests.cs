using Silo.Labs.Regexes;

namespace Silo.Tests.Labs;

public class RegexLabTests
{
    [Test]
    public void Flags_are_read_and_written_in_a_fixed_order()
    {
        Assert.That(RegexFlags.Normalize("smi"), Is.EqualTo("ims"));
        Assert.That(RegexFlags.Normalize("iiQ"), Is.EqualTo("i"));
        Assert.That(RegexFlags.Parse("ix"), Is.EqualTo(System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.IgnorePatternWhitespace));
    }

    [Test]
    public void Options_that_cannot_be_used_together_are_reported()
    {
        Assert.That(RegexFlags.Problem("e"), Is.Null);
        Assert.That(RegexFlags.Problem("es"), Is.Not.Null);
        Assert.That(RegexFlags.Problem("br"), Is.Not.Null);
        Assert.That(RegexFlags.Problem("i"), Is.Null);
    }

    [TestCase("/ab+c/i", "ab+c", "i")]
    [TestCase("/a\\/b/", "a/b", "")]
    [TestCase("/x/smi", "x", "ims")]
    [TestCase("/\\d{2}/", "\\d{2}", "")]
    public void A_slash_literal_is_read(string text, string pattern, string flags)
    {
        Assert.That(RegexLiteral.TryParse(text, out var doc), Is.True);
        Assert.That(doc.Pattern, Is.EqualTo(pattern));
        Assert.That(doc.Flags, Is.EqualTo(flags));
    }

    [TestCase("abc")]
    [TestCase("/abc")]
    [TestCase("/abc/z")]
    [TestCase("")]
    [TestCase("/")]
    public void Text_that_is_not_a_slash_literal_is_not_read(string text)
    {
        Assert.That(RegexLiteral.TryParse(text, out _), Is.False);
    }

    [Test]
    public void A_literal_survives_a_round_trip()
    {
        var doc = new RegexDoc { Pattern = "a/b\\d", Flags = "mi" };

        Assert.That(RegexLiteral.TryParse(RegexLiteral.Format(doc), out var back), Is.True);
        Assert.That(back.Pattern, Is.EqualTo(doc.Pattern));
        Assert.That(back.Flags, Is.EqualTo("im"));
    }

    [Test]
    public void Matches_have_groups_and_positions()
    {
        var doc = new RegexDoc { Pattern = @"(?<k>\w+)=(?<v>\d+)", TestText = "a=1, bb=22" };

        var run = RegexEngine.Run(doc);

        Assert.That(run.IsValid, Is.True);
        Assert.That(run.Matches.Count, Is.EqualTo(2));
        Assert.That(run.Matches[1].Value, Is.EqualTo("bb=22"));
        Assert.That(run.Matches[1].Index, Is.EqualTo(5));
        var k = run.Matches[1].Groups.First(g => g.Name == "k");
        Assert.That(k.Value, Is.EqualTo("bb"));
        Assert.That(k.IsNamed, Is.True);
        Assert.That(run.GroupNames, Is.EqualTo(new[] { "0", "k", "v" }));
        Assert.That(run.Summary, Does.StartWith("2 matches"));
    }

    [Test]
    public void A_bad_pattern_comes_back_as_an_error_with_its_place()
    {
        var run = RegexEngine.Run(new RegexDoc { Pattern = "a(b", TestText = "ab" });

        Assert.That(run.IsValid, Is.False);
        Assert.That(run.Error, Is.Not.Null);
        Assert.That(run.ErrorOffset, Is.Not.Null);
        Assert.That(run.Matches, Is.Empty);
    }

    [Test]
    public void A_pattern_that_backtracks_forever_times_out()
    {
        var doc = new RegexDoc { Pattern = @"^(a+)+$", TestText = new string('a', 60) + "!" };

        var run = RegexEngine.Run(doc, TimeSpan.FromMilliseconds(100));

        Assert.That(run.IsValid, Is.True);
        Assert.That(run.TimedOut, Is.True);
        Assert.That(run.Summary, Does.StartWith("Timed out"));
    }

    [Test]
    public void The_number_of_matches_taken_is_limited()
    {
        var run = RegexEngine.Run(new RegexDoc { Pattern = "a", TestText = new string('a', 50) }, null, 10);

        Assert.That(run.Matches.Count, Is.EqualTo(10));
        Assert.That(run.Truncated, Is.True);
    }

    [Test]
    public void Replace_and_split_use_the_replacement_and_the_matches()
    {
        var doc = new RegexDoc { Pattern = @"(\d+)-(\d+)", TestText = "1-2 and 30-40", Replacement = "$2:$1" };

        Assert.That(RegexEngine.Replace(doc).Text, Is.EqualTo("2:1 and 40:30"));
        Assert.That(RegexEngine.Split(new RegexDoc { Pattern = @",\s*", TestText = "a, b,c" }).Parts, Is.EqualTo(new[] { "a", "b", "c" }));
        Assert.That(RegexEngine.Replace(new RegexDoc { Pattern = "(", TestText = "x" }).Error, Is.Not.Null);
    }

    [Test]
    public void A_full_match_has_to_match_everything()
    {
        var doc = new RegexDoc { Pattern = @"\d+" };

        Assert.That(RegexEngine.IsFullMatch(doc, "123"), Is.True);
        Assert.That(RegexEngine.IsFullMatch(doc, "123x"), Is.False);
        Assert.That(RegexEngine.IsFullMatch(new RegexDoc { Pattern = "a|b" }, "b"), Is.True);
        Assert.That(RegexEngine.IsFullMatch(new RegexDoc { Pattern = "a|b" }, "ab"), Is.False);
    }

    [Test]
    public void The_benchmark_runs_and_reports_a_time()
    {
        var (runs, micros, error) = RegexEngine.Benchmark(new RegexDoc { Pattern = @"\w+", TestText = "the quick brown fox" }, TimeSpan.FromMilliseconds(30));

        Assert.That(error, Is.Null);
        Assert.That(runs, Is.GreaterThan(0));
        Assert.That(micros, Is.GreaterThan(0.0));
    }

    [Test]
    public void The_text_is_cut_into_stretches_by_match_and_group()
    {
        var doc  = new RegexDoc { Pattern = @"(b)c", TestText = "abcd" };
        var run  = RegexEngine.Run(doc);
        var spans = RegexEngine.Spans(doc.TestText, run);

        // a | b (match 1, group 1) | c (match 1) | d
        Assert.That(spans.Count, Is.EqualTo(4));
        Assert.That(spans[0].Match, Is.EqualTo(0));
        Assert.That(spans[1].Match, Is.EqualTo(1));
        Assert.That(spans[1].Group, Is.EqualTo(1));
        Assert.That(spans[2].Group, Is.EqualTo(0));
        Assert.That(spans[2].Length, Is.EqualTo(1));
        Assert.That(spans[3].Match, Is.EqualTo(0));
    }

    [Test]
    public void Every_library_pattern_is_valid_and_matches_its_sample()
    {
        foreach (var entry in RegexLibrary.Entries)
        {
            var run = RegexEngine.Run(RegexLibrary.ToDoc(entry));
            Assert.That(run.IsValid, Is.True, entry.Name + ": " + run.Error);
            Assert.That(run.Matches.Count, Is.GreaterThan(0), entry.Name + " matched nothing in its sample");
        }
    }

    [Test]
    public void The_library_samples_match_what_they_should()
    {
        Assert.That(RegexEngine.Run(RegexLibrary.ToDoc(RegexLibrary.Find("IPv4 address")!)).Matches.Select(m => m.Value), Is.EqualTo(new[] { "10.0.0.1", "192.168.1.255" }));
        Assert.That(RegexEngine.Run(RegexLibrary.ToDoc(RegexLibrary.Find("Repeated word")!)).Matches.Select(m => m.Value), Is.EqualTo(new[] { "is is", "of of", "the the" }));
        Assert.That(RegexEngine.Run(RegexLibrary.ToDoc(RegexLibrary.Find("ISO date")!)).Matches.Count, Is.EqualTo(2));
        Assert.That(RegexEngine.Run(RegexLibrary.ToDoc(RegexLibrary.Find("MAC address")!)).Matches.Count, Is.EqualTo(3));
    }

    [Test]
    public void The_explanation_names_the_parts()
    {
        var lines = RegexExplainer.Explain(@"^(?<user>[\w.]+)@(\w+)\.com$", "");
        string text = RegexExplainer.ToText(lines);

        Assert.That(text, Does.Contain("capturing group called user"));
        Assert.That(text, Does.Contain("capturing group number 2"));
        Assert.That(text, Does.Contain("one or more times"));
        Assert.That(text, Does.Contain("the character '@'"));
        Assert.That(text, Does.Contain("the start of the text"));
        // what is inside a group is indented
        Assert.That(lines.First(l => l.Token.StartsWith("[")).Depth, Is.EqualTo(1));
        Assert.That(lines.First(l => l.Token == "^").Depth, Is.EqualTo(0));
    }

    [Test]
    public void A_quantifier_goes_with_the_item_before_it_only()
    {
        var lines = RegexExplainer.Explain("abc+", "");

        Assert.That(lines.Count, Is.EqualTo(2));
        Assert.That(lines[0].Token, Is.EqualTo("ab"));
        Assert.That(lines[1].Token, Is.EqualTo("c+"));
        Assert.That(lines[1].Meaning, Does.Contain("one or more times"));
    }

    [Test]
    public void Quantifiers_say_greedy_or_lazy()
    {
        Assert.That(RegexExplainer.ToText(RegexExplainer.Explain("a{2,4}?", "")), Does.Contain("2 to 4 times, as few as it can"));
        Assert.That(RegexExplainer.ToText(RegexExplainer.Explain(".*", "")), Does.Contain("zero or more times, as many as it can"));
        Assert.That(RegexExplainer.ToText(RegexExplainer.Explain("a{3}", "")), Does.Contain("exactly 3 times"));
    }

    [Test]
    public void Classes_lookarounds_and_options_are_explained()
    {
        string text = RegexExplainer.ToText(RegexExplainer.Explain(@"(?i)[^a-c\d](?=x)(?<!y)(?:z)", ""));

        Assert.That(text, Does.Contain("changes options from here: on: IgnoreCase"));
        Assert.That(text, Does.Contain("any one character that is not: 'a' to 'c', a digit"));
        Assert.That(text, Does.Contain("look-ahead"));
        Assert.That(text, Does.Contain("negative look-behind"));
        Assert.That(text, Does.Contain("does not capture"));
    }

    [Test]
    public void Free_spacing_ignores_white_space_and_reads_comments()
    {
        var lines = RegexExplainer.Explain("a b # the letter b\n c", "x");

        Assert.That(lines.Any(l => l.Meaning == "a comment"), Is.True);
        Assert.That(lines.Any(l => l.Token.Contains(' ') && l.Meaning.StartsWith("the text")), Is.False);
    }

    [Test]
    public void A_group_left_open_is_reported()
    {
        Assert.That(RegexExplainer.ToText(RegexExplainer.Explain("(ab", "")), Does.Contain("never closed"));
        Assert.That(RegexExplainer.ToText(RegexExplainer.Explain("ab)", "")), Does.Contain("no group open"));
    }

    [Test]
    public void Escapes_are_explained()
    {
        string text = RegexExplainer.ToText(RegexExplainer.Explain(@"\d\u00e9\p{Lu}\.\k<x>\1", ""));

        Assert.That(text, Does.Contain("a digit"));
        Assert.That(text, Does.Contain("U+00E9"));
        Assert.That(text, Does.Contain("the category or block Lu"));
        Assert.That(text, Does.Contain("escaped, so it is not special"));
        Assert.That(text, Does.Contain("back-reference"));
    }

    [Test]
    public void Code_is_generated_for_each_language()
    {
        var doc = new RegexDoc { Pattern = "a\"b", Flags = "i" };

        Assert.That(RegexCode.Generate("C#", doc), Does.Contain("new Regex(@\"a\"\"b\", RegexOptions.IgnoreCase)"));
        Assert.That(RegexCode.Generate("C# (generated)", doc), Does.Contain("[GeneratedRegex("));
        Assert.That(RegexCode.Generate("JavaScript", doc), Does.Contain("/a\"b/ig"));
        Assert.That(RegexCode.Generate("Python", doc), Does.Contain("re.IGNORECASE"));
        Assert.That(RegexCode.Generate("PowerShell", doc), Does.Contain("IgnoreCase"));
        Assert.That(RegexCode.Generate("Java", doc), Does.Contain("a\\\"b"));
        Assert.That(RegexCode.Generate("Literal", doc), Is.EqualTo("/a\"b/i"));
    }

    [Test]
    public void The_reference_has_every_group()
    {
        Assert.That(RegexLibrary.Reference.Select(r => r.Group).Distinct().Count(), Is.GreaterThan(5));
    }
}
