namespace Silo.Unicode;

/// <summary>Which characters the Glyph Filter menu lists.</summary>
public enum GlyphKind
{
    /// <summary>Every character, whether or not a font can draw it. With a block chosen, its unassigned code points too.</summary>
    Everything,

    /// <summary>Every assigned character, whether or not a font can draw it.</summary>
    AssignedOnly,

    /// <summary>Assigned characters that a font on this computer can draw (invisible ones such as spaces and controls stay: they are meant to be invisible).</summary>
    AvailableOnly,

    /// <summary>Available characters that show something: no controls, spaces or format characters.</summary>
    PrintableOnly,

    /// <summary>Available characters that have the Emoji property.</summary>
    EmojiOnly,

    /// <summary>Available characters that are not emoji.</summary>
    NonEmojiOnly,

    /// <summary>
    ///     The characters involved in zero-width joiner sequences: the joiner (U+200D) and non-joiner (U+200C) themselves and the pictographs a joiner
    ///     can join (Extended_Pictographic, the property the text segmentation rules use to decide where a joiner ties characters together).
    /// </summary>
    JoinableOnly
}

/// <summary>How a Unicode version is used in the filter.</summary>
public enum VersionMode
{
    Off,
    Include,
    Exclude
}

/// <summary>How the characters' version is compared with the one chosen.</summary>
public enum VersionOp
{
    /// <summary>Added before that version.</summary>
    Before,

    /// <summary>Added in that version or earlier.</summary>
    Through,

    /// <summary>Added in that version or later.</summary>
    Starting,

    /// <summary>Added after that version.</summary>
    After
}

/// <summary>A condition on the Unicode version that added a character: kept (<see cref="VersionMode.Include" />), left out (<see cref="VersionMode.Exclude" />) or not used.</summary>
public sealed record VersionFilter(VersionMode Mode, VersionOp Op, string? Version)
{
    /// <summary>No condition.</summary>
    public static VersionFilter Off { get; } = new(VersionMode.Off, VersionOp.Starting, null);

    /// <summary>Whether the condition is in use.</summary>
    public bool IsActive => Mode != VersionMode.Off && !string.IsNullOrEmpty(Version);

    /// <summary>The comparison as <see cref="UnicodeQuery.Age" /> writes it (<c>&gt;=6.0</c>); null when not in use.</summary>
    public string? Condition => !IsActive
                                    ? null
                                    : Op switch
                                    {
                                        VersionOp.Before   => "<" + Version,
                                        VersionOp.Through  => "<=" + Version,
                                        VersionOp.Starting => ">=" + Version,
                                        _                  => ">" + Version
                                    };

    /// <summary>Whether a character that was added in <paramref name="age" /> (<c>6.0</c>; empty for none) satisfies the comparison.</summary>
    public bool Satisfies(string? age)
    {
        if (Condition is not { } text || !AgeCondition.TryParse(text, out var condition)) return true;
        return condition.Matches(UnicodeDatabase.AgeKey(age));
    }

    /// <summary>Whether a character with this age stays in the list: with Include it has to satisfy the comparison, with Exclude it must not.</summary>
    public bool Keeps(string? age)
    {
        return Mode switch
               {
                   VersionMode.Include => Satisfies(age),
                   VersionMode.Exclude => !IsActive || !Satisfies(age),
                   _                   => true
               };
    }

    /// <summary>The menu words: <c>Before 6.0</c>.</summary>
    public override string ToString() => IsActive ? $"{Mode} · {Op} {Version}" : "Off";
}

/// <summary>
///     What the filter menus have chosen: the category, block, script, kind of glyph, version and color. The explorer builds its search from this
///     instead of from controls, so the menus only change values.
/// </summary>
public sealed class FilterState
{
    /// <summary>A two-letter category (<c>Lu</c>) or a one-letter group (<c>L</c>); null for any.</summary>
    public string? Category { get; set; }

    /// <summary>The name of a block; null for any.</summary>
    public string? Block { get; set; }

    /// <summary>The four-letter code of a script; null for any.</summary>
    public string? Script { get; set; }

    /// <summary>Which characters are listed.</summary>
    public GlyphKind Kind { get; set; } = GlyphKind.AvailableOnly;

    /// <summary>The condition on the Unicode version.</summary>
    public VersionFilter Version { get; set; } = VersionFilter.Off;

    /// <summary>The color filter: null for none, -1 any mark, -2 no mark, else a palette index.</summary>
    public int? ColorFilter { get; set; }

    /// <summary>Whether nothing is narrowed (the Kind may be any: it is a view of the list, not a narrowing the person has to undo).</summary>
    public bool IsNarrowed => Category is not null || Block is not null || Script is not null || Version.IsActive || ColorFilter is not null || Kind != GlyphKind.AvailableOnly;

    /// <summary>Puts everything back as it starts.</summary>
    public void Reset()
    {
        Category    = Block = Script = null;
        Kind        = GlyphKind.AvailableOnly;
        Version     = VersionFilter.Off;
        ColorFilter = null;
    }

    /// <summary>The query for the database: the text with the category, block, script, emoji and "added in" choices.</summary>
    public UnicodeQuery ToQuery(string text)
    {
        return new UnicodeQuery
               {
                   Text              = text,
                   Categories        = Category is null ? [] : [Category],
                   Block             = Block,
                   Script            = Script,
                   Age               = Version.Mode == VersionMode.Include ? Version.Condition : null,
                   EmojiOnly         = Kind == GlyphKind.EmojiOnly,
                   // a block can be browsed with its gaps; without one there are far too many unassigned code points for it to mean anything
                   IncludeUnassigned = Kind == GlyphKind.Everything && Block is not null
               };
    }

