using System.Linq;
using Silo.Drawio.Live;

namespace Silo.Drawio.Mx;

/// <summary>
///     The edits that turn one generated layer into another, so an <see cref="IIncrementalDiagramSource" /> can simply build its
///     whole model again and report the difference: new cells are added, gone ones removed, moved or resized nodes and changed
///     labels, styles and attributes are updated in place, and a changed connector is removed and added again.
/// </summary>
/// <remarks>
///     Some changes cannot be made in place: a node that changes container (<see cref="DiagramNode.Parent" />) or fold
///     (<see cref="DiagramNode.Fold" />), a style entry without a value (<c>ellipse;</c>) that appears or disappears, or an
///     attribute that disappears. <see cref="Between" /> then returns null and the source should rebuild the layer instead
///     (<see cref="IDiagramContentSource.Changed" />).
/// </remarks>
public static class DiagramDiff
{
    /// <summary>The edits from <paramref name="before" /> to <paramref name="after" />; empty when nothing changed, null when only a rebuild will do.</summary>
    public static List<DiagramEdit>? Between(DiagramModel before, DiagramModel after)
    {
        var oldNodes = before.Nodes.ToDictionary(n => n.Id);
        var newNodes = after.Nodes.ToDictionary(n => n.Id);
        var oldEdges = before.Edges.ToDictionary(e => e.Id);
        var newEdges = after.Edges.ToDictionary(e => e.Id);

        var adds        = new List<DiagramEdit>();
        var updates     = new List<DiagramEdit>();
        var nodeRemoves = new List<DiagramEdit>();
        var edgeRemoves = new List<DiagramEdit>();
        var edgeAdds    = new List<DiagramEdit>();

        // in the new model's order, so a container is added before what it holds
        foreach (var node in after.Nodes)
        {
            if (!oldNodes.TryGetValue(node.Id, out var old))
            {
                adds.Add(new DiagramEdit.AddNode(node));
                continue;
            }

            if (old.Parent != node.Parent || old.Fold != node.Fold) return null;

            var style = StyleChanges(old.Style, node.Style, out bool styleOk);
            if (!styleOk) return null;

            var attributes = AttributeChanges(old.Data, node.Data, out bool attributesOk);
            if (!attributesOk) return null;

            var bounds = (old.X, old.Y, old.Width, old.Height) == (node.X, node.Y, node.Width, node.Height)
                             ? null
                             : new DiagramBounds(node.X, node.Y, node.Width, node.Height);
            string? label = old.Label == node.Label ? null : node.Label;

            if (style is not null || attributes is not null || bounds is not null || label is not null)
                updates.Add(new DiagramEdit.Update(node.Id, label, style, attributes, bounds));
        }

        foreach (var old in before.Nodes)
            if (!newNodes.ContainsKey(old.Id))
                nodeRemoves.Add(new DiagramEdit.Remove(old.Id));

        foreach (var old in before.Edges)
            if (!newEdges.TryGetValue(old.Id, out var edge) || !SameEdge(old, edge))
                edgeRemoves.Add(new DiagramEdit.Remove(old.Id));

        foreach (var edge in after.Edges)
            if (!oldEdges.TryGetValue(edge.Id, out var old) || !SameEdge(old, edge))
                edgeAdds.Add(new DiagramEdit.AddEdge(edge));

        return [..edgeRemoves, ..adds, ..updates, ..nodeRemoves, ..edgeAdds];
    }

    private static bool SameEdge(DiagramEdge a, DiagramEdge b)
    {
        return a.SourceId == b.SourceId && a.TargetId == b.TargetId && a.Label == b.Label && a.Style == b.Style &&
               a.Parent == b.Parent && a.LabelAt == b.LabelAt && SameData(a.Data, b.Data);
    }

    private static bool SameData(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        a ??= new Dictionary<string, string>();
        b ??= new Dictionary<string, string>();
        return a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);
    }

    /// <summary>The style entries that differ (null value = removed); null when none do. Fails on a changed entry without a value.</summary>
    private static Dictionary<string, string?>? StyleChanges(string before, string after, out bool ok)
    {
        ok = true;
        var a = Parse(before);
        var b = Parse(after);

        if (!a.Where(p => p.Value is null).Select(p => p.Key).ToHashSet().SetEquals(b.Where(p => p.Value is null).Select(p => p.Key)))
        {
            ok = false;
            return null;
        }

        var changes = new Dictionary<string, string?>();
        foreach (var (k, v) in b)
            if (v is not null && (!a.TryGetValue(k, out var was) || was != v))
                changes[k] = v;
        foreach (var (k, v) in a)
            if (v is not null && !b.ContainsKey(k))
                changes[k] = null;

        return changes.Count == 0 ? null : changes;
    }

    private static Dictionary<string, string?> Parse(string style)
    {
        var entries = new Dictionary<string, string?>();
        foreach (string part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) entries[part] = null;
            else entries[part[..eq]] = part[(eq + 1)..];
        }

        return entries;
    }

    private static Dictionary<string, string>? AttributeChanges(IReadOnlyDictionary<string, string>? before, IReadOnlyDictionary<string, string>? after, out bool ok)
    {
        ok = true;
        before ??= new Dictionary<string, string>();
        after  ??= new Dictionary<string, string>();

        if (before.Keys.Any(k => !after.ContainsKey(k)))
        {
            ok = false;
            return null;
        }

        var changes = after.Where(p => !before.TryGetValue(p.Key, out var v) || v != p.Value).ToDictionary(p => p.Key, p => p.Value);
        return changes.Count == 0 ? null : changes;
    }
}
