using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Silo.Providers.Document;
using Silo.Providers.Format;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Silo.TextEditing;

/// <summary>A text could not be formatted or converted; the message says why, in words for the person.</summary>
public sealed class TextTransformException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
///     Formats (pretty-prints), minifies and converts JSON, XML and YAML. Every method takes the whole text and returns the whole
///     new text; it throws <see cref="TextTransformException" /> when the text cannot be read. Results use "\n" line ends (the
///     editor puts back the document's own).
/// </summary>
/// <remarks>
///     JSON is formatted by re-spacing its tokens rather than by reading it into objects, so numbers keep their exact digits and
///     comments stay. XML and YAML are read and written again: YAML comments do not survive that (the caller asks first, see
///     <see cref="CountYamlComments" />), XML comments do.
/// </remarks>
public static class TextFormatting
{
    /// <summary>Re-indents the text: <paramref name="indent" /> is the text of one level (two spaces, a tab...).</summary>
    public static string Format(TextLanguage language, string text, string indent) => language switch
                                                                                     {
                                                                                         TextLanguage.Json => JsonLayout(text, indent, false),
                                                                                         TextLanguage.Xml  => XmlWrite(ParseXml(text), indent, false),
                                                                                         TextLanguage.Yaml => YamlReemit(text, IndentWidth(indent), false),
                                                                                         _                 => throw new TextTransformException("Plain text has no structure to format.")
                                                                                     };

    /// <summary>The same content in as little space as the language allows (YAML: one flow-style line per document).</summary>
    public static string Minify(TextLanguage language, string text) => language switch
                                                                      {
                                                                          TextLanguage.Json => JsonLayout(text, string.Empty, true),
                                                                          TextLanguage.Xml  => XmlWrite(ParseXml(text), string.Empty, true),
                                                                          TextLanguage.Yaml => YamlReemit(text, 2, true),
                                                                          _                 => throw new TextTransformException("Plain text has no structure to minify.")
                                                                      };

    /// <summary>The content written in another language, formatted with <paramref name="indent" />.</summary>
    public static string Convert(TextLanguage from, TextLanguage to, string text, string indent)
    {
        if (from == to) return Format(from, text, indent);
        if (from == TextLanguage.PlainText || to == TextLanguage.PlainText)
            throw new TextTransformException("Only JSON, XML and YAML can be converted into each other.");

        var token = ToJToken(from, text);
        return to switch
               {
                   TextLanguage.Json => JsonLayout(token.ToString(Newtonsoft.Json.Formatting.None), indent, false),
                   TextLanguage.Xml  => XmlWrite(JsonToXml(token), indent, false),
                   TextLanguage.Yaml => JsonToYaml(token, IndentWidth(indent)),
                   _                 => throw new TextTransformException("Unknown language.")
               };
    }

    /// <summary>How many comments a YAML text has (they are lost when it is formatted); 0 when it cannot be read.</summary>
    public static int CountYamlComments(string text)
    {
        try
        {
            int count   = 0;
            var scanner = new Scanner(new StringReader(text), false);
            while (scanner.MoveNext())
                if (scanner.Current is YamlDotNet.Core.Tokens.Comment)
                    count++;
            return count;
        }
        catch (YamlException)
        {
            return 0;
        }
    }

    private static int IndentWidth(string indent) => indent.Contains('\t') ? 4 : Math.Clamp(indent.Length, 2, 9);

    // ---- JSON ----------------------------------------------------------------------------------

