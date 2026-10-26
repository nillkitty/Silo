namespace Silo.Labs.Regexes;

/// <summary>What kind of piece of a pattern a <see cref="RegexPartNode" /> is.</summary>
public enum RegexPartKind
{
    /// <summary>A group that captures: <c>(abc)</c> or, named, <c>(?&lt;name&gt;abc)</c>.</summary>
    Group,

    /// <summary>A group that does not capture: <c>(?:abc)</c>, <c>(?i:abc)</c>, or any group when the pattern captures only named groups.</summary>
    NonCapturing,

    /// <summary>A lookahead or lookbehind, positive or negative.</summary>
    Lookaround,

    /// <summary>An atomic group <c>(?&gt;abc)</c>.</summary>
    Atomic,

    /// <summary>A conditional <c>(?(1)yes|no)</c>.</summary>
    Conditional,

    /// <summary>The ways something can go: <c>a|b|c</c> at one level; its children are the <see cref="Branch" />es.</summary>
    Alternation,

    /// <summary>One way of an alternation.</summary>
    Branch
}

/// <summary>A piece of a pattern: a group or an alternation (and its branches), with where it is in the pattern and what is nested in it.</summary>
public sealed class RegexPartNode
{
    public RegexPartNode(RegexPartKind kind, int start, int length)
    {
        Kind   = kind;
        Start  = start;
        Length = length;
    }

    public RegexPartKind Kind { get; }

    /// <summary>Where it begins in the pattern (the opening parenthesis of a group).</summary>
    public int Start { get; }

    /// <summary>How much of the pattern it covers (a group: parentheses included).</summary>
    public int Length { get; internal set; }

    public int End => Start + Length;

    /// <summary>For a group: how long its opening is (<c>(</c>, <c>(?:</c>, <c>(?&lt;name&gt;</c>). Zero for an alternation and its branches.</summary>
    public int HeadLength { get; internal init; }

    /// <summary>Whether the closing parenthesis was found.</summary>
    public bool IsClosed { get; internal set; } = true;

    /// <summary>The group's name, when it has one.</summary>
    public string? Name { get; internal init; }

    /// <summary>The number the engine gives a capturing group (named ones come after the others); null for anything else.</summary>
    public int? Number { get; internal set; }

    public List<RegexPartNode> Children { get; } = [];

    /// <summary>Where the inside of a group begins and ends (the whole range for the others).</summary>
    public int ContentStart => Start + HeadLength;

    public int ContentEnd => Kind is RegexPartKind.Alternation or RegexPartKind.Branch || !IsClosed ? End : End - 1;

    public string Text(string pattern) => pattern.Substring(Start, Math.Min(Length, pattern.Length - Start));

    public string Content(string pattern) => pattern.Substring(ContentStart, Math.Max(0, Math.Min(ContentEnd, pattern.Length) - ContentStart));
}

/// <summary>Reads a .NET pattern into the groups and alternations it is made of (nested as they are written). It does not check the pattern: use the engine for that.</summary>
public static class RegexPartParser
{
    public static IReadOnlyList<RegexPartNode> Parse(string? pattern, string? flags = null)
    {
        pattern ??= string.Empty;
        flags   ??= string.Empty;
        var parser = new Parser(pattern, flags.Contains('n'), flags.Contains('x'));
        var top    = parser.ParseScope(top: true);
        Number(top, flags.Contains('n'));
        return top;
    }

