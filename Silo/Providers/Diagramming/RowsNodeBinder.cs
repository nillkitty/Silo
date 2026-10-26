using System.Globalization;
using System.Linq;
using Silo.Drawio.Extensions;
using Silo.Drawio.Mx;
using Telefrag.DI;

namespace Silo.Providers.Diagramming;

/// <summary>
///     What Table and List nodes share: read the rows, then make one copy of the prototype per row (the prototype itself
///     shows the first), in order, below it; a "… N more" copy when rows were left out; and remove copies of rows that are
///     gone. A copy keeps its cell ids from one read to the next (they come from the row keys), so the person's styling of
///     the prototype and the rows' positions in an undo history stay stable.
/// </summary>
public abstract class RowsNodeBinder : DiagramNodeBinder
{
    /// <summary>A cell of the template a row is copied from.</summary>
    protected sealed record Part(string? Id, string Style, Dictionary<string, string?> Attributes, IReadOnlyList<(string Name, string Template)> Bindings,
                                 string? Label, DiagramBounds Bounds);

    public override async Task<BindResult> Bind(BindContext context)
    {
        var result   = new BindResult();
        var snapshot = context.Snapshot;
        var owner    = snapshot.Owner;
        var settings = (RowsNode)context.Settings;

        BindOwner(context, result);
        if (context.Error is not null || context.Value is null) return result;

        if (snapshot.Prototype is not { } prototype)
        {
            Fail(result, owner, "This node has no row template: mark one child with silo-prototype=1.");
            return result;
        }

        if (DiagramSources.AsRows(context.Value) is not { } source)
        {
            Fail(result, owner, $"'{ObjectPath.Display(context.Value)}' cannot be shown as rows.");
            return result;
        }

        var page  = await source.GetRowsAsync(Math.Max(1, settings.MaxRows), context.Cancel);
        var rows  = page.Rows;
        int extra = page.Total is { } total && total > rows.Count ? total - rows.Count : 0;

        // the template: the prototype and its parts, after any column changes
        var parts = Template(context, prototype, source, result);

        // the prototype's bounds as the template left them (a table's columns may have changed its width)
        var protoBounds = result.Changes.LastOrDefault(c => c.Id == prototype.Id && c.Bounds is not null)?.Bounds ?? prototype.Bounds ?? new DiagramBounds(0, 0, 120, 26);

        double rowHeight  = prototype.Bounds?.Height is > 0 and var h ? h : 26;
        double top        = prototype.Bounds?.Y ?? 0;
        int    firstIndex = prototype.Order;
        var    keys       = new HashSet<string>(StringComparer.Ordinal);

        for (int k = 0; k < rows.Count; k++)
        {
            var    row = rows[k];
            string key = Unique(keys, row.Key);
            var    scope = new BindingScope(row, context.Value);

            if (k == 0)
            {
                // the prototype shows the first row
                var update = new CellChange { Id = prototype.Id, Attributes = new() { [DiagramNodeSchema.KeyAttribute] = key } };
                Fill(update, prototype.Bindings(), scope, row);
                if (!prototype.Visible) update.Visible = true;
                result.Changes.Add(update);
                result.Scopes[prototype.Id] = scope;

                foreach (var part in parts)
                {
                    if (part.Id is null) continue;
                    var cell = new CellChange { Id = part.Id };
                    Fill(cell, part.Bindings, scope, row);
                    result.Changes.Add(cell);
                    result.Scopes[part.Id] = scope;
                }

                continue;
            }

            string rowId = MadeId(owner.Id, "row", key);
            AddCopy(result, owner, prototype, protoBounds, parts, rowId, key, firstIndex + k, top + k * rowHeight, scope, row, null);
        }

        if (rows.Count == 0)
        {
            if (prototype.Visible) result.Changes.Add(new CellChange { Id = prototype.Id, Visible = false });
        }
        else if (extra > 0)
        {
            int    k    = rows.Count;
            string more = $"… {extra.ToString("N0", CultureInfo.CurrentCulture)} more";
            AddCopy(result, owner, prototype, protoBounds, parts, MadeId(owner.Id, "more", "rows"), "", firstIndex + k, top + k * rowHeight, null, null, more);
        }

        // the node grows (or shrinks) to its rows
        int shown = Math.Max(1, rows.Count + (extra > 0 ? 1 : 0));
        // (from the bounds the template may already have changed, e.g. a table's width)
        if ((result.Changes.LastOrDefault(c => c.Id == owner.Id && c.Bounds is not null)?.Bounds ?? owner.Bounds) is { } b)
        {
            var fitted = Fit(owner, b, top + shown * rowHeight);
            if (fitted != b) result.Changes.Add(new CellChange { Id = owner.Id, Bounds = fitted });
        }

        result.RemoveStale(snapshot);
        return result;
    }

