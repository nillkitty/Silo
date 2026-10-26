namespace Silo.Drawio.Extensions;

// Built on the host side and sent to the Silo plugin (silo-plugin.js), which adds it to draw.io's shape sidebar.

/// <summary>A shape in a plugin-supplied sidebar palette.</summary>
/// <param name="Style">The style of a single-cell entry (also used for the sidebar preview when <paramref name="Xml" /> is given).</param>
/// <param name="Xml">
///     For an entry of several cells (a container with children, cells with custom attributes): an <c>mxGraphModel</c> whose
///     cells (below the layer, id 1) are inserted as they are. Null for a single cell made from <paramref name="Style" />.
/// </param>
public sealed record DrawioPaletteEntry(string Style, double Width, double Height, string? Value = null, string? Title = null, string? Xml = null);

/// <summary>A sidebar section ("palette") the plugin adds next to draw.io's own shape libraries.</summary>
public sealed record DrawioPluginPalette(string Id, string Title, IReadOnlyList<DrawioPaletteEntry> Entries, bool Expanded = true)
{
    /// <summary>Merges palettes that share an id into one (entries in order, the first one's title), keeping the first-seen order.</summary>
    public static List<DrawioPluginPalette> Merge(IEnumerable<DrawioPluginPalette> palettes)
    {
        var merged = new List<DrawioPluginPalette>();
        var index  = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in palettes)
            if (index.TryGetValue(p.Id, out int at))
                merged[at] = merged[at] with { Entries = [..merged[at].Entries, ..p.Entries] };
            else
            {
                index[p.Id] = merged.Count;
                merged.Add(p);
            }

        return merged;
    }
}
