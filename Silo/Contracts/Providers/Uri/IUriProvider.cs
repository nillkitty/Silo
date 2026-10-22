namespace Silo.Providers;

public interface IUriProvider
{
    UriSchemeRegistration ForSystem { get; }
    UriSchemeRegistration ForUser   { get; }
    bool                  IsPublic  { get; }

    ExistingProviderHandling OtherProvidersHandling { get; }
    UriSchemeScope           HandlesScheme(string scheme);
}

public interface IUriProvider<out TNative> : IUriProvider
{
    TNative? ParseUri(Uri uri, bool throwErrors);
}