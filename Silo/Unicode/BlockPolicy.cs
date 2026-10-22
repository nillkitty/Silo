using System.Text.RegularExpressions;

namespace Silo.Unicode;

/// <summary>
///     Which blocks are listed. The big ideograph blocks (Chinese, Japanese and Korean characters, Yi, Tangut, Nushu, Khitan) hold tens of thousands of
///     characters most people never look through, so they are switched off until the person asks for them. The person's own choices are kept as
///     <em>differences from that default</em> (<see cref="BlockOverrides" />), so a block the default changes later does not need the choices redone.
/// </summary>
public static class BlockPolicy
{
    /// <summary>Whether the block is switched off before the person has chosen anything: the ideograph, Hangul-syllable, Yi, Tangut, Nushu and Khitan blocks.</summary>
    /// <remarks>Kana, Bopomofo, the Hangul jamo and CJK punctuation and symbols stay on: they are small and are things people look for.</remarks>
    public static bool IsOffByDefault(string blockName)
    {
        if (string.IsNullOrEmpty(blockName)) return false;

        return blockName.StartsWith("CJK Unified Ideographs", StringComparison.OrdinalIgnoreCase)
               || blockName.StartsWith("CJK Compatibility Ideographs", StringComparison.OrdinalIgnoreCase)
               || blockName.Equals("CJK Radicals Supplement", StringComparison.OrdinalIgnoreCase)
               || blockName.Equals("Kangxi Radicals", StringComparison.OrdinalIgnoreCase)
               || blockName.Equals("CJK Strokes", StringComparison.OrdinalIgnoreCase)
               || blockName.Equals("Hangul Syllables", StringComparison.OrdinalIgnoreCase)
               || blockName.StartsWith("Yi ", StringComparison.OrdinalIgnoreCase)
               || blockName.StartsWith("Tangut", StringComparison.OrdinalIgnoreCase)
               || blockName.Equals("Nushu", StringComparison.OrdinalIgnoreCase)
               || blockName.Equals("Khitan Small Script", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the block is listed, given the person's choices.</summary>
    public static bool IsEnabled(string blockName, BlockOverrides? overrides)
    {
        if (overrides is not null)
        {
            if (overrides.ForceOff.Contains(blockName, StringComparer.OrdinalIgnoreCase)) return false;
            if (overrides.ForceOn.Contains(blockName, StringComparer.OrdinalIgnoreCase)) return true;
        }

        return !IsOffByDefault(blockName);
    }

    /// <summary>The names of the blocks that are switched off.</summary>
    public static IReadOnlyList<string> DisabledNames(IEnumerable<UnicodeBlock> blocks, BlockOverrides? overrides)
    {
        return blocks.Where(b => !IsEnabled(b.Name, overrides)).Select(b => b.Name).ToList();
    }

    /// <summary>The code points of the blocks that are switched off, as a set that answers "is this one hidden" quickly.</summary>
    public static CodePointSet DisabledCodePoints(IEnumerable<UnicodeBlock> blocks, BlockOverrides? overrides)
    {
        var set = new CodePointSet();
        foreach (var b in blocks)
            if (!IsEnabled(b.Name, overrides))
                set.AddRange(b.Start, b.End);

        return set;
    }

    /// <summary>
    ///     Makes the choices that leave exactly <paramref name="enabled" /> switched on: only the differences from the default are kept.
    /// </summary>
    public static BlockOverrides FromEnabled(IEnumerable<UnicodeBlock> blocks, Func<string, bool> enabled)
    {
        var on  = new List<string>();
        var off = new List<string>();
        foreach (var b in blocks)
        {
            bool wanted  = enabled(b.Name);
            bool defaulted = !IsOffByDefault(b.Name);
            if (wanted && !defaulted) on.Add(b.Name);
            else if (!wanted && defaulted) off.Add(b.Name);
        }

        return new BlockOverrides { ForceOn = on, ForceOff = off };
    }

    private static readonly Regex AsksForSpecific = new(@"(^|\s)(block:|blk:|plane:)|U\+|\\u|0x[0-9A-Fa-f]|&#|[^\u0000-\u007F]", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    ///     Whether the search text asks for something particular enough that blocks switched off should not get in the way: a block or plane, a code
    ///     point or range, or a character that is not ASCII (looking up 漢 means the ideographs are wanted).
    /// </summary>
    public static bool TextAsksForHiddenBlocks(string? text)
    {
        return !string.IsNullOrWhiteSpace(text) && AsksForSpecific.IsMatch(text);
    }
}

/// <summary>The person's changes to which blocks are listed, relative to the default (see <see cref="BlockPolicy" />). Plain data kept in the Silo.</summary>
public sealed class BlockOverrides
{
    /// <summary>Blocks switched on that the default has off.</summary>
    public List<string> ForceOn { get; set; } = [];

    /// <summary>Blocks switched off that the default has on.</summary>
    public List<string> ForceOff { get; set; } = [];

    /// <summary>Whether this is the default (no changes).</summary>
    public bool IsDefault => ForceOn.Count == 0 && ForceOff.Count == 0;
}
