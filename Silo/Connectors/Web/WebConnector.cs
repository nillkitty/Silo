using System.Net.Http;
using Silo.Model;
using Silo.Ui.Windows.Connecting;
using Telefrag.DI;

namespace Silo.Connectors.Web;

/// <summary>
///     Acts as the primary HTTP(S) handler, and then does whatever's
///     needed based on the <c>Content-Type</c> of the response to a
///     <c>HEAD</c> request.
/// </summary>
[Singleton(typeof(IUriProvider))]
[Singleton(typeof(IConnector))]
public class WebConnector : IConnector, IUriProvider, IUriConnector, IConnectorUi
{
    public ConnectorSetupFlags Flags     { get; } = ConnectorSetupFlags.AlwaysRequiresConfig | ConnectorSetupFlags.IgnoreIcmpVisibility;
    public Exception?          LastError { get; protected set; }
    public List<object>        Metadata  { get; } = [];

    public string? Description => "Web sites and web services (http and https)";

    // Silo only ever opens http(s) inside itself -- it doesn't try to become the system's browser.
    public UriSchemeRegistration    ForSystem              => UriSchemeRegistration.DontRegister;
    public UriSchemeRegistration    ForUser                => UriSchemeRegistration.DontRegister;
    public bool                     IsPublic               => true;
    public ExistingProviderHandling OtherProvidersHandling => ExistingProviderHandling.Default;

    public IConnection CreateConnection(ConnectionRequest crq)
    {
        var target = crq.RequestedUri ?? throw new DestinationRequiredException("WebConnector requires a destination URI");
        if (HandlesScheme(target.Scheme) == default)
            throw new NotSupportedException($"WebConnector cannot handle URI scheme '{target.Scheme}'");

        return new WebConnection(this, target, crq);
    }

    public Type GetStateType()
    {
        return typeof(IConnectionState);
    }

    public IConnectorSetup CreateSetup(ConnectorSetupContext context)
    {
        return new UriFieldSetup(this, text => UriRules.Check(text, [this], UriRules.AnyScope), "Web address",
                                 "Examples:  https://example.com/    https://api.example.com/v1/items");
    }

    public string? Validate(Uri uri)
    {
        return uri.Host.Length > 0 ? null : "A web address needs a host name, such as https://example.com/.";
    }

    public UriSchemeScope HandlesScheme(string scheme)
    {
        return scheme?.ToLowerInvariant() switch
               {
                   "http" or "https" => UriSchemeScope.WellKnown,
                   _                 => UriSchemeScope.Unknown
               };
    }
}

/// <summary>
///     Thrown when a destination URI is required and was not provided.
/// </summary>
public class DestinationRequiredException(string msg, Exception? innerEx = null) : Exception(msg, innerEx);

public class WebConnection : ConnectionBase<HttpClient>, ICredentialSink
{
    public    ICredential?       Credential                   { get; private set; }
    public    bool               UseDefaultCredentials        { get; private set; }
    public    bool               UseDefaultNetworkCredentials { get; private set; }
    protected IHttpClientFactory ClientFactory                { get; }

    public WebConnection(WebConnector connector, Uri target, ConnectionRequest? crq = null) : base(connector)
    {
        Destination       = new Destination(target.Required());
        ConnectionRequest = crq;
        ClientFactory     = App.Require<IHttpClientFactory>();
        Data              = ClientFactory.CreateClient(NodeUri?.ToString() ?? UniqueId.ToString());
    }

    public void ConfigureFrom(ICredential credential)
    {
        Credential                   = credential;
        UseDefaultCredentials        = credential?.Realm == AuthRealm.DefaultCredential;
        UseDefaultNetworkCredentials = credential?.Realm == AuthRealm.DefaultNetwork;
    }
}