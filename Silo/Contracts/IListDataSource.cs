namespace Silo.Contracts;

/// <summary>
///     A data source that exposes its content as a list of items, each optionally containing its
///     own nested children -- the shape an <see cref="IFlatDataProvider" />/
///     <see cref="IHierarchicalDataProvider" /> implementation uses for anything list-like, such as
///     a Markdown bulleted or numbered list (see <c>MarkdownDataProvider</c> in
///     <c>Providers/Format</c>). A flat list is simply one whose items all have empty
///     <see cref="IListItem.Children" />; nesting (for example indented Markdown list items) turns
///     the same shape into a tree.
/// </summary>
public interface IListDataSource : IDataSource
{
    /// <summary>
    ///     Whether this list is ordered (numbered) as opposed to bulleted.
    /// </summary>
    bool IsOrdered { get; }

    /// <summary>
    ///     Where this list appears in its source file, or <see cref="Origin.Unknown" /> if position
    ///     isn't tracked for this source.
    /// </summary>
    Origin Origin { get; }

    /// <summary>
    ///     The list's top-level items, in document order.
    /// </summary>
    IReadOnlyList<IListItem> Items { get; }
}

/// <summary>
///     A single entry in an <see cref="IListDataSource" />, optionally with its own nested
///     sub-list.
/// </summary>
public interface IListItem
{
    /// <summary>
    ///     The item's own text, flattened to plain text (inline formatting such as emphasis or
    ///     links is stripped, link/code text is kept).
    /// </summary>
    string Text { get; }

    /// <summary>
    ///     Where this item appears in its source file, or <see cref="Origin.Unknown" /> if position
    ///     isn't tracked for this source.
    /// </summary>
    Origin Origin { get; }

    /// <summary>
    ///     This item's nested sub-items, if any, in document order. Empty (never null) for an item
    ///     with no nested list.
    /// </summary>
    IReadOnlyList<IListItem> Children { get; }
}
