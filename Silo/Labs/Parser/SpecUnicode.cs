using System.Globalization;

namespace Silo.Labs.Parser;

/// <summary>A class of Unicode characters (a general category such as <c>Lu</c>, or a whole major class such as <c>L</c>) a <c>unicode</c> node matches.</summary>
/// <param name="Code">The short code ("Lu", "L").</param>
/// <param name="Title">What it is called to a person.</param>
/// <param name="Categories">The general categories it covers.</param>
public sealed record UnicodeClass(string Code, string Title, UnicodeCategory[] Categories);

/// <summary>The Unicode classes a spec can match, and how the text that names them is read. Single UTF-16 characters only (no pairs).</summary>
public static class UnicodeClasses
{
    private static UnicodeClass Minor(string code, string title, UnicodeCategory category) => new(code, title, [category]);

    /// <summary>The seven major classes, each followed by its general categories.</summary>
    public static IReadOnlyList<UnicodeClass> All { get; } =
    [
        new("L", "Letter", [UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.TitlecaseLetter, UnicodeCategory.ModifierLetter, UnicodeCategory.OtherLetter]),
        Minor("Lu", "Uppercase letter", UnicodeCategory.UppercaseLetter),
        Minor("Ll", "Lowercase letter", UnicodeCategory.LowercaseLetter),
        Minor("Lt", "Titlecase letter", UnicodeCategory.TitlecaseLetter),
        Minor("Lm", "Modifier letter", UnicodeCategory.ModifierLetter),
        Minor("Lo", "Other letter (no case: CJK, Hebrew, Arabic...)", UnicodeCategory.OtherLetter),

        new("M", "Mark", [UnicodeCategory.NonSpacingMark, UnicodeCategory.SpacingCombiningMark, UnicodeCategory.EnclosingMark]),
        Minor("Mn", "Non-spacing mark (accents)", UnicodeCategory.NonSpacingMark),
        Minor("Mc", "Spacing combining mark", UnicodeCategory.SpacingCombiningMark),
        Minor("Me", "Enclosing mark", UnicodeCategory.EnclosingMark),

        new("N", "Number", [UnicodeCategory.DecimalDigitNumber, UnicodeCategory.LetterNumber, UnicodeCategory.OtherNumber]),
        Minor("Nd", "Decimal digit", UnicodeCategory.DecimalDigitNumber),
        Minor("Nl", "Letter number (Roman numerals)", UnicodeCategory.LetterNumber),
        Minor("No", "Other number (fractions, superscripts)", UnicodeCategory.OtherNumber),

        new("P", "Punctuation", [UnicodeCategory.ConnectorPunctuation, UnicodeCategory.DashPunctuation, UnicodeCategory.OpenPunctuation, UnicodeCategory.ClosePunctuation,
                                  UnicodeCategory.InitialQuotePunctuation, UnicodeCategory.FinalQuotePunctuation, UnicodeCategory.OtherPunctuation]),
        Minor("Pc", "Connector punctuation (_)", UnicodeCategory.ConnectorPunctuation),
        Minor("Pd", "Dash punctuation (-)", UnicodeCategory.DashPunctuation),
        Minor("Ps", "Opening punctuation ( [ {", UnicodeCategory.OpenPunctuation),
        Minor("Pe", "Closing punctuation ) ] }", UnicodeCategory.ClosePunctuation),
        Minor("Pi", "Opening quote", UnicodeCategory.InitialQuotePunctuation),
        Minor("Pf", "Closing quote", UnicodeCategory.FinalQuotePunctuation),
        Minor("Po", "Other punctuation (. , ! ? :)", UnicodeCategory.OtherPunctuation),

        new("S", "Symbol", [UnicodeCategory.MathSymbol, UnicodeCategory.CurrencySymbol, UnicodeCategory.ModifierSymbol, UnicodeCategory.OtherSymbol]),
        Minor("Sm", "Math symbol (+ < =)", UnicodeCategory.MathSymbol),
        Minor("Sc", "Currency symbol ($ €)", UnicodeCategory.CurrencySymbol),
        Minor("Sk", "Modifier symbol (^ `)", UnicodeCategory.ModifierSymbol),
        Minor("So", "Other symbol (© emoji)", UnicodeCategory.OtherSymbol),

        new("Z", "Separator", [UnicodeCategory.SpaceSeparator, UnicodeCategory.LineSeparator, UnicodeCategory.ParagraphSeparator]),
        Minor("Zs", "Space separator", UnicodeCategory.SpaceSeparator),
        Minor("Zl", "Line separator", UnicodeCategory.LineSeparator),
        Minor("Zp", "Paragraph separator", UnicodeCategory.ParagraphSeparator),

        new("C", "Other", [UnicodeCategory.Control, UnicodeCategory.Format, UnicodeCategory.Surrogate, UnicodeCategory.PrivateUse, UnicodeCategory.OtherNotAssigned]),
        Minor("Cc", "Control", UnicodeCategory.Control),
        Minor("Cf", "Format (zero-width joiner...)", UnicodeCategory.Format),
        Minor("Cs", "Surrogate", UnicodeCategory.Surrogate),
        Minor("Co", "Private use", UnicodeCategory.PrivateUse),
        Minor("Cn", "Not assigned", UnicodeCategory.OtherNotAssigned)
    ];

    /// <summary>The class with this code (any case) or <see cref="UnicodeCategory" /> name ("UppercaseLetter"); null when there is none.</summary>
    public static UnicodeClass? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        name = name.Trim();
        var byCode = All.FirstOrDefault(c => string.Equals(c.Code, name, StringComparison.OrdinalIgnoreCase));
        if (byCode is not null) return byCode;

        return Enum.TryParse<UnicodeCategory>(name, true, out var category) && Enum.IsDefined(category)
                   ? All.FirstOrDefault(c => c.Categories.Length == 1 && c.Categories[0] == category)
                   : null;
    }

    /// <summary>Splits "Lu, Nd" into its names.</summary>
    public static IEnumerable<string> Names(string? text)
    {
        return (text ?? string.Empty).Split(new[] { ',', ';', '|', '+', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Reads the classes in <paramref name="text" /> as a test of one character; false (and what is wrong) when there are none or one is unknown.</summary>
    public static bool TryParse(string? text, out Func<char, bool> test, out string? error)
    {
        var categories = new HashSet<UnicodeCategory>();
        test  = _ => false;
        error = null;

        var names = Names(text).ToList();
        if (names.Count == 0)
        {
            error = "name at least one Unicode class (Lu, Ll, Nd, L, P...)";
            return false;
        }

        foreach (string name in names)
        {
            if (Find(name) is not { } found)
            {
                error = $"'{name}' is not a Unicode class (Lu, Ll, Lt, Lm, Lo, Mn, Mc, Me, Nd, Nl, No, Pc, Pd, Ps, Pe, Pi, Pf, Po, Sm, Sc, Sk, So, Zs, Zl, Zp, Cc, Cf, Cs, Co, Cn, or L, M, N, P, S, Z, C)";
                return false;
            }

            foreach (var c in found.Categories) categories.Add(c);
        }

        test = c => categories.Contains(char.GetUnicodeCategory(c));
        return true;
    }
}
