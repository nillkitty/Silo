using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Silo.Tabular;

/// <summary>What a column holds. It decides how a cell is edited, aligned, and written out by the exporters.</summary>
public enum CellType
{
    /// <summary>Text with inline formatting written as markdown (<c>**bold**</c>, <c>*italic*</c>, <c>`code`</c>, <c>~~strike~~</c>, <c>[link](url)</c>).</summary>
    RichText,

    /// <summary>Plain text, taken literally (nothing in it is treated as formatting).</summary>
    Text,

    /// <summary>A number; right-aligned, and written as a JSON number.</summary>
    Number,

    /// <summary>True / false; shown as a check box.</summary>
    Boolean,

    /// <summary>A date (or date and time), kept as the text that was entered.</summary>
    Date
}

/// <summary>Horizontal alignment of a column in the exported tables. <see cref="Auto" /> follows the column's type.</summary>
public enum CellAlign
{
    Auto,
    Left,
    Center,
    Right
}

/// <summary>One column of a <see cref="SheetData" />.</summary>
public sealed class SheetColumn
{
    public string Name { get; set; } = string.Empty;

    public CellType Type { get; set; } = CellType.RichText;

    public CellAlign Align { get; set; } = CellAlign.Auto;

    /// <summary>The alignment actually used: the explicit one, or the type's natural one.</summary>
    [JsonIgnore]
    public CellAlign EffectiveAlign
    {
        get
        {
            if (Align != CellAlign.Auto) return Align;
            return Type switch
                   {
                       CellType.Number  => CellAlign.Right,
                       CellType.Boolean => CellAlign.Center,
                       _                => CellAlign.Left
                   };
        }
    }

    public SheetColumn Clone() => (SheetColumn)MemberwiseClone();
}

/// <summary>
///     The content of a hand-authored table: ordered, typed columns and rows of cell text. Every cell is a string
///     (rich text cells hold inline markdown); the column type says how to read it. This is the model the editor
///     works on, the exporters read, and that is saved into the Silo as JSON.
/// </summary>
public sealed class SheetData
{
    private static readonly JsonSerializerOptions Json = new()
                                                         {
                                                             PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                                                             WriteIndented        = false,
                                                             Converters           = { new JsonStringEnumConverter() }
                                                         };

    public List<SheetColumn> Columns { get; set; } = [];

    /// <summary>Each row has exactly one entry per column (see <see cref="Normalize" />).</summary>
    public List<string[]> Rows { get; set; } = [];

    /// <summary>A starter sheet: <paramref name="columns" /> columns named A, B, C... all of one type, and some empty rows.</summary>
    public static SheetData Create(CellType type, int columns = 3, int rows = 3)
    {
        var s = new SheetData();
        for (int c = 0; c < columns; c++)
            s.Columns.Add(new SheetColumn { Name = ColumnLetter(c), Type = type });
        for (int r = 0; r < rows; r++)
            s.Rows.Add(new string[columns].Select(_ => string.Empty).ToArray());
        return s;
    }

    /// <summary>A, B, ... Z, AA, AB, ... (the default column names).</summary>
    public static string ColumnLetter(int index)
    {
        var sb = new System.Text.StringBuilder();
        for (int n = index + 1; n > 0; n = (n - 1) / 26)
            sb.Insert(0, (char)('A' + (n - 1) % 26));
        return sb.ToString();
    }

    /// <summary>Makes every row exactly as long as <see cref="Columns" /> and no cell null.</summary>
    public SheetData Normalize()
    {
        int n = Columns.Count;
        for (int i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i] ?? [];
            if (row.Length != n)
            {
                var fixedRow = new string[n];
                for (int c = 0; c < n; c++) fixedRow[c] = c < row.Length ? row[c] ?? string.Empty : string.Empty;
                row = fixedRow;
            }
            else
            {
                for (int c = 0; c < n; c++) row[c] ??= string.Empty;
            }

            Rows[i] = row;
        }

