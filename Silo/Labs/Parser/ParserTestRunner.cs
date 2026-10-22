using System.Diagnostics;

namespace Silo.Labs.Parser;

/// <summary>What one parse (or tokenization) of a text came to, the same whether it was a sample, a test or a line typed in the terminal.</summary>
/// <param name="Success">Whether it matched.</param>
/// <param name="Value">The value as one line of text (the tokens, in the tokenizer mode); empty when it did not match.</param>
/// <param name="Error">What was expected where, when it did not match.</param>
/// <param name="ErrorPosition">Where it went wrong; -1 when it matched.</param>
/// <param name="Raw">The value itself.</param>
public sealed record EvalOutcome(bool Success, string Value, string? Error, int ErrorPosition, object? Raw, int Consumed = 0)
{
    /// <summary>What to show for the outcome in a list: the value, or the problem.</summary>
    public string Summary => Success ? Value : $"{Error}" + (ErrorPosition >= 0 ? $" (at {ErrorPosition})" : string.Empty);
}

/// <summary>Parses and tokenizes texts with a compiled spec.</summary>
public static class SpecEval
{
    /// <summary>Parses <paramref name="text" /> as <paramref name="rule" /> (the spec's root when null); in the tokenizer mode cuts it into tokens.</summary>
    public static EvalOutcome Eval(CompiledSpec spec, string text, string? rule)
    {
        try
        {
            if (spec.Doc.Mode == SpecMode.Tokenizer) return Tokens(spec, text);

            var r = spec.Parse(text, rule);
            return r.Success
                       ? new EvalOutcome(true, SpecValues.Format(r.Value), null, -1, r.Value, r.Consumed)
                       : new EvalOutcome(false, string.Empty, r.Error, r.ErrorPosition, null);
        }
        catch (Exception ex)
        {
            return new EvalOutcome(false, string.Empty, ex.Message, 0, null);
        }
    }

    /// <summary>Cuts <paramref name="text" /> into tokens whatever the mode.</summary>
    public static EvalOutcome Tokens(CompiledSpec spec, string text)
    {
        var t = spec.Tokenize(text);
        if (!t.Success) return new EvalOutcome(false, string.Empty, t.Error, t.ErrorPosition, null);

        string shown = string.Join(" ", t.Tokens.Select(x => $"{x.Kind}:{x.Text}"));
        return new EvalOutcome(true, shown, null, -1, t.Tokens, text.Length);
    }
}

/// <summary>Where a test stands.</summary>
public enum TestState
{
    NotRun,
    Passed,
    Failed
}

/// <summary>What one test's sample came to.</summary>
public sealed record SampleOutcome(string Sample, bool Passed, string Detail);

/// <summary>What running a test came to.</summary>
public sealed class TestResult
{
    public TestResult(TestCase test, TestState state, IReadOnlyList<SampleOutcome> outcomes, TimeSpan elapsed)
    {
        Test     = test;
        State    = state;
        Outcomes = outcomes;
        Elapsed  = elapsed;
    }

    public TestCase Test { get; }

    public TestState State { get; }

    public IReadOnlyList<SampleOutcome> Outcomes { get; }

    public TimeSpan Elapsed { get; }

    /// <summary>One line: how many samples passed.</summary>
    public string Summary => State == TestState.NotRun ? "not run" : $"{Outcomes.Count(o => o.Passed)}/{Outcomes.Count} passed in {Elapsed.TotalMilliseconds:0.#} ms";
}

/// <summary>Runs a spec's tests the way a test explorer does: each test is a pass or a fail.</summary>
public static class ParserTestRunner
{
    /// <summary>The strings a test uses: the sets it names, then its own.</summary>
    public static IEnumerable<(string Set, string Sample)> SamplesOf(TestCase test, SpecTestBook book)
    {
        foreach (string name in test.Samples)
        {
            var set = book.Samples.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (set is null) continue;

            foreach (string item in set.Items) yield return (set.Name, item);
        }

        foreach (string extra in test.Extra) yield return ("(own)", extra);
    }

    /// <summary>Runs one test with <paramref name="spec" />.</summary>
    public static TestResult Run(CompiledSpec spec, SpecTestBook book, TestCase test)
    {
        var sw       = Stopwatch.StartNew();
        var outcomes = new List<SampleOutcome>();

        foreach (string missing in test.Samples.Where(n => !book.Samples.Any(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))))
            outcomes.Add(new SampleOutcome("(sample set)", false, $"there is no sample set called '{missing}'"));

        string? rule = test.Rule ?? spec.DefaultRule;
        if (spec.Doc.Mode == SpecMode.Parser && rule is not null && !spec.HasRule(rule))
            outcomes.Add(new SampleOutcome("(rule)", false, $"there is no rule called '{rule}'"));

        var samples = SamplesOf(test, book).ToList();
        if (samples.Count == 0 && outcomes.Count == 0) outcomes.Add(new SampleOutcome("(none)", false, "the test has no samples"));

        foreach (var (_, sample) in samples)
        {
            var got = SpecEval.Eval(spec, sample, rule);
            switch (test.Expect)
            {
                case Expectation.Matches:
                    outcomes.Add(new SampleOutcome(sample, got.Success, got.Success ? got.Value : "expected a match: " + got.Summary));
                    break;
                case Expectation.DoesNotMatch:
                    outcomes.Add(new SampleOutcome(sample, !got.Success, got.Success ? "expected no match, got " + got.Value : got.Summary));
                    break;
                default:
                {
                    string want = test.ExpectedValue ?? string.Empty;
                    bool   ok   = got.Success && string.Equals(got.Value.Trim(), want.Trim(), StringComparison.Ordinal);
                    outcomes.Add(new SampleOutcome(sample, ok, ok ? got.Value : got.Success ? $"expected {want}, got {got.Value}" : "expected a match: " + got.Summary));
                    break;
                }
            }
        }

        sw.Stop();
        return new TestResult(test, outcomes.All(o => o.Passed) ? TestState.Passed : TestState.Failed, outcomes, sw.Elapsed);
    }

    /// <summary>Runs every test of the book.</summary>
    public static IEnumerable<TestResult> RunAll(CompiledSpec spec, SpecTestBook book)
    {
        foreach (var test in book.Tests) yield return Run(spec, book, test);
    }
}
