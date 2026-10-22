namespace Silo.Unicode;

/// <summary>
///     A palette: a small window of characters that stays beside whatever the person is working in. Clicking a character puts it in the text they
///     were typing in. Plain data, kept in the Silo.
/// </summary>
public sealed class PaletteDefinition
{
    /// <summary>What it is called; unique among the palettes of a Silo.</summary>
    public string Name { get; set; } = "Palette";

    /// <summary>The characters, in order.</summary>
    public List<int> CodePoints { get; set; } = [];

    /// <summary>Whether the window stays above the other windows.</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>Whether the window is moved back onto the screen when it would be off it (a monitor that is gone, a changed resolution).</summary>
    public bool KeepOnScreen { get; set; } = true;

    /// <summary>Whether the window opens when the Silo is loaded.</summary>
    public bool ShowOnLoad { get; set; }

    /// <summary>Where the window was last (null: let it start in a default place).</summary>
    public double? Left { get; set; }

    public double? Top { get; set; }

    public double? Width { get; set; }

    public double? Height { get; set; }
}

/// <summary>The rules for naming palettes and keeping their windows where they can be reached.</summary>
public static class PaletteRules
{
    /// <summary>The name asked for, or that with a number after it when a palette has it already (<c>Arrows</c>, <c>Arrows 2</c>); never empty.</summary>
    public static string UniqueName(IEnumerable<string> existing, string? wanted)
    {
        string baseName = string.IsNullOrWhiteSpace(wanted) ? "Palette" : wanted.Trim();
        var    taken    = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;

        for (int n = 2;; n++)
        {
            string candidate = $"{baseName} {n}";
            if (!taken.Contains(candidate)) return candidate;
        }
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
}
