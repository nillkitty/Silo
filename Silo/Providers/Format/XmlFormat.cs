using System.IO;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Silo.Providers.Data.Source;
using Silo.Providers.Diagramming;
using Silo.Providers.Document;
using Silo.Providers.Reflection;
using Telefrag.DI;

namespace Silo.Providers.Format;

[Singleton(typeof(IFileFormat))]
public class XmlFormat : IFileFormat, IDocumentProvider<XDocument>, IBindableFormat
{
    public LoadOptions Options     { get; set; } = LoadOptions.SetBaseUri | LoadOptions.SetLineInfo;
    public string      DisplayName { get; }      = "XML File";

    public string FileExtension { get; } = "xml";
    public string ContentType   { get; } = "application/xml";

    /// <summary>The file read once, as JSON (by the conventions of <see cref="XmlJson" />).</summary>
    public object? Bind(Uri uri)
    {
        return new XmlDocument(this, uri).GetData();
    }

    public IEnumerable<DataVerb> GetSupportedVerbs()
    {
        throw new NotImplementedException();
    }

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
public class XmlDocument(IDocumentProvider<XDocument> provider, Uri uri) : DocumentBase<XDocument>(provider, uri), IStructuredDataSource
{
    public string? AttributePrefix { get; } = "@";

    public string? MetaCommentName      { get; } = "$comment";
    public string? MetaTypeHintName     { get; } = "$type";
    public string? MetaWrapperValueName { get; } = "$value";
    public string? TextPropertyName     { get; } = "#text";

    public XDocument? ReadData()
    {
        using var fs = new FileStream(uri.Required().LocalPath, FileMode.Open, FileAccess.Read);
        return XDocument.Load(fs);
    }

    /// <summary>The document as JSON; the conventions are on <see cref="XmlJson" />.</summary>
    private JToken? XmlToJson(XDocument xd)
    {
        return XmlJson.Convert(xd);
    }

    public JToken? GetData()
    {
        if (ReadData() is XDocument xd)
            return XmlToJson(xd);
        return null;
    }
}

/// <summary>
///     XML to JSON, by the usual convention (the same one Json.NET's <c>JsonConvert.SerializeXNode</c> uses, so the result
///     reads the
///     way people expect):
///     <list type="bullet">
///         <item>The document is an object with one property, the root element's name.</item>
///         <item>An element with only text is that text as a string; an empty element with no attributes is <c>null</c>.</item>
///         <item>Attributes are properties named <c>@name</c> (namespace declarations <c>@xmlns</c> / <c>@xmlns:p</c>).</item>
///         <item>An element with attributes or children and also text keeps the text in <c>#text</c>.</item>
///         <item>
///             Child elements are properties named as written, prefix included (<c>p:name</c>); siblings with the same
///             name become an array, at the position of the first. A lone child is not an array, so a shape that can
///             repeat is not always one.
///         </item>
///         <item>
///             Text, CDATA and character data are all text. Mixed content loses where the text sat among the children:
///             its pieces are trimmed and joined with a space.
///         </item>
///         <item>Comments, processing instructions, the declaration and DTD are left out.</item>
///         <item>Every value is a string: XML has no number or boolean type.</item>
///     </list>
/// </summary>
public static class XmlJson
{
    /// <summary>
    ///     Deepest element nesting converted; more throws <see cref="InvalidDataException" /> rather than overflowing the
    ///     stack.
    /// </summary>
    public const int MaxDepth = 1000;

    /// <summary>The document as a JSON object, or an empty object when it has no root element.</summary>
    public static JToken Convert(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new JObject();
        if (document.Root is { } root)
            result[NameOf(root, root.Name)] = Element(root, 1);
        return result;
    }

    /// <summary>The element's value: see <see cref="XmlJson" />.</summary>
    public static JToken Convert(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return Element(element, 1);
    }

    private static JToken Element(XElement e, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"The XML is nested more than {MaxDepth} elements deep.");

        var attributes = e.Attributes().ToList();
        var children   = e.Elements().ToList();
        var text       = Text(e, children.Count > 0 || attributes.Count > 0);

        if (attributes.Count == 0 && children.Count == 0)
            return text is null ? JValue.CreateNull() : new JValue(text);

        var o = new JObject();
        foreach (var a in attributes)
            o["@" + AttributeName(a)] = new JValue(a.Value);

        foreach (var group in children.GroupBy(c => NameOf(c, c.Name)))
        {
            var items = group.ToList();
            o[group.Key] = items.Count == 1 ? Element(items[0], depth + 1) : new JArray(items.Select(i => Element(i, depth + 1)));
        }

        if (text is not null)
            o["#text"] = new JValue(text);

        return o;
    }

    /// <summary>
    ///     The element's own text. As the whole content of a plain element it is kept exactly; beside attributes or child
    ///     elements the
    ///     pieces are trimmed, blank ones dropped, and the rest joined with a space. Null when there is none.
    /// </summary>
    private static string? Text(XElement e, bool mixed)
    {
        var pieces = e.Nodes().OfType<XText>().Select(t => t.Value).ToList();
        if (pieces.Count == 0) return null;

        if (!mixed)
        {
            var all = string.Concat(pieces);
            return all.Length == 0 ? null : all;
        }

        var kept = pieces.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        return kept.Count == 0 ? null : string.Join(" ", kept);
    }

    /// <summary>
    ///     The name as written in the source: <c>prefix:local</c> for a namespaced one whose prefix is in scope, else the
    ///     local name.
    /// </summary>
    private static string NameOf(XElement e, XName name)
    {
        if (name.Namespace == XNamespace.None) return name.LocalName;
        var prefix = e.GetPrefixOfNamespace(name.Namespace);
        return string.IsNullOrEmpty(prefix) ? name.LocalName : prefix + ":" + name.LocalName;
    }

    private static string AttributeName(XAttribute a)
    {
        if (a.IsNamespaceDeclaration)
            return a.Name.Namespace == XNamespace.None ? "xmlns" : "xmlns:" + a.Name.LocalName;

        if (a.Name.Namespace == XNamespace.None) return a.Name.LocalName;
        var prefix = a.Parent?.GetPrefixOfNamespace(a.Name.Namespace);
        return string.IsNullOrEmpty(prefix) ? a.Name.LocalName : prefix + ":" + a.Name.LocalName;
    }
}