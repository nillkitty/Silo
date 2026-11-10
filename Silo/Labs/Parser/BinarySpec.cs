namespace Silo.Labs.Parser;

/// <summary>
///     A spec in <see cref="SpecMode.Binary" /> read over bytes: the same rules, <c>seq</c>, <c>choice</c>, <c>repeat</c>, <c>named</c> and
///     <c>ref</c> nodes as the text mode, with leaves that read numbers instead of characters.
/// </summary>
/// <remarks>
///     Binary kinds: <c>int</c> (<see cref="SpecNode.Text" /> is <c>u8 s8 u16 s16 u32 s32</c> with <c>be</c> (the default) or <c>le</c>:
///     <c>u16le</c>), <c>bits</c> (a big-endian word cut into fields, most significant first: <c>pri:1,pal:2,yflip:1,xflip:1,tile:11</c>; the value is
///     a record, and in a <c>seq</c> its fields join the record of the sequence), <c>bytes</c> (these bytes, as hex: <c>53 4D</c>), <c>skip</c> (that many bytes),
///     <c>counted</c> (an <c>int</c>, then that many of the second child) and <c>offsets</c> (a table of <c>int</c> entries relative to where the table
///     starts, <see cref="SpecNode.Text" /> the entry type; the first entry says how long the table is; each entry is where one item of the child is read).
///     A sequence with named children is a record (<see cref="Dictionary{TKey,TValue}" /> of name to value); otherwise a list. Bytes nothing read
///     are not an error: <c>Consumed</c> is how far the reading got.
/// </remarks>
public sealed class CompiledBinarySpec
{
    private readonly Dictionary<string, RuleDef>   _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SpecDiagnostic>          _diagnostics = [];

    private CompiledBinarySpec(ParserSpecDoc doc)
    {
        Doc = doc;
        foreach (var rule in doc.Rules.Where(r => !string.IsNullOrWhiteSpace(r.Name)))
            _rules[rule.Name] = rule;

        foreach (var rule in _rules.Values)
            if (rule.Body is null) _diagnostics.Add(new SpecDiagnostic(rule.Name, "The rule is empty."));
            else Check(rule.Body, rule.Name);
    }

    public ParserSpecDoc Doc { get; }

    public IReadOnlyList<SpecDiagnostic> Diagnostics => _diagnostics;

    public string? DefaultRule => Doc.Root is { Length: > 0 } root && _rules.ContainsKey(root) ? root : Doc.Rules.FirstOrDefault()?.Name;

    public static CompiledBinarySpec Compile(ParserSpecDoc doc) => new(doc.Clone());

    /// <summary>Reads <paramref name="data" /> from its start as <paramref name="rule" /> (the spec's root when null).</summary>
    public SpecResult Parse(ReadOnlySpan<byte> data, string? rule = null)
    {
        string? name = rule ?? DefaultRule;
        if (name is null || !_rules.TryGetValue(name, out var def) || def.Body is null)
            return SpecResult.Failed(0, name is null ? "The spec has no rules." : $"There is no rule called '{name}'.");

        var run = new Run(this, data.ToArray());
        try
        {
            object? value = run.Eval(def.Body, 0, out int end);
            return new SpecResult(true, value, Math.Max(end, run.Furthest), -1, null, TimeSpan.Zero);
        }
        catch (BinaryFault f)
        {
            return SpecResult.Failed(run.FaultAt >= 0 ? run.FaultAt : f.Position, "expected " + (run.FaultAt >= 0 ? run.Expected : f.Expected));
        }
    }

    /// <summary>
    ///     Writes <paramref name="value" /> (the shape <see cref="Parse" /> gives) back as bytes: the reverse of reading, so a file read and written
    ///     with the same spec comes out the same (an offset table puts equal items once and points every entry at it).
    /// </summary>
    /// <exception cref="FormatException">The value does not have the shape the rule reads, or a number does not fit its type.</exception>
    public byte[] Write(object? value, string? rule = null)
    {
        string? name = rule ?? DefaultRule;
        if (name is null || !_rules.TryGetValue(name, out var def) || def.Body is null)
            throw new FormatException(name is null ? "The spec has no rules." : $"There is no rule called '{name}'.");

        var buffer = new List<byte>();
        new Writer(this).Write(def.Body, value, buffer, 0);
        return buffer.ToArray();
    }

