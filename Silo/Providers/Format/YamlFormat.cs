using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Silo.Providers.Data;
using Silo.Providers.Data.Source;
using Silo.Providers.Document;
using Silo.Providers.Reflection;
using Telefrag.DI;
using YamlDotNet.Core;

namespace Silo.Providers.Format;

/// <summary>
///     Reads YAML files into a custom <see cref="YamlDocNode" /> tree (see
///     <see cref="YamlDocumentModel" />/<see cref="YamlDocumentParser" />) rather than
///     YamlDotNet's own <c>YamlDotNet.RepresentationModel</c> DOM.
/// </summary>
/// <remarks>
///     <c>YamlDotNet.RepresentationModel</c>'s <c>YamlNode</c> tree is the "obvious" way to parse
///     YAML with this library, but it throws away comments entirely and doesn't surface the
///     explicit/implicit distinction for tags (custom types like <c>!blah</c>, explicit overrides
///     like <c>!!int</c>) as directly as the raw event stream does. Since Silo needs all of that
///     (plus source position -- see <see cref="Origin" />) to round-trip a YAML document
///     faithfully, this format drives <see cref="YamlDotNet.Core.Parser" /> directly instead, via
///     <see cref="YamlDocumentParser" />. This intentionally never imports
///     <c>YamlDotNet.RepresentationModel</c> -- only <c>YamlDotNet.Core</c>/
///     <c>YamlDotNet.Core.Events</c> -- both to avoid needing it and to avoid name collisions
///     with this file's own <c>Yaml*Node</c> types.
/// </remarks>
[Singleton(typeof(IFileFormat))]
public class YamlFormat : IFileFormat, IDocumentProvider<YamlDocNode>
{
    public string DisplayName { get; } = "YAML File";

    public string FileExtension { get; } = "yaml";
    public string ContentType   { get; } = "application/yaml";

    public IEnumerable<DataVerb> GetSupportedVerbs()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    ///     Reads every <c>---</c>-separated document in <paramref name="stream" />, in order.
    ///     Lazily evaluated -- parsing happens as the result is enumerated, not up front.
    /// </summary>
    public IEnumerable<YamlDocNode> Read(FileStream stream)
    {
        stream.Required();

        // skipComments: false is what makes the Scanner (and, through it, the Parser) surface
        // Comment events at all -- by default YamlDotNet discards them during scanning, before
        // the parser (or this method) would ever get a chance to see them.
        var reader  = new StreamReader(stream);
        var scanner = new Scanner(reader, false);
        var parser  = new Parser(scanner);

        return new YamlDocumentParser(parser).ReadDocuments();
    }
}

/// <summary>
///     A YAML document
/// </summary>
public class YamlDocument(YamlFormat provider, Uri uri) : DocumentBase<YamlDocNode>(provider, uri), IStructuredDataSource
{
    public string? AttributePrefix      { get; } = null;
    public string? MetaCommentName      { get; } = "$comment";
    public string? MetaTypeHintName     { get; } = "$type";
    public string? MetaWrapperValueName { get; } = "$value";
    public string? TextPropertyName     { get; } = null;

    /// <summary>
    ///     The documents as JSON: no documents give an empty array, one document gives its own value, several give an array of
    ///     them. See <see cref="YamlJson" /> for how each kind of node is written.
    /// </summary>
    private JToken? YamlToJson(List<YamlDocNode> yamlDocNodes)
    {
        return YamlJson.Convert(yamlDocNodes);
    }

    public JToken? GetData()
    {
        using var fs = new FileStream(uri.Required().LocalPath, FileMode.Open, FileAccess.Read);
        var       d  = provider.Read(fs)?.ToList() ?? [];
        if (d.Count == 0) return new JArray();

        return YamlToJson(d);
    }
}

