using System.Text.RegularExpressions;

namespace Silo.Connectors.System;

/// <summary>Where in WMI a <c>wmi:</c> URI points: a namespace, a class in it, or an instance of a class.</summary>
/// <param name="Namespace">The WMI namespace (<c>root\cimv2</c>).</param>
public abstract record WmiLocation(string Namespace)
{
    /// <summary>The namespace of a URI that names none.</summary>
    public const string DefaultNamespace = @"root\cimv2";

    /// <summary>The <see cref="Host" /> of this computer.</summary>
    public const string LocalHost = ".";

    // WMI names: letters, digits and underscores, not starting with a digit (system classes start with two underscores)
    private static readonly Regex ClassName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    // namespace segments are a little freer
    private static readonly Regex NamespaceSegment = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    /// <summary>The computer to ask: <c>.</c> (<see cref="LocalHost" />) for this one, else its name.</summary>
    public string Host { get; init; } = LocalHost;

    /// <summary>The properties to read (the URI's fragment); none means all of them.</summary>
    public IReadOnlyList<string> PropertiesToLoad { get; init; } = [];

    /// <summary>What to call this kind of thing in the Explorer.</summary>
    public abstract string TypeName { get; }

    /// <summary>What to call this one in the Explorer: the last namespace name, the class, or the class and its key.</summary>
    public abstract string Label { get; }

    /// <summary>The key of this kind of thing's icon in the icon dictionary, as a relative URI.</summary>
    public abstract Uri Icon { get; }

    // a record compares its properties one by one, which would compare the list of properties by reference
    public virtual bool Equals(WmiLocation? other)
    {
        return other is not null && EqualityContract == other.EqualityContract && Namespace == other.Namespace && Host == other.Host &&
               PropertiesToLoad.SequenceEqual(other.PropertiesToLoad);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(EqualityContract);
        hash.Add(Namespace);
        hash.Add(Host);
        foreach (string property in PropertiesToLoad) hash.Add(property);
        return hash.ToHashCode();
    }

    /// <summary>
    ///     Reads a <c>wmi:</c> URI. The host is the computer to ask (none, <c>.</c> or <c>localhost</c>: this one). The path is,
    ///     in order: the namespace segments (none: <c>root\cimv2</c>), one class segment, and optionally <c>@</c>; the class is
    ///     the last segment (before the <c>@</c>), and a path that ends in a slash is a namespace with no class. The namespace is
    ///     written without its leading <c>root</c>, which is added unless it is there (<c>Citrix/Client/ICA</c> is
    ///     <c>root\Citrix\Client\ICA</c>; <c>root/cimv2</c> is left alone). A key is either <c>@</c> (the class's only instance)
    ///     or a query of <c>property=value</c> (the instance whose <c>property</c> is <c>value</c>); the one exception is
    ///     <c>q=</c>, which is a WQL query to run in a namespace (see <see cref="FromUri(Uri, out string?)" />). The fragment is
    ///     the comma-separated names of the properties to read (<see cref="PropertiesToLoad" />).
    /// </summary>
    /// <remarks>
    ///     <code>
    /// wmi:///                                  root\cimv2                            (WmiRoot)
    /// wmi:///Citrix/Client/                    root\Citrix\Client                     (WmiRoot)
    /// wmi:///Win32_Service                     root\cimv2, Win32_Service             (WmiClass)
    /// wmi:///Win32_Service#Name,State          ... reading only Name and State
    /// wmi:///Win32_ComputerSystem/@            root\cimv2, Win32_ComputerSystem=@    (WmiPath)
    /// wmi:///Citrix/Client/ICA/ICA_Client/@    root\Citrix\Client\ICA, ICA_Client=@   (WmiPath)
    /// wmi:///Win32_Volume?name=C:\             root\cimv2, Win32_Volume.name="C:\"   (WmiPath)
    /// wmi://server42/Win32_Service             the same, on server42
    ///     </code>
    /// </remarks>
    /// <exception cref="NotSupportedException">The scheme is not <c>wmi</c>.</exception>
    /// <exception cref="UriFormatException">The rest of the URI is not what is described above.</exception>
    public static WmiLocation FromUri(Uri uri)
    {
        return FromUri(uri, out _);
    }

