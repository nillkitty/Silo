using System.Globalization;

namespace Silo.Unicode;

/// <summary>
///     The Unicode Explorer's own choices, kept together in the Silo: which blocks are switched off, how marks are colored, the list or grid, which
///     panes are shown. Plain data (it is written as JSON); anything not in an older copy has its starting value.
/// </summary>
public sealed class ExplorerSettings
{
    /// <summary>The person's changes to which blocks are listed (see <see cref="BlockPolicy" />).</summary>
    public BlockOverrides Blocks { get; set; } = new();

    /// <summary>How mark colors are shown.</summary>
    public ColorMode ColorMode { get; set; } = ColorMode.Glyph;

    /// <summary>Which characters are listed (Glyph Filter, By Kind).</summary>
    public GlyphKind Kind { get; set; } = GlyphKind.AvailableOnly;

    /// <summary>Whether the results are a list rather than a grid.</summary>
    public bool ListView { get; set; }

    /// <summary>The four-letter code of the script "Default script" stands for; null: the one that fits the display language.</summary>
    public string? DefaultScript { get; set; }

    /// <summary>Whether a click in the palette window types the character into the window that had the focus (otherwise it only copies it).</summary>
    public bool PaletteTypes { get; set; } = true;

    /// <summary>Whether the code point list (the results) is shown.</summary>
    public bool ShowCodepoints { get; set; } = true;

    /// <summary>Whether the details of the character are shown.</summary>
    public bool ShowDetails { get; set; } = true;

    /// <summary>Whether the collection tab is shown.</summary>
    public bool ShowCollection { get; set; } = true;

    /// <summary>Whether the generators tab is shown.</summary>
    public bool ShowGenerators { get; set; } = true;

    /// <summary>Whether the saved tab is shown.</summary>
    public bool ShowSaved { get; set; } = true;

    /// <summary>The page setup used for printing and PDF.</summary>
    public PageSetup Page { get; set; } = new();

    /// <summary>A copy that can be changed without changing this one.</summary>
    public ExplorerSettings Clone()
    {
        return new ExplorerSettings
               {
                   Blocks         = new BlockOverrides { ForceOn = [.. Blocks.ForceOn], ForceOff = [.. Blocks.ForceOff] },
                   ColorMode      = ColorMode,
                   Kind           = Kind,
                   ListView       = ListView,
                   DefaultScript  = DefaultScript,
                   PaletteTypes   = PaletteTypes,
                   ShowCodepoints = ShowCodepoints,
                   ShowDetails    = ShowDetails,
                   ShowCollection = ShowCollection,
                   ShowGenerators = ShowGenerators,
                   ShowSaved      = ShowSaved,
                   Page           = Page.Clone()
               };
    }
}

/// <summary>How a printed or PDF collection is laid out.</summary>
public sealed class PageSetup
{
    /// <summary>Landscape rather than portrait.</summary>
    public bool Landscape { get; set; }

    /// <summary>The margin on every side, in hundredths of an inch.</summary>
    public int MarginHundredthsInch { get; set; } = 50;

    /// <summary>The size of the characters, in points.</summary>
    public double GlyphSize { get; set; } = 28;

    /// <summary>Whether each character has its code point (and name) under it.</summary>
    public bool Captions { get; set; } = true;

    /// <summary>Whether each character's mark color is shown.</summary>
    public bool Colors { get; set; } = true;

    /// <summary>A copy that can be changed without changing this one.</summary>
    public PageSetup Clone() => (PageSetup)MemberwiseClone();

    /// <summary>Puts the values in their allowed ranges.</summary>
    public PageSetup Normalized()
    {
        return new PageSetup
               {
                   Landscape            = Landscape,
                   MarginHundredthsInch = Math.Clamp(MarginHundredthsInch, 0, 200),
                   GlyphSize            = Math.Clamp(double.IsNaN(GlyphSize) ? 28 : GlyphSize, 8, 144),
                   Captions             = Captions,
                   Colors               = Colors
               };
    }
}

/// <summary>Which script "Default script" means: the one a person who reads the display language most likely looks for.</summary>
public static class ScriptDefaults
{
    private static readonly Dictionary<string, string> ByLanguage = new(StringComparer.OrdinalIgnoreCase)
                                                                    {
                                                                        ["ru"] = "Cyrl", ["uk"] = "Cyrl", ["bg"] = "Cyrl", ["be"] = "Cyrl", ["mk"] = "Cyrl", ["sr"] = "Cyrl", ["mn"] = "Cyrl",
                                                                        ["kk"] = "Cyrl", ["el"] = "Grek", ["ar"] = "Arab", ["fa"] = "Arab", ["ur"] = "Arab", ["ps"] = "Arab", ["he"] = "Hebr",
                                                                        ["yi"] = "Hebr", ["hi"] = "Deva", ["mr"] = "Deva", ["ne"] = "Deva", ["sa"] = "Deva", ["bn"] = "Beng", ["as"] = "Beng",
                                                                        ["gu"] = "Gujr", ["pa"] = "Guru", ["ta"] = "Taml", ["te"] = "Telu", ["kn"] = "Knda", ["ml"] = "Mlym", ["si"] = "Sinh",
                                                                        ["th"] = "Thai", ["lo"] = "Laoo", ["km"] = "Khmr", ["my"] = "Mymr", ["bo"] = "Tibt", ["ka"] = "Geor", ["hy"] = "Armn",
                                                                        ["am"] = "Ethi", ["ti"] = "Ethi", ["ja"] = "Hira", ["ko"] = "Hang", ["zh"] = "Hani"
                                                                    };

    /// <summary>The script of a language (a two-letter code such as <c>ru</c>); Latin when it has none listed.</summary>
    public static string ForLanguage(string? twoLetterLanguage)
    {
        return twoLetterLanguage is not null && ByLanguage.TryGetValue(twoLetterLanguage, out string? script) ? script : "Latn";
    }

    /// <summary>The script of a culture's language.</summary>
    public static string ForCulture(CultureInfo? culture) => ForLanguage(culture?.TwoLetterISOLanguageName);
}
