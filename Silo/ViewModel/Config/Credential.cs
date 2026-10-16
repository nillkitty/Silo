using System.Security.Authentication;
using Silo.Connectors.File;
using Silo.Connectors.Ldap;
using Silo.Connectors.Web;
using Silo.Contracts.ConnectorServices;
using SqlConnection = Silo.Connectors.Sql.SqlConnection;

namespace Silo.Model;

public class SiloItem(OpenSilo parent)
{
    public IDatabaseProvider Data   { get; } = parent.Required().Data.Required();
    public OpenSilo          Parent { get; } = parent.Required();
}

/// <summary>
///     An item stored in the silo's global database.
/// </summary>
/// <param name="parent"></param>
public class GlobalItem(OpenSilo parent) : SiloItem(parent)
{
}

/// <summary>
///     A saved way of signing in: a name for it, a user name, a domain, a password and notes. It is kept in the Silo's
///     <c>Secret</c> table,
///     sealed by the Silo's encryption (see <see cref="Silo.Credentials.CredentialService" />); only the name is stored in
///     the clear, so
///     the list of credentials can be shown without opening anything. A connector that takes a credential finds it in the
///     <c>Metadata</c> of its connection request.
/// </summary>
/// <remarks><see cref="ToString" /> gives the name only: a credential is safe to put in a log line or a message.</remarks>
public sealed class Credential : ICredential
{
    /// <summary>
    ///     The account as it is written to sign in: <c>DOMAIN\user</c>, else the user name; null when there is no user
    ///     name.
    /// </summary>
    public string? Account => string.IsNullOrEmpty(Username) ? null : string.IsNullOrEmpty(Domain) ? Username : Domain + "\\" + Username;

    /// <summary>
    ///     The time the credential was created
    /// </summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     The domain or realm name the credential is for
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>The row in the Silo's <c>Secret</c> table; 0 until it is saved.</summary>
    public int Id { get; set; }

    public DateTime Modified { get; set; } = DateTime.UtcNow;

    /// <summary>What the person calls it: "Sales server", "Contoso admin". Required; shown in lists.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Human-readable notes
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    ///     The password or key for the credential
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    ///     The username for the credential, if supplied
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    ///     The target realm of the credential: how it is used to sign in (which of the user name, domain and password
    ///     it uses; see <see cref="Silo.Credentials.CredentialRules.Uses" />). A credential made with a user name and
    ///     password is <see cref="AuthRealm.Conventional" />, which is also what a credential saved before realms existed is read as.
    /// </summary>
    public AuthRealm Realm { get; set; } = AuthRealm.Conventional;

    string? IDisplayName.DisplayName => Name;

    /// <summary>
    ///     Creates a memberwise clone of the Credential
    /// </summary>
    public Credential Clone()
    {
        return (Credential)MemberwiseClone();
    }

    public override string ToString()
    {
        return Name;
    }

    private NotSupportedException Unsupported(ICredentialSink target)
    {
        return new NotSupportedException($"A {Realm} credential cannot be used to sign in to a {target.GetType().Name}.");
    }

    private void RequireUserAndPassword()
    {
        if (Username.Nill()) throw new InvalidCredentialException("'Username' is a required field for this realm.");
        if (Password.Nill()) throw new InvalidCredentialException("'Password' is a required field for this realm.");
    }

    private void _ConfigureAnonymous(ICredentialSink target)
    {
        switch (target.Required())
        {
            case LdapCredentialSink l:
                l.Clear();
                break;
            case WebConnection wr:
            case FileConnection fc:
            case SqlConnection sql:
                break;
        }
    }

    /// <summary>The signed-in Windows user: no name or password is handed over, the sink uses the current security context.</summary>
    private void _ConfigureDefault(ICredentialSink target)
    {
        switch (target.Required())
        {
            case SqlConnection sql:
                sql.SetCredentialFragment("Integrated Security=SSPI;");
                break;
            case LdapCredentialSink ldap:
                ldap.Clear();
                break;
            case IUserPasswordSink sink:
                sink.SetUserPassword(null, null, null);
                break;
            // a WebConnection (and a FileConnection) reads the Realm from the credential itself
        }
    }

