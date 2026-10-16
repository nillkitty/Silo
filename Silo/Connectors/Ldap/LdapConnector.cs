using System.DirectoryServices;
using Silo.Contracts.ConnectorServices;

namespace Silo.Connectors.Ldap;

/// <summary>
///     Connects to directories (Active Directory and other LDAP servers) through ADSI: <c>adsi://</c> and <c>ldap://</c> are the same,
///     <c>ldaps://</c> is over TLS; no host means the current domain.
/// </summary>
public class LdapConnector : ConnectorBase, IAuthenticatable, IUriConnector
{
    /// <summary>
    ///     How credentials are used: none are needed (the signed-in Windows user is who the connection signs in as when it was
    ///     given none).
    /// </summary>
    public ConnectorAuthFlags AuthFlags { get; set; } = ConnectorAuthFlags.None;

    public TimeSpan ConnectionStateThreshold { get; set; } = TimeSpan.FromMinutes(5);
    public Uri      DefaultUri               => new("adsi://", UriKind.Absolute);

    public int? PageSize { get; set; } = 20_000;

    public PasswordEncodingMethod PasswordEncoding { get; set; } = PasswordEncodingMethod.PasswordEncodingSsl;
    public ReferralChasingOption  Referral         { get; set; } = ReferralChasingOption.Subordinate;

    protected override IConnection OnCreateConnection(ConnectionRequest crq)
    {
        return LdapModel.Create(this, crq);
    }


    public UriSchemeScope HandlesScheme(string scheme)
    {
        return scheme?.ToLowerInvariant() switch
               {
                   "adsi"            => UriSchemeScope.Private,
                   "ldap" or "ldaps" => UriSchemeScope.WellKnown,
                   _                 => UriSchemeScope.Unknown
               };
    }

    public string? Validate(Uri uri)
    {
        try
        {
            LdapModel.ToAdsPath(uri);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    public override Type GetStateType()
    {
        return typeof(LdapState);
    }
}