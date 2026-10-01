using System.IO;
using System.Xml.Linq;

namespace Silo.Providers.Format;

public class XmlFormat : IFileFormat, IDocumentProvider<XDocument>
{
    public LoadOptions Options       { get; set; } = LoadOptions.SetBaseUri | LoadOptions.SetLineInfo;
    public string      DisplayName   { get; }      = "XML File";
    public string      FileExtension { get; }      = "xml";

    /// <summary>
    ///     Reads the single XML document in <paramref name="stream" />. XML has no convention for
    ///     concatenating multiple documents in one stream the way NDJSON or YAML's '---' do, so
    ///     this always yields exactly one <see cref="XDocument" /> -- still as an
    ///     <see cref="IEnumerable{T}" />, to satisfy <see cref="IDocumentProvider{TContent}" />.
    /// </summary>
    public IEnumerable<XDocument> Read(FileStream stream)
    {
        var x = new StreamReader(stream.Required());
        var s = x.ReadToEnd();
        yield return XDocument.Parse(s, Options);
    }
}

/// <summary>
///     An XML document
/// </summary>
public class XmlDocument(IDocumentProvider<XDocument> provider, Uri uri) : DocumentBase<XDocument>(provider, uri)
{
}
