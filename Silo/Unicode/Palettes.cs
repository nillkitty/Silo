namespace Silo.Unicode;

/// <summary>
///     A palette: a small window of characters that stays beside whatever the person is working in. Clicking a character puts it in the text they
///     were typing in. Its characters are kept on one or more pages, shown as tabs. Plain data, kept in the Silo.
/// </summary>
/// <remarks>
///     Only settable properties are kept (the helpers below are methods), so the stored JSON holds nothing twice. Palettes kept before pages existed
///     have their characters in <see cref="CodePoints" />; <see cref="PaletteRules.Normalize" /> moves them onto a first page.
/// </remarks>
public sealed class PaletteDefinition
{
    /// <summary>What it is called; unique among the palettes of a Silo.</summary>
    public string Name { get; set; } = "Palette";

    /// <summary>The pages (tabs), in order; never empty once <see cref="PaletteRules.Normalize" /> ran.</summary>
    public List<PalettePage> Pages { get; set; } = [];

    /// <summary>The page shown (an index into <see cref="Pages" />).</summary>
    public int CurrentPage { get; set; }

    /// <summary>The characters of a palette kept before it had pages; empty afterwards.</summary>
    public List<int> CodePoints { get; set; } = [];

    /// <summary>The size of a tile in device-independent pixels (the character is drawn at about half of it).</summary>
    public double TileSize { get; set; } = PaletteRules.DefaultTileSize;

    /// <summary>Whether a character's color mark shows on its tile (in the way the explorer's color mode says).</summary>
    public bool ShowColors { get; set; } = true;

    /// <summary>Whether the window has a button on the taskbar.</summary>
    public bool ShowInTaskbar { get; set; }

    /// <summary>Whether the window stays above the other windows.</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>Whether the window is moved back onto the screen when it would be off it (a monitor that is gone, a changed resolution).</summary>
    public bool KeepOnScreen { get; set; } = true;

    /// <summary>Whether the window opens when the Silo is loaded.</summary>
    public bool ShowOnLoad { get; set; }

    /// <summary>The person's Lock Palette switch. What it locks is chosen in Author mode (<see cref="LockSize" /> and the others).</summary>
    public bool Locked { get; set; }

    /// <summary>A locked palette cannot be resized.</summary>
    public bool LockSize { get; set; } = true;

    /// <summary>A locked palette cannot be moved.</summary>
    public bool LockPosition { get; set; } = true;

    /// <summary>A locked palette's tiles cannot be added, removed or rearranged (they can still be clicked and dragged out).</summary>
    public bool LockTiles { get; set; } = true;

    /// <summary>A locked palette's pages cannot be added, renamed, deleted or rearranged (they can still be switched).</summary>
    public bool LockTabs { get; set; } = true;

    /// <summary>Where the window was last (null: let it start in a default place).</summary>
    public double? Left { get; set; }

    public double? Top { get; set; }

    public double? Width { get; set; }

    public double? Height { get; set; }

    // ---- helpers (methods, so they are not stored) ----------------------------------------------------------------

    /// <summary>The page shown.</summary>
    public PalettePage Page()
    {
        if (Pages.Count == 0) PaletteRules.Normalize(this);
        return Pages[Math.Clamp(CurrentPage, 0, Pages.Count - 1)];
    }

    /// <summary>Every character on every page (a character on two pages is listed twice).</summary>
    public IEnumerable<int> AllCodePoints() => Pages.SelectMany(p => p.CodePoints);

    public bool SizeIsLocked()     => Locked && LockSize;
    public bool PositionIsLocked() => Locked && LockPosition;
    public bool TilesAreLocked()   => Locked && LockTiles;
    public bool TabsAreLocked()    => Locked && LockTabs;
}

/// <summary>One page (tab) of a palette.</summary>
public sealed class PalettePage
{
    public string Name { get; set; } = "Glyphs";

    /// <summary>The characters, in order; each at most once on a page.</summary>
    public List<int> CodePoints { get; set; } = [];
}

/// <summary>The rules for naming palettes, arranging their characters and keeping their windows where they can be reached.</summary>
public static class PaletteRules
{
    public const double DefaultTileSize = 40;
    public const double MinTileSize     = 24;
    public const double MaxTileSize     = 120;

