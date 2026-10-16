namespace Silo.Contracts.Core;

/// <summary>
///     A sink that signs in with a user name and a password. A credential (<see cref="ICredential.Configure" />) hands them
///     over by calling <see cref="SetUserPassword" />; nothing reads them back out of the credential.
/// </summary>
public interface IUserPasswordSink : ICredentialSink
{
    /// <param name="userName">The user name, as written (<c>bob</c>, <c>bob@contoso.com</c> or <c>CONTOSO\bob</c>).</param>
    /// <param name="password">The password.</param>
    /// <param name="domain">The domain, when it is kept apart from the user name.</param>
    void SetUserPassword(string? userName, string? password, string? domain);
}
