namespace Silo.Connectors;

public interface IUriProvider
{
    bool HandlesScheme(string scheme);
}

public interface IUriProvider<out TNative> : IUriProvider
{
    TNative? ParseUri(Uri uri, bool throwErrors);
}