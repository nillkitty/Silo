namespace Silo.Providers.Diagramming;

/// <summary>
///     Turns an object a bound diagram node's URI resolved to into an <see cref="ITreeSource" /> or <see cref="IRowSource" />.
///     Registered adapters are asked in descending <see cref="Priority" /> order; the first non-null answer wins. Objects that
///     already implement <see cref="ITreeSource" />/<see cref="IRowSource" /> are used as they are, before any adapter.
/// </summary>
[RequireRegistration]
public interface IDiagramSourceAdapter
{
    /// <summary>Higher goes first. The built-in heuristics run at <see cref="int.MinValue" />.</summary>
    int Priority { get; }

    /// <summary>The object as a tree, or null if this adapter does not know how.</summary>
    ITreeSource? AsTree(object value);

    /// <summary>The object as rows, or null if this adapter does not know how.</summary>
    IRowSource? AsRows(object value);
}

/// <summary>
///     Optional styling an <see cref="ITreeItem" /> or <see cref="IRowItem" /> (or a Status node's object) can give the cell
///     it becomes. Applied after the prototype's style and before the cell's own <c>silo-style.*</c> bindings.
/// </summary>
public interface IDiagramItemHints
{
    /// <summary>Style entries to set (a null value removes the entry), e.g. <c>fillColor</c>.</summary>
    IReadOnlyDictionary<string, string?>? StyleEntries { get; }

    /// <summary>A tooltip, or null to leave it to the cell's bindings.</summary>
    string? Tooltip { get; }

    /// <summary>A cell link (e.g. a <c>silo:</c> click-through address), or null.</summary>
    string? Link { get; }
}

/// <summary>
///     A bound object (or tree/row source) that says when it changed, so Live nodes refresh without polling. Sources that
///     implement <see cref="Contracts.IObservableDataSource" /> are watched through it instead.
/// </summary>
public interface INotifySourceChanged
{
    /// <summary>Raised (on any thread) whenever what the source shows may have changed.</summary>
    event Action? Changed;
}