        return this;
    }

    /// <summary>A copy without the empty rows at the end (what the editor leaves behind for typing into) -- used for every export.</summary>
    public SheetData TrimTrailingBlankRows()
    {
        var copy = Clone();
        while (copy.Rows.Count > 0 && IsBlank(copy.Rows[^1])) copy.Rows.RemoveAt(copy.Rows.Count - 1);
        return copy;
    }

    /// <summary>The first column name of the form A, B, C... that is not taken yet.</summary>
    public string NextColumnName()
    {
        for (int i = Columns.Count; ; i++)
        {
            string name = ColumnLetter(i);
            if (!Columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }

    /// <summary>Inserts a column (blank in every row) at <paramref name="index" />.</summary>
    public void InsertColumn(int index, SheetColumn column)
    {
        index = Math.Clamp(index, 0, Columns.Count);
        Columns.Insert(index, column);
        for (int r = 0; r < Rows.Count; r++)
        {
            var list = Rows[r].ToList();
            list.Insert(index, string.Empty);
            Rows[r] = list.ToArray();
        }
    }

    public void RemoveColumn(int index)
    {
        if (index < 0 || index >= Columns.Count) return;
        Columns.RemoveAt(index);
        for (int r = 0; r < Rows.Count; r++)
        {
            var list = Rows[r].ToList();
            if (index < list.Count) list.RemoveAt(index);
            Rows[r] = list.ToArray();
        }
    }

    /// <summary>Moves a column (and its cells) from one position to another.</summary>
    public void MoveColumn(int from, int to)
    {
        if (from < 0 || from >= Columns.Count || to < 0 || to >= Columns.Count || from == to) return;
        var col = Columns[from];
        Columns.RemoveAt(from);
        Columns.Insert(to, col);
        for (int r = 0; r < Rows.Count; r++)
        {
            var list = Rows[r].ToList();
            var cell = list[from];
            list.RemoveAt(from);
            list.Insert(to, cell);
            Rows[r] = list.ToArray();
        }
    }

    /// <summary>True when the row has no content in any cell.</summary>
    public static bool IsBlank(string[] row) => row.All(string.IsNullOrWhiteSpace);

    /// <summary>
    ///     Whether <paramref name="other" /> holds the same columns (name, type, alignment) and cells. Blank rows at the end
    ///     are ignored: they are room for typing, not content.
    /// </summary>
    public bool ContentEquals(SheetData? other)
    {
        if (other is null || other.Columns.Count != Columns.Count) return false;

        for (int c = 0; c < Columns.Count; c++)
        {
            var a = Columns[c];
            var b = other.Columns[c];
            if (a.Type != b.Type || a.Align != b.Align || !string.Equals(a.Name, b.Name, StringComparison.Ordinal)) return false;
        }

        int rows = ContentRowCount();
        if (rows != other.ContentRowCount()) return false;

        for (int r = 0; r < rows; r++)
        {
            var x = Rows[r];
            var y = other.Rows[r];
            for (int c = 0; c < Columns.Count; c++)
            {
                string p = c < x.Length ? x[c] ?? string.Empty : string.Empty;
                string q = c < y.Length ? y[c] ?? string.Empty : string.Empty;
                if (!string.Equals(p, q, StringComparison.Ordinal)) return false;
            }
        }

        return true;
    }

    /// <summary>The number of rows up to and including the last one that is not blank.</summary>
    public int ContentRowCount()
    {
        int n = Rows.Count;
        while (n > 0 && IsBlank(Rows[n - 1])) n--;
        return n;
    }

    /// <summary>The number of cells (rows x columns): a rough measure of how much memory a copy takes.</summary>
    public long CellCount => (long)Rows.Count * Math.Max(1, Columns.Count);

    /// <summary>The header names, with blanks and duplicates made unique (a blank becomes the column letter).</summary>
    public IReadOnlyList<string> UniqueHeaders()
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(Columns.Count);
        for (int i = 0; i < Columns.Count; i++)
        {
            string name = string.IsNullOrWhiteSpace(Columns[i].Name) ? ColumnLetter(i) : Columns[i].Name.Trim();
            string key  = name;
            for (int k = 2; !seen.Add(key); k++) key = $"{name}_{k}";
            result.Add(key);
        }

        return result;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static SheetData FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new SheetData();
        return (JsonSerializer.Deserialize<SheetData>(json, Json) ?? new SheetData()).Normalize();
    }

    public SheetData Clone()
    {
        return new SheetData
               {
                   Columns = Columns.Select(c => c.Clone()).ToList(),
                   Rows    = Rows.Select(r => (string[])r.Clone()).ToList()
               };
    }
}
