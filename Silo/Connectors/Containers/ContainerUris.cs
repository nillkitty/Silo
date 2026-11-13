using System.IO;
using Silo.Ui.Windows.Connecting;
using IOFile = System.IO.File;

namespace Silo.Connectors.Containers;

/// <summary>
///     The URIs of the connections that open a file as a tree: <c>archive:</c> for a container file (a zip, a tar, an ISO
///     image, a cabinet) and <c>datafile:</c> for a structured data file (JSON, XML, YAML). The rest of the URI is the file's
///     <c>file:</c> URI (<c>archive:///C:/Data/Backup.zip</c>, <c>datafile://server/share/settings.json</c>); a place inside
///     the file is the fragment (<c>archive:///C:/Data/Backup.zip#docs/readme.txt</c>).
/// </summary>
public static class ContainerUris
{
    public const string ArchiveScheme  = "archive";
    public const string DataFileScheme = "datafile";

    /// <summary>The URI of a file opened with <paramref name="scheme" />, and of a place inside it when <paramref name="inner" /> is given.</summary>
    public static Uri For(string scheme, string path, string? inner = null)
    {
        var file    = new Uri(Path.GetFullPath(path));
        var builder = new UriBuilder(file) { Scheme = scheme };
        if (inner is { Length: > 0 }) builder.Fragment = Uri.EscapeDataString(inner).Replace("%2F", "/");
        return builder.Uri;
    }

    /// <summary>The local path of the file a <c>archive:</c>, <c>datafile:</c> or <c>file:</c> URI names; null when it names none.</summary>
    public static string? PathOf(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true }) return null;

        try
        {
            if (uri.IsFile) return uri.LocalPath;

            var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeFile, Fragment = string.Empty, Query = string.Empty };
            return builder.Uri.IsFile ? builder.Uri.LocalPath : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    ///     What the text in a connection's box means for a connector that opens files with <paramref name="scheme" />: a path,
    ///     a <c>file:</c> URI or a URI of the scheme, naming a file that is there and that <paramref name="accepts" /> (which
    ///     gives the reason when it does not).
    /// </summary>
    public static UriCheck Check(string? text, string scheme, IConnector connector, Func<string, string?> accepts)
    {
        var uri = UriRules.Parse(text);
        if (uri is null) return new UriCheck(false, null, null, "Enter the path of a file, such as C:\\Data\\file" + (scheme == ArchiveScheme ? ".zip" : ".json") + ".");
        if (!uri.IsFile && !string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
            return new UriCheck(false, null, null, $"Only a file path, a file: URI or a {scheme}: URI opens here.");

        string? path = PathOf(uri);
        if (path is null) return new UriCheck(false, null, null, "That does not name a file.");
        if (accepts(path) is { } problem) return new UriCheck(false, null, null, problem);
        if (!IOFile.Exists(path)) return new UriCheck(false, null, null, $"\"{path}\" does not exist.");

        return new UriCheck(true, For(scheme, path), connector, $"Opens {Path.GetFileName(path)}");
    }
}
