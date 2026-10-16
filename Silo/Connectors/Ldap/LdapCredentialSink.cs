namespace Silo.Connectors.Ldap;

/// <summary>
///     Takes a user name and password from a credential (<see cref="ICredential.Configure" />) so the connection can sign
///     in
///     with them. The user name carries the domain (<c>DOMAIN\user</c>) when it was kept apart.
/// </summary>
public sealed class LdapCredentialSink : IUserPasswordSink
{
    public string? Password { get; private set; }

    /// <summary>
    ///     The name to sign in as, domain included; null when the credential gave none (the signed-in Windows user is
    ///     used).
    /// </summary>
    public string? UserName { get; private set; }

    /// <summary>
    ///     <c>DOMAIN\user</c> from a user and a domain; the user alone when there is no domain or it is already written
    ///     (<c>CONTOSO\bob</c>, <c>bob@contoso.com</c>); null when there is no user.
    /// </summary>
    public static string? Combine(string? userName, string? domain)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;
        if (string.IsNullOrWhiteSpace(domain) || userName.Contains('\\') || userName.Contains('@')) return userName;

        return domain + "\\" + userName;
    }

    public void Clear()
    {
        UserName = null;
        Password = null;
    }

    public void SetBasicCredential(string username, string password)
    {
        UserName = username;
        Password = password;
    }

    public void ConfigureFrom(ICredential credential)
    {
        credential.Required().Configure(this);
    }

    public void SetUserPassword(string? userName, string? password, string? domain)
    {
        UserName = Combine(userName, domain);
        Password = password;
    }
}