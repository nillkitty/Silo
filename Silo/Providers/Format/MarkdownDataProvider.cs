using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdigDocument = Markdig.Syntax.MarkdownDocument;

namespace Silo.Providers.Format;

/// <summary>
///     Projects a parsed Markdown document's pipe tables and bulleted/numbered lists onto
///     <see cref="IFlatDataProvider" />, so they can be browsed as tabular/list data alongside
///     every other <see cref="IDataSource" /> in the Silo without anything downstream needing to
///     know they originated from Markdown at all.
/// </summary>
/// <remarks>
///     Tables and lists get simple ordinal names ("Table 1", "List 1", ...) rather than anything
///     derived from a nearby heading -- Markdown has no formal link between a heading and the
///     table/list that follows it, and guessing one would be a heuristic worth its own design
///     pass rather than something to bolt on here.
/// </remarks>
public class MarkdownDataProvider : IFlatDataProvider
{
    private readonly MarkdigDocument _document;
    private readonly Uri?            _sourceUri;

    public MarkdownDataProvider(MarkdigDocument document, Uri? sourceUri = null)
    {
        _document  = document.Required();
        _sourceUri = sourceUri;
    }

    public string? DisplayName { get; } = "Markdown Tables & Lists";

    /// <summary>
    ///     Every table, and every top-level list, found anywhere in the document, in document
    ///     order. A list nested inside another list's item isn't yielded on its own here -- it's
    ///     reached through its parent <see cref="MarkdownListDataSource" />'s
    ///     <see cref="IListItem.Children" /> instead, the same way the Markdown source nests it. A
    ///     table or list nested inside a list item or table cell is still found and yielded, since
    ///     the walk doesn't stop at the first one it enters.
    /// </summary>
    public IEnumerable<IDataSource> GetDataSources()
    {
        var tableIndex = 0;
        var listIndex  = 0;

        foreach (var block in Descendants(_document))
            switch (block)
            {
                case Table table:
                    yield return new MarkdownTableDataSource(this, table, ++tableIndex, _sourceUri);
                    break;

                case ListBlock list when list.Parent is not ListItemBlock:
                    yield return new MarkdownListDataSource(this, list, ++listIndex, _sourceUri);
                    break;
            }
    }

    /// <summary>
    ///     Walks every block in the tree, depth-first, in document order.
    /// </summary>
    internal static IEnumerable<Block> Descendants(ContainerBlock container)
    {
        foreach (var child in container)
        {
            yield return child;

            if (child is ContainerBlock nested)
                foreach (var descendant in Descendants(nested))
                    yield return descendant;
        }
    }

    /// <summary>
    ///     Flattens an inline tree to plain text: emphasis/strong/link markup is discarded while
    ///     the text it wraps is kept, inline code keeps its literal content, an autolink falls back
    ///     to its URL, and a hard line break becomes a single space.
    /// </summary>
    internal static string PlainText(Inline? inline)
    {
        if (inline is null)
            return string.Empty;

        var builder = new StringBuilder();
        AppendPlainText(builder, inline);
        return builder.ToString();
    }

    private static void AppendPlainText(StringBuilder builder, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                builder.Append(literal.Content.ToString());
                break;

            case CodeInline code:
                builder.Append(code.Content);
                break;

            case AutolinkInline autolink:
                builder.Append(autolink.Url);
                break;

            case LineBreakInline:
                builder.Append(' ');
                break;

            case ContainerInline container:
                for (var child = container.FirstChild; child is not null; child = child.NextSibling)
                    AppendPlainText(builder, child);
                break;
        }
    }

    /// <summary>
    ///     Converts a Markdig source position to Silo's <see cref="Origin" />, adjusting for
    ///     Markdig's zero-based <see cref="MarkdownObject.Line" />/<see cref="MarkdownObject.Column" />
    ///     -- <see cref="Origin" />, like every other format in Silo, is one-based.
    /// </summary>
    internal static Origin OriginOf(MarkdownObject obj) => new(obj.Line + 1, obj.Column + 1, obj.Span.Start);
}

/// <summary>
///     A Markdown pipe table, projected onto <see cref="ITabularDataSource" />. Columns are
///     matched up by left-to-right position in each row rather than
///     <see cref="TableCell.ColumnIndex" />, since Markdig's pipe-table parser never actually
///     populates that property -- it leaves every cell at its default of -1.
/// </summary>
internal sealed class MarkdownTableDataSource : ITabularDataSource
{
    private readonly Table _table;

