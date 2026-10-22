using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Silo.Labs.Regexes;

/// <summary>One group of a match.</summary>
/// <param name="Number">The number of the group (0 is the whole match).</param>
/// <param name="Name">Its name; the number as text when it has none.</param>
/// <param name="Captures">Everything it captured (a group in a loop captures more than once).</param>
public sealed record GroupInfo(int Number, string Name, bool Success, int Index, int Length, string Value, IReadOnlyList<CaptureInfo> Captures)
{
    public bool IsNamed => Name != Number.ToString();
}

public sealed record CaptureInfo(int Index, int Length, string Value);

public sealed record MatchInfo(int Number, int Index, int Length, string Value, IReadOnlyList<GroupInfo> Groups);

/// <summary>What trying a regex on a text came to.</summary>
public sealed class RegexRun
{
    public bool IsValid { get; init; }

    /// <summary>Why the pattern cannot be used.</summary>
    public string? Error { get; init; }

    /// <summary>Where in the pattern it went wrong, when that is known.</summary>
    public int? ErrorOffset { get; init; }

    public bool TimedOut { get; init; }

    public IReadOnlyList<MatchInfo> Matches { get; init; } = [];

    /// <summary>True when there were more matches than were taken.</summary>
    public bool Truncated { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>The names of the groups, in order (0 is the whole match).</summary>
    public IReadOnlyList<string> GroupNames { get; init; } = [];

    public string Summary
    {
        get
        {
            if (!IsValid) return Error ?? "The pattern is not valid.";
            if (TimedOut) return $"Timed out after {Elapsed.TotalMilliseconds:0} ms: the pattern backtracks too much on this text";
            string count = Matches.Count == 1 ? "1 match" : $"{Matches.Count:N0}{(Truncated ? "+" : string.Empty)} matches";
            return $"{count} in {FormatTime(Elapsed)}";
        }
    }

    public static string FormatTime(TimeSpan time)
    {
        double us = time.TotalMilliseconds * 1000;
        return us < 1000 ? $"{us:0} µs" : $"{us / 1000:0.##} ms";
    }
}

/// <summary>Builds and runs regexes for the Lab: pattern problems come back as a result, not an exception.</summary>
public static class RegexEngine
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    public const int DefaultMaxMatches = 1000;

    /// <summary>Builds the regex of a document; null (with the reason) when it cannot be built.</summary>
    public static Regex? TryBuild(RegexDoc doc, TimeSpan timeout, out string? error, out int? offset)
    {
        error  = null;
        offset = null;

        if (RegexFlags.Problem(doc.Flags) is { } problem)
        {
            error = problem;
            return null;
        }

        try
        {
            return new Regex(doc.Pattern ?? string.Empty, RegexFlags.Parse(doc.Flags), timeout);
        }
        catch (RegexParseException ex)
        {
            error  = ex.Message;
            offset = ex.Offset;
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return null;
        }
    }

