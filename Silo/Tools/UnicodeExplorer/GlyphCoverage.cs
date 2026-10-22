using System.Windows.Media;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>Works out which characters the explorer's fonts can draw, so the ones that would show as an empty box can be hidden.</summary>
internal static class GlyphCoverage
{
    /// <summary>
    ///     Every code point that has a glyph in at least one of the installed fonts named in <paramref name="familyNames" /> (a comma separated list, like
    ///     a WPF <c>FontFamily</c> string). It reads the fonts' character maps, which takes a moment, so call it from a background thread.
    /// </summary>
    public static CodePointSet Build(string familyNames)
    {
        var wanted = familyNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var set    = new CodePointSet();

        foreach (var family in Fonts.SystemFontFamilies)
        {
            try
            {
                if (!family.FamilyNames.Values.Any(wanted.Contains)) continue;

                // the regular face has the characters (bold and italic ones add none); a family without one is read by its first face
                var faces   = family.GetTypefaces().ToList();
                var regular = faces.FirstOrDefault(f => f.Weight == FontWeights.Normal && f.Style == FontStyles.Normal && f.Stretch == FontStretches.Normal) ?? faces.FirstOrDefault();
                if (regular is null || !regular.TryGetGlyphTypeface(out var glyphs)) continue;

                foreach (int cp in glyphs.CharacterToGlyphMap.Keys) set.Add(cp);
            }
            catch (Exception)
            {
                // a font that cannot be read adds nothing
            }
        }

        return set;
    }
}