/// <summary>
///     Turns the <see cref="YamlDocNode" /> trees <see cref="YamlFormat" /> reads into JSON.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             A plain scalar is typed by YAML's core schema: <c>null</c>/<c>~</c>/empty, <c>true</c>/<c>false</c>,
///             decimal, <c>0x</c>
///             hex and <c>0o</c> octal integers (a number too big for a 64-bit integer stays an exact integer) and floats.
///             A quoted,
///             literal (<c>|</c>) or folded (<c>&gt;</c>) scalar is a string. <c>.inf</c> and <c>.nan</c> are written as
///             the
///             floating-point values Newtonsoft's writers spell as the strings <c>"Infinity"</c> and <c>"NaN"</c>, since
///             JSON has no
///             number for them. Timestamps and <c>!!binary</c> data stay strings.
///         </item>
///         <item>
///             An explicit standard tag decides the type (<c>!!str 123</c> is a string, <c>!!int "42"</c> is a number); a
///             value that
///             does not fit its tag stays a string. A custom tag (<c>!blah</c>) is dropped and the scalar is a string.
///         </item>
///         <item>
///             A mapping is an object. Its keys are the keys' text (<c>1: a</c> is <c>"1"</c>); a key that is itself a
///             mapping or
///             sequence is that key as compact JSON text. A repeated key keeps its last value. The merge key
///             <c>&lt;&lt;</c> (with a
///             mapping, an alias to one, or a sequence of them) brings in their entries; the mapping's own entries win
///             over merged
///             ones, and earlier merged mappings win over later ones.
///         </item>
///         <item>
///             An alias is written out as the node it refers to. A node that contains itself cannot be written as JSON and
///             throws an
///             <see cref="InvalidDataException" />, as does a document that expands to more than <see cref="MaxNodes" />
///             values.
///         </item>
///         <item>
///             JSON has no tags or comments, so they are kept only when asked for (<c>annotate: true</c>): a node with a
///             custom tag or
///             comments becomes
///             <c>
///                 { "$tag": "!blah", "$comment": "own-line comments, one per line", "$inlineComment": "...", "$value":
///                 ...
///                 }
///             </c>
///             (only the parts it has). A mapping entry's comments include those on its key. Standard tags (<c>!!int</c>)
///             only decide
///             the type and are not kept. A real mapping that uses these four names as keys cannot be told from a wrapper.
///         </item>
///     </list>
/// </remarks>
public static class YamlJson
{
    /// <summary>
    ///     The most values one document may expand to (aliases count each time they are used); a guard against "billion
    ///     laughs" files.
    /// </summary>
    public const int MaxNodes = 5_000_000;

    private const string StandardTagPrefix = "tag:yaml.org,2002:";

    /// <summary>The property of an annotated node's wrapper that holds its custom tag.</summary>
    public const string TagProperty = "$tag";

    /// <summary>The property that holds the comments on their own lines before the node, one per line.</summary>
    public const string CommentProperty = "$comment";

    /// <summary>The property that holds the comment at the end of the node's line.</summary>
    public const string InlineCommentProperty = "$inlineComment";

    /// <summary>The property that holds the node's value.</summary>
    public const string ValueProperty = "$value";

    /// <summary>No documents: an empty array. One: its value (a JSON null for an empty document). Several: an array of them.</summary>
    /// <param name="documents">The documents.</param>
    /// <param name="annotate">Keep custom tags and comments, in wrapper objects (see the remarks on <see cref="YamlJson" />).</param>
    public static JToken Convert(IReadOnlyList<YamlDocNode> documents, bool annotate = false)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (documents.Count == 0) return new JArray();
        if (documents.Count == 1) return new Converter(annotate).Convert(documents[0]);

