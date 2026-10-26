using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Silo.Ui.Panes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Silo.TextEditing;

/// <summary>One thing wrong with (or worth knowing about) a text: where it is, as lines and columns counted from 1.</summary>
/// <param name="Severity">How bad it is.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Line">The line it starts on (1-based).</param>
/// <param name="Column">The column it starts at (1-based, in characters).</param>
/// <param name="Length">How many characters it covers; 0 to mark the word (or character) at that place.</param>
/// <param name="Source">What found it ("JSON", "XML", "YAML").</param>
public sealed record TextDiagnostic(ProblemSeverity Severity, string Message, int Line, int Column, int Length, string Source);

/// <summary>Checks a text in its language. Pure functions of the text: safe to run off the UI thread.</summary>
public static class TextValidators
{
    /// <summary>The most problems one check reports; a badly broken file would otherwise list thousands.</summary>
    public const int Limit = 200;

    /// <summary>What is wrong with <paramref name="text" /> as <paramref name="language" />. Plain text has nothing to check.</summary>
    public static IReadOnlyList<TextDiagnostic> Validate(TextLanguage language, string text, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        try
        {
            return language switch
                   {
                       TextLanguage.Json => Json(text, cancel),
                       TextLanguage.Xml  => Xml(text, cancel),
                       TextLanguage.Yaml => Yaml(text, cancel),
                       _                 => []
                   };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // a checker that fails is a problem of its own, not a reason to lose the editor
            return [new TextDiagnostic(ProblemSeverity.Warning, "The text could not be checked: " + ex.Message, 1, 1, 0, language.DisplayName())];
        }
    }

    // ---- JSON ----------------------------------------------------------------------------------

    private static readonly Regex JsonSuffix = new(@"\s*(LineNumber|Path):.*$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static IReadOnlyList<TextDiagnostic> Json(string text, CancellationToken cancel)
    {
        var    result = new List<TextDiagnostic>();
        int    skip   = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        string body   = skip == 0 ? text : text[skip..];
        byte[] bytes  = Encoding.UTF8.GetBytes(body);
        var    map    = new Utf8Positions(body);

        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = false, MaxDepth = 512 });

        // the names seen in each object being read, to find a name used twice
        var objects = new Stack<HashSet<string>?>();
        try
        {
            while (reader.Read())
            {
                cancel.ThrowIfCancellationRequested();
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objects.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        objects.Push(null);
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        if (objects.Count > 0) objects.Pop();
                        break;
                    case JsonTokenType.PropertyName when objects.Count > 0 && objects.Peek() is { } names:
                    {
                        string name = reader.GetString() ?? string.Empty;
                        if (!names.Add(name) && result.Count < Limit)
                        {
                            var (line, column) = map.LineColumnOfByte(reader.TokenStartIndex);
                            result.Add(new TextDiagnostic(ProblemSeverity.Warning, $"The name \"{name}\" is used more than once in this object; most readers keep only the last one.",
                                                          line, column + skip0(line, skip), name.Length + 2, "JSON"));
                        }

                        break;
                    }
                    case JsonTokenType.Comment when result.Count < Limit:
                    {
                        var (line, column) = map.LineColumnOfByte(reader.TokenStartIndex);
                        result.Add(new TextDiagnostic(ProblemSeverity.Info, "A comment: allowed in JSON with comments (JSONC), not in standard JSON.", line, column + skip0(line, skip), 2, "JSON"));
                        break;
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            int line   = (int)(ex.LineNumber ?? 0) + 1;
            int column = map.ColumnOfByteInLine(line, (int)(ex.BytePositionInLine ?? 0)) + skip0(line, skip);
            result.Add(new TextDiagnostic(ProblemSeverity.Error, JsonSuffix.Replace(ex.Message, string.Empty).Trim(), line, column, 0, "JSON"));
        }

        return result;

        // the byte-order mark was taken off the front, so the first line's columns are one short
        static int skip0(int line, int skip) => line == 1 ? skip : 0;
    }

    /// <summary>Turns positions in the UTF-8 bytes of a text back into lines and columns of its characters.</summary>
    private sealed class Utf8Positions
    {
        private readonly string    _text;
        private readonly List<int> _lineStarts = [0];

        public Utf8Positions(string text)
        {
            _text = text;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n')
                    _lineStarts.Add(i + 1);
        }

        /// <summary>The 1-based column of the character <paramref name="bytes" /> UTF-8 bytes into line <paramref name="line" />.</summary>
        public int ColumnOfByteInLine(int line, int bytes)
        {
            int start = _lineStarts[Math.Clamp(line - 1, 0, _lineStarts.Count - 1)];
            int i = start, count = 0;
            while (i < _text.Length && count < bytes && _text[i] != '\n')
            {
                count += ByteCount(i, out int width);
                i     += width;
            }

            return i - start + 1;
        }

        /// <summary>The line and column of the character that starts <paramref name="offset" /> bytes into the text.</summary>
        public (int Line, int Column) LineColumnOfByte(long offset)
        {
            int  i     = 0;
            long count = 0;
            while (i < _text.Length && count < offset)
            {
                count += ByteCount(i, out int width);
                i     += width;
            }

            int index = _lineStarts.BinarySearch(i);
            int line  = index >= 0 ? index : ~index - 1;
            return (line + 1, i - _lineStarts[line] + 1);
        }

        private int ByteCount(int i, out int width)
        {
            char c = _text[i];
            width = 1;
            if (c < 0x80) return 1;
            if (c < 0x800) return 2;
            if (char.IsHighSurrogate(c) && i + 1 < _text.Length && char.IsLowSurrogate(_text[i + 1]))
            {
                width = 2;
                return 4;
            }

            return 3;
        }
    }

    // ---- XML -----------------------------------------------------------------------------------

    private static readonly Regex XmlSuffix = new(@"\s*Line \d+, position \d+\.\s*$", RegexOptions.CultureInvariant);

    private static IReadOnlyList<TextDiagnostic> Xml(string text, CancellationToken cancel)
    {
        var settings = new XmlReaderSettings
                       {
                           DtdProcessing    = DtdProcessing.Ignore, // a DOCTYPE is allowed, never fetched or expanded
                           XmlResolver      = null,
                           IgnoreWhitespace = true,
                           CheckCharacters  = true
                       };

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            while (reader.Read()) cancel.ThrowIfCancellationRequested();
            return [];
        }
        catch (XmlException ex)
        {
            return [new TextDiagnostic(ProblemSeverity.Error, XmlSuffix.Replace(ex.Message, string.Empty).Trim(), Math.Max(1, ex.LineNumber), Math.Max(1, ex.LinePosition), 0, "XML")];
        }
    }

