using System.Text;
using System.Text.RegularExpressions;

namespace Silo.Labs.Regexes;

/// <summary>One line of the explanation of a pattern: the piece of the pattern and what it does, indented as far as it is inside groups.</summary>
public sealed record ExplainLine(int Depth, string Token, string Meaning);

/// <summary>
///     Explains a .NET pattern piece by piece. It reads the pattern the way the engine does (escapes, classes, groups, quantifiers, inline options) but
///     does not check it: use the engine for that. What it cannot make sense of is shown as it is.
/// </summary>
public static class RegexExplainer
{
    public static IReadOnlyList<ExplainLine> Explain(string? pattern, string? flags = null)
    {
        var o     = new Explainer(pattern ?? string.Empty, RegexFlags.Parse(flags));
        o.Run();
        return o.Lines;
    }

    /// <summary>The explanation as indented text.</summary>
    public static string ToText(IEnumerable<ExplainLine> lines)
    {
        var text = new StringBuilder();
        foreach (var l in lines) text.Append(new string(' ', l.Depth * 2)).Append(l.Token.PadRight(Math.Max(1, 16 - l.Depth * 2))).Append("  ").AppendLine(l.Meaning);
        return text.ToString().TrimEnd();
    }

    private sealed class Explainer(string p, RegexOptions options)
    {
        private int    _i;
        private int    _depth;
        private int    _groups;
        private readonly Stack<string> _open  = new();
        private readonly bool          _free = (options & RegexOptions.IgnorePatternWhitespace) != 0;

        public List<ExplainLine> Lines { get; } = [];

        public void Run()
        {
            if (p.Length == 0)
            {
                Add(string.Empty, "an empty pattern: matches the empty text everywhere");
                return;
            }

            while (_i < p.Length) Step();

            while (_open.Count > 0)
            {
                _depth--;
                Add("", $"(the group {_open.Pop()} is never closed)");
            }
        }

        private void Add(string token, string meaning)
        {
            Lines.Add(new ExplainLine(_depth, token, meaning));
        }

        private void Step()
        {
            char c = p[_i];

            if (_free && char.IsWhiteSpace(c))
            {
                _i++;
                return;
            }

            if (_free && c == '#')
            {
                int end = p.IndexOf('\n', _i);
                if (end < 0) end = p.Length;
                Add(p[_i..end].TrimEnd(), "a comment");
                _i = end;
                return;
            }

            switch (c)
            {
                case '\\':
                    Escape();
                    break;
                case '[':
                    Class();
                    break;
                case '(':
                    Group();
                    break;
                case ')':
                    CloseGroup();
                    break;
                case '|':
                    Add("|", "or: either what is before it or what is after it");
                    _i++;
                    break;
                case '.':
                    Add(".", "any character except a new line (with the Singleline option, any character at all)");
                    _i++;
                    Quantifier();
                    break;
                case '^':
                    Add("^", "the start of the text (the start of a line with the Multiline option)");
                    _i++;
                    break;
                case '$':
                    Add("$", "the end of the text, or before the new line that ends it (the end of a line with the Multiline option)");
                    _i++;
                    break;
                case '*' or '+' or '?':
                    Quantifier();
                    break;
                case '{' when QuantifierAt(_i):
                    Quantifier();
                    break;
                default:
                    Literal();
                    break;
            }
        }

        private void Literal()
        {
            var text = new StringBuilder();
            int j    = _i;
            while (j < p.Length && IsLiteral(j))
            {
                // a quantifier belongs to the character before it only
                if (text.Length > 0 && NextIsQuantifier(j + 1)) break;
                text.Append(p[j]);
                j++;
                if (NextIsQuantifier(j)) break;
            }

            if (text.Length == 0)
            {
                text.Append(p[_i]);
                j = _i + 1;
            }

            _i = j;
            Add(text.ToString(), text.Length == 1 ? $"the character {Show(text[0])}" : $"the text \"{text}\"");
            Quantifier();
        }

        private bool IsLiteral(int at)
        {
            char c = p[at];
            if (_free && (char.IsWhiteSpace(c) || c == '#')) return false;
            return "\\[()|.^$*+?{".IndexOf(c) < 0 || (c == '{' && !QuantifierAt(at));
        }

        private bool NextIsQuantifier(int at)
        {
            return at < p.Length && (p[at] is '*' or '+' or '?' || (p[at] == '{' && QuantifierAt(at)));
        }

