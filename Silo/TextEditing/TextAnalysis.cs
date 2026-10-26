using System.Text.RegularExpressions;
using Silo.Ui.Panes;

namespace Silo.TextEditing;

/// <summary>A stretch of text the editor can fold away: from the end of its first line to the end of its last.</summary>
/// <param name="Start">Offset where the fold starts (what stays visible ends here).</param>
/// <param name="End">Offset where it ends.</param>
/// <param name="Name">What is shown in place of the folded text.</param>
public readonly record struct FoldRange(int Start, int End, string Name);

/// <summary>Two parts of the text that belong together (a bracket and the one that closes it, an XML start tag's name and its end tag's).</summary>
public readonly record struct MatchPair(int AStart, int ALength, int BStart, int BLength);

/// <summary>What one pass over the text found: problems, folds, and matching brackets or tags.</summary>
public sealed class TextAnalysisResult
{
    public static readonly TextAnalysisResult Empty = new([], [], new Dictionary<int, MatchPair>());

    public TextAnalysisResult(IReadOnlyList<TextDiagnostic> diagnostics, IReadOnlyList<FoldRange> folds, IReadOnlyDictionary<int, MatchPair> pairs)
    {
        Diagnostics = diagnostics;
        Folds       = folds;
        Pairs       = pairs;
    }

    public IReadOnlyList<TextDiagnostic> Diagnostics { get; }

    /// <summary>Sorted by start, as the folding manager needs them.</summary>
    public IReadOnlyList<FoldRange> Folds { get; }

    /// <summary>For each offset that is part of a bracket or a tag name with a partner: the pair.</summary>
    public IReadOnlyDictionary<int, MatchPair> Pairs { get; }

    public int ErrorCount   => Diagnostics.Count(d => d.Severity == ProblemSeverity.Error);
    public int WarningCount => Diagnostics.Count(d => d.Severity == ProblemSeverity.Warning);
}

/// <summary>Reads a text once, off the UI thread, for everything the editor shows about its structure.</summary>
public static class TextAnalysis
{
    /// <summary>Larger texts get the problems only: folding and bracket matching would slow typing down.</summary>
    public const int StructureLimit = 8 * 1024 * 1024;

    public static TextAnalysisResult Run(TextLanguage language, string text, CancellationToken cancel = default)
    {
        var diagnostics = TextValidators.Validate(language, text, cancel);
        if (text.Length > StructureLimit) return new TextAnalysisResult(diagnostics, [], new Dictionary<int, MatchPair>());

        cancel.ThrowIfCancellationRequested();
        var pairs = new Dictionary<int, MatchPair>();
        var folds = new List<FoldRange>();
        switch (language)
        {
            case TextLanguage.Json:
                Brackets(text, pairs, folds, true, cancel);
                break;
            case TextLanguage.Xml:
                Xml(text, pairs, folds, cancel);
                break;
            case TextLanguage.Yaml:
                YamlFolds(text, folds, cancel);
                Brackets(text, pairs, null, false, cancel, yamlComments: true);
                break;
            default:
                Brackets(text, pairs, null, false, cancel);
                break;
        }

        folds.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        return new TextAnalysisResult(diagnostics, folds, pairs);
    }

    // ---- brackets ------------------------------------------------------------------------------

