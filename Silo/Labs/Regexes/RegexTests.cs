using System.Diagnostics;
using Silo.Labs.Parser;

namespace Silo.Labs.Regexes;

/// <summary>
///     Runs a regex's tests the way the Parser Lab runs a spec's: a sample is only found or not found in the regex's hands; a test says what is
///     expected of the samples it uses, and passes or fails. Found means the pattern matches somewhere in the sample (use anchors for the whole of it).
/// </summary>
public static class RegexTestRunner
{
    /// <summary>What trying the regex on one sample came to: whether it matched, and the first match.</summary>
    public static (bool Found, string Value, string? Error) Try(RegexDoc doc, string sample, TimeSpan? timeout = null)
    {
        var regex = RegexEngine.TryBuild(doc, timeout ?? RegexEngine.DefaultTimeout, out string? error, out _);
        if (regex is null) return (false, string.Empty, error ?? "The pattern is not valid.");

        try
        {
            var m = regex.Match(sample);
            return (m.Success, m.Success ? m.Value : string.Empty, null);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return (false, string.Empty, "Timed out");
        }
    }

    /// <summary>Runs one test of <paramref name="doc" />.</summary>
    public static TestResult Run(RegexDoc doc, TestCase test, TimeSpan? timeout = null)
    {
        var sw       = Stopwatch.StartNew();
        var outcomes = new List<SampleOutcome>();
        var book     = new SpecTestBook { Samples = doc.Samples, Tests = doc.Tests };

        foreach (string missing in test.Samples.Where(n => !doc.Samples.Any(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))))
            outcomes.Add(new SampleOutcome("(sample set)", false, $"there is no sample set called '{missing}'"));

        var samples = ParserTestRunner.SamplesOf(test, book).ToList();
        if (samples.Count == 0 && outcomes.Count == 0) outcomes.Add(new SampleOutcome("(none)", false, "the test has no samples"));

        foreach (var (_, sample) in samples)
        {
            var (found, value, error) = Try(doc, sample, timeout);
            if (error is not null)
            {
                outcomes.Add(new SampleOutcome(sample, false, error));
                continue;
            }

            switch (test.Expect)
            {
                case Expectation.Matches:
                    outcomes.Add(new SampleOutcome(sample, found, found ? value : "expected a match"));
                    break;
                case Expectation.DoesNotMatch:
                    outcomes.Add(new SampleOutcome(sample, !found, found ? "expected no match, found " + value : "no match"));
                    break;
                default:
                {
                    string want = test.ExpectedValue ?? string.Empty;
                    bool   ok   = found && string.Equals(value, want, StringComparison.Ordinal);
                    outcomes.Add(new SampleOutcome(sample, ok, ok ? value : found ? $"expected {want}, found {value}" : "expected a match"));
                    break;
                }
            }
        }

        sw.Stop();
        return new TestResult(test, outcomes.All(o => o.Passed) ? TestState.Passed : TestState.Failed, outcomes, sw.Elapsed);
    }
}