    private sealed class Writer(CompiledBinarySpec spec)
    {
        private int _depth;

        public void Write(SpecNode node, object? value, List<byte> output, int depth)
        {
            if (++_depth > 200) throw new FormatException("The rules refer to each other without end.");
            try
            {
                WriteNode(node, value is NamedValue nv && node.Kind != "named" ? nv.Value : value, output);
            }
            finally
            {
                _depth--;
            }
        }

        private static long Number(object? v) => v switch
                                                 {
                                                     null                                            => 0,
                                                     NamedValue n                                    => Number(n.Value),
                                                     Dictionary<string, object?> { Count: 1 } d      => Number(d.Values.First()),
                                                     _                                               => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)
                                                 };

        private static void PutInt(string? type, long v, List<byte> output, int at = -1)
        {
            var (size, signed, big) = IntType(type) ?? throw new FormatException($"'{type}' is not a number type.");
            long min = signed ? -(1L << (size * 8 - 1)) : 0;
            long max = signed ? (1L << (size * 8 - 1)) - 1 : (1L << (size * 8)) - 1;
            if (v < min || v > max) throw new FormatException($"{v} does not fit {type}.");

            for (int i = 0; i < size; i++)
            {
                byte b = (byte)((v >> (8 * (big ? size - 1 - i : i))) & 0xFF);
                if (at >= 0) output[at + i] = b;
                else output.Add(b);
            }
        }

