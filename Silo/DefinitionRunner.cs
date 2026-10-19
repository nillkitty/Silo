using System.Buffers.Binary;
using System.Globalization;
using System.Text.Unicode;

namespace Silo.DataDefs;

/// <summary>
///     A set of characters written as ranges and single characters, e.g. <c>a-zA-Z0-9_+#</c>.
/// </summary>
internal sealed class CharSet
{
    private readonly List<(char Lo, char Hi)> _ranges = [];

    public CharSet(string spec)
    {
        var chars = new List<(char C, bool Escaped)>();
        for (int i = 0; i < spec.Length; i++)
        {
            if (spec[i] != '\\' || i + 1 >= spec.Length)
            {
                chars.Add((spec[i], false));
                continue;
            }

            char n = spec[i + 1];
            switch (n)
            {
                case 'x' when DefText.TryHex(spec, i + 2, 2, out int x):
                    chars.Add(((char)x, true));
                    i += 3;
                    break;
                case 'u' when DefText.TryHex(spec, i + 2, 4, out int u):
                    chars.Add(((char)u, true));
                    i += 5;
                    break;
                default:
                    chars.Add((n switch { 't' => '\t', 'r' => '\r', 'n' => '\n', '0' => '\0', _ => n }, true));
                    i++;
                    break;
            }
        }

        for (int i = 0; i < chars.Count; i++)
            if (i + 2 < chars.Count && chars[i + 1] is ('-', false))
            {
                _ranges.Add((chars[i].C, chars[i + 2].C));
                i += 2;
            }
            else
            {
                _ranges.Add((chars[i].C, chars[i].C));
            }
    }

    public bool Contains(char c)
    {
        foreach (var (lo, hi) in _ranges)
            if (c >= lo && c <= hi)
                return true;
        return false;
    }
}

/// <summary>
///     A view of the data being matched: bytes or text, between <see cref="Start" /> and <see cref="End" />.
/// </summary>
internal sealed class Scope
{
    private Scope(byte[]? bytes, string? text, int start, int end, bool open, Scope? parent, int parentStart, int rawLength)
    {
        Bytes       = bytes;
        Text        = text;
        Start       = start;
        End         = end;
        Open        = open;
        Parent      = parent;
        ParentStart = parentStart;
        RawLength   = rawLength;
    }

    public byte[]? Bytes { get; }

    public int End { get; }

    public bool IsText => Text is not null;

    /// <summary>
    ///     Whether <see cref="End" /> is where the available data ran out (more could follow).
    /// </summary>
    public bool Open { get; }

    public int Start { get; }

    public string? Text { get; }

    private Scope? Parent      { get; }
    private int    ParentStart { get; }
    private int    RawLength   { get; }

    public static Scope Of(byte[] data, int start, int end, bool open)
    {
        return new Scope(data, null, start, end, open, null, 0, 0);
    }

    public static Scope Of(string text, int start, int end, bool open)
    {
        return new Scope(null, text, start, end, open, null, 0, 0);
    }

    /// <summary>
    ///     A narrower view of the same buffer.
    /// </summary>
    public Scope Sub(int start, int end, bool open = false)
    {
        return new Scope(Bytes, Text, start, end, open, Parent, ParentStart, RawLength);
    }

    /// <summary>
    ///     A new text buffer standing for <paramref name="rawLength" /> characters of this one at
    ///     <paramref name="parentStart" />.
    /// </summary>
    public Scope Element(string text, int parentStart, int rawLength, bool open)
    {
        return new Scope(null, text, 0, text.Length, open, this, parentStart, rawLength);
    }

    /// <summary>
    ///     A position in this buffer as an offset in the outermost buffer of the same kind.
    /// </summary>
    public int Abs(int pos)
    {
        return Parent is null ? pos : Parent.Abs(ParentStart + Math.Min(pos, RawLength));
    }
}

/// <summary>
///     Captures made so far, newest first (shared between backtracking paths).
/// </summary>
internal sealed record Caps(CaptureNode Node, Caps? Next)
{
    public static List<CaptureNode> ToList(Caps? caps)
    {
        var list = new List<CaptureNode>();
        for (var c = caps; c is not null; c = c.Next) list.Add(c.Node);
        list.Reverse();
        return list;
    }

