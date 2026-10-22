using System.Text.RegularExpressions;
using Telefrag.DI;

namespace Silo.Labs.Regexes;

public static class RegexLabParameters
{
    public const string TimeoutMs  = "timeoutMs";
    public const string MaxMatches = "maxMatches";
    public const string NewFlags   = "newFlags";
}

/// <summary>Builds and tests regular expressions: the pattern, its options, a text to try it on, what it matched and in which groups, what it explains to be, and what a replacement makes of the text.</summary>
[Singleton(typeof(ILab))]
public class RegexLab : LabBase<RegexDoc>
{
    public override string Description => "Build and test regular expressions: see every match and group in your text, replace, split, have the pattern explained, and keep the ones you want.";

    public override string IconKey => "DataSearching";

    public override string Id => "regex";

    public override string Name => "Regex Lab";

    public override int Order => 50;

    protected override IEnumerable<LabParameter> DefineParameters()
    {
        yield return new LabParameter(RegexLabParameters.TimeoutMs, "Time limit (ms)", LabParameterKind.Text, "1000",
                                      "How long a try may take before it is given up on (a pattern that backtracks too much would hang the window).", "Running", hint: "1000");
        yield return new LabParameter(RegexLabParameters.MaxMatches, "Most matches", LabParameterKind.Text, "1000",
                                      "How many matches are listed and marked in the text (counting goes on only up to this).", "Running", hint: "1000");
        yield return new LabParameter(RegexLabParameters.NewFlags, "Options of a new regex", LabParameterKind.Flags, null,
                                      "The options that are on when a new regex is started.", "Running", RegexFlags.All.Select(f => f.Name).ToList());
    }

    public override LabSession<RegexDoc> CreateSession()
    {
        return new RegexLabSession(this);
    }

    protected override bool TryConvert(object? candidate, out RegexDoc? value)
    {
        switch (candidate)
        {
            case RegexDoc doc:
                value = doc;
                return true;
            case Regex regex:
                value = new RegexDoc { Pattern = regex.ToString(), Flags = RegexFlags.Format(regex.Options) };
                return true;
            // asked of every node when a menu is built: only the /pattern/flags way of writing one is taken
            case string text when text.Length > 2 && text[0] == '/' && RegexLiteral.TryParse(text.Trim(), out var literal):
                value = literal;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public override bool TryParse(string text, out RegexDoc? value)
    {
        if (RegexLiteral.TryParse(text.Trim(), out var literal))
        {
            value = literal;
            return true;
        }

        // anything else typed in the value box is a pattern
        value = new RegexDoc { Pattern = text, Flags = RegexFlags.Normalize(string.Concat(Parameters[RegexLabParameters.NewFlags].Lines.Select(FlagLetter))) };
        return true;
    }

    internal static string FlagLetter(string name)
    {
        var f = RegexFlags.All.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return f.Name is null ? string.Empty : f.Letter.ToString();
    }

    /// <summary>The time limit, as set in the parameters.</summary>
    internal TimeSpan Timeout =>
        int.TryParse(Parameters[RegexLabParameters.TimeoutMs].Text, out int ms) && ms > 0 ? TimeSpan.FromMilliseconds(Math.Min(ms, 600_000)) : RegexEngine.DefaultTimeout;

    internal int MaxMatches =>
        int.TryParse(Parameters[RegexLabParameters.MaxMatches].Text, out int n) && n > 0 ? Math.Min(n, 100_000) : RegexEngine.DefaultMaxMatches;
}
