using Silo.Drawio.Host;
using Silo.Drawio.Live;

namespace Silo.Drawio.Mx;

/// <summary>A box in a generated diagram.</summary>
/// <param name="Id">Stable cell id. Reusing the same id keeps the cell's identity across regenerations.</param>
/// <param name="Style">draw.io style string, e.g. <c>rounded=1;fillColor=#dae8fc;</c>.</param>
/// <param name="Data">Extra custom properties stored on the cell (shown under Edit Data in draw.io). Names starting with <c>silo-</c> are live bindings.</param>
/// <param name="Link">Cell link. A <see cref="DiagramLink" /> address makes the node click-through to the Silo construct.</param>
public sealed record DiagramNode(
    string                                Id,
    string                                Label,
    double                                X,
    double                                Y,
    double                                Width,
    double                                Height,
    string                                Style,
    IReadOnlyDictionary<string, string>? Data = null,
    string?                               Link = null);

/// <summary>A connector between two <see cref="DiagramNode" />s (by id).</summary>
public sealed record DiagramEdge(
    string                                Id,
    string                                SourceId,
    string                                TargetId,
    string?                               Label,
    string                                Style,
    IReadOnlyDictionary<string, string>? Data = null);

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

    /// <summary>Changes whichever of label / style entries / attributes are given on an existing cell.</summary>
    public sealed record Update(
        string                                Id,
        string?                               Label = null,
        IReadOnlyDictionary<string, string>? Style = null,
        IReadOnlyDictionary<string, string>? Attributes = null) : DiagramEdit;

    /// <summary>Removes a cell, and any connector attached to it.</summary>
    public sealed record Remove(string Id) : DiagramEdit;
}