    /// <summary>
    ///     <paramref name="added" /> (newest first) pushed on top of <paramref name="onto" />.
    /// </summary>
    public static Caps? Append(Caps? onto, Caps? added)
    {
        if (added is null) return onto;
        foreach (var n in ToList(added)) onto = new Caps(n, onto);
        return onto;
    }
}

/// <summary>
///     The element numbers of the enclosing repeaters, innermost first.
/// </summary>
internal sealed record Inst(int Index, Inst? Outer)
{
    public int At(int depth)
    {
        var f = this;
        for (int i = 0; i < depth && f is not null; i++) f = f.Outer;
        return f?.Index ?? throw new DefinitionException($"InstanceNum {{{depth}}} refers past the outermost repeater.");
    }
}

/// <summary>
///     The state carried along one matching path.
/// </summary>
internal readonly record struct St(
    Caps?                      Caps,
    IReadOnlyDictionary<string, long> Vars,
    Inst                       Inst,
    object?                    Value,
    (int Start, int End)       ValueAt,
    Encoding?                  Encoding,
    int                        Depth);

/// <summary>
///     One way a part matched: where it ended, and the state after it.
/// </summary>
internal readonly record struct Res(int End, St St);

/// <summary>
///     Runs one match. Not thread-safe; make one per match.
/// </summary>
internal sealed class Runner
{
    private const int MaxDepth = 256;

    private static readonly IReadOnlyDictionary<string, long> NoVars = new Dictionary<string, long>();

    private readonly Dictionary<string, CharSet> _sets = new(StringComparer.Ordinal);

    /// <summary>
    ///     Set when some path failed only because the data ran out.
    /// </summary>
    public bool NeedMore { get; private set; }

    public DefinitionMatch? Run(IDefPart root, Scope scope, int number, bool whole)
    {
        var st = new St(null, NoVars, new Inst(number, null), null, default, null, 0);
        IEnumerable<Res> results = root is DataDefinition dd ? Seq(dd.Items, 0, scope, scope.Start, st) : M(root, scope, scope.Start, st);

        foreach (var r in results)
        {
            if (whole && r.End != scope.End) continue;

            string name = root switch
                          {
                              DefinitionBase<IDefPart> { Name: { } n } => n,
                              DataDefinition { ContentType: { } c }    => c,
                              _                                        => string.Empty
                          };
            var node = new CaptureNode(name, r.St.Value, scope.Abs(scope.Start), r.End - scope.Start, scope.IsText, Caps.ToList(r.St.Caps));
            return new DefinitionMatch(scope.Start, r.End - scope.Start, r.St.Encoding, node);
        }

        return null;
    }

    // ---- sequencing ------------------------------------------------------------------------------------------------

    private static (int Min, int Max) Bounds(IDefPart part, bool element)
    {
        return (part as IHasArity)?.Arity switch
               {
                   Arity.One        => (1, 1),
                   Arity.ZeroOrOne  => (0, 1),
                   Arity.OneOrMore  => (1, int.MaxValue),
                   Arity.ZeroOrMore => (0, int.MaxValue),
                   _                => element ? (0, int.MaxValue) : (1, 1)
               };
    }

    /// <summary>
    ///     Matches <paramref name="parts" /> from <paramref name="i" /> in sequence, honouring each part's arity.
    /// </summary>
    private IEnumerable<Res> Seq(IReadOnlyList<IDefPart> parts, int i, Scope s, int pos, St st)
    {
        if (i == parts.Count)
        {
            yield return new Res(pos, st);
            yield break;
        }

        var part       = parts[i] ?? throw new DefinitionException($"A definition has an empty item (#{i + 1}).");
        var (min, max) = Bounds(part, false);

        if (min == 1 && max == 1)
        {
            foreach (var r in M(part, s, pos, st))
            foreach (var x in Seq(parts, i + 1, s, r.End, r.St))
                yield return x;
            yield break;
        }

        // Greedy repetition, then give repetitions back one at a time.
        var chain = new List<Res> { new(pos, st) };
        while (chain.Count - 1 < max)
        {
            var  last = chain[^1];
            Res? next = null;
            foreach (var r in M(part, s, last.End, last.St))
            {
                if (r.End == last.End && chain.Count - 1 >= min) continue; // no progress
                next = r;
                break;
            }

            if (next is null) break;
            chain.Add(next.Value);
        }

        for (int k = chain.Count - 1; k >= min; k--)
        foreach (var x in Seq(parts, i + 1, s, chain[k].End, chain[k].St))
            yield return x;
    }