    /// <summary>
    ///     Makes a palette read from storage whole: a first page for characters kept before pages existed (or an empty one), a current page that
    ///     exists, a tile size within limits, no missing lists.
    /// </summary>
    public static void Normalize(PaletteDefinition palette)
    {
        palette.Name       =   string.IsNullOrWhiteSpace(palette.Name) ? "Palette" : palette.Name;
        palette.Pages      ??= [];
        palette.CodePoints ??= [];
        palette.Pages.RemoveAll(p => p is null);
        foreach (var page in palette.Pages)
        {
            page.CodePoints ??= [];
            page.Name       =   string.IsNullOrWhiteSpace(page.Name) ? "Glyphs" : page.Name;
        }

        if (palette.Pages.Count == 0)
        {
            palette.Pages.Add(new PalettePage { Name = "Glyphs", CodePoints = palette.CodePoints.Distinct().ToList() });
            palette.CodePoints = [];
        }

        palette.CurrentPage = Math.Clamp(palette.CurrentPage, 0, palette.Pages.Count - 1);
        palette.TileSize    = double.IsFinite(palette.TileSize) && palette.TileSize > 0 ? Math.Clamp(palette.TileSize, MinTileSize, MaxTileSize) : DefaultTileSize;
    }

    /// <summary>The name asked for, or that with a number after it when a palette has it already (<c>Arrows</c>, <c>Arrows 2</c>); never empty.</summary>
    public static string UniqueName(IEnumerable<string> existing, string? wanted, string fallback = "Palette")
    {
        string baseName = string.IsNullOrWhiteSpace(wanted) ? fallback : wanted.Trim();
        var    taken    = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;

        for (int n = 2;; n++)
        {
            string candidate = $"{baseName} {n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>
    ///     Moves the items at <paramref name="indexes" /> so that they sit, in their order, where <paramref name="insertAt" /> was (an index into the
    ///     list as it was before the move). Returns the index the first moved item ends up at; indexes out of range are ignored.
    /// </summary>
    public static int Move<T>(List<T> list, IEnumerable<int> indexes, int insertAt)
    {
        var picked = indexes.Where(i => i >= 0 && i < list.Count).Distinct().OrderBy(i => i).ToList();
        if (picked.Count == 0) return -1;

        insertAt = Math.Clamp(insertAt, 0, list.Count);
        var items = picked.Select(i => list[i]).ToList();
        int before = picked.Count(i => i < insertAt);
        foreach (int i in picked.OrderByDescending(i => i)) list.RemoveAt(i);

        int at = Math.Clamp(insertAt - before, 0, list.Count);
        list.InsertRange(at, items);
        return at;
    }

    /// <summary>
    ///     Inserts the characters at <paramref name="insertAt" />, leaving out the ones already in the list (a page holds each character once).
    ///     Returns how many were added.
    /// </summary>
    public static int InsertNew(List<int> list, IEnumerable<int> codePoints, int insertAt)
    {
        var have  = list.ToHashSet();
        var fresh = codePoints.Where(have.Add).ToList();
        list.InsertRange(Math.Clamp(insertAt, 0, list.Count), fresh);
        return fresh.Count;
    }

    /// <summary>
    ///     Where a window of the size should be so that it can be reached: when its top-left part is off the screen area it is moved in, and a window
    ///     bigger than the area is put at the area's corner.
    /// </summary>
    public static (double Left, double Top) KeepOnScreen(double left, double top, double width, double height, double areaLeft, double areaTop, double areaWidth, double areaHeight)
    {
        double maxLeft = areaLeft + areaWidth - width;
        double maxTop  = areaTop + areaHeight - height;
        double l = Math.Max(areaLeft, Math.Min(left, Math.Max(areaLeft, maxLeft)));
        double t = Math.Max(areaTop, Math.Min(top, Math.Max(areaTop, maxTop)));
        return (l, t);
    }

    /// <summary>The window size that fits a number of characters in cells of <paramref name="cell" />, about as wide as it is tall, within limits.</summary>
    public static (double Width, double Height) SuggestSize(int count, double cell)
    {
        int columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(Math.Max(1, count))), 4, 12);
        int rows    = Math.Max(1, (int)Math.Ceiling(count / (double)columns));
        return (columns * cell + 24, Math.Min(rows, 10) * cell + 48);
    }

    /// <summary>The columns and rows of tiles that fit a length closest to <paramref name="available" /> (at least one).</summary>
    public static int Fit(double available, double pitch) => Math.Max(1, (int)Math.Round(available / pitch));
}
