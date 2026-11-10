using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Silo.DataEditing.Sprites;

/// <summary>
///     Sprite mappings and DPLCs as the Sonic 2 disassembly keeps them: assembler text built from the macros of <c>mappings/MapMacros.asm</c>
///     (<c>mappingsTable</c>, <c>mappingsTableEntry.w</c>, <c>spriteHeader</c>, <c>spritePiece</c>, <c>dplcHeader</c>, <c>dplcEntry</c>). Reading
///     follows the labels (frames that several entries share come out as the same frame again, and local <c>.labels</c> belong to the label before
///     them); writing makes new labels, one per distinct frame, and puts the sizes the macros work out where the data is.
/// </summary>
public static partial class SpriteAsm
{
    [GeneratedRegex(@"^\s*(?<label>[A-Za-z_.][A-Za-z0-9_.]*)\s*:?\s*(?<rest>.*)$")]
    private static partial Regex LabelLine();

    /// <summary>Whether the text uses the mapping macros: a sprite mappings or DPLC file of the disassembly.</summary>
    public static bool LooksLikeAsm(string text) => text.Contains("mappingsTable", StringComparison.Ordinal);

    /// <summary>Whether the text is a DPLC (it has <c>dplcHeader</c>s) rather than sprite mappings.</summary>
    public static bool IsDplc(string text) => text.Contains("dplcHeader", StringComparison.Ordinal);

    // ---- reading ---------------------------------------------------------------------------------

    private sealed record Line(int Number, string Label, string Op, List<string> Args);

    /// <summary>Cuts the text into lines of (label, instruction, arguments); a label stands alone or before an instruction, as in the disassembly.</summary>
    private static IEnumerable<Line> Lines(string text)
    {
        int n = 0;
        foreach (string raw in text.Split('\n'))
        {
            n++;
            string line = raw.Split(';')[0].TrimEnd('\r', ' ', '\t');
            if (line.Trim().Length == 0) continue;

            string label = string.Empty, rest = line;
            // a label starts in the first column (the disassembly never indents one); an indented line is an instruction
            if (!char.IsWhiteSpace(line[0]) && LabelLine().Match(line) is { Success: true } m)
            {
                label = m.Groups["label"].Value;
                rest  = m.Groups["rest"].Value;
            }

            rest = rest.Trim();
            if (rest.Length == 0)
            {
                yield return new Line(n, label, string.Empty, []);
                continue;
            }

            int    space = rest.IndexOfAny([' ', '\t']);
            string op    = space < 0 ? rest : rest[..space];
            var    args  = space < 0 ? [] : rest[(space + 1)..].Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            yield return new Line(n, label, op, args);
        }
    }

    /// <summary>A number as the assembler reads it: decimal, <c>$hex</c> or <c>0xhex</c>, with a minus sign.</summary>
    private static int Number(Line line, string text)
    {
        string t = text.Trim();
        bool negative = t.StartsWith('-');
        if (negative || t.StartsWith('+')) t = t[1..].Trim();

        int v;
        if (t.StartsWith('$')) v = int.TryParse(t[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int h) ? h : throw Bad(line, text);
        else if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = int.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int x) ? x : throw Bad(line, text);
        else v = int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int d) ? d : throw Bad(line, text);

