using System.IO;
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
public class YamlFormat : IFileFormat, IDocumentProvider<YamlDocNode>
{
    public string DisplayName   { get; } = "YAML File";
    public string FileExtension { get; } = "yaml";

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
        var scanner = new Scanner(reader, skipComments: false);
        var parser  = new Parser(scanner);

        return new YamlDocumentParser(parser).ReadDocuments();
    }
}

/// <summary>
///     A YAML document
/// </summary>
public class YamlDocument(YamlFormat provider, Uri uri) : DocumentBase<YamlDocNode>(provider, uri);