    /// <summary>The template's parts below the prototype (a table row's cells; nothing for a list line), making any column changes on the way.</summary>
    protected abstract List<Part> Template(BindContext context, BoundCell prototype, IRowSource source, BindResult result);

    /// <summary>The node's bounds once its content is <paramref name="contentHeight" /> high.</summary>
    protected virtual DiagramBounds Fit(BoundCell owner, DiagramBounds bounds, double contentHeight)
    {
        return bounds with { Height = contentHeight };
    }

    /// <summary>Adds (or updates) one copy of the prototype and its parts.</summary>
    private static void AddCopy(BindResult result, BoundCell owner, BoundCell prototype, DiagramBounds pb, List<Part> parts, string rowId, string key, int index,
                                double y, BindingScope? scope, IRowItem? row, string? fixedText)
    {
        var attributes = CopyAttributes(prototype);
        attributes[DiagramNodeSchema.BoundOfAttribute] = owner.Id;
        attributes[DiagramNodeSchema.KeyAttribute]     = key;
        if (fixedText is not null) attributes[DiagramNodeSchema.MoreAttribute] = "1";

        var copy = new CellChange
        {
            Id         = rowId,
            Add        = true,
            Parent     = owner.Id,
            Index      = index,
            Style      = prototype.Style,
            Label      = parts.Count == 0 ? fixedText ?? prototype.Label : prototype.Label,
            Attributes = attributes,
            Bounds     = pb with { Y = y }
        };
        if (fixedText is not null && parts.Count == 0) copy.StyleEntries = new() { ["fontStyle"] = "2" };
        if (scope is not null && fixedText is null) Fill(copy, prototype.Bindings(), scope, row);
        if (fixedText is not null) RemoveBindings(copy);

        result.Changes.Add(copy);
        result.Keep.Add(rowId);
        if (scope is not null) result.Scopes[rowId] = scope;

        for (int j = 0; j < parts.Count; j++)
        {
            var part   = parts[j];
            string id  = MadeId(owner.Id, "cell", key + "/" + j.ToString(CultureInfo.InvariantCulture));
            var attrs  = new Dictionary<string, string?>(part.Attributes, StringComparer.Ordinal)
            {
                [DiagramNodeSchema.BoundOfAttribute] = owner.Id,
                [DiagramNodeSchema.KeyAttribute]     = key
            };

            var cell = new CellChange
            {
                Id         = id,
                Add        = true,
                Parent     = rowId,
                Index      = j,
                Style      = part.Style,
                Label      = fixedText is not null ? j == 0 ? fixedText : "" : part.Label,
                Attributes = attrs,
                Bounds     = part.Bounds
            };

            if (fixedText is not null)
            {
                RemoveBindings(cell);
                if (j == 0) cell.StyleEntries = new() { ["fontStyle"] = "2" };
            }
            else if (scope is not null)
            {
                Fill(cell, part.Bindings, scope, row);
                result.Scopes[id] = scope;
            }

            result.Changes.Add(cell);
            result.Keep.Add(id);
        }
    }

    /// <summary>A "more" copy shows fixed text: the bindings it copied from the prototype go.</summary>
    private static void RemoveBindings(CellChange change)
    {
        if (change.Attributes is null) return;
        foreach (string k in change.Attributes.Keys.Where(k => k.StartsWith("silo-label", StringComparison.Ordinal) ||
                                                               k.StartsWith("silo-tooltip", StringComparison.Ordinal) ||
                                                               k.StartsWith("silo-style.", StringComparison.Ordinal) ||
                                                               k.StartsWith("silo-attr.", StringComparison.Ordinal)).ToList())
            change.Attributes.Remove(k);
    }

    private static string Unique(HashSet<string> seen, string key)
    {
        string k = string.IsNullOrEmpty(key) ? "_" : key;
        for (int n = 2; !seen.Add(k); n++) k = $"{key}#{n}";
        return k;
    }