        var result = new JArray();
        foreach (var document in documents)
            result.Add(new Converter(annotate).Convert(document));
        return result;
    }

    /// <summary>One YAML node, as JSON.</summary>
    /// <param name="node">The node.</param>
    /// <param name="annotate">Keep custom tags and comments, in wrapper objects (see the remarks on <see cref="YamlJson" />).</param>
    public static JToken Convert(YamlDocNode node, bool annotate = false)
    {
        return new Converter(annotate).Convert(node.Required());
    }

    private sealed class Converter(bool annotate)
    {
        /// <summary>
        ///     The collections being converted right now, from the root down: meeting one again means the document contains
        ///     itself.
        /// </summary>
        private readonly HashSet<YamlDocNode> _path = new(ReferenceEqualityComparer.Instance);

        /// <summary>Whether tags and comments are being kept; off while writing a key's text, which has no room for them.</summary>
        private bool _annotate = annotate;

        private int _count;

        /// <summary>The node as JSON; when annotating, wrapped with its custom tag and comments if it has any.</summary>
        public JToken Convert(YamlDocNode node)
        {
            return Annotated(node, null);
        }

        /// <param name="node">The node.</param>
        /// <param name="key">The mapping key the node is the value of, whose comments belong to the entry.</param>
        private JToken Annotated(YamlDocNode node, YamlDocNode? key)
        {
            var value = Plain(node);
            return _annotate ? Wrap(node, key, value) : value;
        }

        private static JToken Wrap(YamlDocNode node, YamlDocNode? key, JToken value)
        {
            string? tag = node is { TagIsExplicit: true, Tag: { } t } && !node.IsStandardTag ? t : null;

            var own     = key is null ? node.LeadingComments : key.LeadingComments.Concat(node.LeadingComments);
            var comment = string.Join("\n", own.Select(c => c.Trim()));
            var inline = string.Join(" ", new[] { key?.InlineComment, node.InlineComment }.Select(c => c?.Trim())
                                                                                          .Where(c => !string.IsNullOrEmpty(c)));

            if (tag is null && comment.Length == 0 && inline.Length == 0) return value;

            var wrapper                                            = new JObject();
            if (tag is not null) wrapper[TagProperty]              = tag;
            if (comment.Length > 0) wrapper[CommentProperty]       = comment;
            if (inline.Length  > 0) wrapper[InlineCommentProperty] = inline;
            wrapper[ValueProperty] = value;
            return wrapper;
        }

        /// <summary>The node's value, without its own wrapper.</summary>
        private JToken Plain(YamlDocNode node)
        {
            if (++_count > MaxNodes)
                throw new
                    InvalidDataException($"The YAML document expands to more than {MaxNodes:N0} values, which is too many to write as JSON.");

            switch (node)
            {
                case YamlScalarNode scalar:
                    return ConvertScalar(scalar);
                case YamlMappingNode mapping:
                    return Within(mapping, () => ConvertMapping(mapping));
                case YamlSequenceNode sequence:
                    return Within(sequence, () => ConvertSequence(sequence));
                case YamlAliasNode alias:
                    return Annotated(Target(alias), null);
                default:
                    throw new NotSupportedException($"Unknown YAML node type {node.GetType().Name}.");
            }
        }

        private JToken Within(YamlDocNode node, Func<JToken> convert)
        {
            if (!_path.Add(node))
                throw new InvalidDataException("A YAML node that contains itself (through an alias) cannot be written as JSON.");

            try
            {
                return convert();
            }
            finally
            {
                _path.Remove(node);
            }
        }

        private static YamlDocNode Target(YamlAliasNode alias)
        {
            return alias.Target ??
                   throw new InvalidDataException($"The alias *{alias.TargetAnchor} refers to an anchor that is not defined before it.");
        }

        private static JToken ConvertScalar(YamlScalarNode scalar)
        {
            object? value;
            if (scalar.TagIsExplicit)
                value = scalar.IsStandardTag
                            ? YamlScalars.ResolveTagged(scalar.Tag![StandardTagPrefix.Length..], scalar.Value)
                            : scalar.Value;
            else
                value = scalar.Style == ScalarStyle.Plain ? YamlScalars.ResolvePlain(scalar.Value) : scalar.Value;

            return value is null ? JValue.CreateNull() : new JValue(value);
        }

        private JToken ConvertSequence(YamlSequenceNode sequence)
        {
            var array = new JArray();
            foreach (var item in sequence.Items)
                array.Add(Annotated(item, null));
            return array;
        }

        private JToken ConvertMapping(YamlMappingNode mapping)
        {
            var merged = new JObject();
            var own    = new List<YamlMappingEntry>();
            foreach (var entry in mapping.Entries)
                if (IsMergeKey(entry.Key) && TryMergeSources(entry.Value, out var sources))
                {
                    // the first mapping to have a key wins
                    foreach (var source in sources)
                        foreach (var property in ((JObject)Plain(source)).Properties())
                            if (!merged.ContainsKey(property.Name))
                                merged[property.Name] = property.Value.DeepClone();
                }
                else
                {
                    own.Add(entry);
                }

            var result = new JObject();
            foreach (var property in merged.Properties())
                result[property.Name] = property.Value.DeepClone();

            // the mapping's own entries override what was merged in, and the last of a repeated key wins
            foreach (var entry in own)
                result[KeyOf(entry.Key)] = Annotated(entry.Value, entry.Key);

            return result;
        }

        private static bool IsMergeKey(YamlDocNode key)
        {
            return key is YamlScalarNode { Value: "<<" } scalar &&
                   ((scalar.Style == ScalarStyle.Plain && !scalar.TagIsExplicit) || scalar.Tag == StandardTagPrefix + "merge");
        }

        /// <summary>The mappings a merge key's value stands for: a mapping, an alias to one, or a sequence of those.</summary>
        private static bool TryMergeSources(YamlDocNode value, out List<YamlMappingNode> sources)
        {
            sources = [];
            var resolved = Resolve(value);
            if (resolved is YamlMappingNode single)
            {
                sources.Add(single);
                return true;
            }

            if (resolved is not YamlSequenceNode sequence) return false;

            foreach (var item in sequence.Items)
            {
                if (Resolve(item) is not YamlMappingNode mapping) return false;
                sources.Add(mapping);
            }

            return true;
        }

        /// <summary>Follows aliases to the node they end at.</summary>
        private static YamlDocNode Resolve(YamlDocNode node)
        {
            // an alias chain cannot loop (an alias can only point back at an anchor that was already complete), but do not trust it
            for (int hops = 0; node is YamlAliasNode alias; hops++)
            {
                if (hops > 1000) throw new InvalidDataException($"The alias *{alias.TargetAnchor} leads to itself.");
                node = Target(alias);
            }

            return node;
        }

        /// <summary>
        ///     The text of a mapping key: a scalar's own text, an alias's target's, and for a mapping or sequence its compact
        ///     JSON.
        /// </summary>
        private string KeyOf(YamlDocNode key)
        {
            var resolved = Resolve(key);
            if (resolved is YamlScalarNode scalar) return scalar.Value;

            bool was = _annotate;
            _annotate = false;
            try
            {
                return Plain(resolved).ToString(Formatting.None);
            }
            finally
            {
                _annotate = was;
            }
        }
    }
}

