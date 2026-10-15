using System.IO;
using Silo.Contracts.Connectors;

namespace Silo.Connectors;

/// <summary>What <see cref="UriRules.Check" /> found out about a piece of text.</summary>
/// <param name="IsValid">True when it is a URI some connector will take.</param>
/// <param name="Uri">The URI the text means, when it means one.</param>
/// <param name="Connector">The connector that will take it.</param>
/// <param name="Message">What to tell the person: which connector it goes to, or what is wrong.</param>
public sealed record UriCheck(bool IsValid, Uri? Uri, IConnector? Connector, string Message);

/// <summary>
///     The rules for a URI typed into the URI tab (also used to decide whether a listed destination can be connected to):
///     it must be absolute, its scheme must belong to a connector -- Silo's own schemes (reg:, hkcu:, ...) or a well-known
///     one a connector has taken (file:, http:) -- and that connector must accept the rest of it. Pure logic, no UI.
/// </summary>
public static class UriRules
{
    /// <summary>The scopes that count: <c>Private</c> (Silo owns the scheme) and <c>WellKnown</c> (a standard scheme a connector handles).</summary>
    public static readonly IReadOnlyCollection<UriSchemeScope> DefaultScopes = [UriSchemeScope.Private, UriSchemeScope.WellKnown];

    /// <summary>Every scope but <c>Unknown</c>: used when the person has already picked the connector, so any scheme it handles is fine.</summary>
    public static readonly IReadOnlyCollection<UriSchemeScope> AnyScope = [UriSchemeScope.Private, UriSchemeScope.WellKnown, UriSchemeScope.AdHoc];

    /// <summary>
    ///     The URI the text means: a URI as it is, or a Windows path (<c>C:\Data</c>, <c>\\server\share</c>) as a file: URI.
    ///     Null if it is neither.
    /// </summary>
    public static Uri? Parse(string? text)
    {
        text = text?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(text)) return null;

        try
        {
            if (Path.IsPathFullyQualified(text) && (text.StartsWith(@"\\", StringComparison.Ordinal) || (text.Length > 1 && text[1] == ':')))
                return new Uri(text);
        }
        catch (Exception)
        {
            /* not a usable path: try it as a URI */
        }

        // one-letter "schemes" are drive letters that were not fully qualified (c:foo), not URIs
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1 ? uri : null;
    }

    /// <summary>The connector that handles <paramref name="uri" />'s scheme, or null.</summary>
    public static IUriConnector? FindConnector(Uri uri, IEnumerable<IUriConnector> connectors, IReadOnlyCollection<UriSchemeScope>? scopes = null)
    {
        scopes ??= DefaultScopes;
        return connectors.FirstOrDefault(c => scopes.Contains(c.HandlesScheme(uri.Scheme)));
    }

    /// <summary>Whether <paramref name="text" /> is a URI one of <paramref name="connectors" /> will take, and if not, why.</summary>
    public static UriCheck Check(string? text, IEnumerable<IUriConnector> connectors, IReadOnlyCollection<UriSchemeScope>? scopes = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new UriCheck(false, null, null, "Enter a URI such as reg:///hkey_current_user/software, a folder such as C:\\Data, or a share such as \\\\server\\share.");

        var uri = Parse(text);
        if (uri is null)
            return new UriCheck(false, null, null, "That is not a complete URI: it needs a scheme (such as reg:, file: or https:), or has to be a full Windows path (C:\\Data, \\\\server\\share).");

        return CheckUri(uri, connectors, scopes);
    }

    /// <summary>Whether <paramref name="uri" /> is a URI one of <paramref name="connectors" /> will take, and if not, why.</summary>
    public static UriCheck CheckUri(Uri uri, IEnumerable<IUriConnector> connectors, IReadOnlyCollection<UriSchemeScope>? scopes = null)
    {
        var list      = connectors.ToList();
        var connector = FindConnector(uri, list, scopes);
        if (connector is null)
            return new UriCheck(false, uri, null, $"No connector handles \"{uri.Scheme}:\" URIs.");

        string name = connector is IConnector c ? c.DisplayName : connector.GetType().Name;
        if (connector.Validate(uri) is { Length: > 0 } problem)
            return new UriCheck(false, uri, connector as IConnector, problem);

        var kind = connector.HandlesScheme(uri.Scheme) == UriSchemeScope.Private ? "private scheme" : "scheme";
        return new UriCheck(true, uri, connector as IConnector, $"Goes to the {name} connector ({kind} \"{uri.Scheme}:\").");
    }

    /// <summary>A short name for what the URI points at: the last part of the path, the registry key, or the host.</summary>
    public static string NameFor(Uri uri)
    {
        string last = uri.IsFile
                          ? Path.GetFileName(uri.LocalPath.TrimEnd('\\', '/')) is { Length: > 0 } leaf ? leaf : uri.LocalPath
                          : Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/')).Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

        if (last.Length > 0) return last;
        return uri.Host.Length > 0 ? uri.Host : uri.ToString();
    }
}