    protected static void Fail(BindResult result, BoundCell owner, string message)
    {
        result.Changes.Add(new CellChange { Id = owner.Id, Attributes = new() { [DiagramNodeSchema.ErrorAttribute] = message } });
    }

    protected static Part PartOf(BoundCell cell)
    {
        return new Part(cell.Id, cell.Style, CopyAttributes(cell), cell.Bindings().ToList(), cell.Label, cell.Bounds ?? new DiagramBounds(0, 0, 120, 26));
    }
}

/// <summary>
///     The List node: a UML-style list (a swimlane with a stack layout) whose prototype is one line; every row is a copy of
///     the line.
/// </summary>
[Singleton(typeof(IPaletteProvider))]
public sealed class ListNodeBinder : RowsNodeBinder
{
    public override string Kind         => "list";
    public override Type   SettingsType => typeof(ListNode);

    protected override DrawioPaletteEntry CreatePaletteEntry()
    {
        const string lane = "swimlane;fontStyle=1;childLayout=stackLayout;horizontal=1;startSize=26;horizontalStack=0;resizeParent=1;" +
                            "resizeParentMax=0;resizeLast=0;collapsible=1;marginBottom=0;whiteSpace=wrap;html=1;fillColor=#e1d5e7;strokeColor=#9673a6;";
        const string line = "text;strokeColor=none;fillColor=none;align=left;verticalAlign=top;spacingLeft=4;spacingRight=4;overflow=hidden;rotatable=0;" +
                            "points=[[0,0.5],[1,0.5]];portConstraint=eastwest;whiteSpace=wrap;html=1;";

        string xml = PaletteTemplates.Model(
            $"<object id=\"2\" label=\"List\" silo-label=\"{{.}}\"{DefaultAttributesXml()}>" +
            $"<mxCell style=\"{lane}\" vertex=\"1\" parent=\"1\"><mxGeometry width=\"180\" height=\"52\" as=\"geometry\"/></mxCell></object>" +
            $"<object id=\"3\" label=\"item\" silo-prototype=\"1\" silo-label=\"{{item}}\">" +
            $"<mxCell style=\"{line}\" vertex=\"1\" parent=\"2\"><mxGeometry y=\"26\" width=\"180\" height=\"26\" as=\"geometry\"/></mxCell></object>");

        return new DrawioPaletteEntry(lane, 180, 52, "List", "List node (one line per row of a URI)", xml);
    }

    protected override List<Part> Template(BindContext context, BoundCell prototype, IRowSource source, BindResult result)
    {
        return [];
    }
}

/// <summary>
///     The Table node: a draw.io table (<c>shape=table</c>) whose prototype is a table row; every data row is a copy of it.
///     With automatic columns, the header row (<c>silo-header=1</c>) and the prototype get one cell per shown column
///     (<c>silo-column</c>), keeping the cells and widths the person already has.
/// </summary>
[Singleton(typeof(IPaletteProvider))]
public sealed class TableNodeBinder : RowsNodeBinder
{
    private const string CellStyle = "shape=partialRectangle;html=1;whiteSpace=wrap;connectable=0;strokeColor=inherit;overflow=hidden;fillColor=none;" +
                                     "top=0;left=0;bottom=0;right=0;pointerEvents=1;align=left;spacingLeft=4;";

    private const string RowStyle = "shape=tableRow;horizontal=0;startSize=0;swimlaneHead=0;swimlaneBody=0;strokeColor=inherit;top=0;left=0;bottom=0;" +
                                    "right=0;collapsible=0;dropTarget=0;fillColor=none;points=[[0,0.5],[1,0.5]];portConstraint=eastwest;";

    private const double DefaultWidth = 120;

    public override string Kind         => "table";
    public override Type   SettingsType => typeof(TableNode);