        private void WriteNode(SpecNode node, object? value, List<byte> output)
        {
            switch (node.Kind)
            {
                case "int":
                    PutInt(node.Text, Number(value), output);
                    return;

                case "bits":
                {
                    var fields = Fields(node.Text) ?? throw new FormatException($"'{node.Text}' is not a list of bit fields.");
                    var record = value as IDictionary<string, object?> ?? new Dictionary<string, object?>();
                    int total  = fields.Sum(f => f.Width);
                    ulong word = 0;
                    foreach (var (name, width) in fields)
                    {
                        long v = record.TryGetValue(name, out var x) ? Number(x) : 0;
                        if (v < 0 || v >= 1L << width) throw new FormatException($"{name} = {v} does not fit {width} bit(s).");

                        word = (word << width) | (ulong)v;
                    }

                    for (int i = total / 8 - 1; i >= 0; i--) output.Add((byte)((word >> (8 * i)) & 0xFF));
                    return;
                }

                case "bytes":
                    output.AddRange(HexBytes(node.Text) ?? []);
                    return;

                case "skip":
                    output.AddRange(new byte[int.TryParse(node.Text, out int n) ? n : 0]);
                    return;

                case "eof":
                    return;

                case "ref":
                {
                    if (!spec._rules.TryGetValue(node.Text ?? string.Empty, out var def) || def.Body is null) throw new FormatException($"There is no rule called '{node.Text}'.");

                    Write(def.Body, value, output, 0);
                    return;
                }

                case "named":
                    if (node.Children.Count > 0) Write(node.Children[0], value is NamedValue nv ? nv.Value : value, output, 0);
                    return;

                case "seq":
                    WriteSeq(node, value, output);
                    return;

                case "choice":
                {
                    foreach (var child in node.Children)
                    {
                        var attempt = new List<byte>(output);
                        try
                        {
                            Write(child, value, attempt, 0);
                            output.Clear();
                            output.AddRange(attempt);
                            return;
                        }
                        catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException or OverflowException)
                        {
                        }
                    }

                    throw new FormatException("The value fits none of the choices.");
                }

                case "repeat":
                {
                    var items = AsList(value);
                    if (items.Count < node.Min || (node.Max is { } max && items.Count > max)) throw new FormatException($"{items.Count} item(s) where {node.Min}..{node.Max?.ToString() ?? "*"} are read.");

                    foreach (var item in items) Write(node.Children[0], item, output, 0);
                    return;
                }

                case "counted":
                {
                    var items = AsList(value);
                    Write(node.Children[0], (long)items.Count, output, 0);
                    foreach (var item in items) Write(node.Children[1], item, output, 0);
                    return;
                }

                case "offsets":
                {
                    var items = AsList(value);
                    var (size, _, _) = IntType(node.Text) ?? throw new FormatException($"'{node.Text}' is not a number type.");
                    int start = output.Count;
                    output.AddRange(new byte[items.Count * size]);

                    var written = new List<(byte[] Bytes, int Offset)>();
                    for (int i = 0; i < items.Count; i++)
                    {
                        var body = new List<byte>();
                        Write(node.Children[0], items[i], body, 0);
                        byte[] bytes = body.ToArray();

                        int offset = -1;
                        foreach (var w in written)
                            if (w.Bytes.AsSpan().SequenceEqual(bytes))
                            {
                                offset = w.Offset;
                                break;
                            }

                        if (offset < 0)
                        {
                            offset = output.Count - start;
                            output.AddRange(bytes);
                            written.Add((bytes, offset));
                        }

                        PutInt(node.Text, offset, output, start + i * size);
                    }

                    return;
                }

                default:
                    throw new FormatException($"There is no kind of node called '{node.Kind}' for binary data.");
            }
        }

        private static List<object?> AsList(object? value) => value switch
                                                              {
                                                                  null                          => [],
                                                                  string                        => throw new FormatException("A list was expected."),
                                                                  System.Collections.IEnumerable e => e.Cast<object?>().ToList(),
                                                                  _                             => throw new FormatException("A list was expected.")
                                                              };

        private void WriteSeq(SpecNode node, object? value, List<byte> output)
        {
            bool record = node.Children.Any(c => c.Kind is "named" or "bits");
            var  dict   = value as IDictionary<string, object?>;
            int  kept   = node.Children.Count(c => !c.Ignore && c.Kind is not ("skip" or "bytes" or "eof"));
            var  list   = record ? [] : (kept == 1 ? [value] : AsList(value));
            if (record && dict is null) throw new FormatException("A record was expected.");

            int next = 0;
            foreach (var child in node.Children)
            {
                if (child.Kind is "skip" or "bytes" or "eof") { WriteNode(child, null, output); continue; }

                if (record)
                {
                    if (child.Kind == "named") Write(child, dict!.TryGetValue(child.Name ?? string.Empty, out var v) ? v : null, output, 0);
                    else if (child.Kind == "bits") WriteNode(child, dict, output);
                    else Write(child, null, output, 0);
                }
                else
                {
                    Write(child, child.Ignore ? null : next < list.Count ? list[next++] : null, output, 0);
                }
            }
        }
    }

    private static readonly string[] IntTypes = ["u8", "s8", "u16", "s16", "u32", "s32"];

    private void Check(SpecNode node, string rule)
    {
        switch (node.Kind)
        {
            case "int":
            case "offsets":
                if (IntType(node.Text) is null) _diagnostics.Add(new SpecDiagnostic(rule, $"'{node.Text}' is not a number type (u8 s8 u16 s16 u32 s32, with be or le)."));
                break;
            case "bits":
                if (Fields(node.Text) is null) _diagnostics.Add(new SpecDiagnostic(rule, $"'{node.Text}' is not a list of fields (name:width, 8, 16 or 32 bits in all)."));
                break;
            case "bytes":
                if (HexBytes(node.Text) is null) _diagnostics.Add(new SpecDiagnostic(rule, $"'{node.Text}' is not hex bytes."));
                break;
            case "skip":
                if (!int.TryParse(node.Text, out int n) || n < 0) _diagnostics.Add(new SpecDiagnostic(rule, $"'{node.Text}' is not a number of bytes."));
                break;
            case "counted":
                if (node.Children.Count != 2) _diagnostics.Add(new SpecDiagnostic(rule, "Counted needs a count and an item."));
                break;
            case "ref":
                if (!_rules.ContainsKey(node.Text ?? string.Empty)) _diagnostics.Add(new SpecDiagnostic(rule, $"There is no rule called '{node.Text}'."));
                break;
            case "seq" or "choice" or "repeat" or "named" or "eof":
                break;
            default:
                _diagnostics.Add(new SpecDiagnostic(rule, $"There is no kind of node called '{node.Kind}' for binary data."));
                break;
        }

        if (node.Kind == "offsets" && node.Children.Count != 1) _diagnostics.Add(new SpecDiagnostic(rule, "Offsets needs one item."));
        foreach (var c in node.Children) Check(c, rule);
    }

    // ---- leaf definitions ---------------------------------------------------------------------

    /// <summary>(size in bytes, signed, big-endian) of a number type; null when it is not one.</summary>
    internal static (int Size, bool Signed, bool Big)? IntType(string? text)
    {
        string t = (text ?? string.Empty).Trim().ToLowerInvariant();
        bool   big = true;
        if (t.EndsWith("le")) { big = false; t = t[..^2]; }
        else if (t.EndsWith("be")) t = t[..^2];

        if (Array.IndexOf(IntTypes, t) < 0) return null;
        return (int.Parse(t[1..]) / 8, t[0] == 's', big);
    }

    internal static List<(string Name, int Width)>? Fields(string? text)
    {
        var fields = new List<(string, int)>();
        foreach (string part in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] p = part.Split(':', StringSplitOptions.TrimEntries);
            if (p.Length != 2 || p[0].Length == 0 || !int.TryParse(p[1], out int w) || w is < 1 or > 32) return null;
            fields.Add((p[0], w));
        }

        int total = fields.Sum(f => f.Item2);
        return total is 8 or 16 or 32 ? fields : null;
    }

    internal static byte[]? HexBytes(string? text)
    {
        string[] parts = (text ?? string.Empty).Split([' ', ',', '-', ':'], StringSplitOptions.RemoveEmptyEntries);
        try
        {
            return parts.Select(p => Convert.ToByte(p, 16)).ToArray();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ---- running ------------------------------------------------------------------------------

    private sealed class BinaryFault(int position, string expected) : Exception(expected)
    {
        public int Position { get; } = position;

        public string Expected { get; } = expected;
    }

    private sealed class Run(CompiledBinarySpec spec, byte[] data)
    {
        private int _depth;

        /// <summary>The furthest byte any successful read reached (what has been consumed, with items read from elsewhere).</summary>
        public int Furthest { get; private set; }

        /// <summary>Where the furthest attempt failed, and what it wanted there; -1 while nothing has failed.</summary>
        public int FaultAt { get; private set; } = -1;

        public string Expected { get; private set; } = string.Empty;

        private BinaryFault Fail(int at, string expected)
        {
            if (at > FaultAt)
            {
                FaultAt  = at;
                Expected = expected;
            }
            else if (at == FaultAt && !Expected.Split(" or ").Contains(expected) && Expected.Length < 120)
            {
                Expected += " or " + expected;
            }

            return new BinaryFault(at, expected);
        }

        private long ReadInt(string? type, int at, out int end)
        {
            var (size, signed, big) = IntType(type) ?? (1, false, true);
            if (at < 0 || at + size > data.Length) throw Fail(Math.Max(at, 0), $"{size} more byte(s) for {type}");

            long v = 0;
            for (int i = 0; i < size; i++)
            {
                int b = data[at + (big ? i : size - 1 - i)];
                v = (v << 8) | (uint)b;
            }

            if (signed && size < 8 && (v & (1L << (size * 8 - 1))) != 0) v -= 1L << (size * 8);
            end = at + size;
            Furthest = Math.Max(Furthest, end);
            return v;
        }

        public object? Eval(SpecNode node, int at, out int end)
        {
            if (++_depth > 200) throw Fail(at, "a shallower structure (rules refer to each other without end)");
            try
            {
                return EvalNode(node, at, out end);
            }
            finally
            {
                _depth--;
            }
        }

        private object? EvalNode(SpecNode node, int at, out int end)
        {
            end = at;
            switch (node.Kind)
            {
                case "int":
                    return ReadInt(node.Text, at, out end);

                case "bits":
                {
                    var fields = Fields(node.Text) ?? throw Fail(at, "a list of bit fields");
                    int total  = fields.Sum(f => f.Width);
                    var word   = ReadWord(total / 8, at, out end);
                    var record = new Dictionary<string, object?>();
                    int shift  = total;
                    foreach (var (name, width) in fields)
                    {
                        shift -= width;
                        record[name] = (long)((word >> shift) & ((1UL << width) - 1));
                    }

                    return record;
                }

                case "bytes":
                {
                    var want = HexBytes(node.Text) ?? [];
                    if (at + want.Length > data.Length || !data.AsSpan(at, want.Length).SequenceEqual(want))
                        throw Fail(at, "the bytes " + Convert.ToHexString(want));

                    end      = at + want.Length;
                    Furthest = Math.Max(Furthest, end);
                    return null;
                }

                case "skip":
                {
                    int n = int.TryParse(node.Text, out int k) ? k : 0;
                    if (at + n > data.Length) throw Fail(at, $"{n} more byte(s)");

                    end      = at + n;
                    Furthest = Math.Max(Furthest, end);
                    return null;
                }

                case "eof":
                    if (at < data.Length) throw Fail(at, "end of data");
                    return null;

                case "ref":
                {
                    if (!spec._rules.TryGetValue(node.Text ?? string.Empty, out var def) || def.Body is null) throw Fail(at, $"a rule called '{node.Text}'");
                    return Eval(def.Body, at, out end);
                }

                case "named":
                {
                    object? v = node.Children.Count > 0 ? Eval(node.Children[0], at, out end) : null;
                    return new NamedValue(node.Name ?? string.Empty, v);
                }

                case "seq":
                    return EvalSeq(node, at, out end);

                case "choice":
                {
                    foreach (var child in node.Children)
                    {
                        try
                        {
                            return Eval(child, at, out end);
                        }
                        catch (BinaryFault)
                        {
                        }
                    }

                    throw Fail(at, "one of the choices");
                }

                case "repeat":
                {
                    var  item = node.Children.Count > 0 ? node.Children[0] : null;
                    var  list = new List<object?>();
                    int  pos  = at;
                    while (item is not null && (node.Max is null || list.Count < node.Max) && (node.Max is not null || pos < data.Length))
                    {
                        int mark = Furthest;
                        try
                        {
                            object? v = Eval(item, pos, out int next);
                            if (next == pos) { list.Add(v); break; }

                            list.Add(v);
                            pos = next;
                        }
                        catch (BinaryFault) when (list.Count >= Math.Max(0, node.Min))
                        {
                            Furthest = mark;
                            break;
                        }
                    }

                    if (list.Count < node.Min) throw Fail(pos, $"at least {node.Min} of it (found {list.Count})");

                    end = pos;
                    return list.ToArray();
                }

                case "counted":
                {
                    long count = ToLong(Eval(node.Children[0], at, out int pos));
                    if (count < 0 || count > data.Length) throw Fail(at, "a count that fits the data");

                    var list = new object?[count];
                    for (long i = 0; i < count; i++)
                        list[i] = Eval(node.Children[1], pos, out pos);

                    end = pos;
                    return list;
                }

                case "offsets":
                {
                    var (size, _, _) = IntType(node.Text) ?? (2, false, true);
                    long first = ReadInt(node.Text, at, out _);
                    if (first <= 0 || first % size != 0 || at + first > data.Length) throw Fail(at, $"a first offset that gives the length of the table (found {first})");

                    int count = (int)(first / size);
                    var items = new object?[count];
                    for (int i = 0; i < count; i++)
                    {
                        long offset = ReadInt(node.Text, at + i * size, out _);
                        if (at + offset > data.Length) throw Fail(at + i * size, $"an offset inside the data (found {offset})");

                        items[i] = Eval(node.Children[0], (int)(at + offset), out _);
                    }

                    end = at + count * size;
                    return items;
                }

                default:
                    throw Fail(at, $"a node I know ('{node.Kind}')");
            }
        }

        private object? EvalSeq(SpecNode node, int at, out int end)
        {
            var  record = new Dictionary<string, object?>();
            var  list   = new List<object?>();
            bool named  = false;
            int  pos    = at;
            foreach (var child in node.Children)
            {
                object? v = Eval(child, pos, out pos);
                if (child.Ignore || child.Kind is "skip" or "bytes" or "eof") continue;

                if (v is NamedValue nv) { record[nv.Name] = nv.Value; named = true; }
                else if (child.Kind == "bits" && v is Dictionary<string, object?> fields)
                {
                    foreach (var (k, x) in fields) record[k] = x;
                    named = true;
                }
                else list.Add(v);
            }

            end = pos;
            if (named) return record;
            return list.Count == 1 ? list[0] : list.ToArray();
        }

        private ulong ReadWord(int size, int at, out int end)
        {
            if (at + size > data.Length) throw Fail(at, $"{size} more byte(s)");

            ulong v = 0;
            for (int i = 0; i < size; i++) v = (v << 8) | data[at + i];
            end      = at + size;
            Furthest = Math.Max(Furthest, end);
            return v;
        }

        private long ToLong(object? v) => v switch
                                          {
                                              long l                              => l,
                                              NamedValue { Value: long l }        => l,
                                              Dictionary<string, object?> d when d.Count == 1 => Convert.ToInt64(d.Values.First()),
                                              _                                   => throw Fail(Furthest, "a number")
                                          };
    }
}