    /// <summary>
    ///     Pairs (), [] and {}. With <paramref name="skipStrings" /> (JSON) the inside of strings and comments is not looked at;
    ///     folds are made for brackets that span lines when <paramref name="folds" /> is given.
    /// </summary>
    private static void Brackets(string text, Dictionary<int, MatchPair> pairs, List<FoldRange>? folds, bool skipStrings, CancellationToken cancel,
                                 bool yamlComments = false)
    {
        var open = new Stack<(char Bracket, int Offset)>();
        for (int i = 0; i < text.Length; i++)
        {
            if ((i & 0xFFFF) == 0) cancel.ThrowIfCancellationRequested();
            char c = text[i];

            if (skipStrings)
            {
                if (c == '"')
                {
                    i = SkipString(text, i);
                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    i = LineEnd(text, i) - 1;
                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 1;
                    continue;
                }
            }
            else if (yamlComments && c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1])))
            {
                i = LineEnd(text, i) - 1;
                continue;
            }

            switch (c)
            {
                case '(' or '[' or '{':
                    open.Push((c, i));
                    break;
                case ')' or ']' or '}':
                {
                    char wanted = c switch { ')' => '(', ']' => '[', _ => '{' };
                    if (open.Count == 0 || open.Peek().Bracket != wanted) break; // a stray closer: leave it unpaired
                    var (bracket, start) = open.Pop();
                    var pair = new MatchPair(start, 1, i, 1);
                    pairs[start] = pair;
                    pairs[i]     = pair;

                    if (folds is not null && text.IndexOf('\n', start, i - start) >= 0)
                        folds.Add(new FoldRange(start, i + 1, bracket == '{' ? "{…}" : "[…]"));
                    break;
                }
            }
        }
    }

    private static int SkipString(string text, int quote)
    {
        for (int i = quote + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') i++;
            else if (text[i] == '"' || text[i] == '\n') return i;
        }

        return text.Length;
    }

    private static int LineEnd(string text, int from)
    {
        int end = text.IndexOf('\n', from);
        return end < 0 ? text.Length : end;
    }

    // ---- XML -----------------------------------------------------------------------------------

    /// <summary>A start, end or empty tag, or a comment / CDATA / processing instruction / DOCTYPE (which tags inside them are not).</summary>
    private static readonly Regex XmlToken = new(@"<!--.*?-->|<!\[CDATA\[.*?\]\]>|<\?.*?\?>|<!DOCTYPE[^>\[]*(?:\[.*?\])?\s*>|<(/?)([\w:.\-]+)(?:[^>""']|""[^""]*""|'[^']*')*?(/?)>",
                                                 RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static void Xml(string text, Dictionary<int, MatchPair> pairs, List<FoldRange> folds, CancellationToken cancel)
    {
        var open  = new Stack<(string Name, int NameStart, int TagEnd)>();
        int count = 0;
        foreach (Match m in XmlToken.Matches(text))
        {
            if ((++count & 0x3FF) == 0) cancel.ThrowIfCancellationRequested();

            if (!m.Groups[2].Success)
            {
                // a comment (or CDATA) over several lines folds too
                if (m.Value.StartsWith("<!--", StringComparison.Ordinal) && m.Value.Contains('\n'))
                    folds.Add(new FoldRange(m.Index, m.Index + m.Length, "<!--…-->"));
                continue;
            }

            var  name    = m.Groups[2];
            bool closing = m.Groups[1].Length > 0;
            bool empty   = m.Groups[3].Length > 0;
            if (empty) continue;

            if (!closing)
            {
                open.Push((name.Value, name.Index, m.Index + m.Length));
                continue;
            }

            // an end tag closes the nearest start tag of that name (tags left open in between are dropped, as a forgiving reader would)
            if (!open.Any(o => o.Name == name.Value)) continue;
            while (open.Count > 0)
            {
                var start = open.Pop();
                if (start.Name != name.Value) continue;

                var pair = new MatchPair(start.NameStart, name.Length, name.Index, name.Length);
                for (int k = 0; k < name.Length; k++)
                {
                    pairs[start.NameStart + k] = pair;
                    pairs[name.Index + k]      = pair;
                }

                if (text.IndexOf('\n', start.TagEnd, Math.Max(0, m.Index - start.TagEnd)) >= 0)
                    folds.Add(new FoldRange(start.TagEnd, m.Index + m.Length, "…</" + name.Value + ">"));
                break;
            }
        }
    }

    // ---- YAML: folds by indentation ------------------------------------------------------------

    private static void YamlFolds(string text, List<FoldRange> folds, CancellationToken cancel)
    {
        // each significant line: where it ends and how deep it is ("- " items count half a step deeper than their own indent, so
        // a list written at the same indent as its key still belongs to it)
        var open          = new Stack<(double Level, int LineEnd, int Line)>();
        int lastEnd       = 0;
        int lastLine      = -1;
        int lineNo        = 0;
        int position      = 0;
        bool inBlockScalar = false;
        double blockLevel  = 0;

        void Close(double level)
        {
            while (open.Count > 0 && open.Peek().Level >= level)
            {
                var (_, start, line) = open.Pop();
                if (lastLine > line) folds.Add(new FoldRange(start, lastEnd, "…"));
            }
        }

        while (position <= text.Length)
        {
            if ((lineNo & 0x3FF) == 0) cancel.ThrowIfCancellationRequested();

            int end = text.IndexOf('\n', position);
            if (end < 0) end = text.Length;
            int contentEnd = end > position && text[end - 1] == '\r' ? end - 1 : end;
            string line = text[position..contentEnd];

            int indent = 0;
            while (indent < line.Length && line[indent] == ' ') indent++;
            string rest = line[indent..];

            bool significant = rest.Length > 0 && !rest.StartsWith('#');
            if (significant)
            {
                double level = rest.StartsWith("- ", StringComparison.Ordinal) || rest == "-" ? indent + 0.5 : indent;

                // the lines of a | or > block are part of it, whatever they hold
                if (inBlockScalar && level > blockLevel)
                {
                    lastEnd  = contentEnd;
                    lastLine = lineNo;
                }
                else
                {
                    inBlockScalar = false;
                    if (rest is "---" or "..." || rest.StartsWith("--- ", StringComparison.Ordinal))
                    {
                        Close(double.MinValue);
                    }
                    else
                    {
                        Close(level);
                        open.Push((level, contentEnd, lineNo));
                    }

                    lastEnd  = contentEnd;
                    lastLine = lineNo;

                    string trimmed = rest.TrimEnd();
                    int    hash    = trimmed.IndexOf(" #", StringComparison.Ordinal);
                    if (hash >= 0) trimmed = trimmed[..hash].TrimEnd();
                    if (Regex.IsMatch(trimmed, @"(^|[:\-]\s)[|>][+\-]?\d*$"))
                    {
                        inBlockScalar = true;
                        blockLevel    = level;
                    }
                }
            }

            lineNo++;
            position = end + 1;
        }

        Close(double.MinValue);
    }
}
