using Telefrag.DI;

namespace Silo.Providers;

[Transient(typeof(IUriResolver))]
public class PrivateUriResolver : IUriResolver
{
    public object? Resolve(Uri uri, UriContext context)
    {
        throw new NotImplementedException();
    }
}