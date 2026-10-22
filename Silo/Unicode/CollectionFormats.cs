using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Silo.Unicode;

/// <summary>
///     A character in the Unicode Explorer's tray: the code point, and the name the person gave it (<c>green</c>), if
///     any.
/// </summary>
/// <param name="CodePoint">The code point.</param>
/// <param name="Label">The person's own name for it; null or empty when it has none.</param>
public sealed record CollectedCharacter(CodePoint CodePoint, string? Label = null);

/// <summary>The file formats the tray can be saved in and loaded from.</summary>
public enum CollectionFormat
{
    /// <summary>The characters, one after the other (<c>.txt</c>).</summary>
    Text,

    /// <summary>
    ///     A JSON array of objects with <c>codePoint</c>, <c>character</c>, <c>name</c>, <c>label</c>, <c>category</c>
    ///     and <c>block</c> (<c>.json</c>).
    /// </summary>
    Json,

    /// <summary>Tab-separated values with a header row (<c>.tsv</c>).</summary>
    Tsv,

    /// <summary>Comma-separated values with a header row (<c>.csv</c>).</summary>
    Csv,

    /// <summary>The code points as text, <c>U+1F7E2 U+1F7E1</c>, one per line, with the name after each (<c>.txt</c>).</summary>
    CodePointList
}

/// <summary>Writes the tray to text in one of the <see cref="CollectionFormat" />s and reads it back.</summary>
public static class CollectionFormats
{
    /// <summary>
    ///     The filter for a Save As dialog, in the order of <see cref="CollectionFormat" /> (Text, JSON, TSV, CSV, code
    ///     point list).
    /// </summary>
    public const string SaveFilter =
        "Characters (*.txt)|*.txt|JSON (*.json)|*.json|Tab-separated (*.tsv)|*.tsv|Comma-separated (*.csv)|*.csv|Code point list (*.txt)|*.txt";

    /// <summary>The filter for an Open dialog.</summary>
    public const string OpenFilter = "Character collections (*.txt;*.json;*.tsv;*.csv)|*.txt;*.json;*.tsv;*.csv|All files (*.*)|*.*";

    private static readonly string[] Header = ["CodePoint", "Character", "Name", "Label", "Category", "Block"];

    /// <summary>The format a file's extension stands for; null for an extension that stands for none.</summary>
    public static CollectionFormat? FromExtension(string? path)
    {
        return Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch
               {
                   ".txt"  => CollectionFormat.Text,
                   ".json" => CollectionFormat.Json,
                   ".tsv"  => CollectionFormat.Tsv,
                   ".csv"  => CollectionFormat.Csv,
                   _       => null
               };
    }