    /// <summary>Gives the capturing groups their numbers: the unnamed ones left to right, then the named ones left to right (as the engine does).</summary>
    private static void Number(List<RegexPartNode> top, bool explicitCapture)
    {
        var all = new List<RegexPartNode>();

        void Walk(IEnumerable<RegexPartNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Kind == RegexPartKind.Group) all.Add(node);
                Walk(node.Children);
            }
        }

        Walk(top);
        int number = 1;
        foreach (var node in all.Where(n => n.Name is null)) node.Number = number++;
        foreach (var node in all.Where(n => n.Name is not null)) node.Number = number++;
    }

    private sealed class Parser(string p, bool explicitCapture, bool free)
    {
        private int _i;

        /// <summary>Reads up to the closing parenthesis of the group it is in (left for the caller), or the end of the pattern at the top.</summary>
        public List<RegexPartNode> ParseScope(bool top)
        {
            var branches = new List<List<RegexPartNode>> { new() };
            var starts   = new List<int> { _i };
            var ends     = new List<int> { _i };

            while (_i < p.Length)
            {
                char c = p[_i];
                if (c == '\\')
                {
                    _i += 2;
                    continue;
                }

                if (c == '[')
                {
                    SkipClass();
                    continue;
                }

                if (free && c == '#')
                {
                    while (_i < p.Length && p[_i] != '\n') _i++;
                    continue;
                }

                if (c == '(')
                {
                    if (ParseGroup() is { } group) branches[^1].Add(group);
                    continue;
                }

                if (c == ')')
                {
                    if (top)
                    {
                        _i++; // a closing parenthesis nothing opened: the engine will say so
                        continue;
                    }

                    break;
                }

                if (c == '|')
                {
                    ends[^1] = _i;
                    _i++;
                    branches.Add([]);
                    starts.Add(_i);
                    ends.Add(_i);
                    continue;
                }

                _i++;
            }

            ends[^1] = Math.Min(_i, p.Length);
            if (branches.Count == 1) return branches[0];

            var alternation = new RegexPartNode(RegexPartKind.Alternation, starts[0], ends[^1] - starts[0]);
            for (int b = 0; b < branches.Count; b++)
            {
                var branch = new RegexPartNode(RegexPartKind.Branch, starts[b], Math.Max(0, ends[b] - starts[b]));
                branch.Children.AddRange(branches[b]);
                alternation.Children.Add(branch);
            }

            return [alternation];
        }

        private void SkipClass()
        {
            _i++; // [
            if (_i < p.Length && p[_i] == '^') _i++;
            if (_i < p.Length && p[_i] == ']') _i++; // a ] first is a character of the class
            while (_i < p.Length && p[_i] != ']')
            {
                if (p[_i] == '\\') _i++;
                _i++;
            }

            _i++; // ]
        }

        private RegexPartNode? ParseGroup()
        {
            int start = _i;
            _i++; // (

            var    kind = explicitCapture ? RegexPartKind.NonCapturing : RegexPartKind.Group;
            string? name = null;

            if (_i < p.Length && p[_i] == '?')
            {
                int q = _i + 1;
                if (q >= p.Length) return null;

                char c = p[q];
                if (c == '#')
                {
                    // a comment
                    while (_i < p.Length && p[_i] != ')') _i++;
                    _i++;
                    return null;
                }

                if (c == ':')
                {
                    kind = RegexPartKind.NonCapturing;
                    _i   = q + 1;
                }
                else if (c is '=' or '!')
                {
                    kind = RegexPartKind.Lookaround;
                    _i   = q + 1;
                }
                else if (c == '>')
                {
                    kind = RegexPartKind.Atomic;
                    _i   = q + 1;
                }
                else if (c == '<' && q + 1 < p.Length && p[q + 1] is '=' or '!')
                {
                    kind = RegexPartKind.Lookaround;
                    _i   = q + 2;
                }
                else if (c is '<' or '\'')
                {
                    char close = c == '<' ? '>' : '\'';
                    int  end   = p.IndexOf(close, q + 1);
                    if (end < 0) return null;

                    name = p.Substring(q + 1, end - q - 1);
                    kind = RegexPartKind.Group;
                    if (name.Length > 0 && char.IsDigit(name[0])) name = null; // a number, not a name
                    _i = end + 1;
                }
                else if (c == '(')
                {
                    kind = RegexPartKind.Conditional;
                    _i   = q; // the test is read as part of the content
                }
                else
                {
                    // inline options: (?i) switches them on from here; (?i:abc) is a group
                    int o = q;
                    while (o < p.Length && (char.IsLetter(p[o]) || p[o] == '-')) o++;
                    if (o < p.Length && p[o] == ')')
                    {
                        _i = o + 1;
                        return null;
                    }

                    if (o < p.Length && p[o] == ':')
                    {
                        kind = RegexPartKind.NonCapturing;
                        _i   = o + 1;
                    }
                    else
                    {
                        return null;
                    }
                }
            }

            int contentStart = _i;
            var node = new RegexPartNode(kind, start, 0) { HeadLength = contentStart - start, Name = name };
            node.Children.AddRange(ParseScope(top: false));
            if (_i < p.Length && p[_i] == ')')
            {
                _i++;
                node.IsClosed = true;
            }
            else
            {
                node.IsClosed = false;
            }

            node.Length = Math.Min(_i, p.Length) - start;
            return node;
        }
    }
}
