namespace Silo.Unicode;

/// <summary>One of the colors a character can be marked with.</summary>
/// <param name="Index">Its place in the palette (what is stored).</param>
/// <param name="Name">What it is called.</param>
/// <param name="Hex">Its value as <c>#RRGGBB</c>.</param>
public sealed record CharacterColor(int Index, string Name, string Hex);

/// <summary>The palette a character can be marked from: sixteen colors that are easy to tell apart on a dark and on a light background.</summary>
public static class CharacterColors
{
    /// <summary>The sixteen colors, in the order they are shown.</summary>
    public static IReadOnlyList<CharacterColor> Palette { get; } = Make(
    [
        ("Red", "#E53935"), ("Orange", "#FB8C00"), ("Amber", "#FFB300"), ("Yellow", "#FDD835"),
        ("Lime", "#9CCC65"), ("Green", "#43A047"), ("Teal", "#00897B"), ("Cyan", "#00ACC1"),
        ("Sky", "#29B6F6"), ("Blue", "#1E88E5"), ("Indigo", "#5C6BC0"), ("Purple", "#8E24AA"),
        ("Magenta", "#D81B60"), ("Pink", "#F48FB1"), ("Brown", "#8D6E63"), ("Gray", "#9E9E9E")
    ]);

    private static IReadOnlyList<CharacterColor> Make((string Name, string Hex)[] colors)
    {
        return colors.Select((c, i) => new CharacterColor(i, c.Name, c.Hex)).ToList();
    }

    /// <summary>The color at this place in the palette; null for none (or a place that is not in it).</summary>
    public static CharacterColor? At(int? index) => index is { } i && i >= 0 && i < Palette.Count ? Palette[i] : null;

    /// <summary>The color with this name, any case.</summary>
    public static CharacterColor? Named(string? name) => name is null ? null : Palette.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
///     The colors the person marked characters with: a palette index per code point. Plain data kept in the Silo as a dictionary; a place
///     outside the palette (from a file of another version) is dropped when it is read.
/// </summary>
public sealed class CharacterColorMap
{
    private readonly Dictionary<int, int> _marks = [];

    /// <summary>How many characters are marked.</summary>
    public int Count => _marks.Count;

    /// <summary>The palette index of the color a character is marked with; null when it is not marked.</summary>
    public int? Get(int codePoint) => _marks.TryGetValue(codePoint, out int index) ? index : null;

    /// <summary>The color a character is marked with; null when it is not marked.</summary>
    public CharacterColor? ColorOf(int codePoint) => CharacterColors.At(Get(codePoint));

    /// <summary>Marks a character with a palette color, or (null) takes the mark off. A place that is not in the palette is the same as null.</summary>
    /// <returns>Whether anything changed.</returns>
    public bool Set(int codePoint, int? index)
    {
        if (index is not { } i || i < 0 || i >= CharacterColors.Palette.Count) return _marks.Remove(codePoint);
        if (_marks.TryGetValue(codePoint, out int old) && old == i) return false;

        _marks[codePoint] = i;
        return true;
    }

    /// <summary>Marks several characters; true when anything changed.</summary>
    public bool Set(IEnumerable<int> codePoints, int? index)
    {
        bool changed = false;
        foreach (int cp in codePoints) changed |= Set(cp, index);
        return changed;
    }

    /// <summary>The marks as a plain dictionary (what is kept).</summary>
    public Dictionary<int, int> ToDictionary() => new(_marks);

    /// <summary>The marks read back from what was kept; entries with a place outside the palette are left out.</summary>
    public static CharacterColorMap From(IEnumerable<KeyValuePair<int, int>>? kept)
    {
        var map = new CharacterColorMap();
        if (kept is null) return map;

        foreach (var (cp, index) in kept)
            if ((uint)cp <= CodePoint.MaxValue)
                map.Set(cp, index);
        return map;
    }

    /// <summary>
    ///     Whether a character passes a color filter: <paramref name="filter" /> null is no filter, -1 is "any color", a palette index is that color,
    ///     -2 is "no color".
    /// </summary>
    public bool Passes(int codePoint, int? filter)
    {
        return filter switch
               {
                   null => true,
                   -1   => _marks.ContainsKey(codePoint),
                   -2   => !_marks.ContainsKey(codePoint),
                   { } i => _marks.TryGetValue(codePoint, out int mine) && mine == i
               };
    }
}
