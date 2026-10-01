namespace Silo.Contracts;

/// <summary>
///     A data source that exposes its content as rows of named columns -- the shape an
///     <see cref="IFlatDataProvider" />/<see cref="IHierarchicalDataProvider" /> implementation
///     uses for anything table-like, such as a Markdown pipe table (see
///     <c>MarkdownDataProvider</c> in <c>Providers/Format</c>).
/// </summary>
public interface ITabularDataSource : IDataSource
{
    /// <summary>
    ///     The column headers, in left-to-right order. Never empty for a well-formed table.
    /// </summary>
    IReadOnlyList<string> ColumnNames { get; }

    /// <summary>
    ///     Where this table appears in its source file, or <see cref="Origin.Unknown" /> if
    ///     position isn't tracked for this source.
    /// </summary>
    Origin Origin { get; }

    /// <summary>
    ///     Reads the table's data rows (the header is not included). Each row has exactly
    ///     <see cref="ColumnNames" />.Count cells, in the same left-to-right order; a cell with no
    ///     content is an empty string, never null -- there's no SQL-style NULL at this level.
    /// </summary>
    IEnumerable<IReadOnlyList<string>> GetRows();
}