    /// <summary>
    ///     Lays the JSON out again token by token: strings, numbers and literals are copied exactly; only the white space between
    ///     tokens changes. Comments stay when formatting and go when minifying.
    /// </summary>
    private static string JsonLayout(string text, string indent, bool minify)
    {
        var  output = new StringBuilder(text.Length);
        int  level  = 0;
        int  i      = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        bool lineStart = true; // nothing but indentation is on the current output line

        void NewLine()
        {
            if (minify) return;
            output.Append('\n');
            for (int n = 0; n < level; n++) output.Append(indent);
            lineStart = true;
        }

        void Reindent()
        {
            if (minify) return;
            while (output.Length > 0 && output[^1] is ' ' or '\t') output.Length--;
            for (int n = 0; n < level; n++) output.Append(indent);
        }

        void Emit(char c)
        {
            output.Append(c);
            lineStart = false;
        }

        int NextSignificant(int from)
        {
            while (from < text.Length && char.IsWhiteSpace(text[from])) from++;
            return from;
        }

        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                {
                    int end = i + 1;
                    while (end < text.Length && text[end] != c)
                    {
                        if (text[end] == '\\') end++;
                        if (end < text.Length && text[end] == '\n') throw new TextTransformException("A string runs past the end of its line.");
                        end++;
                    }

                    if (end >= text.Length) throw new TextTransformException("A string is not closed.");
                    output.Append(text, i, end - i + 1);
                    lineStart = false;
                    i         = end + 1;
                    break;
                }
                case '{' or '[':
                {
                    Emit(c);
                    int next = NextSignificant(i + 1);
                    if (next < text.Length && text[next] == (c == '{' ? '}' : ']'))
                    {
                        Emit(text[next]); // {} and [] stay on one line
                        i = next + 1;
                        break;
                    }

                    level++;
                    NewLine();
                    i++;
                    break;
                }
                case '}' or ']':
                    level = Math.Max(0, level - 1);
                    if (lineStart) Reindent(); // the line holds only indentation (after a comment): indent it for this level instead
                    else NewLine();
                    Emit(c);
                    i++;
                    break;
                case ',':
                    Emit(c);
                    NewLine();
                    i++;
                    break;
                case ':':
                    Emit(c);
                    if (!minify) output.Append(' ');
                    i++;
                    break;
                case '/' when i + 1 < text.Length && text[i + 1] == '/':
                {
                    int end = text.IndexOf('\n', i);
                    if (end < 0) end = text.Length;
                    if (!minify)
                    {
                        if (!lineStart) output.Append(' ');
                        output.Append(text.AsSpan(i, end - i).TrimEnd('\r'));
                        NewLine();
                    }

                    i = end;
                    break;
                }
                case '/' when i + 1 < text.Length && text[i + 1] == '*':
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) throw new TextTransformException("A /* comment is not closed.");
                    end += 2;
                    if (!minify)
                    {
                        if (!lineStart) output.Append(' ');
                        output.Append(text, i, end - i);
                        lineStart = false;
                    }

