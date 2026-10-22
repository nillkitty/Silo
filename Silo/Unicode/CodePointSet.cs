namespace Silo.Unicode;

/// <summary>
///     A set of code points kept as one bit each (17 pages of 65 536 bits is about 140 KB for all of Unicode), for questions asked of many code
///     points quickly: which ones a font can draw.
/// </summary>
public sealed class CodePointSet
{
    private readonly ulong[] _bits = new ulong[(CodePoint.MaxValue + 1 + 63) / 64];

    /// <summary>How many code points are in the set.</summary>
    public int Count { get; private set; }

    /// <summary>Whether the code point is in the set; false for a value that is no code point.</summary>
    public bool Contains(int codePoint)
    {
        return (uint)codePoint <= CodePoint.MaxValue && (_bits[codePoint >> 6] & (1UL << (codePoint & 63))) != 0;
    }

    /// <summary>Puts a code point in the set (a value that is no code point is ignored); true when it was not there yet.</summary>
    public bool Add(int codePoint)
    {
        if ((uint)codePoint > CodePoint.MaxValue) return false;

        ulong mask = 1UL << (codePoint & 63);
        ref ulong word = ref _bits[codePoint >> 6];
        if ((word & mask) != 0) return false;

        word |= mask;
        Count++;
        return true;
    }

    /// <summary>Puts every code point of the list in the set.</summary>
    public void AddRange(IEnumerable<int> codePoints)
    {
        foreach (int cp in codePoints) Add(cp);
    }

    /// <summary>Puts every code point from <paramref name="first" /> to <paramref name="last" /> (both included) in the set.</summary>
    public void AddRange(int first, int last)
    {
        first = Math.Max(0, first);
        last  = Math.Min(CodePoint.MaxValue, last);
        for (int cp = first; cp <= last; cp++) Add(cp);
    }

    /// <summary>Puts every code point of the other set in this one.</summary>
    public void UnionWith(CodePointSet other)
    {
        Count = 0;
        for (int i = 0; i < _bits.Length; i++)
        {
            _bits[i] |= other._bits[i];
            Count    += System.Numerics.BitOperations.PopCount(_bits[i]);
        }
    }
}

/// <summary>Which characters are expected to have no visible shape: spaces, controls and format characters. Not having a glyph is no fault in those.</summary>
public static class GlyphVisibility
{
    /// <summary>
    ///     Whether a character of this general category is invisible by design (<c>Cc</c> controls, <c>Cf</c> format characters such as the zero width
    ///     joiner, and the <c>Zs</c>, <c>Zl</c>, <c>Zp</c> separators): the Unicode Explorer keeps those when it hides characters without a glyph,
    ///     because they are what a person looks for when something invisible is in their text.
    /// </summary>
    public static bool IsInvisibleByDesign(string category) => category is "Cc" or "Cf" or "Zs" or "Zl" or "Zp";

    /// <summary>
    ///     Whether a character should be listed when the person asked to hide the characters that have no glyph.
    /// </summary>
    /// <param name="category">The general category.</param>
    /// <param name="drawable">Whether any display font has a glyph for it.</param>
    /// <param name="keepUnassigned">The list is of the unassigned code points on purpose (a block with the gaps shown).</param>
    public static bool Keep(string category, bool drawable, bool keepUnassigned)
    {
        if (drawable || IsInvisibleByDesign(category)) return true;
        return keepUnassigned && category == "Cn";
    }
}
