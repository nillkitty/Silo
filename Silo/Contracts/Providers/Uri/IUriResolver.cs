namespace Silo.Providers;

public interface IUriResolver
{
    /// <summary>
    ///     Resolves the specified URI for the specified context to an object,
    ///     or null if nothing was registered at that location
    /// </summary>
    object? Resolve(Uri uri, UriContext context);

    /// <summary>
    ///     Resolves the specified URI for the specified context,
    ///     casts the result to a <typeparamref name="TReturn" />
    ///     and returns it (or null) to the caller.
    /// </summary>
    public TReturn? Resolve<TReturn>(Uri uri, UriContext context)
    {
        return Resolve(uri, context) is TReturn tr ? tr : default;
    }
}