        private bool QuantifierAt(int at)
        {
            return Regex.IsMatch(p[at..], @"^\{\d+(,\d*)?\}");
        }

        private void Quantifier()
        {
            if (_i >= p.Length) return;

            string? meaning = null;
            string  token   = string.Empty;
            switch (p[_i])
            {
                case '*':
                    token   = "*";
                    meaning = "zero or more times";
                    _i++;
                    break;
                case '+':
                    token   = "+";
                    meaning = "one or more times";
                    _i++;
                    break;
                case '?':
                    token   = "?";
                    meaning = "optional (zero or one time)";
                    _i++;
                    break;
                case '{' when QuantifierAt(_i):
                    var m = Regex.Match(p[_i..], @"^\{(\d+)(,(\d*))?\}");
                    token = m.Value;
                    _i   += m.Length;
                    string min = m.Groups[1].Value;
                    meaning = !m.Groups[2].Success ? $"exactly {min} time{(min == "1" ? "" : "s")}" :
                              m.Groups[3].Value.Length == 0 ? $"{min} or more times" : $"{min} to {m.Groups[3].Value} times";
                    break;
            }

            if (meaning is null) return;

            if (_i < p.Length && p[_i] == '?')
            {
                token   += "?";
                meaning += ", as few as it can (lazy)";
                _i++;
            }
            else meaning += ", as many as it can (greedy)";

            // the quantifier goes with the line before it
            var last = Lines[^1];
            Lines[^1] = last with { Token = last.Token + token, Meaning = last.Meaning + ", " + meaning };
        }

        private void Escape()
        {
            if (_i + 1 >= p.Length)
            {
                Add("\\", "a backslash with nothing after it");
                _i++;
                return;
            }

            char n = p[_i + 1];
            string token = "\\" + n;
            string? meaning = n switch
                              {
                                  'd'  => "a digit (0-9 and the digits of other scripts)",
                                  'D'  => "anything but a digit",
                                  'w'  => "a word character: a letter, a digit or _",
                                  'W'  => "anything but a word character",
                                  's'  => "white space (space, tab, new line...)",
                                  'S'  => "anything but white space",
                                  'b'  => "a word boundary: between a word character and anything else",
                                  'B'  => "not at a word boundary",
                                  'A'  => "the very start of the text",
                                  'z'  => "the very end of the text",
                                  'Z'  => "the end of the text, or before the new line that ends it",
                                  'G'  => "where the last match ended",
                                  'n'  => "a new line (line feed)",
                                  'r'  => "a carriage return",
                                  't'  => "a tab",
                                  'f'  => "a form feed",
                                  'v'  => "a vertical tab",
                                  'e'  => "an escape character (U+001B)",
                                  'a'  => "a bell (U+0007)",
                                  _    => null
                              };
            _i += 2;

            if (meaning is null)
            {
                switch (n)
                {
                    case 'u' when _i + 4 <= p.Length && IsHex(p.AsSpan(_i, 4)):
                        token = "\\u" + p.Substring(_i, 4);
                        meaning = $"the character U+{p.Substring(_i, 4).ToUpperInvariant()}";
                        _i += 4;
                        break;
                    case 'x' when _i + 2 <= p.Length && IsHex(p.AsSpan(_i, 2)):
                        token = "\\x" + p.Substring(_i, 2);
                        meaning = $"the character U+00{p.Substring(_i, 2).ToUpperInvariant()}";
                        _i += 2;
                        break;
                    case 'c' when _i < p.Length:
                        token = "\\c" + p[_i];
                        meaning = $"the control character Ctrl+{char.ToUpperInvariant(p[_i])}";
                        _i++;
                        break;
                    case 'p' or 'P' when _i < p.Length && p[_i] == '{' && p.IndexOf('}', _i) is var close and > 0:
                        string name = p[(_i + 1)..close];
                        token = $"\\{n}{{{name}}}";
                        meaning = (n == 'p' ? "a character of the category or block " : "a character not of the category or block ") + name;
                        _i = close + 1;
                        break;
                    case 'k' when _i < p.Length && p[_i] is '<' or '\'':
                        char end = p[_i] == '<' ? '>' : '\'';
                        int  at  = p.IndexOf(end, _i + 1);
                        if (at > 0)
                        {
                            token = "\\k" + p[_i..(at + 1)];
                            meaning = $"the same text the group {p[(_i + 1)..at]} matched";
                            _i = at + 1;
                        }

                        break;
                    case >= '1' and <= '9':
                        meaning = $"the same text group {n} matched (a back-reference)";
                        break;
                    case '0':
                        meaning = "the null character";
                        break;
                }
            }

            meaning ??= $"the character {Show(n)} (escaped, so it is not special)";
            Add(token, meaning);
            if (n is not ('b' or 'B' or 'A' or 'z' or 'Z' or 'G')) Quantifier();
        }