    /// <summary>Whether the blocks that are switched off should be listed anyway: a block or script was chosen on purpose, or the text names something particular.</summary>
    public bool OverridesBlockPolicy(string? text) => Block is not null || Script is not null || BlockPolicy.TextAsksForHiddenBlocks(text);
}

/// <summary>What <see cref="ResultFilter.Apply" /> found.</summary>
/// <param name="Kept">The code points that stay in the list, in the order they were given.</param>
/// <param name="NoGlyph">How many were left out because no font could draw them.</param>
/// <param name="BlocksOff">How many were left out because their block is switched off.</param>
public readonly record struct FilterOutcome(int[] Kept, int NoGlyph, int BlocksOff);

/// <summary>
///     The part of the filtering that is done on the found code points rather than inside the search: blocks switched off, colors, versions to
///     leave out and the kind of glyph. It has no controls in it, so it can run on a worker thread.
/// </summary>
public sealed class ResultFilter
{
    /// <summary>Which characters are listed.</summary>
    public GlyphKind Kind { get; init; } = GlyphKind.AvailableOnly;

    /// <summary>What the display fonts can draw; null when it is not known (then no character counts as lacking a glyph).</summary>
    public CodePointSet? Coverage { get; init; }

    /// <summary>The version condition; only <see cref="VersionMode.Exclude" /> is applied here (including is done by the search).</summary>
    public VersionFilter Version { get; init; } = VersionFilter.Off;

    /// <summary>The code points of the blocks that are switched off; null to list them all.</summary>
    public CodePointSet? DisabledBlocks { get; init; }

    /// <summary>The colors marked on characters, for the color filter.</summary>
    public CharacterColorMap? Colors { get; init; }

    /// <summary>The color filter (see <see cref="FilterState.ColorFilter" />).</summary>
    public int? ColorFilter { get; init; }

    /// <summary>The list is of the unassigned code points on purpose (a block shown with its gaps).</summary>
    public bool KeepUnassigned { get; init; }

    /// <summary>Whether the kind needs a font to be able to draw the character.</summary>
    public static bool NeedsGlyph(GlyphKind kind) => kind is not (GlyphKind.Everything or GlyphKind.AssignedOnly);

    /// <summary>Whether the filter changes nothing.</summary>
    public bool IsPassThrough => DisabledBlocks is null && (Colors is null || ColorFilter is null) && Version.Mode != VersionMode.Exclude && Kind is GlyphKind.Everything or GlyphKind.AssignedOnly;

    /// <summary>Keeps the code points that pass.</summary>
    public FilterOutcome Apply(UnicodeDatabase db, int[] all, CancellationToken cancel = default)
    {
        if (IsPassThrough) return new FilterOutcome(all, 0, 0);

        var keep    = new List<int>(all.Length);
        int noGlyph = 0, blocksOff = 0, tick = 0;
        bool exclude = Version.Mode == VersionMode.Exclude && Version.IsActive;

        foreach (int cp in all)
        {
            if ((++tick & 0x3FFF) == 0) cancel.ThrowIfCancellationRequested();

            if (DisabledBlocks is not null && DisabledBlocks.Contains(cp))
            {
                blocksOff++;
                continue;
            }

            if (Colors is not null && ColorFilter is not null && !Colors.Passes(cp, ColorFilter)) continue;

            var info = db.Get(cp);
            if (exclude && !Version.Keeps(info.Age)) continue;

            if (!PassesKind(info, out bool lackedGlyph))
            {
                if (lackedGlyph) noGlyph++;
                continue;
            }

            keep.Add(cp);
        }

        return new FilterOutcome(keep.ToArray(), noGlyph, blocksOff);
    }

    private bool PassesKind(UnicodeCharacter info, out bool lackedGlyph)
    {
        lackedGlyph = false;
        switch (Kind)
        {
            case GlyphKind.Everything:
                return true;
            case GlyphKind.AssignedOnly:
                return info.IsAssigned || KeepUnassigned;
        }

        if (Coverage is { } coverage && !GlyphVisibility.Keep(info.Category, coverage.Contains(info.Value), KeepUnassigned))
        {
            lackedGlyph = true;
            return false;
        }

        return Kind switch
               {
                   GlyphKind.PrintableOnly => IsPrintable(info.Category),
                   GlyphKind.EmojiOnly     => info.IsEmoji,
                   GlyphKind.NonEmojiOnly  => !info.IsEmoji,
                   GlyphKind.JoinableOnly  => IsJoinable(info),
                   _                       => true
               };
    }

    /// <summary>Whether a character of this category shows something when drawn (not a control, space, format character or unassigned code point).</summary>
    public static bool IsPrintable(string category) => !GlyphVisibility.IsInvisibleByDesign(category) && category is not ("Cn" or "Co" or "Cs");

    /// <summary>Whether the character takes part in zero-width joiner sequences (see <see cref="GlyphKind.JoinableOnly" />).</summary>
    public static bool IsJoinable(UnicodeCharacter info) => info.Value is 0x200C or 0x200D || info.IsExtendedPictographic;
}
