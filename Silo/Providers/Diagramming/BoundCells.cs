using System.Linq;
using System.Threading;
using Silo.Drawio.Live;
using Silo.Drawio.Mx;

namespace Silo.Providers.Diagramming;

/// <summary>A cell as the editor reported it, for a binder to read (the node, its children, the cells it made).</summary>
public sealed class BoundCell
{
    public required string Id { get; init; }

    /// <summary>The cell's parent (its container, or the layer).</summary>
    public string? Parent { get; init; }

    public bool    Edge   { get; init; }
    public string? Source { get; init; }
    public string? Target { get; init; }

    public string  Style { get; init; } = "";
    public string? Label { get; init; }

    /// <summary>The cell's custom attributes (Edit Data), without <c>label</c>.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();

    public DiagramBounds? Bounds    { get; init; }
    public bool           Collapsed { get; init; }
    public bool           Visible   { get; init; } = true;

    /// <summary>The cell's position among its parent's children.</summary>
    public int Order { get; init; }

    public string? Attribute(string name)
    {
        return Attributes.TryGetValue(name, out string? v) ? v : null;
    }

    public bool Is(string attribute)
    {
        return Attribute(attribute) is "1" or "true";
    }

    /// <summary>The <c>silo-*</c> binding templates the cell carries (label, tooltip, style and attribute bindings).</summary>
    public IEnumerable<(string Name, string Template)> Bindings()
    {
        return Attributes.Where(a => a.Key == DiagramBindings.LabelAttribute || a.Key == DiagramBindings.TooltipAttribute ||
                                     a.Key.StartsWith(DiagramBindings.StylePrefix, StringComparison.Ordinal) ||
                                     a.Key.StartsWith(DiagramBindings.AttributePrefix, StringComparison.Ordinal))
                         .Select(a => (a.Key, a.Value));
    }
}

/// <summary>A node's cell and every cell that belongs to it: its descendants and the cells it made (by <c>silo-bound-of</c>).</summary>
public sealed class BoundSnapshot(BoundCell owner, IReadOnlyList<BoundCell> cells)
{
    private readonly Dictionary<string, BoundCell> _byId = cells.ToDictionary(c => c.Id, StringComparer.Ordinal);

    public BoundCell Owner { get; } = owner;

    /// <summary>Every cell of the node but the node's own.</summary>
    public IReadOnlyList<BoundCell> Cells { get; } = cells;

    public BoundCell? Find(string id)
    {
        return _byId.TryGetValue(id, out var c) ? c : id == Owner.Id ? Owner : null;
    }

    /// <summary>The direct children of a cell, in order.</summary>
    public IReadOnlyList<BoundCell> ChildrenOf(string id)
    {
        return Cells.Where(c => c.Parent == id).OrderBy(c => c.Order).ToList();
    }

    /// <summary>The cells the node made (its rows, tree items, connectors and "more" cells), not the ones the person drew.</summary>
    public IEnumerable<BoundCell> Made => Cells.Where(c => c.Attribute(DiagramNodeSchema.BoundOfAttribute) == Owner.Id);

    /// <summary>The first cell marked <c>silo-prototype</c> among the node's direct children (or anywhere below it).</summary>
    public BoundCell? Prototype =>
        ChildrenOf(Owner.Id).FirstOrDefault(c => c.Is(DiagramNodeSchema.PrototypeAttribute)) ??
        Cells.FirstOrDefault(c => c.Is(DiagramNodeSchema.PrototypeAttribute) && c.Attribute(DiagramNodeSchema.BoundOfAttribute) is null);
}

/// <summary>One change a binder asks for: add, update or remove a cell (the plugin's <c>upsertCells</c> entry).</summary>
public sealed class CellChange
{
    public required string Id { get; init; }

    /// <summary>Create the cell when it does not exist (otherwise only an existing cell is changed).</summary>
    public bool Add { get; init; }

    public bool Remove { get; init; }
    public bool Edge   { get; init; }

    public string? Parent { get; init; }
    public string? Source { get; init; }
    public string? Target { get; init; }

    /// <summary>The position among the parent's children (stack and table layouts order by it); null leaves it.</summary>
    public int? Index { get; set; }

    public string? Label   { get; set; }
    public string? Tooltip { get; set; }
    public string? Link    { get; set; }

    /// <summary>The whole style (replaces the cell's).</summary>
    public string? Style { get; set; }

    /// <summary>Single style entries to set; a null value removes the entry.</summary>
    public Dictionary<string, string?>? StyleEntries { get; set; }

    /// <summary>Custom attributes to set; a null value removes the attribute.</summary>
    public Dictionary<string, string?>? Attributes { get; set; }

    public DiagramBounds? Bounds    { get; set; }
    public bool?          Collapsed { get; set; }
    public bool?          Visible   { get; set; }

    public static CellChange Delete(string id)
    {
        return new CellChange { Id = id, Remove = true };
    }
}

/// <summary>What a binder works from.</summary>
public sealed class BindContext
{
    /// <summary>The node and its cells as they are in the editor.</summary>
    public required BoundSnapshot Snapshot { get; init; }

    /// <summary>The node's settings, read from its attributes.</summary>
    public required DynamicNode Settings { get; init; }

    /// <summary>What the node's URI (and <c>silo-path</c>) resolved to; null when it did not.</summary>
    public object? Value { get; init; }

    /// <summary>Why it did not resolve, if it failed.</summary>
    public Exception? Error { get; init; }

    /// <summary>Whether the node has a URI at all (an unbound node is left alone).</summary>
    public bool HasSource => Settings.Uri is not null;

    /// <summary>Tree item keys the person asked to expand (double-clicked their "…" placeholder).</summary>
    public IReadOnlySet<string> Expand { get; init; } = new HashSet<string>();

    public CancellationToken Cancel { get; init; }
}

/// <summary>What a binder produced.</summary>
public sealed class BindResult
{
    /// <summary>The changes, in the order they must run (parents before children).</summary>
    public List<CellChange> Changes { get; } = [];

    /// <summary>The scope each cell's bindings read, by cell id (kept by the pane for later binding refreshes).</summary>
    public Dictionary<string, IBindingScope> Scopes { get; } = new(StringComparer.Ordinal);

    /// <summary>Every cell id the node made that should exist now; made cells not in it are removed.</summary>
    public HashSet<string> Keep { get; } = new(StringComparer.Ordinal);

    /// <summary>Queues removal of every cell the node made that is not in <see cref="Keep" />.</summary>
    public void RemoveStale(BoundSnapshot snapshot)
    {
        foreach (var made in snapshot.Made)
            if (!Keep.Contains(made.Id))
                Changes.Add(CellChange.Delete(made.Id));
    }
}
