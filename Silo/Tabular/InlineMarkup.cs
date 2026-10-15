using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Silo.Tabular;

/// <summary>One run of text with the same formatting.</summary>
public sealed record InlineSpan(string Text, bool Bold = false, bool Italic = false, bool Strike = false, bool Code = false, string? Link = null);

/// <summary>
///     A deliberately small inline-markdown dialect for rich text cells: <c>**bold**</c>, <c>*italic*</c> (or
///     <c>_italic_</c>), <c>~~strike~~</c>, <c>`code`</c> and <c>[text](url)</c>, with backslash escapes. A marker with
///     nothing to close it is just text. The same parse drives the editor's formatted display, the HTML export and the
///     plain-text exports; markdown output needs no conversion since the cell already is markdown.
/// </summary>
public static partial class InlineMarkup
{
    [GeneratedRegex(@"^\[(?<t>[^\]\n]+)\]\((?<u>[^)\s]+)\)")]
    private static partial Regex LinkPattern();

    public static IReadOnlyList<InlineSpan> Parse(string? text)
    {
        var spans = new List<InlineSpan>();
        if (!string.IsNullOrEmpty(text)) ParseInto(text, spans, false, false, false, null);
        return spans;
    }

    private static void ParseInto(string s, List<InlineSpan> spans, bool bold, bool italic, bool strike, string? link)
    {
        var  buf = new StringBuilder();
        int  i   = 0;

        void Flush()
        {
            if (buf.Length == 0) return;
            spans.Add(new InlineSpan(buf.ToString(), bold, italic, strike, false, link));
            buf.Clear();
        }

        while (i < s.Length)
        {
            char c = s[i];

            // escape: \* -> literal *
            if (c == '\\' && i + 1 < s.Length && IsMarker(s[i + 1]))
            {
                buf.Append(s[i + 1]);
                i += 2;
                continue;
            }

            // code span: everything up to the next backtick is literal
            if (c == '`')
            {
                int end = s.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    spans.Add(new InlineSpan(s.Substring(i + 1, end - i - 1), bold, italic, strike, true, link));
                    i = end + 1;
                    continue;
                }
            }

            // link: [text](url) -- the text keeps its own formatting
            if (c == '[' && link is null)
            {
                var m = LinkPattern().Match(s.Substring(i));
                if (m.Success)
                {
                    Flush();
                    ParseInto(m.Groups["t"].Value, spans, bold, italic, strike, m.Groups["u"].Value);
                    i += m.Length;
                    continue;
                }
            }

            if (c == '*' && Next(s, i) == '*')
            {
                if (Toggle(s, i, "**", ref bold, buf, Flush)) { i += 2; continue; }
            }
            else if (c == '~' && Next(s, i) == '~')
            {
                if (Toggle(s, i, "~~", ref strike, buf, Flush)) { i += 2; continue; }
            }
            else if (c == '*' || (c == '_' && IsWordEdge(s, i, italic)))
            {
                if (Toggle(s, i, c.ToString(), ref italic, buf, Flush)) { i += 1; continue; }
            }

            buf.Append(c);
            i++;
        }

        Flush();
    }

    private static bool Toggle(string s, int at, string marker, ref bool state, StringBuilder buf, Action flush)
    {
        int m = marker.Length;
        if (state)
        {
            // closing: not directly after a space ("a ** b" is not a close)
            if (at == 0 || char.IsWhiteSpace(s[at - 1])) return false;
        }
        else
        {
            // opening: needs text right after it and a proper closer later ("a * b" is just text)
            if (at + m >= s.Length || char.IsWhiteSpace(s[at + m])) return false;
            if (FindCloser(s, at + m, marker) < 0) return false;
        }

        flush();
        state = !state;
        return true;
    }

    private static int FindCloser(string s, int from, string marker)
    {
        for (int i = s.IndexOf(marker, from, StringComparison.Ordinal); i >= 0; i = s.IndexOf(marker, i + 1, StringComparison.Ordinal))
            if (i > from && !char.IsWhiteSpace(s[i - 1]))
                return i;
        return -1;
    }

    private static char Next(string s, int i) => i + 1 < s.Length ? s[i + 1] : '\0';

    private static bool IsMarker(char c) => c is '\\' or '*' or '_' or '`' or '~' or '[' or ']' or '(' or ')';

    // underscores inside words (snake_case) are not emphasis: an opening one needs a non-word before it and
    // something other than a space after it; a closing one needs a non-space before it and a non-word after it
    private static bool IsWordEdge(string s, int i, bool closing)
    {
        char before = i > 0 ? s[i - 1] : ' ';
        char after  = i + 1 < s.Length ? s[i + 1] : ' ';
        return closing
                   ? !char.IsWhiteSpace(before) && !char.IsLetterOrDigit(after)
                   : !char.IsLetterOrDigit(before) && !char.IsWhiteSpace(after);
    }

    /// <summary>The text without formatting. A link reads <c>text (url)</c>.</summary>
    public static string ToPlain(string? text)
    {
        var sb = new StringBuilder();
        foreach (var span in Parse(text))
        {
            sb.Append(span.Text);
            if (span.Link is not null && span.Link != span.Text) sb.Append(" (").Append(span.Link).Append(')');
        }

        return sb.ToString();
    }

    /// <summary>HTML for the cell: tags for the formatting, everything else encoded, line breaks as &lt;br&gt;.</summary>
    public static string ToHtml(string? text, bool rich = true)
    {
        if (!rich) return Encode(text);

        var sb = new StringBuilder();
        foreach (var span in Parse(text))
        {
            string t = Encode(span.Text);
            if (span.Code) t = $"<code>{t}</code>";
            if (span.Italic) t = $"<em>{t}</em>";
            if (span.Bold) t = $"<strong>{t}</strong>";
            if (span.Strike) t = $"<del>{t}</del>";
            if (span.Link is not null) t = $"<a href=\"{WebUtility.HtmlEncode(span.Link)}\">{t}</a>";
            sb.Append(t);
        }

        return sb.ToString();
    }

    private static string Encode(string? s)
    {
        return WebUtility.HtmlEncode(s ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "<br>");
    }

    /// <summary>Escapes plain text so a markdown renderer shows it literally.</summary>
    public static string EscapeMarkdown(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length + 4);
        foreach (char c in s)
        {
            if (c is '\\' or '*' or '_' or '`' or '~' or '[' or ']' or '<' or '>' or '|') sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Wraps the selected part of <paramref name="text" /> in <paramref name="marker" />, or removes it if already wrapped.</summary>
    public static (string Text, int Start, int Length) ToggleWrap(string text, int start, int length, string marker)
    {
        int m = marker.Length;
        if (length > 0 && length >= 2 * m && string.CompareOrdinal(text, start, marker, 0, m) == 0
            && string.CompareOrdinal(text, start + length - m, marker, 0, m) == 0)
        {
            string inner = text.Substring(start + m, length - 2 * m);
            return (text.Remove(start, length).Insert(start, inner), start, inner.Length);
        }

        if (start >= m && start + length + m <= text.Length
                       && string.CompareOrdinal(text, start - m, marker, 0, m) == 0
                       && string.CompareOrdinal(text, start + length, marker, 0, m) == 0)
        {
            return (text.Remove(start + length, m).Remove(start - m, m), start - m, length);
        }

        string wrapped = marker + text.Substring(start, length) + marker;
        return (text.Remove(start, length).Insert(start, wrapped), start + m, length);
    }
}
