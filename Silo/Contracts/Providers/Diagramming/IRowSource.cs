using System.Threading;

namespace Silo.Providers.Diagramming;

/// <summary>
///     A Silo-aware source of rows for a Table node (one cell per column) or a List node (one line per row). Implement it
///     (or return it from an <see cref="IDiagramSourceAdapter" />) for full control; objects that do not are adapted by
///     heuristics (<see cref="Data.Source.ITabularDataSource" />, <see cref="Data.Source.IListDataSource" />, JSON arrays,
///     directories, any <see cref="System.Collections.IEnumerable" />).
/// </summary>
public interface IRowSource
{
    /// <summary>The column names, left to right. A list-like source may have just one.</summary>
    IReadOnlyList<string> Columns { get; }

    /// <summary>Reads up to <paramref name="limit" /> rows, in display order.</summary>
    ValueTask<RowPage> GetRowsAsync(int limit, CancellationToken cancel);
}

/// <summary>Rows read from an <see cref="IRowSource" />.</summary>
/// <param name="Rows">The rows read.</param>
/// <param name="Total">How many rows the source has in all, when known (a "… N more" row is shown when it is larger).</param>
public sealed record RowPage(IReadOnlyList<IRowItem> Rows, int? Total = null);

/// <summary>One row of an <see cref="IRowSource" />.</summary>
/// <remarks>Rows may also implement <see cref="IDiagramItemHints" /> to style the cells they become.</remarks>
public interface IRowItem
{
    /// <summary>Identifies the row within the source; stable across reads (a primary key, a file name, an index).</summary>
    string Key { get; }

    /// <summary>The row as one line of text (what a List node's <c>{item}</c> shows).</summary>
    string Text { get; }

    /// <summary>The object behind the row, read by <c>{item:Path}</c> tokens that are not column names; null means the row itself.</summary>
    object? Value { get; }

    /// <summary>Reads a column's value; false when the row has no such column.</summary>
    bool TryGetCell(string column, out object? value);
}