    /// <summary>
    ///     The first way <paramref name="parts" /> consume all of <paramref name="s" />.
    /// </summary>
    private St? Whole(IReadOnlyList<IDefPart> parts, Scope s, St st)
    {
        foreach (var r in Seq(parts, 0, s, s.Start, st))
            if (r.End == s.End)
                return r.St;
        return null;
    }

    /// <summary>
    ///     Matches the element sequence <paramref name="parts" /> against <paramref name="elems" />; each element is matched
    ///     whole by one part. Returns each number of elements that can be consumed (most first) with its state.
    /// </summary>
    private List<(int Count, St St)> Elements(IReadOnlyList<IDefPart> parts, IReadOnlyList<Scope> elems, St st, bool open)
    {
        var accepts = new List<(int, St)>();
        var memo    = new Dictionary<(int, int), St?>();

        St? MatchElement(int i, int j, St cur)
        {
            if (!memo.TryGetValue((i, j), out var got))
            {
                var fresh = cur with { Caps = null, Inst = new Inst(j, st.Inst), Value = null, Depth = cur.Depth + 1 };
                got = null;
                foreach (var r in M(parts[i], elems[j], elems[j].Start, fresh))
                    if (r.End == elems[j].End)
                    {
                        got = r.St;
                        break;
                    }

                memo[(i, j)] = got;
            }

            return got is { } g ? cur with { Caps = Caps.Append(cur.Caps, g.Caps), Vars = g.Vars, Encoding = cur.Encoding ?? g.Encoding } : null;
        }

        var threads = Closure([(0, 0, st)]);
        Accept(0);

        for (int j = 0; j < elems.Count && threads.Count > 0; j++)
        {
            var next = new List<(int I, int C, St St)>();
            foreach (var (i, c, tst) in threads)
                if (i < parts.Count && c < Bounds(parts[i], true).Max && MatchElement(i, j, tst) is { } ns)
                    next.Add((i, c + 1, ns));

            threads = Closure(next);
            Accept(j + 1);
        }

        if (open && threads.Any(t => t.I < parts.Count)) NeedMore = true;

        accepts.Reverse();
        return accepts;

        void Accept(int consumed)
        {
            int k = threads.FindIndex(t => t.I == parts.Count);
            if (k >= 0) accepts.Add((consumed, threads[k].St));
        }

        List<(int I, int C, St St)> Closure(List<(int I, int C, St St)> start)
        {
            var result = new List<(int I, int C, St St)>();
            var seen   = new HashSet<(int, int)>();

            void Add((int I, int C, St St) t)
            {
                if (!seen.Add((t.I, Math.Min(t.C, 1)))) return;
                result.Add(t);
                if (t.I < parts.Count && t.C >= Bounds(parts[t.I], true).Min) Add((t.I + 1, 0, t.St));
            }

            foreach (var t in start) Add(t);
            return result;
        }
    }

    // ---- parts -----------------------------------------------------------------------------------------------------

    private static bool IsTextPart(IDefPart part)
    {
        return part is Lines or Split or Span or Skip { Chars: > 0 };
    }

    private static void RequireBinary(IDefPart part, Scope s)
    {
        if (s.IsText) throw new DefinitionException($"{part.GetType().Name} matches binary data, but it is inside text.");
    }

    private CharSet Set(string spec)
    {
        if (!_sets.TryGetValue(spec, out var set)) _sets[spec] = set = new CharSet(spec);
        return set;
    }