    /// <summary>
    ///     The characters written out in <paramref name="format" />; <paramref name="db" /> (the default database when
    ///     null) supplies names, categories and blocks.
    /// </summary>
    public static string Write(IEnumerable<CollectedCharacter> items, CollectionFormat format, UnicodeDatabase? db = null)
    {
        var list = items.ToList();
        db ??= UnicodeDatabase.Default;

        switch (format)
        {
            case CollectionFormat.Text:
                return CodePoint.ToText(list.Select(i => i.CodePoint));

            case CollectionFormat.CodePointList:
            {
                var sb = new StringBuilder();
                foreach (var i in list)
                {
                    sb.Append(i.CodePoint.Hex);
                    string name = Describe(db, i);
                    if (name.Length > 0) sb.Append("  ").Append(name);
                    sb.Append('\n');
                }

                return sb.ToString();
            }

            case CollectionFormat.Json:
            {
                var array = new JArray();
                foreach (var i in list)
                {
                    var c = db.Get(i.CodePoint);
                    var o = new JObject
                            {
                                ["codePoint"] = i.CodePoint.Hex,
                                ["character"] = i.CodePoint.Text,
                                ["name"]      = c.Name
                            };
                    if (!string.IsNullOrEmpty(i.Label)) o["label"] = i.Label;
                    o["category"] = c.Category;
                    if (c.Block is { } b) o["block"] = b.Name;
                    array.Add(o);
                }

                return array.ToString(Formatting.Indented) + "\n";
            }

            case CollectionFormat.Tsv:
            case CollectionFormat.Csv:
            {
                char sep = format == CollectionFormat.Tsv ? '\t' : ',';
                var  sb  = new StringBuilder();
                sb.Append(string.Join(sep, Header)).Append('\n');
                foreach (var i in list)
                {
                    var      c     = db.Get(i.CodePoint);
                    string[] cells = [i.CodePoint.Hex, c.Glyph, c.Name, i.Label ?? string.Empty, c.Category, c.Block?.Name ?? string.Empty];
                    sb.Append(string.Join(sep, cells.Select(x => Cell(x, sep)))).Append('\n');
                }

                return sb.ToString();
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }
    }

    /// <summary>
    ///     Reads characters from text written by <see cref="Write" />, or from any text: JSON, a TSV or CSV with a CodePoint
    ///     column, a list of
    ///     <c>U+...</c> code points, or just characters. The format is recognised from the text when
    ///     <paramref name="format" /> is null.
    /// </summary>
    /// <exception cref="FormatException">The text says it is JSON (or a table) but is not.</exception>
    public static IReadOnlyList<CollectedCharacter> Read(string? text, CollectionFormat? format = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        string trimmed = text.TrimStart('﻿').Trim();
        format ??= Sniff(trimmed);

        switch (format)
        {
            case CollectionFormat.Json:
                return ReadJson(trimmed);
            case CollectionFormat.Tsv:
                return ReadTable(trimmed, '\t');
            case CollectionFormat.Csv:
                return ReadTable(trimmed, ',');
            case CollectionFormat.CodePointList:
                return ReadCodePointList(trimmed);
            default:
                return CodePoint.ParseList(trimmed.Replace("\r", string.Empty).Replace("\n", string.Empty))
                                .Select(c => new CollectedCharacter(c))
                                .ToList();
        }
    }

    private static CollectionFormat Sniff(string text)
    {
        if (text[0] is '[' or '{') return CollectionFormat.Json;

        string first = text.Split('\n')[0].TrimEnd('\r');
        if (first.StartsWith("CodePoint\t", StringComparison.OrdinalIgnoreCase)) return CollectionFormat.Tsv;
        if (first.StartsWith("CodePoint,",  StringComparison.OrdinalIgnoreCase)) return CollectionFormat.Csv;
        if (first.StartsWith("U+", StringComparison.OrdinalIgnoreCase) && text.Contains('\n')) return CollectionFormat.CodePointList;
        return CollectionFormat.Text;
    }

    private static IReadOnlyList<CollectedCharacter> ReadJson(string text)
    {
        JToken token;
        try
        {
            token = JToken.Parse(text);
        }
        catch (JsonReaderException ex)
        {
            throw new FormatException("The file is not valid JSON: " + ex.Message, ex);
        }

        return FromJson(token);
    }

    /// <summary>
    ///     Reads characters from JSON that was already parsed: an array (or a single value) of code points written as text or numbers, or of objects with
    ///     <c>codePoint</c> (or <c>character</c>) and optionally <c>label</c>. This is what a YAML file becomes once it is converted to JSON.
    /// </summary>
    /// <exception cref="FormatException">An entry is not a code point.</exception>
    public static IReadOnlyList<CollectedCharacter> FromJson(JToken token)
    {
        // an object that wraps the list ({ "characters": [ ... ] }) is read through the list
        if (token is JObject wrapper && wrapper["characters"] is JArray inner) token = inner;

        var result = new List<CollectedCharacter>();
        foreach (var item in token is JArray array ? array : [token])
            switch (item)
            {
                case JValue { Type: JTokenType.String } v when CodePoint.TryParse((string?)v.Value, out var cp):
                    result.Add(new CollectedCharacter(cp));
                    break;
                case JValue { Type: JTokenType.Integer } v when Convert.ToInt64(v.Value) is >= 0 and <= CodePoint.MaxValue:
                    result.Add(new CollectedCharacter(new CodePoint((int)Convert.ToInt64(v.Value))));
                    break;
                case JObject o:
                {
                    string? written = (string?)o["codePoint"] ?? (string?)o["character"];
                    if (!CodePoint.TryParse(written, out var cp2))
                        throw new FormatException("An entry of the JSON has no codePoint: " + o.ToString(Formatting.None));
                    string? label = (string?)o["label"];
                    result.Add(new CollectedCharacter(cp2, string.IsNullOrWhiteSpace(label) ? null : label));
                    break;
                }
                default:
                    throw new FormatException("An entry of the JSON is not a code point: " + item.ToString(Formatting.None));
            }

        return result;
    }

    private static IReadOnlyList<CollectedCharacter> ReadTable(string text, char sep)
    {
        var rows = ParseDelimited(text, sep);
        if (rows.Count == 0) return [];

        var header   = rows[0].Select(h => h.Trim()).ToList();
        int cpCol    = header.FindIndex(h => h.Equals("CodePoint", StringComparison.OrdinalIgnoreCase));
        int labelCol = header.FindIndex(h => h.Equals("Label",     StringComparison.OrdinalIgnoreCase));
        if (cpCol < 0) throw new FormatException("The table has no CodePoint column.");

        var result = new List<CollectedCharacter>();
        foreach (var row in rows.Skip(1))
        {
            if (row.Count <= cpCol || string.IsNullOrWhiteSpace(row[cpCol])) continue;
            if (!CodePoint.TryParse(row[cpCol], out var cp)) throw new FormatException($"\"{row[cpCol]}\" is not a code point.");

            string? label = labelCol >= 0 && row.Count > labelCol ? row[labelCol] : null;
            result.Add(new CollectedCharacter(cp, string.IsNullOrWhiteSpace(label) ? null : label));
        }

        return result;
    }

    private static IReadOnlyList<CollectedCharacter> ReadCodePointList(string text)
    {
        var result = new List<CollectedCharacter>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            string token = line.Split(new[] { ' ', '\t' }, 2)[0];
            if (!CodePoint.TryParseExplicit(token, out var cp))
                throw new FormatException($"\"{token}\" is not a code point (write it like U+1F7E2).");
            result.Add(new CollectedCharacter(cp));
        }

        return result;
    }

    private static string Describe(UnicodeDatabase db, CollectedCharacter i)
    {
        string name = db.Get(i.CodePoint).Name;
        return string.IsNullOrEmpty(i.Label) ? name : i.Label + " (" + name + ")";
    }

    private static string Cell(string value, char sep)
    {
        if (value.IndexOfAny(new[] { sep, '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static List<List<string>> ParseDelimited(string text, char sep)
    {
        var  rows   = new List<List<string>>();
        var  row    = new List<string>();
        var  cell   = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == sep)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = [];
            }
            else
            {
                cell.Append(c);
            }
        }

        row.Add(cell.ToString());
        if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
        return rows;
    }
}