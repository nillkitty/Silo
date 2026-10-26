using System.Threading;

namespace Silo.Providers.Diagramming;

/// <summary>
///     A Silo-aware tree a Tree node grows from. The node's own shape stands for the object the URI resolved to; the
///     source's <see cref="GetRootsAsync" /> items become its children, and so on down. Implement it (or return it from an
///     <see cref="IDiagramSourceAdapter" />) for full control over what a Tree node shows; objects that do not are adapted
///     by heuristics (directories, JSON/XML, nested lists, hierarchical data providers).
/// </summary>
public interface ITreeSource
{
    /// <summary>The items directly below the node, in display order.</summary>
    ValueTask<IReadOnlyList<ITreeItem>> GetRootsAsync(CancellationToken cancel);
}

/// <summary>One item of an <see cref="ITreeSource" />.</summary>
/// <remarks>Items may also implement <see cref="IDiagramItemHints" /> to style the vertex they become.</remarks>
public interface ITreeItem
{
    /// <summary>Identifies the item among its siblings; stable across reads (a file name, a JSON property name, an index).</summary>
    string Key { get; }

    /// <summary>The item's display text (what <c>{item}</c> shows).</summary>
    string Text { get; }

    /// <summary>The object behind the item, read by <c>{item:Path}</c> tokens; null means the item itself.</summary>
    object? Value { get; }

    /// <summary>
    ///     Whether the item has children, if that is known without reading them. <see langword="null" /> means unknown: the
    ///     item is shown as expandable and its children are read when it is expanded.
    /// </summary>
    bool? HasChildren { get; }

    /// <summary>The item's children, in display order. Empty (never null) for a leaf.</summary>
    ValueTask<IReadOnlyList<ITreeItem>> GetChildrenAsync(CancellationToken cancel);
}