    /// <summary>
    ///     As <see cref="FromUri(Uri)" />, and also says the WQL query the URI carries (<c>wmi:///Citrix/?q=SELECT * FROM X</c>),
    ///     or null when it carries none. A query is run in a namespace, so a URI with one names a <see cref="WmiRoot" /> and no
    ///     class.
    /// </summary>
    public static WmiLocation FromUri(Uri uri, out string? wql)
    {
        uri.Required();
        if (!uri.Scheme.Is("wmi"))
            throw new NotSupportedException("WmiLocation only supports wmi: URIs");

        string host = uri.Host.Trim();
        if (host.Length == 0 || IsLocalHost(host)) host = LocalHost;

        IReadOnlyList<string> properties = ParseProperties(uri);

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();

        bool singleton     = segments.Count > 0 && segments[^1]   == "@";
        bool namespaceOnly = !singleton         && segments.Count > 0 && uri.AbsolutePath.EndsWith('/');
        if (singleton) segments.RemoveAt(segments.Count - 1);

        (string Property, string Value)? key = ParseQuery(uri);

        wql = null;
        if (key is { } k && k.Property.Equals("q", StringComparison.OrdinalIgnoreCase))
        {
            wql = k.Value;
            key = null;

            if (singleton || segments.Count > 0 && !namespaceOnly)
                throw new UriFormatException($"WMI URI '{uri}' has a WQL query (q=) and a class; a query is run in a namespace (end the path with '/').");

            return new WmiRoot(NamespaceOf(segments, uri)) { Host = host, PropertiesToLoad = properties };
        }

        if (singleton && key is not null)
            throw new UriFormatException($"WMI URI '{uri}' has both '@' and a query; use one of them to say which instance.");

        if (namespaceOnly || segments.Count == 0)
        {
            if (singleton) throw new UriFormatException($"WMI URI '{uri}' has '@' but no class.");
            if (key is not null) throw new UriFormatException($"WMI URI '{uri}' has a key but no class.");
            if (properties.Count > 0) throw new UriFormatException($"WMI URI '{uri}' has properties but no class.");
            return new WmiRoot(NamespaceOf(segments, uri)) { Host = host };
        }

        string className = segments[^1];
        if (!ClassName.IsMatch(className))
            throw new UriFormatException($"'{className}' is not a WMI class name (letters, digits and underscores).");

        string ns = NamespaceOf(segments.Take(segments.Count - 1).ToList(), uri);

        if (singleton) return new WmiPath(ns, new WmiHop(className, "@")) { Host = host, PropertiesToLoad = properties };
        if (key is { } q) return new WmiPath(ns, new WmiHop(className, q.Value, q.Property)) { Host = host, PropertiesToLoad = properties };
        return new WmiClass(ns, className) { Host = host, PropertiesToLoad = properties };
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

    // #Name,State
    private static IReadOnlyList<string> ParseProperties(Uri uri)
    {
        if (uri.Fragment.Length <= 1) return [];

        var names = Uri.UnescapeDataString(uri.Fragment[1..]).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string name in names)
            if (!ClassName.IsMatch(name))
                throw new UriFormatException($"'{name}' is not a WMI property name in the fragment of '{uri}' (letters, digits and underscores).");

        return names;
    }

    public Uri ToUri(WmiQuery wmiQuery)
    {
        return ToUri(wmiQuery.Required().Query);
    }

    /// <summary>
    ///     The URI that <see cref="FromUri(Uri)" /> reads back as this location (the same one, but for the case of a leading
    ///     <c>root</c>). With a <paramref name="wql" /> query it is the URI of the query: a query is run in a namespace, so the
    ///     class and key of this location are left out.
    /// </summary>
    /// <exception cref="NotSupportedException">The location cannot be written as a URI (a <see cref="WmiPath" /> of other than one hop with a key).</exception>
    public Uri ToUri(string? wql = null)
    {
        // the namespace segments, written without their leading root, and none at all for the default namespace
        var segments = Namespace.Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (Namespace == DefaultNamespace) segments.Clear();
        else if (segments.Count > 1 && segments[0] == "root") segments.RemoveAt(0);

        var path  = new StringBuilder("/");
        string query = string.Empty;
        foreach (string segment in segments) path.Append(Uri.EscapeDataString(segment)).Append('/');

        bool isRoot = wql is not null || this is WmiRoot;
        if (!isRoot)
            switch (this)
            {
                case WmiClass c:
                    path.Append(Uri.EscapeDataString(c.ClassName));
                    break;
                case WmiPath { Hops: [var hop] }:
                    path.Append(Uri.EscapeDataString(hop.ClassName));
                    if (hop.Key == "@") path.Append("/@");
                    else if (hop.KeyProperty is { } property) query = "?" + property + "=" + Uri.EscapeDataString(hop.Key);
                    else throw new NotSupportedException($"The key '{hop.Key}' of {hop.ClassName} has no property name, so it has no URI.");
                    break;
                default:
                    throw new NotSupportedException($"{GetType().Name} cannot be written as a URI.");
            }

        if (wql is not null) query = "?q=" + Uri.EscapeDataString(wql);

        string fragment = PropertiesToLoad.Count > 0 && (wql is not null || this is not WmiRoot) ? "#" + string.Join(',', PropertiesToLoad) : string.Empty;

        return new Uri($"wmi://{(Host == LocalHost ? string.Empty : Host)}{path}{query}{fragment}", UriKind.Absolute);
    }
}

public record WmiHop(string ClassName, string Key, string? KeyProperty = null);

/// <summary>A namespace, to look for classes in.</summary>
public record WmiRoot(string Namespace) : WmiLocation(Namespace)
{
    public override string TypeName => "WMI Namespace";
    public override string Label    => Namespace.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? Namespace;
    public override Uri    Icon     => new("Folder17", UriKind.Relative);
}

/// <summary>A class, to look for instances of.</summary>
public record WmiClass(string Namespace, string ClassName) : WmiLocation(Namespace)
{
    public override string TypeName => "WMI Class";
    public override string Label    => ClassName;
    public override Uri    Icon     => new("Cube", UriKind.Relative);
}

/// <summary>An instance of a class.</summary>
public record WmiPath(string Namespace, params WmiHop[] Hops) : WmiLocation(Namespace)
{
    public override string TypeName => "WMI Instance";
    public override string Label    => Hops.LastOrDefault() is { } hop ? hop.KeyProperty is null ? hop.ClassName : $"{hop.ClassName} ({hop.KeyProperty}={hop.Key})" : Namespace;
    public override Uri    Icon     => new("Record", UriKind.Relative);

    // the hops are an array, which a record compares by reference
    public virtual bool Equals(WmiPath? other)
    {
        return base.Equals(other) && other is not null && Hops.SequenceEqual(other.Hops);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        foreach (var hop in Hops) hash.Add(hop);
        return hash.ToHashCode();
    }
}
