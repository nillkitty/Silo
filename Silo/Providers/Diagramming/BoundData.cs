using System.Linq;
using System.Threading;
using Silo.Drawio.Live;
using Silo.Providers.Format;

namespace Silo.Providers.Diagramming;

/// <summary>
///     Resolves a bound diagram node's URI to the object it binds to. In order: a live value scheme registered with
///     <see cref="DiagramData" /> (<c>silo:</c>, <c>bind:</c>, <c>push:</c>, ...), the registered <see cref="IUriResolver" />s,
///     then <c>file:</c>: a folder as itself, a file through the <see cref="IBindableFormat" /> that claims its extension
///     (JSON and XML as structured data, Markdown tables and lists, CSV and TSV as rows), any other file as itself. Then
///     <c>silo-path</c> is followed into the result.
/// </summary>
public static class BoundData
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext(typeof(BoundData));

    /// <summary>Resolves <paramref name="uri" /> (off the UI thread) and follows <paramref name="path" /> into it.</summary>
    /// <exception cref="InvalidOperationException">Nothing resolves the URI, or the path is not there.</exception>
    public static async Task<object> ResolveAsync(Uri uri, string? path, CancellationToken cancel)
    {
        object? value = await Task.Run(() => Resolve(uri), cancel).ConfigureAwait(false);
        if (value is null) throw new InvalidOperationException($"Nothing in Silo can open '{uri.OriginalString}'.");

        if (!string.IsNullOrWhiteSpace(path))
        {
            string p = path;
            value = await Task.Run(() => ObjectPath.TryGet(value, p, out object? found) ? found : null, cancel).ConfigureAwait(false);
            if (value is null) throw new InvalidOperationException($"'{path}' was not found in '{uri.OriginalString}'.");
        }

        return value;
    }

    private static object? Resolve(Uri uri)
    {
        if (uri.IsAbsoluteUri && DiagramData.IsRegistered(uri.Scheme) &&
            DiagramData.TryResolve(uri.Scheme + ":" + uri.OriginalString[(uri.Scheme.Length + 1)..], out object? live))
            return live;

        foreach (var resolver in Resolvers())
            foreach (var context in (UriContext[])[UriContext.Any, UriContext.Private])
                try
                {
                    if (resolver.Resolve(uri, context) is { } found) return found;
                }
                catch (NotImplementedException)
                {
                    break; // a resolver that is not written yet
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "{resolver} failed to resolve {uri}", resolver.GetType().Name, uri);
                    break;
                }

        return uri.IsAbsoluteUri && uri.IsFile ? ResolveFile(uri) : null;
    }

    private static List<IUriResolver> Resolvers()
    {
        try
        {
            return App.ResolveAll<IUriResolver>();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not resolve the URI resolvers");
            return [];
        }
    }

    /// <summary>
    ///     A folder as itself; a file through the first <see cref="IBindableFormat" /> that claims its extension
    ///     (<see cref="Formats.ForFile" />) and has something for it; any other file as itself.
    /// </summary>
    private static object? ResolveFile(Uri uri)
    {
        string path = uri.LocalPath;
        if (Directory.Exists(path)) return new DirectoryInfo(path);
        if (!File.Exists(path)) throw new FileNotFoundException($"'{path}' does not exist.", path);

        foreach (var format in Formats.ForFile(path).OfType<IBindableFormat>())
            if (format.Bind(uri) is { } bound)
                return bound;

        return new FileInfo(path);
    }
}