    /// <summary>The network credential of the process: the same identity as <see cref="_ConfigureDefault" /> for what signs in here.</summary>
    private void _ConfigureDefaultNet(ICredentialSink target)
    {
        _ConfigureDefault(target);
    }

    private void _ConfigureDomain(ICredentialSink target)
    {
        RequireUserAndPassword();
        switch (target.Required())
        {
            case SqlConnection: // a connection string cannot sign in as another Windows account
                throw Unsupported(target);
            case IUserPasswordSink sink:
                sink.SetUserPassword(Username, Password, Domain);
                break;
        }
    }

    private void _ConfigureBearer(ICredentialSink target)
    {
        throw Unsupported(target.Required());
    }

    private void _ConfigureCert(ICredentialSink target)
    {
        throw Unsupported(target.Required());
    }

    private void _ConfigureRsaKey(ICredentialSink target)
    {
        throw Unsupported(target.Required());
    }

    private void _ConfigureConventional(ICredentialSink target)
    {
        RequireUserAndPassword();
        switch (target.Required())
        {
            case SqlConnection sql:
                // the builder quotes what needs it: a password with a ; in it must not end the setting
                sql.SetCredentialFragment(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder { UserID = Username, Password = Password }.ConnectionString + ";");
                break;
            case LdapCredentialSink ldap:
                ldap.SetBasicCredential(Username!, Password!);
                break;
            case IUserPasswordSink sink:
                sink.SetUserPassword(Username, Password, null);
                break;
        }
    }

    /// <summary>
    ///     Hands what the <see cref="Realm" /> uses (the signed-in user, a user name and password, ...) to the sink; a sink that
    ///     does not take the realm's kind of credential is left alone, except where it cannot be used at all (<see cref="NotSupportedException" />).
    /// </summary>
    /// <exception cref="InvalidCredentialException">The realm needs a user name or a password that is not there.</exception>
    public void Configure(ICredentialSink target)
    {
        switch (Realm)
        {
            case AuthRealm.DefaultCredential:
                _ConfigureDefault(target);
                break;
            case AuthRealm.DefaultNetwork:
                _ConfigureDefaultNet(target);
                break;
            case AuthRealm.Domain:
                _ConfigureDomain(target);
                break;
            case AuthRealm.Conventional:
                _ConfigureConventional(target);
                break;
            case AuthRealm.Bearer:
                _ConfigureBearer(target);
                break;
            case AuthRealm.Certificate:
                _ConfigureCert(target);
                break;
            case AuthRealm.RsaKey:
                _ConfigureRsaKey(target);
                break;
            case AuthRealm.Anonymous:
                _ConfigureAnonymous(target);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Realm), Realm, "Unknown realm.");
        }
    }
}

public enum AuthRealm
{
    /// <summary>
    ///     The current security context of the current user
    /// </summary>
    DefaultCredential,

    /// <summary>
    ///     The current network credential assigned for the current process
    /// </summary>
    DefaultNetwork,

    /// <summary>
    ///     A credential consisting of a domain, username, and password, intented for Windows (Negotiate/Kerberos)
    ///     authentication with a target
    /// </summary>
    Domain,

    /// <summary>
    ///     A credential consisting of a username and password intended for SQL or LDAP Simple Auth
    ///     authentication
    /// </summary>
    Conventional,

    /// <summary>
    ///     A credential consisting of a Bearer token
    /// </summary>
    Bearer,

    /// <summary>
    ///     A credential consisting of an X509 thumbprint for which the current user has the private key
    /// </summary>
    Certificate,

    /// <summary>
    ///     A credential consisting of an RSA Private Key
    /// </summary>
    RsaKey,

    /// <summary>
    ///     No credential, anonymous
    /// </summary>
    Anonymous
}