    protected override DrawioPaletteEntry CreatePaletteEntry()
    {
        const string table = "shape=table;startSize=26;container=1;collapsible=0;childLayout=tableLayout;fixedRows=1;rowLines=1;fontStyle=1;" +
                             "align=center;resizeLast=1;html=1;whiteSpace=wrap;fillColor=#d5e8d4;strokeColor=#82b366;";

        string headerCell(int id, int x, string column) =>
            $"<object id=\"{id}\" label=\"{column}\" silo-column=\"{column}\"><mxCell style=\"{CellStyle}fontStyle=1;\" vertex=\"1\" parent=\"3\">" +
            $"<mxGeometry x=\"{x}\" width=\"120\" height=\"26\" as=\"geometry\"/></mxCell></object>";

        string protoCell(int id, int x, string column) =>
            $"<object id=\"{id}\" label=\"{column}\" silo-column=\"{column}\" silo-label=\"{{row:{column}}}\"><mxCell style=\"{CellStyle}\" vertex=\"1\" parent=\"4\">" +
            $"<mxGeometry x=\"{x}\" width=\"120\" height=\"26\" as=\"geometry\"/></mxCell></object>";

        string xml = PaletteTemplates.Model(
            $"<object id=\"2\" label=\"Table\" silo-label=\"{{.}}\"{DefaultAttributesXml()}>" +
            $"<mxCell style=\"{table}\" vertex=\"1\" parent=\"1\"><mxGeometry width=\"240\" height=\"78\" as=\"geometry\"/></mxCell></object>" +
            $"<object id=\"3\" label=\"\" silo-header=\"1\"><mxCell style=\"{RowStyle}\" vertex=\"1\" parent=\"2\"><mxGeometry y=\"26\" width=\"240\" height=\"26\" as=\"geometry\"/></mxCell></object>" +
            headerCell(5, 0, "Name") + headerCell(6, 120, "Value") +
            $"<object id=\"4\" label=\"\" silo-prototype=\"1\"><mxCell style=\"{RowStyle}\" vertex=\"1\" parent=\"2\"><mxGeometry y=\"52\" width=\"240\" height=\"26\" as=\"geometry\"/></mxCell></object>" +
            protoCell(7, 0, "Name") + protoCell(8, 120, "Value"));

        return new DrawioPaletteEntry(table, 240, 78, "Table", "Table node (one row per row of a URI)", xml);
    }

    protected override List<Part> Template(BindContext context, BoundCell prototype, IRowSource source, BindResult result)
    {
        var snapshot = context.Snapshot;
        var settings = (TableNode)context.Settings;
        var cells    = snapshot.ChildrenOf(prototype.Id);

        if (!settings.AutoColumns) return cells.Select(PartOf).ToList();

        var columns = Columns(settings.Columns, source.Columns);
        if (columns.Count == 0) return cells.Select(PartOf).ToList();

        var header = snapshot.ChildrenOf(snapshot.Owner.Id).FirstOrDefault(c => c.Is(DiagramNodeSchema.HeaderAttribute));

        // widths: what the person gave a column in the prototype (or header), else the default
        var protoByColumn  = Assign(cells, columns);
        var headerByColumn = header is null ? new Dictionary<string, BoundCell>() : Assign(snapshot.ChildrenOf(header.Id), columns);

        var    parts = new List<Part>();
        double x     = 0;
        for (int j = 0; j < columns.Count; j++)
        {
            var (name, caption) = columns[j];
            protoByColumn.TryGetValue(name, out var pc);
            headerByColumn.TryGetValue(name, out var hc);
            double width = pc?.Bounds?.Width ?? hc?.Bounds?.Width ?? DefaultWidth;
            double rowH  = prototype.Bounds?.Height ?? 26;

            // the prototype's cell for this column
            if (pc is null)
            {
                string id = MadeId(snapshot.Owner.Id, "proto", name);
                var attributes = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [DiagramNodeSchema.ColumnAttribute]  = name,
                    [DiagramNodeSchema.BoundOfAttribute] = snapshot.Owner.Id,
                    ["silo-label"]                       = $"{{row:{name}}}"
                };
                string style = cells.FirstOrDefault()?.Style ?? CellStyle;
                var bounds = new DiagramBounds(x, 0, width, rowH);
                result.Changes.Add(new CellChange { Id = id, Add = true, Parent = prototype.Id, Index = j, Style = style, Label = "", Attributes = attributes, Bounds = bounds });
                result.Keep.Add(id);
                parts.Add(new Part(id, style, StripMachinery(attributes), [("silo-label", $"{{row:{name}}}")], "", bounds));
            }
            else
            {
                var bounds = (pc.Bounds ?? new DiagramBounds(x, 0, width, rowH)) with { X = x, Width = width };
                result.Changes.Add(new CellChange
                {
                    Id = pc.Id, Index = j, Bounds = bounds,
                    Attributes = new() { [DiagramNodeSchema.ColumnAttribute] = name }
                });
                if (pc.Attribute(DiagramNodeSchema.BoundOfAttribute) is not null) result.Keep.Add(pc.Id);
                var attributes = CopyAttributes(pc);
                attributes[DiagramNodeSchema.ColumnAttribute] = name;
                parts.Add(new Part(pc.Id, pc.Style, attributes, pc.Bindings().ToList(), pc.Label, bounds));
            }

            // the header's cell for this column
            if (header is not null)
            {
                if (hc is null)
                {
                    string id = MadeId(snapshot.Owner.Id, "head", name);
                    string style = snapshot.ChildrenOf(header.Id).FirstOrDefault()?.Style ?? CellStyle + "fontStyle=1;";
                    result.Changes.Add(new CellChange
                    {
                        Id = id, Add = true, Parent = header.Id, Index = j, Style = style, Label = caption,
                        Attributes = new() { [DiagramNodeSchema.ColumnAttribute] = name, [DiagramNodeSchema.BoundOfAttribute] = snapshot.Owner.Id },
                        Bounds = new DiagramBounds(x, 0, width, header.Bounds?.Height ?? 26)
                    });
                    result.Keep.Add(id);
                }
                else
                {
                    var change = new CellChange
                    {
                        Id = hc.Id, Index = j, Bounds = (hc.Bounds ?? new DiagramBounds(x, 0, width, 26)) with { X = x, Width = width },
                        Attributes = new() { [DiagramNodeSchema.ColumnAttribute] = name }
                    };
                    // a header cell shows its caption unless the person bound it to something else
                    if (hc.Attribute("silo-label") is null) change.Label = caption;
                    result.Changes.Add(change);
                    if (hc.Attribute(DiagramNodeSchema.BoundOfAttribute) is not null) result.Keep.Add(hc.Id);
                }
            }

            x += width;
        }