/// <summary>How the text of a YAML scalar is read as a value, by YAML 1.2's core schema.</summary>
public static class YamlScalars
{
    private static readonly Regex IntegerPattern = new(@"^[-+]?[0-9]+\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FloatPattern = new(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?\z",
                                                     RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    ///     What an unquoted, untagged scalar means: null, a bool, a long (or a <see cref="BigInteger" /> too big for one), a
    ///     double, or
    ///     else its own text.
    /// </summary>
    public static object? ResolvePlain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return true;
            case "false" or "False" or "FALSE":
                return false;
            case ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF":
                return double.PositiveInfinity;
            case "-.inf" or "-.Inf" or "-.INF":
                return double.NegativeInfinity;
            case ".nan" or ".NaN" or ".NAN":
                return double.NaN;
        }

        if (IntegerPattern.IsMatch(text))
            return Narrow(BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        if (IsDigits(text, "0x", 16))
            return Narrow(BigInteger.Parse("0" + text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
        if (IsDigits(text, "0o", 8)) return Narrow(Octal(text[2..]));
        if (FloatPattern.IsMatch(text)) return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

        return text;
    }

    /// <summary>
    ///     What a scalar with an explicit standard tag (<c>!!int</c>: <paramref name="tagName" /> is "int") means. A value
    ///     that
    ///     does not fit its tag stays its own text, and so does anything that is not null, bool, int or float.
    /// </summary>
    public static object? ResolveTagged(string tagName, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var plain = ResolvePlain(text);
        switch (tagName)
        {
            case "null":
                return plain is null ? null : text;
            case "bool":
                return plain is bool ? plain : text;
            case "int":
                return plain is long or BigInteger ? plain : text;
            case "float":
                return plain switch
                       {
                           double       => plain,
                           long l       => (double)l,
                           BigInteger b => (double)b,
                           _            => text
                       };
            default:
                return text;
        }
    }

    private static bool IsDigits(string text, string prefix, int radix)
    {
        if (text.Length <= prefix.Length || !text.StartsWith(prefix, StringComparison.Ordinal)) return false;

        foreach (char c in text.AsSpan(prefix.Length))
        {
            bool ok = radix == 16 ? Uri.IsHexDigit(c) : c is >= '0' and <= '7';
            if (!ok) return false;
        }

        return true;
    }

    private static BigInteger Octal(string digits)
    {
        BigInteger value = BigInteger.Zero;
        foreach (char c in digits)
            value = value * 8 + (c - '0');
        return value;
    }

    private static object Narrow(BigInteger value)
    {
        return value >= long.MinValue && value <= long.MaxValue ? (long)value : value;
    }
}