                    i = end;
                    break;
                }
                default:
                {
                    // a number or a literal (true, false, null): up to the next delimiter
                    int end = i;
                    while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not (',' or ':' or '{' or '}' or '[' or ']' or '"' or '/'))
                        end++;
                    if (end == i) end++;
                    output.Append(text, i, end - i);
                    lineStart = false;
                    i         = end;
                    break;
                }
            }
        }

        // a top-level comment can leave indentation behind it
        return output.ToString().TrimEnd(' ', '\t', '\n') + (minify ? string.Empty : "\n");
    }

    private static JToken ParseJson(string text)
    {
        // decimals keep the digits as written (1.50 stays 1.50); a number too big for a decimal is read as a double instead
        try
        {
            return ParseJson(text, FloatParseHandling.Decimal);
        }
        catch (TextTransformException ex) when (ex.InnerException is JsonReaderException or OverflowException)
        {
            return ParseJson(text, FloatParseHandling.Double);
        }
    }

    private static JToken ParseJson(string text, FloatParseHandling floats)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = floats };
            var token = JToken.ReadFrom(reader, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore, DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace });
            if (reader.Read() && reader.TokenType != JsonToken.Comment)
                throw new TextTransformException("There is more than one value at the top level of the JSON.");
            return token;
        }
        catch (Exception ex) when (ex is JsonException or OverflowException)
        {
            throw new TextTransformException("The JSON cannot be read: " + ex.Message, ex);
        }
    }

    // ---- XML -----------------------------------------------------------------------------------

    private static XDocument ParseXml(string text)
    {
        var settings = new XmlReaderSettings
                       {
                           DtdProcessing             = DtdProcessing.Parse,
                           XmlResolver               = null,     // nothing outside the text is ever fetched
                           MaxCharactersFromEntities = 1_000_000, // and entities cannot blow up
                           IgnoreWhitespace          = true
                       };
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new TextTransformException("The XML cannot be read: " + ex.Message, ex);
        }
    }

    private static string XmlWrite(XDocument document, string indent, bool minify)
    {
        // a StringWriter would make the declaration say utf-16: the declaration is written as it was, by hand
        var settings = new XmlWriterSettings
                       {
                           OmitXmlDeclaration  = true,
                           Indent              = !minify,
                           IndentChars         = indent,
                           NewLineChars        = "\n",
                           NewLineHandling     = NewLineHandling.Replace,
                           NewLineOnAttributes = false
                       };

        var output = new StringBuilder();
        if (document.Declaration is { } declaration)
        {
            output.Append(declaration);
            if (!minify) output.Append('\n');
        }

        using (var writer = XmlWriter.Create(output, settings))
        {
            document.Save(writer);
        }

        return minify ? output.ToString() : output.ToString().TrimEnd() + "\n";
    }

    private static XDocument JsonToXml(JToken token)
    {
        // XML needs one root element: a JSON object with one property names it, anything else is put under <root>
        try
        {
            if (token is JObject { Count: 1 } single && single.First is JProperty { Value: JObject or JValue } only && only.Value.Type != JTokenType.Null)
                return JsonConvert.DeserializeXNode(single.ToString(Newtonsoft.Json.Formatting.None))!;

            var wrapped = token is JObject ? token : new JObject { ["item"] = token };
            return JsonConvert.DeserializeXNode(wrapped.ToString(Newtonsoft.Json.Formatting.None), "root")!;
        }
        catch (Exception ex) when (ex is JsonException or XmlException or InvalidOperationException)
        {
            throw new TextTransformException("It cannot be written as XML: " + ex.Message, ex);
        }
    }

    // ---- YAML ----------------------------------------------------------------------------------

    private static Emitter NewEmitter(TextWriter writer, int indent) =>
        new(writer, EmitterSettings.Default.WithBestIndent(indent).WithBestWidth(int.MaxValue));

    /// <summary>Reads the YAML and writes the same events again: block layout (or flow, minified), even indentation, no comments.</summary>
    private static string YamlReemit(string text, int indent, bool flow)
    {
        var output = new StringWriter();
        try
        {
            var parser  = new Parser(new Scanner(new StringReader(text), true));
            var emitter = NewEmitter(output, indent);
            while (parser.MoveNext())
            {
                var e = parser.Current!;
                if (flow)
                    e = e switch
                        {
                            MappingStart m  => new MappingStart(m.Anchor, m.Tag, m.IsImplicit, MappingStyle.Flow),
                            SequenceStart s => new SequenceStart(s.Anchor, s.Tag, s.IsImplicit, SequenceStyle.Flow),
                            Scalar { Style: ScalarStyle.Literal or ScalarStyle.Folded } v =>
                                new Scalar(v.Anchor, v.Tag, v.Value, ScalarStyle.DoubleQuoted, v.IsPlainImplicit, v.IsQuotedImplicit),
                            _ => e
                        };
                emitter.Emit(e);
            }
        }
        catch (YamlException ex)
        {
            throw new TextTransformException("The YAML cannot be read: " + ex.Message, ex);
        }

        return output.ToString().Replace("\r\n", "\n");
    }

    private static string JsonToYaml(JToken token, int indent)
    {
        var output  = new StringWriter();
        var emitter = NewEmitter(output, indent);
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart());
        WriteYaml(emitter, token);
        emitter.Emit(new DocumentEnd(true));
        emitter.Emit(new StreamEnd());
        return output.ToString().Replace("\r\n", "\n");
    }

    private static void WriteYaml(IEmitter emitter, JToken token)
    {
        switch (token)
        {
            case JObject obj:
                emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, MappingStyle.Block));
                foreach (var property in obj.Properties())
                {
                    WriteYamlString(emitter, property.Name);
                    WriteYaml(emitter, property.Value);
                }

                emitter.Emit(new MappingEnd());
                break;

            case JArray array:
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Block));
                foreach (var item in array) WriteYaml(emitter, item);
                emitter.Emit(new SequenceEnd());
                break;

            case JValue value:
                switch (value.Type)
                {
                    case JTokenType.Null or JTokenType.Undefined:
                        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, "null", ScalarStyle.Plain, true, false));
                        break;
                    case JTokenType.Boolean:
                        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, (bool)value ? "true" : "false", ScalarStyle.Plain, true, false));
                        break;
                    case JTokenType.Integer or JTokenType.Float:
                        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, value.ToString(Newtonsoft.Json.Formatting.None), ScalarStyle.Plain, true, false));
                        break;
                    default:
                        WriteYamlString(emitter, System.Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty);
                        break;
                }

                break;

            default:
                WriteYamlString(emitter, token.ToString());
                break;
        }
    }

    /// <summary>Words that YAML 1.1 readers (still common) take for true or false: written quoted, so every reader sees a string.</summary>
    private static readonly HashSet<string> Yaml11Booleans = new(StringComparer.OrdinalIgnoreCase) { "y", "n", "yes", "no", "on", "off" };

    /// <summary>A string, unquoted when YAML would read it back as the same string, else quoted (a multi-line one as a block).</summary>
    private static void WriteYamlString(IEmitter emitter, string text)
    {
        bool plain = text.Length > 0 && YamlScalars.ResolvePlain(text) is string same && same == text && text.Trim() == text &&
                     text.IndexOfAny([':', '#', '\n', '\r', '\t', '"', '\'', '{', '}', '[', ']', ',', '&', '*', '!', '|', '>', '%', '@', '`']) < 0 &&
                     text[0] is not ('-' or '?') && !Yaml11Booleans.Contains(text);
        var style = plain ? ScalarStyle.Plain : text.Contains('\n') && !text.EndsWith(' ') ? ScalarStyle.Literal : ScalarStyle.DoubleQuoted;
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, text, style, plain, !plain));
    }

    // ---- reading any of them as JSON -----------------------------------------------------------

    /// <summary>The content of the text as a JSON value (the meeting point of every conversion).</summary>
    private static JToken ToJToken(TextLanguage language, string text)
    {
        switch (language)
        {
            case TextLanguage.Json:
                return ParseJson(text);

            case TextLanguage.Xml:
                try
                {
                    // the root element, not the document: the XML declaration is not content
                    var document = ParseXml(text);
                    return document.Root is { } root ? JToken.Parse(JsonConvert.SerializeXNode(root, Newtonsoft.Json.Formatting.None)) : new JObject();
                }
                catch (JsonException ex)
                {
                    throw new TextTransformException("The XML cannot be written as JSON: " + ex.Message, ex);
                }

            case TextLanguage.Yaml:
                try
                {
                    var parser = new Parser(new Scanner(new StringReader(text), false));
                    return YamlJson.Convert(new YamlDocumentParser(parser).ReadDocuments().ToList());
                }
                catch (YamlException ex)
                {
                    throw new TextTransformException("The YAML cannot be read: " + ex.Message, ex);
                }

            default:
                throw new TextTransformException("Plain text has no structure to convert.");
        }
    }
}