        // cells of columns no longer shown go (the person's own unassigned cells were adopted above, in order)
        foreach (var c in cells.Where(c => !protoByColumn.Values.Contains(c)))
            result.Changes.Add(CellChange.Delete(c.Id));
        if (header is not null)
            foreach (var c in snapshot.ChildrenOf(header.Id).Where(c => !headerByColumn.Values.Contains(c)))
                result.Changes.Add(CellChange.Delete(c.Id));

        // the table and its rows are as wide as the columns
        var owner = snapshot.Owner;
        if (owner.Bounds is { } ob && Math.Abs(ob.Width - x) > 0.5) result.Changes.Add(new CellChange { Id = owner.Id, Bounds = ob with { Width = x } });
        foreach (var row in new[] { header, prototype })
            if (row?.Bounds is { } rb && Math.Abs(rb.Width - x) > 0.5)
                result.Changes.Add(new CellChange { Id = row.Id, Bounds = rb with { Width = x } });

        return parts;
    }

    /// <summary>The shown columns (name, header caption): the setting's list (<c>Name as Caption</c>) or every source column.</summary>
    private static List<(string Name, string Caption)> Columns(string? setting, IReadOnlyList<string> source)
    {
        if (string.IsNullOrWhiteSpace(setting)) return source.Take(30).Select(c => (c, c)).ToList();

        return setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Select(entry =>
                              {
                                  int at = entry.IndexOf(" as ", StringComparison.OrdinalIgnoreCase);
                                  return at > 0 ? (entry[..at].Trim(), entry[(at + 4)..].Trim()) : (entry, entry);
                              })
                      .ToList();
    }

    /// <summary>
    ///     Which existing cell is which column: by <c>silo-column</c>; cells without one are given the columns no cell has
    ///     yet, in order (so a table the person drew by hand is adopted rather than replaced).
    /// </summary>
    private static Dictionary<string, BoundCell> Assign(IReadOnlyList<BoundCell> cells, List<(string Name, string Caption)> columns)
    {
        var map = new Dictionary<string, BoundCell>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cells)
            if (c.Attribute(DiagramNodeSchema.ColumnAttribute) is { } col && columns.Any(x => x.Name.Equals(col, StringComparison.OrdinalIgnoreCase)) && !map.ContainsKey(col))
                map[col] = c;

        var free = new Queue<BoundCell>(cells.Where(c => c.Attribute(DiagramNodeSchema.ColumnAttribute) is null));
        foreach (var (name, _) in columns)
            if (!map.ContainsKey(name) && free.Count > 0)
                map[name] = free.Dequeue();

        return map;
    }

    private static Dictionary<string, string?> StripMachinery(Dictionary<string, string?> attributes)
    {
        return attributes.Where(a => !DiagramNodeSchema.StructuralAttributes.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
    }
}