        private static bool IsHex(ReadOnlySpan<char> text)
        {
            foreach (char c in text)
                if (!Uri.IsHexDigit(c))
                    return false;
            return true;
        }

        private void Class()
        {
            int start = _i;
            int j     = _i + 1;
            bool negate = j < p.Length && p[j] == '^';
            if (negate) j++;

            var parts = new List<string>();
            int first = j;
            string? subtract = null;
            while (j < p.Length && (p[j] != ']' || j == first))
            {
                if (p[j] == '-' && j + 1 < p.Length && p[j + 1] == '[')
                {
                    // [a-z-[aeiou]]: take away what the inner class has
                    int depth = 0;
                    int k = j + 1;
                    for (; k < p.Length; k++)
                    {
                        if (p[k] == '\\') k++;
                        else if (p[k] == '[') depth++;
                        else if (p[k] == ']' && --depth == 0) break;
                    }

                    subtract = p[(j + 1)..Math.Min(k + 1, p.Length)];
                    j = k + 1;
                    continue;
                }

                if (p[j] == '\\' && j + 1 < p.Length)
                {
                    string item = ClassEscape(p, ref j);
                    parts.Add(item);
                    continue;
                }

                char a = p[j];
                if (j + 2 < p.Length && p[j + 1] == '-' && p[j + 2] != ']' && p[j + 2] != '[')
                {
                    char b = p[j + 2];
                    if (b == '\\' && j + 3 < p.Length) b = p[j + 3];
                    parts.Add($"{Show(a)} to {Show(b)}");
                    j += b == p[j + 2] ? 3 : 4;
                    continue;
                }

                parts.Add(Show(a));
                j++;
            }

            int end = Math.Min(j + 1, p.Length);
            string token = p[start..end];
            _i = end;

            string list = parts.Count == 0 ? "nothing" : string.Join(", ", parts);
            string meaning = negate ? $"any one character that is not: {list}" : $"any one of: {list}";
            if (subtract is not null) meaning += $", except what {subtract} has";
            Add(token, meaning);
            Quantifier();
        }

        private static string ClassEscape(string text, ref int j)
        {
            char n = text[j + 1];
            j += 2;
            switch (n)
            {
                case 'd': return "a digit";
                case 'D': return "a non-digit";
                case 'w': return "a word character";
                case 'W': return "a non-word character";
                case 's': return "white space";
                case 'S': return "non-white space";
                case 'n': return "a new line";
                case 't': return "a tab";
                case 'r': return "a carriage return";
                case 'b': return "a backspace";
                case 'u' when j + 4 <= text.Length && IsHex(text.AsSpan(j, 4)):
                    string u = text.Substring(j, 4).ToUpperInvariant();
                    j += 4;
                    return "U+" + u;
                case 'x' when j + 2 <= text.Length && IsHex(text.AsSpan(j, 2)):
                    string x = text.Substring(j, 2).ToUpperInvariant();
                    j += 2;
                    return "U+00" + x;
                case 'p' or 'P' when j < text.Length && text[j] == '{' && text.IndexOf('}', j) is var close and > 0:
                    string name = text[(j + 1)..close];
                    j = close + 1;
                    return (n == 'p' ? "a character of " : "a character not of ") + name;
                default: return Show(n);
            }
        }

