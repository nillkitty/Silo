using Silo.Contracts.Connectors;
using Silo.Shell;
using Silo.Ui.Windows.Connecting;
using Telefrag.DI;

namespace Silo.Connectors.Web;

/// <summary>
///     Acts as the primary HTTP(S) handler, and then does whatever's
///     needed based on the <c>Content-Type</c> of the response to a
///     <c>HEAD</c> request.
/// </summary>
[Singleton(typeof(IUriProvider))]
public class WebConnector : IConnector, IUriProvider, IUriConnector, IConnectorUi
{
    public ConnectorSetupFlags Flags { get; }

    // Silo only ever opens http(s) inside itself -- it doesn't try to become the system's browser.
    public UriSchemeRegistration    ForSystem              => UriSchemeRegistration.DontRegister;
    public UriSchemeRegistration    ForUser                => UriSchemeRegistration.DontRegister;
    public ExistingProviderHandling OtherProvidersHandling => ExistingProviderHandling.Default;
    public Exception?               LastError              { get; }
    public List<object>             Metadata               { get; }

    public string? Description => "Web sites and web services (http and https)";

    public string? Validate(Uri uri)
    {
        return uri.Host.Length > 0 ? null : "A web address needs a host name, such as https://example.com/.";
    }

    public IConnectorSetup CreateSetup(ConnectorSetupContext context)
    {
        return new UriFieldSetup(this, text => UriRules.Check(text, [this], UriRules.AnyScope), "Web address",
                                 "Examples:  https://example.com/    https://api.example.com/v1/items");
    }

    public UriSchemeScope HandlesScheme(string scheme)
    {
        return scheme?.ToLowerInvariant() switch
               {
                   "http" or "https" => UriSchemeScope.WellKnown,
                   _                 => UriSchemeScope.Unknown
               };
    }

    public IConnection CreateConnection(ConnectionRequest crq)
    {
        throw new NotImplementedException();
    }

    public Type GetStateType()
    {
        throw new NotImplementedException();
    }
}