using System.Linq;
using Silo.Drawio.Host;
using Silo.Drawio.Live;

namespace Silo.Drawio.Mx;

/// <summary>A box in a generated diagram.</summary>
/// <param name="Id">Stable cell id. Reusing the same id keeps the cell's identity across regenerations.</param>
/// <param name="Style">draw.io style string, e.g. <c>rounded=1;fillColor=#dae8fc;</c>.</param>
/// <param name="Data">Extra custom properties stored on the cell (shown under Edit Data in draw.io). Names starting with <c>silo-</c> are live bindings.</param>
/// <param name="Link">Cell link. A <see cref="DiagramLink" /> address makes the node click-through to the Silo construct.</param>
/// <param name="Parent">The container node this one sits in (then <see cref="X" />/<see cref="Y" /> are relative to it); null = the page.</param>
/// <param name="Fold">Makes the node a collapsible container and says whether it starts collapsed.</param>
public sealed record DiagramNode(
    string                                Id,
    string                                Label,
    double                                X,
    double                                Y,
    double                                Width,
    double                                Height,
    string                                Style,
    IReadOnlyDictionary<string, string>? Data = null,
    string?                               Link = null,
    string?                               Parent = null,
    DiagramFold?                          Fold = null);

/// <summary>
///     The fold of a container node (draw.io's collapse/expand, the +/- at its top-left): whether it starts collapsed and the bounds
///     it has in the other state. The node's own X/Y/Width/Height are its bounds in the state it starts in.
/// </summary>
/// <param name="Collapsed">Whether the container starts collapsed (its children hidden, connectors to them drawn to it).</param>
/// <param name="X">The other state's X.</param>
/// <param name="Y">The other state's Y.</param>
/// <param name="Width">The other state's width.</param>
/// <param name="Height">The other state's height.</param>
public sealed record DiagramFold(bool Collapsed, double X, double Y, double Width, double Height);

/// <summary>A connector between two <see cref="DiagramNode" />s (by id).</summary>
/// <param name="Parent">The container the connector belongs to (hidden with it when it is collapsed); null = the page.</param>
/// <param name="LabelAt">Where along the connector its label sits, from -1 (the source end) to 1 (the target end); null = the middle.</param>
public sealed record DiagramEdge(
    string                                Id,
    string                                SourceId,
    string                                TargetId,
    string?                               Label,
    string                                Style,
    IReadOnlyDictionary<string, string>? Data = null,
    string?                               Parent = null,
    double?                               LabelAt = null);

/// <summary>A node's position and size.</summary>
public sealed record DiagramBounds(double X, double Y, double Width, double Height);

/// <summary>What a generator produces: nodes and edges, ready to be merged into a diagram.</summary>
public sealed class DiagramModel
{
    public List<DiagramEdge> Edges { get; } = [];
    public List<DiagramNode> Nodes { get; } = [];
}

/// <summary>An add / update / remove applied to a diagram by code (see <see cref="DrawioView.ApplyEditsAsync" />).</summary>
public abstract record DiagramEdit
{
    public sealed record AddNode(DiagramNode Node) : DiagramEdit;

    public sealed record AddEdge(DiagramEdge Edge) : DiagramEdit;

    /// <summary>
    ///     Changes whichever of label / style entries / attributes / bounds are given on an existing cell. A null style entry
    ///     removes that entry.
    /// </summary>
    public sealed record Update(
        string                                 Id,
        string?                                Label = null,
        IReadOnlyDictionary<string, string?>? Style = null,
        IReadOnlyDictionary<string, string>?  Attributes = null,
        DiagramBounds?                         Bounds = null) : DiagramEdit;

    /// <summary>Removes a cell, and any connector attached to it.</summary>
    public sealed record Remove(string Id) : DiagramEdit;

    /// <summary>
    ///     Drops what a later remove in the same batch undoes: a cell whose last edit in the batch is a remove loses every other
    ///     edit of the batch, and the remove too when the batch also added it (it never reached the editor); otherwise one remove
    ///     stays, where the last one was.
    /// </summary>
    public static List<DiagramEdit> Coalesce(IEnumerable<DiagramEdit> edits)
    {
        var list = edits.ToList();

        var lastAt  = new Dictionary<string, int>();
        var firstAt = new Dictionary<string, int>();
        for (int i = 0; i < list.Count; i++)
            if (IdOf(list[i]) is { } id)
            {
                lastAt[id] = i;
                firstAt.TryAdd(id, i);
            }

        var result = new List<DiagramEdit>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            if (IdOf(list[i]) is not { } id || list[lastAt[id]] is not DiagramEdit.Remove)
            {
                result.Add(list[i]);
                continue;
            }

            bool addedHere = list[firstAt[id]] is DiagramEdit.AddNode or DiagramEdit.AddEdge;
            if (i == lastAt[id] && !addedHere) result.Add(list[i]);
        }

        return result;
    }

    /// <summary>The id of the cell an edit is about.</summary>
    public static string? IdOf(DiagramEdit edit)
    {
        return edit switch
               {
                   DiagramEdit.AddNode a => a.Node.Id,
                   DiagramEdit.AddEdge a => a.Edge.Id,
                   DiagramEdit.Update u  => u.Id,
                   DiagramEdit.Remove r  => r.Id,
                   _                     => null
               };
    }
}
