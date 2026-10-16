using System.DirectoryServices;

namespace Silo.Connectors.Ldap;

public class LdapConnector : ConnectorBase
{
    public TimeSpan ConnectionStateThreshold { get; set; } = TimeSpan.FromMinutes(5);
    public Uri      DefaultUri               => new("adsi://", UriKind.Absolute);

    public int? PageSize { get; set; } = 20_000;

    public PasswordEncodingMethod PasswordEncoding { get; set; } = PasswordEncodingMethod.PasswordEncodingSsl;
    public ReferralChasingOption  Referral         { get; set; } = ReferralChasingOption.Subordinate;

    protected override IConnection OnCreateConnection(ConnectionRequest crq)
    {
        return LdapModel.Connect(this, crq);
    }


    public override Type GetStateType()
    {
        return typeof(LdapState);
    }
}