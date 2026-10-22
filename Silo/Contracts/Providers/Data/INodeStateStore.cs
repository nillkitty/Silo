namespace Silo.Providers.Data;

/// <summary>What the person left a node of the Explorer tree like: open or closed, showing in a tab, selected.</summary>
/// <param name="Key">
///     The Explorer node's key (stable across runs: <c>local/tables/5</c>, <c>node/silo/file:///C:/Data</c>
///     ...).
/// </param>
/// <param name="Name">The node's text when it was saved. Only for reading the table; a node is found by its key.</param>
/// <param name="Expanded">Whether the node was expanded.</param>
/// <param name="Open">Whether the node's document was open as a permanent tab.</param>
/// <param name="Selected">Whether it was the selected node.</param>
public sealed record NodeStateRecord(string Key, string Name, bool Expanded, bool Open, bool Selected);

/// <summary>
///     Remembers how the Explorer tree was left (the <c>Node</c> table), for the person who is using the Silo: what was
///     expanded, which
///     documents were open and which node was selected come back the next time they open it.
/// </summary>
/// <remarks>
///     Rows belong to the current user once that user is one of the Silo's Users; until then they are shared. Like the
///     window layout,
///     writing here does <b>not</b> mark the Silo as having unsaved changes -- the state is kept by the next save that
///     happens for
///     another reason (and is written just before any save or close).
/// </remarks>
public interface INodeStateStore
{
    /// <summary>The current user's remembered nodes, else the Silo's shared ones.</summary>
    IReadOnlyList<NodeStateRecord> GetNodeStates();

    /// <summary>Remembers exactly these nodes, replacing what was remembered before for the current user.</summary>
    void SaveNodeStates(IReadOnlyCollection<NodeStateRecord> states);
}