    // ---- YAML ----------------------------------------------------------------------------------

    private static readonly Regex YamlPrefix = new(@"^\(Line: \d+, Col: \d+, Idx: \d+\) - \(Line: \d+, Col: \d+, Idx: \d+\):\s*", RegexOptions.CultureInvariant);

    /// <summary>Where the reader is inside a collection: for a mapping, whether a key comes next and which keys it had.</summary>
    private sealed class YamlFrame(bool isMapping)
    {
        public bool             IsMapping { get; } = isMapping;
        public bool             ExpectKey { get; set; } = true;
        public HashSet<string>? Keys      { get; } = isMapping ? new HashSet<string>(StringComparer.Ordinal) : null;
    }

    private static IReadOnlyList<TextDiagnostic> Yaml(string text, CancellationToken cancel)
    {
        var result = new List<TextDiagnostic>();
        var frames = new Stack<YamlFrame>();

        void Done()
        {
            if (frames.Count > 0 && frames.Peek().IsMapping) frames.Peek().ExpectKey = !frames.Peek().ExpectKey;
        }

        try
        {
            var parser = new Parser(new Scanner(new StringReader(text), true));
            while (parser.MoveNext())
            {
                cancel.ThrowIfCancellationRequested();
                switch (parser.Current)
                {
                    case DocumentStart:
                        frames.Clear();
                        break;
                    case Scalar scalar:
                        if (frames.Count > 0 && frames.Peek() is { IsMapping: true, ExpectKey: true, Keys: { } keys } && scalar.Value != "<<" && !keys.Add(scalar.Value) &&
                            result.Count < Limit)
                            result.Add(new TextDiagnostic(ProblemSeverity.Warning, $"The key \"{scalar.Value}\" is used more than once in this mapping; most readers keep only the last one.",
                                                          (int)scalar.Start.Line, (int)scalar.Start.Column, Math.Max(1, scalar.Value.Length), "YAML"));
                        Done();
                        break;
                    case AnchorAlias:
                        Done();
                        break;
                    case MappingStart:
                        frames.Push(new YamlFrame(true));
                        break;
                    case SequenceStart:
                        frames.Push(new YamlFrame(false));
                        break;
                    case MappingEnd or SequenceEnd:
                        if (frames.Count > 0) frames.Pop();
                        Done();
                        break;
                }
            }
        }
        catch (YamlException ex)
        {
            string message = YamlPrefix.Replace(ex.Message, string.Empty).Trim();
            if (message.Length == 0) message = "The YAML is not valid here.";
            result.Add(new TextDiagnostic(ProblemSeverity.Error, message, Math.Max(1, (int)ex.Start.Line), Math.Max(1, (int)ex.Start.Column), 0, "YAML"));
        }

        return result;
    }
}