        private void Group()
        {
            string rest = p[_i..];
            string token;
            string meaning;
            string label;
            Match m;

            if ((m = Regex.Match(rest, @"^\(\?#[^)]*\)")).Success)
            {
                Add(m.Value, "a comment, ignored");
                _i += m.Length;
                return;
            }

            if ((m = Regex.Match(rest, @"^\(\?<([A-Za-z_]\w*)>")).Success || (m = Regex.Match(rest, @"^\(\?'([A-Za-z_]\w*)'")).Success)
            {
                _groups++;
                token   = m.Value;
                meaning = $"start of the capturing group called {m.Groups[1].Value} (number {_groups})";
                label   = m.Groups[1].Value;
            }
            else if ((m = Regex.Match(rest, @"^\(\?<([A-Za-z_]\w*)-([A-Za-z_]\w*)>")).Success)
            {
                token   = m.Value;
                meaning = $"start of a balancing group: takes away the last capture of {m.Groups[2].Value}";
                label   = m.Groups[1].Value;
            }
            else if (rest.StartsWith("(?:", StringComparison.Ordinal))
            {
                token   = "(?:";
                meaning = "start of a group that does not capture";
                label   = "(non-capturing)";
            }
            else if (rest.StartsWith("(?=", StringComparison.Ordinal))
            {
                token   = "(?=";
                meaning = "start of a look-ahead: what follows must match this (it is not used up)";
                label   = "(look-ahead)";
            }
            else if (rest.StartsWith("(?!", StringComparison.Ordinal))
            {
                token   = "(?!";
                meaning = "start of a negative look-ahead: what follows must not match this";
                label   = "(negative look-ahead)";
            }
            else if (rest.StartsWith("(?<=", StringComparison.Ordinal))
            {
                token   = "(?<=";
                meaning = "start of a look-behind: what is before must match this (it is not used up)";
                label   = "(look-behind)";
            }
            else if (rest.StartsWith("(?<!", StringComparison.Ordinal))
            {
                token   = "(?<!";
                meaning = "start of a negative look-behind: what is before must not match this";
                label   = "(negative look-behind)";
            }
            else if (rest.StartsWith("(?>", StringComparison.Ordinal))
            {
                token   = "(?>";
                meaning = "start of an atomic group: once it has matched, the engine never goes back into it";
                label   = "(atomic)";
            }
            else if ((m = Regex.Match(rest, @"^\(\?([imnsx\-]+)\)")).Success)
            {
                Add(m.Value, "changes options from here: " + DescribeOptions(m.Groups[1].Value));
                _i += m.Length;
                return;
            }
            else if ((m = Regex.Match(rest, @"^\(\?([imnsx\-]+):")).Success)
            {
                token   = m.Value;
                meaning = "start of a group that does not capture, with options: " + DescribeOptions(m.Groups[1].Value);
                label   = "(options)";
            }
            else if ((m = Regex.Match(rest, @"^\(\?\(([^)]*)\)")).Success)
            {
                token   = m.Value;
                meaning = $"start of a condition: if {m.Groups[1].Value} matches (or is a group that matched), use the first alternative, else the second";
                label   = "(condition)";
            }
            else if (rest.StartsWith("(?", StringComparison.Ordinal))
            {
                token   = "(?";
                meaning = "a group construct this explainer does not know";
                label   = "(?)";
            }
            else if ((options & RegexOptions.ExplicitCapture) != 0)
            {
                token   = "(";
                meaning = "start of a group that does not capture (the ExplicitCapture option is on)";
                label   = "(non-capturing)";
            }
            else
            {
                _groups++;
                token   = "(";
                meaning = $"start of the capturing group number {_groups}";
                label   = _groups.ToString();
            }

            Add(token, meaning);
            _i += token.Length;
            _open.Push(label);
            _depth++;
        }

        private void CloseGroup()
        {
            if (_open.Count == 0)
            {
                Add(")", "a closing parenthesis with no group open");
                _i++;
                return;
            }

            _depth--;
            string label = _open.Pop();
            Add(")", $"end of the group {label}");
            _i++;
            Quantifier();
        }

        private static string DescribeOptions(string text)
        {
            var on  = new List<string>();
            var off = new List<string>();
            bool turningOff = false;
            foreach (char c in text)
            {
                if (c == '-')
                {
                    turningOff = true;
                    continue;
                }

                string name = RegexFlags.All.FirstOrDefault(f => f.Letter == c).Name ?? c.ToString();
                (turningOff ? off : on).Add(name);
            }

            var parts = new List<string>();
            if (on.Count > 0) parts.Add("on: " + string.Join(", ", on));
            if (off.Count > 0) parts.Add("off: " + string.Join(", ", off));
            return string.Join("; ", parts);
        }

        private static string Show(char c)
        {
            return c switch
                   {
                       ' '  => "space",
                       '\t' => "tab",
                       '\n' => "new line",
                       '\r' => "carriage return",
                       _ when char.IsControl(c) => $"U+{(int)c:X4}",
                       _    => $"'{c}'"
                   };
        }
    }
}
