using Silo.Providers.Reflection;
using Telefrag.DI;

namespace Silo.Providers.Format;

/// <summary>
///     A file format that is only named: what it is called, its extensions and its content type. The application knows such
///     a file when it sees one (to filter a dialog, pick a highlighter, answer with a <c>Content-Type</c>) without reading
///     it itself.
/// </summary>
/// <param name="displayName">What the format is called.</param>
/// <param name="fileExtension">The extensions, comma-separated, without dots; the first is the one a new file gets.</param>
/// <param name="contentType">The MIME type.</param>
public abstract class KnownFileFormat(string displayName, string fileExtension, string contentType) : IFileFormat
{
    public string DisplayName   { get; } = displayName;
    public string FileExtension { get; } = fileExtension;
    public string ContentType   { get; } = contentType;

    /// <summary>The display name.</summary>
    public override string ToString()
    {
        return DisplayName;
    }
}

/// <summary>A <see cref="KnownFileFormat" /> written in the syntax of <typeparamref name="TBase" />.</summary>
/// <typeparam name="TBase">The format whose syntax this one uses (<see cref="JsonFormat" />, <see cref="XmlFormat" />, ...).</typeparam>
public abstract class DialectFileFormat<TBase>(string displayName, string fileExtension, string contentType)
    : KnownFileFormat(displayName, fileExtension, contentType), IDialectFormat
    where TBase : IFileFormat
{
    public Type BaseFormat => typeof(TBase);
}

// ---- Silo's own -------------------------------------------------------------------------------------------------

/// <summary>A Silo file.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class SiloFileFormat() : KnownFileFormat("Silo File", "silo", "application/x-silo");

/// <summary>A draw.io diagram (the <c>mxfile</c> XML).</summary>
[Singleton(typeof(IFileFormat))]
public sealed class DrawioFormat() : DialectFileFormat<XmlFormat>("draw.io Diagram", "drawio", "application/vnd.jgraph.mxfile");

// ---- text -------------------------------------------------------------------------------------------------------

/// <summary>Plain text.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class PlainTextFormat() : KnownFileFormat("Text File", "txt", "text/plain");

/// <summary>A log written as lines of text.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class LogFormat() : KnownFileFormat("Log File", "log", "text/plain");

/// <summary>An INI settings file.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class IniFormat() : KnownFileFormat("INI File", "ini", "text/plain");

/// <summary>C# source code.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class CSharpFormat() : KnownFileFormat("C# Source", "cs", "text/x-csharp");

// ---- JSON dialects ----------------------------------------------------------------------------------------------

/// <summary>JSON with comments (and the looser JSON5).</summary>
[Singleton(typeof(IFileFormat))]
public sealed class JsonWithCommentsFormat() : DialectFileFormat<JsonFormat>("JSON with Comments", "jsonc,json5", "application/json");

/// <summary>GeoJSON.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class GeoJsonFormat() : DialectFileFormat<JsonFormat>("GeoJSON File", "geojson", "application/geo+json");

/// <summary>An HTTP Archive: the requests a browser made, as JSON.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class HarFormat() : DialectFileFormat<JsonFormat>("HTTP Archive", "har", "application/json");

/// <summary>A Jupyter notebook.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class NotebookFormat() : DialectFileFormat<JsonFormat>("Jupyter Notebook", "ipynb", "application/x-ipynb+json");

/// <summary>A web app manifest.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class WebManifestFormat() : DialectFileFormat<JsonFormat>("Web App Manifest", "webmanifest", "application/manifest+json");

// ---- XML dialects -----------------------------------------------------------------------------------------------

/// <summary>XAML.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class XamlFormat() : DialectFileFormat<XmlFormat>("XAML File", "xaml", "application/xaml+xml");

/// <summary>An XML Schema.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class XsdFormat() : DialectFileFormat<XmlFormat>("XML Schema", "xsd", "application/xml");

/// <summary>An XSLT stylesheet.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class XsltFormat() : DialectFileFormat<XmlFormat>("XSLT Stylesheet", "xslt,xsl", "application/xslt+xml");

/// <summary>A .NET configuration file.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class ConfigFormat() : DialectFileFormat<XmlFormat>("Configuration File", "config", "application/xml");

/// <summary>An MSBuild project or import.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class MsBuildFormat() : DialectFileFormat<XmlFormat>("MSBuild File", "csproj,vbproj,props,targets", "application/xml");

/// <summary>A NuGet package manifest.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class NuspecFormat() : DialectFileFormat<XmlFormat>("NuGet Manifest", "nuspec", "application/xml");

/// <summary>A .NET resource file.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class ResxFormat() : DialectFileFormat<XmlFormat>("Resource File", "resx", "application/xml");

/// <summary>An Apple property list (the XML form).</summary>
[Singleton(typeof(IFileFormat))]
public sealed class PlistFormat() : DialectFileFormat<XmlFormat>("Property List", "plist", "application/x-plist");

/// <summary>An AvalonEdit syntax definition.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class XshdFormat() : DialectFileFormat<XmlFormat>("Syntax Definition", "xshd", "application/xml");

/// <summary>An RSS feed.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class RssFormat() : DialectFileFormat<XmlFormat>("RSS Feed", "rss", "application/rss+xml");

/// <summary>An Atom feed.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class AtomFormat() : DialectFileFormat<XmlFormat>("Atom Feed", "atom", "application/atom+xml");

// ---- web --------------------------------------------------------------------------------------------------------

/// <summary>An HTML page.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class HtmlFormat() : KnownFileFormat("HTML Document", "html,htm,xhtml", "text/html"), IWebDocumentFormat;

/// <summary>An SVG picture.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class SvgFormat() : DialectFileFormat<XmlFormat>("SVG Image", "svg", "image/svg+xml"), IWebDocumentFormat;

/// <summary>A PDF document.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class PdfFormat() : KnownFileFormat("PDF Document", "pdf", "application/pdf"), IWebDocumentFormat;

/// <summary>A web page saved with its files in one MIME file.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class MhtmlFormat() : KnownFileFormat("MHTML Web Archive", "mhtml,mht", "multipart/related");

/// <summary>JavaScript.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class JavaScriptFormat() : KnownFileFormat("JavaScript File", "js,mjs", "application/javascript");

/// <summary>A CSS stylesheet.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class CssFormat() : KnownFileFormat("CSS Stylesheet", "css", "text/css");

/// <summary>A WOFF web font.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class WoffFormat() : KnownFileFormat("WOFF Font", "woff", "font/woff");

/// <summary>A WOFF 2 web font.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class Woff2Format() : KnownFileFormat("WOFF2 Font", "woff2", "font/woff2");

// ---- documents and programs -------------------------------------------------------------------------------------

/// <summary>An XPS document.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class XpsFormat() : KnownFileFormat("XPS Document", "xps", "application/vnd.ms-xpsdocument");

/// <summary>A .NET assembly or native library.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class AssemblyFormat() : KnownFileFormat("Assembly", "dll", "application/x-msdownload"), IProgramFormat;

/// <summary>A Windows program.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class ExecutableFormat() : KnownFileFormat("Program", "exe", "application/vnd.microsoft.portable-executable"), IProgramFormat;
