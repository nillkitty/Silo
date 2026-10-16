using System.Text.RegularExpressions;

namespace Silo.Connectors.System;

/// <summary>Where in WMI a <c>wmi:</c> URI points: a namespace, a class in it, or an instance of a class.</summary>
/// <param name="Namespace">The WMI namespace (<c>root\cimv2</c>).</param>
public abstract record WmiLocation(string Namespace)
{
    /// <summary>The namespace of a URI that names none.</summary>
    public const string DefaultNamespace = @"root\cimv2";

    // WMI names: letters, digits and underscores, not starting with a digit (system classes start with two underscores)
    private static readonly Regex ClassName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    // namespace segments are a little freer
    private static readonly Regex  NamespaceSegment = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    public                  string Host { get; set; }
    public                  Uri?   Icon { get; }

    public List<string> PropertiesToLoad { get; } = [];
    public string?      TypeName         { get; }

    /// <summary>
    ///     Reads a <c>wmi:</c> URI. The path is, in order: the namespace segments (none: <c>root\cimv2</c>), one class
    ///     segment, and
    ///     optionally <c>@</c>; the class is the last segment (before the <c>@</c>), and a path that ends in a slash is a
    ///     namespace with no class. The namespace is written without its leading <c>root</c>, which is added unless it is
    ///     there
    ///     (<c>Citrix/Client/ICA</c> is <c>root\Citrix\Client\ICA</c>; <c>root/cimv2</c> is left alone). A key is either
    ///     <c>@</c>
    ///     (the class's only instance) or a query of <c>property=value</c> (the instance whose <c>property</c> is <c>value</c>
    ///     );
    ///     nothing else is allowed in a query and a fragment is not used.
    /// </summary>
    /// <remarks>
    ///     <code>
    /// wmi:///                                  root\cimv2                            (WmiRoot)
    /// wmi:///Citrix/Client/                    root\Citrix\Client                     (WmiRoot)
    /// wmi:///Win32_Service                     root\cimv2, Win32_Service             (WmiClass)
    /// wmi:///Win32_ComputerSystem/@            root\cimv2, Win32_ComputerSystem=@    (WmiPath)
    /// wmi:///Citrix/Client/ICA/ICA_Client/@    root\Citrix\Client\ICA, ICA_Client=@   (WmiPath)
    /// wmi:///Win32_Volume?name=C:\             root\cimv2, Win32_Volume.name="C:\"   (WmiPath)
    ///     </code>
    ///     A <see cref="WmiLocation" /> has no machine name yet, so a host other than this computer is refused rather than
    ///     dropped, as <c>RegistryUriProvider</c> does.
    /// </remarks>
    /// <exception cref="NotSupportedException">The scheme is not <c>wmi</c>, or the host is another computer.</exception>
    /// <exception cref="UriFormatException">The rest of the URI is not what is described above.</exception>
    public static WmiLocation FromUri(Uri uri)
    {
        uri.Required();
        if (!uri.Scheme.Is("wmi"))
            throw new NotSupportedException("WmiLocation only supports wmi: URIs");

        string host                = uri.Host.Trim();
        if (host.Length == 0) host = ".";
        if (!IsLocalHost(host))
            throw new
                NotSupportedException($"WMI on another computer (host '{host}') is not supported yet -- a WmiLocation has no field for a machine name.");

        if (uri.Fragment.Length > 1)
            throw new UriFormatException($"WMI URI '{uri}' has a fragment, which is not used (a key is '@' or ?property=value).");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();

        bool singleton     = segments.Count > 0 && segments[^1]   == "@";
        bool namespaceOnly = !singleton         && segments.Count > 0 && uri.AbsolutePath.EndsWith('/');
        if (singleton) segments.RemoveAt(segments.Count - 1);

        (string Property, string Value)? query = ParseQuery(uri);
        if (singleton && query is not null)
            throw new UriFormatException($"WMI URI '{uri}' has both '@' and a query; use one of them to say which instance.");

        if (namespaceOnly)
        {
            if (query is not null) throw new UriFormatException($"WMI URI '{uri}' has a key but no class.");
            return new WmiRoot(NamespaceOf(segments, uri));
        }

        if (segments.Count == 0)
        {
            if (singleton) throw new UriFormatException($"WMI URI '{uri}' has '@' but no class.");
            if (query is not null) throw new UriFormatException($"WMI URI '{uri}' has a key but no class.");
            return new WmiRoot(DefaultNamespace);
        }

        string className = segments[^1];
        if (!ClassName.IsMatch(className))
            throw new UriFormatException($"'{className}' is not a WMI class name (letters, digits and underscores).");

        string ns = NamespaceOf(segments.Take(segments.Count - 1).ToList(), uri);

        if (singleton) return new WmiPath(ns,      new WmiHop(className, "@"));
        if (query is { } q) return new WmiPath(ns, new WmiHop(className, q.Value, q.Property));
        return new WmiClass(ns, className);
    }

    // this computer, however it is written
    private static bool IsLocalHost(string host)
    {
        return host is "." or "::1" or "[::1]" or "127.0.0.1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.Equals(Environment.MachineName,                                       StringComparison.OrdinalIgnoreCase);
    }

    // Citrix/Client/ICA -> root\Citrix\Client\ICA; root/cimv2 stays; nothing -> root\cimv2
    private static string NamespaceOf(IReadOnlyList<string> segments, Uri uri)
    {
        if (segments.Count == 0) return DefaultNamespace;

        foreach (string segment in segments)
            if (!NamespaceSegment.IsMatch(segment))
                throw new UriFormatException($"'{segment}' is not a WMI namespace name in '{uri}'.");

        return segments[0].Equals("root", StringComparison.OrdinalIgnoreCase)
                   ? string.Join('\\', segments)
                   : "root\\" + string.Join('\\', segments);
    }

    // ?property=value -- the whole query is the one key; the value may hold anything (C:\, = and &)
    private static (string Property, string Value)? ParseQuery(Uri uri)
    {
        if (uri.Query.Length <= 1) return null;

        string text = Uri.UnescapeDataString(uri.Query[1..]);
        int    eq   = text.IndexOf('=');
        if (eq <= 0 || eq == text.Length - 1)
            throw new UriFormatException($"The query of WMI URI '{uri}' must be property=value.");

        string property = text[..eq].Trim();
        if (!ClassName.IsMatch(property))
            throw new UriFormatException($"'{property}' is not a WMI property name (letters, digits and underscores).");

        return (property, text[(eq + 1)..]);
    }

    public Uri ToUri(WmiQuery wmiQuery)
    {
        return ToUri(wmiQuery.Required().Query);
    }

    public Uri ToUri(string? query = null)
    {
        string? path = Namespace.Replace('\\', '/').TrimStart('/');
        if (path.ToLower().StartsWith("root/"))
            path = path?[5..];
        if (this is WmiClass cl) path = $"{path}/{cl.ClassName.Trim()}";
        var ub                        = new UriBuilder("wmi");
        ub.Host     = Host;
        ub.Path     = path;
        ub.Query    = query is string q ? $"?q={q}" : "";
        ub.Fragment = string.Join(',', PropertiesToLoad);
        return ub.Uri;
    }
}

public record WmiHop(string ClassName, string Key, string? KeyProperty = null);

public record WmiClass(string Namespace, string ClassName) : WmiLocation(Namespace)
{
}

public record WmiPath(string Namespace, params WmiHop[] Hops) : WmiLocation(Namespace);

public record WmiRoot(string Namespace) : WmiLocation(Namespace);