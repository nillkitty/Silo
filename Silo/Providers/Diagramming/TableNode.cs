using System.ComponentModel;

namespace Silo.Providers.Diagramming;

/// <summary>
///     Settings shared by the nodes that grow one child per row (<see cref="TableNode" />, <see cref="ListNode" />). The
///     node's child marked <c>silo-prototype</c> is the row template: every row is a copy of it with the row in scope for
///     its bindings, and the prototype itself shows the first row (it is hidden while there are none).
/// </summary>
public abstract class RowsNode : DynamicNode
{
    [NodeAttribute("silo-max-rows")]
    [DefaultValue(100)]
    [Description("The most rows shown; a '… N more' row says how many were left out")]
    public int MaxRows { get; set; } = 100;
}

/// <summary>
///     A draw.io table (<c>shape=table</c>) with one table row per data row. The prototype row's cells bind to columns with
///     <c>{row:Column}</c>; with <see cref="AutoColumns" /> on, the header and prototype get one cell per shown column.
/// </summary>
[Description("A table shape which will populate child rows from a tabular data source")]
public class TableNode : RowsNode
{
    [NodeAttribute("silo-columns")]
    [Description("Which columns to show, comma separated, in order (empty: every column of the source). 'Name as Caption' renames one in the header")]
    public string? Columns { get; set; }

    [NodeAttribute("silo-auto-columns")]
    [DefaultValue(true)]
    [Description("Rebuild the header and prototype cells to match the columns (off: keep the cells as designed)")]
    public bool AutoColumns { get; set; } = true;
}

/// <summary>A UML-style list (a swimlane with a stack layout) with one line per row; the prototype line binds with <c>{item}</c> or <c>{item:Path}</c>.</summary>
[Description("A list shape (UML-like swimlane) which will populate child lines from a linear data source")]
public class ListNode : RowsNode
{
}