    /// <summary>
    ///     Every way <paramref name="part" /> matches at <paramref name="pos" />, preferred first.
    /// </summary>
    private IEnumerable<Res> M(IDefPart part, Scope s, int pos, St st)
    {
        if (st.Depth > MaxDepth) throw new DefinitionException("The definition nests too deeply (does it contain itself?).");
        st = st with { Depth = st.Depth + 1 };

        if (!s.IsText && IsTextPart(part)) return Text(new TextDefinition { Items = { part } }, s, pos, st);

        return part switch
               {
                   TextDefinition t => Text(t, s, pos, st),
                   DataDefinition d => Group(d, s, pos, st),
                   Capture c        => CaptureOf(c, s, pos, st),
                   Any a            => Alternatives(a, s, pos, st),
                   Not n            => NotOf(n, s, pos, st),
                   Block b          => BlockOf(b, s, pos, st),
                   Lines l          => LinesOf(l, s, pos, st),
                   Split p          => SplitOf(p, s, pos, st),
                   Span p           => SpanOf(p, s, pos, st),
                   Rest             => [new Res(s.End, st)],
                   End              => pos == s.End ? [new Res(pos, st)] : [],
                   Skip k           => SkipOf(k, s, pos, st),
                   Byte b           => Literal(part, [b.Value], s, pos, st),
                   Bytes b          => Literal(part, b.Value ?? [], s, pos, st),
                   Int n            => IntOf(n, s, pos, st),
                   _                => throw new DefinitionException($"{part.GetType().Name} is not a supported definition part.")
               };
    }

    private IEnumerable<Res> Group(DataDefinition d, Scope s, int pos, St st)
    {
        if (d.Name is null) return Seq(d.Items, 0, s, pos, st);
        return Named(d.Name, d.Items, s, pos, st);
    }

    private IEnumerable<Res> CaptureOf(Capture c, Scope s, int pos, St st)
    {
        IReadOnlyList<IDefPart> items = c.Items.Count > 0 ? c.Items : [new Rest()];
        if (c.To is null) return Seq(items, 0, s, pos, st);
        return Named(Expand(c.To, st.Inst), items, s, pos, st);
    }

    /// <summary>
    ///     Matches <paramref name="items" /> and records what they matched as a node named <paramref name="name" />.
    /// </summary>
    private IEnumerable<Res> Named(string name, IReadOnlyList<IDefPart> items, Scope s, int pos, St st)
    {
        foreach (var r in Seq(items, 0, s, pos, st with { Caps = null, Value = null }))
        {
            object? value = r.St.Value is not null && r.St.ValueAt == (pos, r.End)
                                 ? r.St.Value
                                 : (s.IsText
                                               ? s.Text!.Substring(pos, r.End - pos)
                                               : s.Bytes.AsSpan(pos, r.End - pos).ToArray());
            var node = new CaptureNode(name, value, s.Abs(pos), s.Abs(r.End) - s.Abs(pos), s.IsText, Caps.ToList(r.St.Caps));
            yield return new Res(r.End, r.St with { Caps = new Caps(node, st.Caps), Value = null });
        }
    }