        return negative ? -v : v;
    }

    private static FormatException Bad(Line line, string text) => new($"line {line.Number}: '{text}' is not a number (only numbers are read, not expressions)");

    /// <summary>The table of a file: the label of each entry, in order. Labels are scoped: a <c>.local</c> belongs to the label before it.</summary>
    private static (List<string> Entries, Dictionary<string, List<Line>> Bodies, string TableKey) Read(string text, string header)
    {
        var    entries = new List<string>();
        var    bodies  = new Dictionary<string, List<Line>>(StringComparer.Ordinal);
        string scope   = string.Empty;
        string tableScope = string.Empty, tableKey = string.Empty;
        var    pending = new List<string>(); // labels that stand on lines of their own: the instruction after them is theirs (two labels at one place are the same frame)
        List<Line>? current = null;
        bool inTable = false;

        string Key(string label) => label.StartsWith('.') ? scope + label : label;

        foreach (var line in Lines(text))
        {
            string label = line.Label;
            if (label.Length > 0)
            {
                if (!label.StartsWith('.')) scope = label;
                current = null;
                if (line.Op.Length == 0) pending.Add(Key(label));
            }
            else if (line.Op.Length > 0 && pending.Count > 0)
            {
                label = pending[^1];
            }

            switch (line.Op)
            {
                case "mappingsTable":
                    inTable    = true;
                    tableScope = scope;
                    tableKey   = Key(label);
                    pending.Clear();
                    continue;
                case "mappingsTableEntry.w" or "mappingsTableEntry":
                    if (!inTable) throw new FormatException($"line {line.Number}: a table entry before the table");
                    if (line.Args.Count != 1) throw new FormatException($"line {line.Number}: a table entry takes one label");

                    entries.Add(line.Args[0].StartsWith('.') ? tableScope + line.Args[0] : line.Args[0]);
                    continue;
            }

            if (line.Op == header)
            {
                inTable = false;
                current = [];
                foreach (string alias in pending) bodies[alias] = current;
                if (label.Length > 0) bodies[Key(label)] = current;
                pending.Clear();
                continue;
            }

            if (line.Op is "even" or "") continue;

            if (current is null) continue; // an instruction that belongs to no frame (a label of its own such as Foo_End has none)
            current.Add(line);
        }

        if (entries.Count == 0) throw new FormatException("There is no mappings table in the text (a mappingsTable with mappingsTableEntry lines).");
        return (entries, bodies, tableKey);
    }

    /// <summary>Reads sprite mappings from the disassembly's text.</summary>
    /// <exception cref="FormatException">The text is not mappings in the macros' form (the message says which line).</exception>
    public static SpriteMappings ParseMappings(string text) => ParseMappings(text, out _);

    /// <summary>
    ///     Reads sprite mappings, and says what was odd. An entry that points at the table itself (a few objects have such a first frame, which
    ///     the game never draws) comes out as an empty frame, and is noted.
    /// </summary>
    public static SpriteMappings ParseMappings(string text, out IReadOnlyList<string> notes)
    {
        var (entries, bodies, tableKey) = Read(text, "spriteHeader");
        var frames = new Dictionary<string, SpriteFrame>(StringComparer.Ordinal);
        var result = new SpriteMappings();
        var found  = new List<string>();
        notes = found;

        for (int index = 0; index < entries.Count; index++)
        {
            string entry = entries[index];
            if (!frames.TryGetValue(entry, out var frame))
            {
                if (!bodies.TryGetValue(entry, out var lines))
                {
                    if (entry != tableKey) throw new FormatException($"the table points at '{entry}', which is not a frame in the text");

                    found.Add($"Frame {index} points at the table itself (an unused frame): it is shown as empty, and written as a real empty frame.");
                    frames[entry] = new SpriteFrame();
                    result.Frames.Add(new SpriteFrame());
                    continue;
                }

                frame = new SpriteFrame();
                foreach (var line in lines)
                {
                    if (line.Op is not ("spritePiece" or "spritePiece2P")) throw new FormatException($"line {line.Number}: '{line.Op}' is not a piece of a sprite frame");
                    if (line.Args.Count < 9) throw new FormatException($"line {line.Number}: a piece needs x, y, width, height, tile, xflip, yflip, palette and priority");

                    int[] v = line.Args.Take(9).Select(a => Number(line, a)).ToArray();
                    if (v[2] is < 1 or > 4 || v[3] is < 1 or > 4) throw new FormatException($"line {line.Number}: a piece is 1 to 4 tiles each way");

                    frame.Pieces.Add(new SpritePiece { X = v[0], Y = v[1], Width = v[2], Height = v[3], Tile = v[4], XFlip = v[5] != 0, YFlip = v[6] != 0, Palette = v[7], Priority = v[8] != 0 });
                }

                frames[entry] = frame;
            }

            result.Frames.Add(frame.Clone());
        }

        return result;
    }

    /// <summary>Reads a DPLC from the disassembly's text.</summary>
    public static DplcFile ParseDplc(string text)
    {
        var (entries, bodies, _) = Read(text, "dplcHeader");
        var frames = new Dictionary<string, List<DplcRequest>>(StringComparer.Ordinal);
        var result = new DplcFile();

        foreach (string entry in entries)
        {
            if (!frames.TryGetValue(entry, out var requests))
            {
                if (!bodies.TryGetValue(entry, out var lines)) throw new FormatException($"the table points at '{entry}', which is not a frame in the text");

                requests = [];
                foreach (var line in lines)
                {
                    if (line.Op != "dplcEntry") throw new FormatException($"line {line.Number}: '{line.Op}' is not a request of a DPLC frame");
                    if (line.Args.Count != 2) throw new FormatException($"line {line.Number}: a request needs a tile count and an offset");

                    requests.Add(new DplcRequest(Number(line, line.Args[0]), Number(line, line.Args[1])));
                }

                frames[entry] = requests;
            }

            result.Frames.Add(requests.ToList());
        }

        return result;
    }

    // ---- writing ---------------------------------------------------------------------------------

    private static string Num(int v) => v < 0 ? (v > -10 ? v.ToString(CultureInfo.InvariantCulture) : "-$" + (-v).ToString("X", CultureInfo.InvariantCulture)) : v < 10 ? v.ToString(CultureInfo.InvariantCulture) : "$" + v.ToString("X", CultureInfo.InvariantCulture);

    private static string PieceLine(SpritePiece p) =>
        $"\tspritePiece\t{Num(p.X)}, {Num(p.Y)}, {p.Width}, {p.Height}, {Num(p.Tile)}, {(p.XFlip ? 1 : 0)}, {(p.YFlip ? 1 : 0)}, {p.Palette}, {(p.Priority ? 1 : 0)}";

    /// <summary>Writes sprite mappings as the disassembly's macros: <paramref name="name" /> for the table, then a label per distinct frame (named after where the frame falls in the file, as the disassembly does).</summary>
    public static string WriteMappings(SpriteMappings mappings, string name)
    {
        var frames = Distinct(mappings.Frames.Select(f => (f, string.Join("|", f.Pieces.Select(PieceLine)))).ToList(), name, f => 2 + f.Pieces.Count * 8, mappings.Frames.Count * 2);
        var sb     = new StringBuilder();
        sb.Append(name).Append(":\tmappingsTable\n");
        foreach (var label in frames.Order) sb.Append("\tmappingsTableEntry.w\t").Append(label).Append('\n');
        sb.Append('\n');

        foreach (var (label, frame) in frames.Distinct)
        {
            sb.Append(label).Append(":\tspriteHeader\n");
            foreach (var piece in frame.Pieces) sb.Append(PieceLine(piece)).Append('\n');
            sb.Append(label).Append("_End\n\n");
        }

        sb.Append("\teven\n");
        return sb.ToString();
    }

    /// <summary>Writes a DPLC as the disassembly's macros.</summary>
    public static string WriteDplc(DplcFile dplc, string name)
    {
        var sb = new StringBuilder();
        var frames = dplc.Frames.Select(f => (f, string.Join("|", f.Select(r => $"{r.Tiles},{r.Offset}")))).ToList();

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var order  = new List<string>();
        var shown  = new List<(string Label, List<DplcRequest> Frame)>();
        int offset = dplc.Frames.Count * 2;
        foreach (var (frame, key) in frames)
        {
            if (!labels.TryGetValue(key, out string? label))
            {
                label       = $"{name}_{offset:X4}";
                labels[key] = label;
                shown.Add((label, frame));
                offset += 2 + frame.Count * 2;
            }

            order.Add(label);
        }

        sb.Append(name).Append(":\tmappingsTable\n");
        foreach (string label in order) sb.Append("\tmappingsTableEntry.w\t").Append(label).Append('\n');
        sb.Append('\n');
        foreach (var (label, frame) in shown)
        {
            sb.Append(label).Append(":\tdplcHeader\n");
            foreach (var r in frame) sb.Append("\tdplcEntry\t").Append(r.Tiles).Append(", ").Append(Num(r.Offset)).Append('\n');
            sb.Append(label).Append("_End\n\n");
        }

        sb.Append("\teven\n");
        return sb.ToString();
    }

    private static (List<string> Order, List<(string Label, SpriteFrame Frame)> Distinct) Distinct(List<(SpriteFrame Frame, string Key)> frames, string name, Func<SpriteFrame, int> size, int tableSize)
    {
        var labels   = new Dictionary<string, string>(StringComparer.Ordinal);
        var order    = new List<string>();
        var distinct = new List<(string, SpriteFrame)>();
        int offset   = tableSize;
        foreach (var (frame, key) in frames)
        {
            if (!labels.TryGetValue(key, out string? label))
            {
                label       = $"{name}_{offset:X4}";
                labels[key] = label;
                distinct.Add((label, frame));
                offset += size(frame);
            }

            order.Add(label);
        }

        return (order, distinct);
    }

    /// <summary>
    ///     What a file of the disassembly's <c>mappings</c> folder is when it is not sprite mappings (level block and chunk maps, Enigma tile maps,
    ///     special stage data), for saying so instead of failing to read it; null when it may be sprite mappings.
    /// </summary>
    public static string? NotSpriteMappings(string path)
    {
        string p = path.Replace('\\', '/').ToLowerInvariant();
        string name = Path.GetFileName(path);
        if (p.Contains("/mappings/16x16/")) return $"{name} is a level 16x16 block map: each block is four tiles, not sprite pieces.";
        if (p.Contains("/mappings/128x128/")) return $"{name} is a level 128x128 chunk map: each chunk is a grid of 16x16 blocks, not sprite pieces.";
        if (p.EndsWith(".eni")) return $"{name} is an Enigma-compressed tile map (the layout of a screen), not sprite mappings.";
        if (p.Contains("/mappings/special stage/")) return $"{name} is special stage track data, not sprite mappings.";
        return null;
    }

    /// <summary>The name a table gets from the file's name: <c>Map_</c> and the name with anything but letters and digits made into <c>_</c>.</summary>
    public static string TableName(string path, bool dplc)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        return (dplc ? "DPLC_" : "Map_") + Regex.Replace(stem, @"[^A-Za-z0-9]+", "_").Trim('_');
    }
}