    public static RegexRun Run(RegexDoc doc, TimeSpan? timeout = null, int maxMatches = DefaultMaxMatches)
    {
        var regex = TryBuild(doc, timeout ?? DefaultTimeout, out string? error, out int? offset);
        if (regex is null) return new RegexRun { IsValid = false, Error = error, ErrorOffset = offset };

        string text    = doc.TestText ?? string.Empty;
        var    matches = new List<MatchInfo>();
        bool   more    = false;
        bool   timedOut = false;
        var    sw      = Stopwatch.StartNew();
        try
        {
            var names = regex.GetGroupNames();
            int n     = 0;
            for (var m = regex.Match(text); m.Success; m = m.NextMatch())
            {
                if (matches.Count >= maxMatches)
                {
                    more = true;
                    break;
                }

                matches.Add(Describe(regex, m, ++n));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
        }

        sw.Stop();
        return new RegexRun
               {
                   IsValid    = true,
                   TimedOut   = timedOut,
                   Matches    = matches,
                   Truncated  = more,
                   Elapsed    = sw.Elapsed,
                   GroupNames = regex.GetGroupNames()
               };
    }

    private static MatchInfo Describe(Regex regex, Match m, int number)
    {
        var groups = new List<GroupInfo>();
        foreach (string name in regex.GetGroupNames())
        {
            var g = m.Groups[name];
            int num = regex.GroupNumberFromName(name);
            groups.Add(new GroupInfo(num, name, g.Success, g.Success ? g.Index : -1, g.Success ? g.Length : 0, g.Success ? g.Value : string.Empty,
                                     g.Captures.Select(c => new CaptureInfo(c.Index, c.Length, c.Value)).ToList()));
        }

        return new MatchInfo(number, m.Index, m.Length, m.Value, groups);
    }

    /// <summary>The text with every match replaced; the reason when it cannot be.</summary>
    public static (string? Text, string? Error) Replace(RegexDoc doc, TimeSpan? timeout = null)
    {
        var regex = TryBuild(doc, timeout ?? DefaultTimeout, out string? error, out _);
        if (regex is null) return (null, error);

        try
        {
            return (regex.Replace(doc.TestText ?? string.Empty, doc.Replacement ?? string.Empty), null);
        }
        catch (RegexMatchTimeoutException)
        {
            return (null, "Timed out");
        }
    }

    /// <summary>The text cut at every match (what <c>Regex.Split</c> gives, captured groups included).</summary>
    public static (IReadOnlyList<string>? Parts, string? Error) Split(RegexDoc doc, TimeSpan? timeout = null)
    {
        var regex = TryBuild(doc, timeout ?? DefaultTimeout, out string? error, out _);
        if (regex is null) return (null, error);

        try
        {
            return (regex.Split(doc.TestText ?? string.Empty), null);
        }
        catch (RegexMatchTimeoutException)
        {
            return (null, "Timed out");
        }
    }

    /// <summary>Whether the whole text matches (what a Syntax of the Pattern kind asks).</summary>
    public static bool IsFullMatch(RegexDoc doc, string text, TimeSpan? timeout = null)
    {
        var whole = new RegexDoc { Pattern = $@"\A(?:{doc.Pattern})\z", Flags = doc.Flags };
        var regex = TryBuild(whole, timeout ?? DefaultTimeout, out _, out _);
        try
        {
            return regex?.IsMatch(text) ?? false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Times the pattern on the text: how long a match takes, how many matches per second. Runs for about <paramref name="budget" />.
    /// </summary>
    public static (int Runs, double MicrosPerRun, string? Error) Benchmark(RegexDoc doc, TimeSpan budget, TimeSpan? timeout = null)
    {
        var regex = TryBuild(doc, timeout ?? DefaultTimeout, out string? error, out _);
        if (regex is null) return (0, 0, error);

        string text = doc.TestText ?? string.Empty;
        int    runs = 0;
        try
        {
            // the first run builds what the engine needs
            regex.Count(text);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < budget && runs < 1_000_000)
            {
                regex.Count(text);
                runs++;
            }

            sw.Stop();
            return (runs, sw.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, runs), null);
        }
        catch (RegexMatchTimeoutException)
        {
            return (runs, 0, "Timed out");
        }
    }

    /// <summary>
    ///     The text cut into the stretches to draw: each is plain or inside a match (number from 1) and a group (number from 1, or 0 for none).
    ///     The groups of a match are drawn inside the match, the innermost group wins.
    /// </summary>
    public static IReadOnlyList<Span> Spans(string text, RegexRun run)
    {
        int[] match = new int[text.Length];
        int[] group = new int[text.Length];
        foreach (var m in run.Matches)
        {
            for (int i = m.Index; i < m.Index + m.Length && i < text.Length; i++) match[i] = m.Number;
            foreach (var g in m.Groups)
            {
                if (g.Number == 0) continue;
                foreach (var c in g.Captures)
                    for (int i = c.Index; i < c.Index + c.Length && i < text.Length; i++)
                        group[i] = g.Number;
            }
        }

        var spans = new List<Span>();
        int start = 0;
        for (int i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || match[i] != match[start] || group[i] != group[start])
            {
                spans.Add(new Span(start, i - start, match[start], group[start]));
                start = i;
            }
        }

        return spans;
    }

    public sealed record Span(int Start, int Length, int Match, int Group);
}