    /// <summary>
    ///     Fills <c>{n}</c> in a capture name with enclosing element numbers.
    /// </summary>
    internal static string Expand(string template, Inst inst)
    {
        if (template.IndexOfAny(['{', '}']) < 0) return template;

        var sb = new StringBuilder(template.Length + 8);
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
            {
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '{')
            {
                int close = template.IndexOf('}', i);
                if (close > i + 1 && int.TryParse(template.AsSpan(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int depth))
                {
                    sb.Append(inst.At(depth).ToString(CultureInfo.InvariantCulture));
                    i = close;
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private IEnumerable<Res> Alternatives(Any a, Scope s, int pos, St st)
    {
        foreach (var child in a.Items)
        foreach (var r in Seq([child], 0, s, pos, st))
            yield return r;
    }

    private IEnumerable<Res> NotOf(Not n, Scope s, int pos, St st)
    {
        bool needMore = NeedMore;
        bool matched  = Seq(n.Items, 0, s, pos, st).Any();
        NeedMore = needMore; // a look-ahead running out of data says nothing about the match
        if (!matched) yield return new Res(pos, st);
    }

    // ---- binary ----------------------------------------------------------------------------------------------------

    private bool Short(Scope s, int pos, int count)
    {
        if (pos + count <= s.End) return false;
        if (s.Open) NeedMore = true;
        return true;
    }

    private IEnumerable<Res> Literal(IDefPart part, byte[] value, Scope s, int pos, St st)
    {
        RequireBinary(part, s);
        int n = Math.Min(value.Length, s.End - pos);
        if (!s.Bytes.AsSpan(pos, n).SequenceEqual(value.AsSpan(0, n))) yield break;
        if (Short(s, pos, value.Length)) yield break;
        yield return new Res(pos + value.Length, st);
    }

    private IEnumerable<Res> IntOf(Int n, Scope s, int pos, St st)
    {
        RequireBinary(n, s);
        if (n.Size is not (1 or 2 or 4 or 8)) throw new DefinitionException($"Int Size must be 1, 2, 4 or 8, not {n.Size}.");
        if (Short(s, pos, n.Size)) yield break;

        long v = Read(s.Bytes!, pos, n);

        if (n.Value is { } eq && v != eq) yield break;
        if (n.Min is { } min && v < min) yield break;
        if (n.Max is { } max && v > max) yield break;

        var vars = st.Vars;
        if (n.Let is { Length: > 0 } let) vars = new Dictionary<string, long>(vars) { [let] = v };
        yield return new Res(pos + n.Size, st with { Vars = vars, Value = v, ValueAt = (pos, pos + n.Size) });
    }

    private static long Read(byte[] data, int pos, Int n)
    {
        var b = data.AsSpan(pos, n.Size);
        return (n.Size, n.BigEndian, n.Signed) switch
                 {
                     (1, _, false)     => b[0],
                     (1, _, true)      => (sbyte)b[0],
                     (2, false, false) => BinaryPrimitives.ReadUInt16LittleEndian(b),
                     (2, false, true)  => BinaryPrimitives.ReadInt16LittleEndian(b),
                     (2, true, false)  => BinaryPrimitives.ReadUInt16BigEndian(b),
                     (2, true, true)   => BinaryPrimitives.ReadInt16BigEndian(b),
                     (4, false, false) => BinaryPrimitives.ReadUInt32LittleEndian(b),
                     (4, false, true)  => BinaryPrimitives.ReadInt32LittleEndian(b),
                     (4, true, false)  => BinaryPrimitives.ReadUInt32BigEndian(b),
                     (4, true, true)   => BinaryPrimitives.ReadInt32BigEndian(b),
                     (8, false, _)     => BinaryPrimitives.ReadInt64LittleEndian(b),
                     _                 => BinaryPrimitives.ReadInt64BigEndian(b)
                 };
    }

    private IEnumerable<Res> BlockOf(Block b, Scope s, int pos, St st)
    {
        RequireBinary(b, s);
        long len = b.Length switch
                   {
                       null => throw new DefinitionException("Block needs a Length."),
                       _ when long.TryParse(b.Length, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) => l,
                       _ when st.Vars.TryGetValue(b.Length, out long v) => v,
                       _ => throw new DefinitionException($"Block Length '{b.Length}' is neither a number nor a variable set earlier by Int Let.")
                   };
        len += b.Adjust;
        if (len < 0 || len > int.MaxValue || Short(s, pos, (int)len)) yield break;

        var body = s.Sub(pos, pos + (int)len);
        if (Whole(b.Items, body, st) is { } after) yield return new Res(body.End, after);
    }

    private IEnumerable<Res> SkipOf(Skip k, Scope s, int pos, St st)
    {
        if (k.Bytes > 0)
        {
            RequireBinary(k, s);
            if (Short(s, pos, k.Bytes)) yield break;
            pos += k.Bytes;
        }

        if (k.Chars > 0)
        {
            if (Short(s, pos, k.Chars)) yield break;
            pos += k.Chars;
        }

        if (k.Values.Length > 0)
            while (pos < s.End && Array.IndexOf(k.Values, s.IsText ? s.Text![pos] : (char)s.Bytes![pos]) >= 0)
                pos++;

        yield return new Res(pos, st);
    }

    // ---- text ------------------------------------------------------------------------------------------------------

    private IEnumerable<Res> Text(TextDefinition t, Scope s, int pos, St st)
    {
        if (s.IsText)
        {
            foreach (var r in Seq(t.Items, 0, s, pos, st)) yield return r;
            yield break;
        }

        foreach (var (encoding, bom) in Encodings(t, s, pos))
        {
            int start = pos + (t.ConsumeBOM ? bom : 0);
            var (text, all) = Decode(encoding, s.Bytes!, start, s.End - start);
            var scope = Scope.Of(text, 0, text.Length, s.Open && all);
            var inner = st with { Encoding = st.Encoding ?? encoding };

            foreach (var r in Seq(t.Items, 0, scope, 0, inner))
            {
                int end = start + encoding.GetByteCount(text.AsSpan(0, r.End));
                yield return new Res(end, r.St with { Value = text[..r.End], ValueAt = (pos, end) });
            }
        }
    }

    private static readonly Encoding Utf8Strict    = new UTF8Encoding(false, true);
    private static readonly Encoding Utf16LE = new UnicodeEncoding(false, false, true);
    private static readonly Encoding Utf16BE = new UnicodeEncoding(true, false, true);
    private static readonly Encoding Utf32LE = new UTF32Encoding(false, false, true);
    private static readonly Encoding Utf32BE = new UTF32Encoding(true, false, true);

    /// <summary>
    ///     The encodings to try at <paramref name="pos" />, with the length of the byte-order-mark found for each.
    /// </summary>
    private static List<(Encoding Encoding, int Bom)> Encodings(TextDefinition t, Scope s, int pos)
    {
        var b = s.Bytes.AsSpan(pos, s.End - pos);
        if (!t.AnyEncoding)
        {
            var e   = t.Encoding ?? Utf8Strict;
            var pre = e.Preamble;
            return [(e, pre.Length > 0 && b.StartsWith(pre) ? pre.Length : 0)];
        }

        if (b.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return [(Utf8Strict, 3)];
        if (b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0, 0])) return [(Utf32LE, 4)];
        if (b.StartsWith((ReadOnlySpan<byte>)[0, 0, 0xFE, 0xFF])) return [(Utf32BE, 4)];
        if (b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return [(Utf16LE, 2)];
        if (b.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return [(Utf16BE, 2)];

        var list = new List<(Encoding, int)> { (Utf8Strict, 0) };

        // UTF-16 without a BOM: mostly-ASCII text has a zero in every other byte.
        int sample = Math.Min(b.Length, 256) & ~1, evenZeros = 0, oddZeros = 0;
        for (int i = 0; i < sample; i += 2)
        {
            if (b[i] == 0) evenZeros++;
            if (b[i + 1] == 0) oddZeros++;
        }

        if (sample >= 4 && oddZeros > sample / 4 && evenZeros == 0) list.Add((Utf16LE, 0));
        if (sample >= 4 && evenZeros > sample / 4 && oddZeros == 0) list.Add((Utf16BE, 0));

        list.Add((Encoding.Latin1, 0));
        return list;
    }

    /// <summary>
    ///     Decodes the longest valid prefix; <c>All</c> is whether it reached the end (or stopped only at a truncated
    ///     character).
    /// </summary>
    private static (string Text, bool All) Decode(Encoding encoding, byte[] data, int start, int count)
    {
        var bytes = data.AsSpan(start, count);
        if (ReferenceEquals(encoding, Utf8Strict) || encoding is UTF8Encoding)
        {
            var chars  = new char[count];
            var status = Utf8.ToUtf16(bytes, chars, out int read, out int written, replaceInvalidSequences: false);
            return (new string(chars, 0, written), read == count || status == System.Buffers.OperationStatus.NeedMoreData);
        }

        if (encoding is UnicodeEncoding or UTF32Encoding)
        {
            int unit  = encoding is UTF32Encoding ? 4 : 2;
            int valid = count - count % unit;
            while (valid > 0)
                try
                {
                    return (encoding.GetString(bytes[..valid]), valid == count);
                }
                catch (DecoderFallbackException)
                {
                    valid -= unit; // invalid surrogates are rare; back off to a clean prefix
                }

            return (string.Empty, count < unit);
        }

        try
        {
            return (encoding.GetString(bytes), true);
        }
        catch (DecoderFallbackException)
        {
            return (string.Empty, false);
        }
    }

    private IEnumerable<Res> LinesOf(Lines l, Scope s, int pos, St st)
    {
        var  text  = s.Text!;
        bool lax   = l.Newline == NewLine.None || l.Newline.HasFlag(NewLine.Lax);
        bool crlf  = !lax && (l.Newline & NewLine.CrLf) == NewLine.CrLf;
        var  lines = new List<(int Start, int End, int Next)>();

        int from = pos;
        for (int i = pos; i < s.End; i++)
        {
            char c    = text[i];
            int  term = 0;
            if (lax)
                term = c == '\r' ? i + 1 < s.End && text[i + 1] == '\n' ? 2 : 1 : c == '\n' ? 1 : 0;
            else if (crlf)
                term = c == '\r' && i + 1 < s.End && text[i + 1] == '\n' ? 2 : 0;
            else if (l.Newline.HasFlag(NewLine.Lf))
                term = c == '\n' ? 1 : 0;
            else if (l.Newline.HasFlag(NewLine.Cr))
                term = c == '\r' ? 1 : 0;

            if (term == 0) continue;
            lines.Add((from, i, i + term));
            i    += term - 1;
            from =  i + 1;
        }

        bool unterminated = from < s.End;
        if (unterminated) lines.Add((from, s.End, s.End));
        if (l.Consolidate) lines.RemoveAll(x => x.Start == x.End);

        var elems = new List<Scope>(lines.Count);
        for (int k = 0; k < lines.Count; k++)
            elems.Add(s.Sub(lines[k].Start, lines[k].End, s.Open && unterminated && k == lines.Count - 1));

        foreach (var (count, after) in Elements(l.Items, elems, st, s.Open))
            yield return new Res(count == lines.Count ? s.End : count == 0 ? pos : lines[count - 1].Next, after);
    }

    private IEnumerable<Res> SplitOf(Split p, Scope s, int pos, St st)
    {
        var tokens = Tokenize(p, s.Text!, pos, s.End);
        if (p.Consolidate || string.IsNullOrEmpty(p.Delimiter)) tokens.RemoveAll(t => t.Value.Length == 0);

        var elems = new List<Scope>(tokens.Count);
        for (int k = 0; k < tokens.Count; k++)
        {
            var t = tokens[k];
            elems.Add(s.Element(t.Value, t.ContentStart, t.RawEnd - t.ContentStart, s.Open && k == tokens.Count - 1));
        }

        foreach (var (count, after) in Elements(p.Items, elems, st, s.Open))
            yield return new Res(count == tokens.Count ? s.End : count == 0 ? pos : tokens[count - 1].RawEnd, after);
    }

    /// <summary>
    ///     Splits <paramref name="text" /> between <paramref name="start" /> and <paramref name="end" /> into unquoted,
    ///     unescaped elements.
    /// </summary>
    internal static List<(string Value, int RawStart, int ContentStart, int RawEnd)> Tokenize(Split p, string text, int start, int end)
    {
        string? delim = p.Delimiter is { Length: > 0 } d ? DefText.Unescape(d) : null;
        string? q     = p.Qualifier is { Length: > 0 } qq ? DefText.Unescape(qq) : null;
        string? esc   = p.Escape is { Length: > 0 } ee ? DefText.Unescape(ee) : null;
        int     max   = p.MaxTokens is > 0 and var m ? m : int.MaxValue;

        var tokens = new List<(string, int, int, int)>();
        var sb     = new StringBuilder();
        int i      = start;

        bool At(string? s) => s is not null && i + s.Length <= end && string.CompareOrdinal(text, i, s, 0, s.Length) == 0;

        int DelimAt()
        {
            if (tokens.Count >= max - 1) return 0;
            if (delim is not null) return At(delim) ? delim.Length : 0;
            if (i >= end || !char.IsWhiteSpace(text[i])) return 0;
            int j = i;
            while (j < end && char.IsWhiteSpace(text[j])) j++;
            return j - i;
        }

        while (true)
        {
            int  rawStart = i, contentStart = i, dl = 0;
            bool quoted   = false;
            sb.Clear();

            if (q is not null && At(q))
            {
                quoted       = true;
                i           += q.Length;
                contentStart = i;
            }

            while (i < end)
            {
                if (quoted)
                {
                    if (esc is not null && esc == q && i + 2 * q.Length <= end && At(q) && string.CompareOrdinal(text, i + q.Length, q, 0, q.Length) == 0)
                    {
                        sb.Append(q);
                        i += 2 * q.Length;
                        continue;
                    }

                    if (At(q))
                    {
                        quoted =  false;
                        i      += q!.Length;
                        continue;
                    }
                }
                else if ((dl = DelimAt()) > 0)
                {
                    break;
                }

                if (esc is not null && esc != q && At(esc))
                {
                    int after = i + esc.Length;
                    string? escaped = new[] { q, esc, delim }.FirstOrDefault(x => x is not null && after + x.Length <= end &&
                                                                               string.CompareOrdinal(text, after, x, 0, x.Length) == 0);
                    if (escaped is not null)
                    {
                        sb.Append(escaped);
                        i = after + escaped.Length;
                        continue;
                    }
                }

                sb.Append(text[i]);
                i++;
            }

            tokens.Add((sb.ToString(), rawStart, contentStart, i));
            if (dl == 0) break;
            i += dl;
            if (i >= end && delim is null) break; // trailing whitespace is not an empty element
        }

        return tokens;
    }

    private IEnumerable<Res> SpanOf(Span p, Scope s, int pos, St st)
    {
        var text = s.Text!;

        bool Lit(string lit, int at)
        {
            int n = Math.Min(lit.Length, s.End - at);
            if (n < 0 || string.CompareOrdinal(text, at, lit, 0, n) != 0) return false;
            if (n == lit.Length) return true;
            if (s.Open) NeedMore = true;
            return false;
        }

        IEnumerable<Res> Body(int bodyStart, int bodyEnd, int after)
        {
            if (p.Items.Count == 0)
            {
                yield return new Res(after, st);
                yield break;
            }

            if (Whole(p.Items, s.Sub(bodyStart, bodyEnd), st) is { } inner) yield return new Res(after, inner);
        }

        if (p.Exactly is { } exactly)
        {
            exactly = DefText.Unescape(exactly);
            return Lit(exactly, pos) ? Body(pos, pos + exactly.Length, pos + exactly.Length) : [];
        }

        int p0 = pos;
        if (p.Start is { Length: > 0 } startLit)
        {
            startLit = DefText.Unescape(startLit);
            if (!Lit(startLit, p0)) return [];
            p0 += startLit.Length;
        }
        else if (p.Starts.Length > 0)
        {
            if (p0 >= s.End)
            {
                if (s.Open) NeedMore = true;
                return [];
            }

            if (Array.IndexOf(p.Starts, text[p0]) < 0) return [];
            p0++;
        }

        int? bodyEnd = null;
        if (p.While is { Length: > 0 } w)
        {
            var set = Set(w);
            int q   = p0;
            while (q < s.End && set.Contains(text[q])) q++;
            if (q == p0) return [];
            bodyEnd = q;
        }
        else if (p.WhileNot is { Length: > 0 } wn)
        {
            var set = Set(wn);
            int q   = p0;
            while (q < s.End && !set.Contains(text[q])) q++;
            if (q == p0) return [];
            bodyEnd = q;
        }
        else if (p.Until is { Length: > 0 } u)
        {
            var set = Set(u);
            int q   = p0;
            while (q < s.End && !set.Contains(text[q])) q++;
            if (q == s.End)
            {
                if (s.Open) NeedMore = true;
                return [];
            }

            bodyEnd = q;
        }

        string? endLit = p.End is { Length: > 0 } e ? DefText.Unescape(e) : null;
        bool    hasEnd = endLit is not null || p.Ends.Length > 0;

        int EndAt(int at)
        {
            if (endLit is not null) return Lit(endLit, at) ? endLit.Length : -1;
            return at < s.End && Array.IndexOf(p.Ends, text[at]) >= 0 ? 1 : -1;
        }

        if (bodyEnd is { } fixedEnd)
        {
            if (!hasEnd) return Body(p0, fixedEnd, fixedEnd);
            int el = EndAt(fixedEnd);
            return el < 0 ? [] : Body(p0, fixedEnd, fixedEnd + el);
        }

        if (hasEnd) return Closers(p0);

        if (p.Items.Count == 0) return [new Res(p0, st)];
        return Seq(p.Items, 0, s, p0, st);

        IEnumerable<Res> Closers(int from)
        {
            bool found = false;
            for (int k = from; k < s.End; k++)
            {
                int el = EndAt(k);
                if (el < 0) continue;
                found = true;
                foreach (var r in Body(from, k, k + el)) yield return r;
            }

            if (!found && s.Open) NeedMore = true;
        }
    }
}