    public MarkdownTableDataSource(IDataProvider provider, Table table, int ordinal, Uri? sourceUri)
    {
        _table = table.Required();

        Provider        = provider;
        NativeNamespace = CanonicalName = $"Table {ordinal}";
        LocalNamespace  = sourceUri?.ToString() ?? string.Empty;
        Origin          = MarkdownDataProvider.OriginOf(table);
        ColumnNames     = BuildColumnNames(table);
    }

    public string         CanonicalName   { get; }
    public string         LocalNamespace  { get; }
    public string         NativeNamespace { get; }
    public IDataProvider? Provider        { get; }

    public IReadOnlyList<string> ColumnNames { get; }
    public Origin                Origin      { get; }

    public IEnumerable<IReadOnlyList<string>> GetRows()
    {
        foreach (var block in _table)
        {
            if (block is not TableRow { IsHeader: false } row)
                continue;

            yield return BuildRow(row, ColumnNames.Count);
        }
    }

    private static IReadOnlyList<string> BuildColumnNames(Table table)
    {
        var headerRow = table.OfType<TableRow>().FirstOrDefault(r => r.IsHeader);
        if (headerRow is null)
            // A compliant pipe table always has a header separator row, so this shouldn't
            // happen in practice -- fall back to ordinal names instead of throwing.
            return Enumerable.Range(1, table.ColumnDefinitions.Count)
                              .Select(i => $"Column {i}")
                              .ToList();

        return headerRow.OfType<TableCell>().Select(CellText).ToList();
    }

    private static IReadOnlyList<string> BuildRow(TableRow row, int columnCount)
    {
        var cells = row.OfType<TableCell>().Select(CellText).ToList();

        // Line up with ColumnNames cell-for-cell even for a ragged row -- pad a short row with
        // empty cells, truncate a long one.
        if (cells.Count < columnCount)
            cells.AddRange(Enumerable.Repeat(string.Empty, columnCount - cells.Count));
        else if (cells.Count > columnCount)
            cells = cells.Take(columnCount).ToList();

        return cells;
    }

    private static string CellText(TableCell cell)
    {
        // A cell's content is itself a sequence of blocks (almost always a single paragraph) --
        // flatten every leaf block's inline content and join on a space.
        var parts = cell.OfType<LeafBlock>()
                         .Select(leaf => MarkdownDataProvider.PlainText(leaf.Inline))
                         .Where(text => text.Length > 0);
        return string.Join(" ", parts);
    }
}

/// <summary>
///     A Markdown bulleted or numbered list, projected onto <see cref="IListDataSource" />. A
///     sub-list nested inside one of this list's items becomes that item's
///     <see cref="IListItem.Children" />, so the same recursive shape covers both a flat list and
///     a deeply nested one.
/// </summary>
internal sealed class MarkdownListDataSource : IListDataSource
{
    public MarkdownListDataSource(IDataProvider provider, ListBlock list, int ordinal, Uri? sourceUri)
    {
        list.Required();

        Provider        = provider;
        NativeNamespace = CanonicalName = $"List {ordinal}";
        LocalNamespace  = sourceUri?.ToString() ?? string.Empty;
        IsOrdered       = list.IsOrdered;
        Origin          = MarkdownDataProvider.OriginOf(list);
        Items           = BuildItems(list);
    }

    public string         CanonicalName   { get; }
    public string         LocalNamespace  { get; }
    public string         NativeNamespace { get; }
    public IDataProvider? Provider        { get; }

    public bool                     IsOrdered { get; }
    public Origin                   Origin    { get; }
    public IReadOnlyList<IListItem> Items     { get; }

    private static IReadOnlyList<IListItem> BuildItems(ListBlock list)
    {
        var items = new List<IListItem>();

        foreach (var block in list)
            if (block is ListItemBlock itemBlock)
                items.Add(BuildItem(itemBlock));

        return items;
    }

    private static IListItem BuildItem(ListItemBlock itemBlock)
    {
        var textParts = new List<string>();
        var children  = (IReadOnlyList<IListItem>)Array.Empty<IListItem>();

        foreach (var block in itemBlock)
            switch (block)
            {
                // An item's own nested sub-list (indented further under it) becomes its
                // Children rather than text; everything else leafy contributes to its Text.
                case ListBlock nested:
                    children = BuildItems(nested);
                    break;

                case LeafBlock leaf:
                    var text = MarkdownDataProvider.PlainText(leaf.Inline);
                    if (text.Length > 0)
                        textParts.Add(text);
                    break;
            }

        return new MarkdownListItem(string.Join(" ", textParts), MarkdownDataProvider.OriginOf(itemBlock), children);
    }
}

/// <summary>
///     A single <see cref="MarkdownListDataSource" /> entry.
/// </summary>
internal sealed record MarkdownListItem(string Text, Origin Origin, IReadOnlyList<IListItem> Children) : IListItem;
