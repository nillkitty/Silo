using System.Linq;
using Silo.Model;

namespace Silo.Credentials;

/// <summary>Which of a credential's fields a realm uses.</summary>
public readonly record struct RealmFields(bool UserName, bool Domain, bool Secret);

/// <summary>What makes a credential acceptable to save. Pure, so it can be tested on its own.</summary>
public static class CredentialRules
{
    public const int MaxNameLength = 100;

    /// <summary>
    ///     The fields the realm signs in with. The password box holds the realm's secret: the password, or the token,
    ///     thumbprint or key (see <see cref="SecretLabel" />).
    /// </summary>
    public static RealmFields Uses(AuthRealm realm)
    {
        return realm switch
               {
                   AuthRealm.Conventional                                    => new RealmFields(true, false, true),
                   AuthRealm.Domain                                          => new RealmFields(true, true, true),
                   AuthRealm.Bearer or AuthRealm.Certificate or AuthRealm.RsaKey => new RealmFields(false, false, true),
                   _                                                         => new RealmFields(false, false, false)
               };
    }

    /// <summary>What a person calls the realm in a list.</summary>
    public static string Describe(AuthRealm realm)
    {
        return realm switch
               {
                   AuthRealm.DefaultCredential => "Signed-in Windows user",
                   AuthRealm.DefaultNetwork    => "Process network credential",
                   AuthRealm.Domain            => "Windows domain account",
                   AuthRealm.Conventional      => "User name and password",
                   AuthRealm.Bearer            => "Bearer token",
                   AuthRealm.Certificate       => "Certificate",
                   AuthRealm.RsaKey            => "RSA private key",
                   AuthRealm.Anonymous         => "Anonymous",
                   _                           => realm.ToString()
               };
    }

    /// <summary>What the realm calls the secret in the password box.</summary>
    public static string SecretLabel(AuthRealm realm)
    {
        return realm switch
               {
                   AuthRealm.Bearer      => "token",
                   AuthRealm.Certificate => "certificate thumbprint",
                   AuthRealm.RsaKey      => "private key",
                   _                     => "password"
               };
    }

    /// <summary>The reason the credential cannot be saved, or null when it can.</summary>
    /// <param name="credential">The credential to check.</param>
    /// <param name="others">The names (and ids) of the credentials that are already saved.</param>
    public static string? Check(Credential credential, IEnumerable<(int Id, string Name)> others)
    {
        string name = credential.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) return "Give the credential a name.";
        if (name.Length > MaxNameLength) return $"The name can be at most {MaxNameLength} characters.";
        if (name.StartsWith('$')) return "A name cannot start with $.";

        if (others.Any(o => o.Id != credential.Id && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
            return $"There is already a credential called \"{name}\".";

        if (!Enum.IsDefined(credential.Realm)) return "Choose what kind of credential it is.";

        var uses = Uses(credential.Realm);
        if (uses.UserName && string.IsNullOrWhiteSpace(credential.Username)) return "Enter a user name.";
        if (uses.Secret && string.IsNullOrEmpty(credential.Password)) return $"Enter a {SecretLabel(credential.Realm)}.";

        return null;
    }
}
