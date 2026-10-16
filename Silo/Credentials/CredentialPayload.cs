using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Silo.Contracts.Providers.Crypto;
using Silo.Model;

namespace Silo.Credentials;

/// <summary>
///     What goes inside the sealed bytes of a credential: everything but its name. JSON, so a later version can add fields without
///     breaking old rows.
/// </summary>
public sealed class CredentialPayload
{
    /// <summary>The context the payload is sealed under (see <see cref="Silo.Contracts.Providers.Crypto.ISiloEncryption.Encrypt" />).</summary>
    public const string Context = "credential";

    [JsonPropertyName("v")]        public int     Version  { get; set; } = 1;

    /// <summary>The realm by name (stable if the enum is reordered); null in a credential saved before realms existed, which is read as <see cref="AuthRealm.Conventional" />.</summary>
    [JsonPropertyName("realm")]
    public string? Realm { get; set; }

    [JsonPropertyName("user")]     public string? Username { get; set; }
    [JsonPropertyName("domain")]   public string? Domain   { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("notes")]    public string? Notes    { get; set; }

    /// <summary>The realm the payload names; <see cref="AuthRealm.Conventional" /> when it names none (it was saved before realms) or one this version does not know.</summary>
    public AuthRealm ReadRealm()
    {
        return Enum.TryParse<AuthRealm>(Realm, true, out var realm) && Enum.IsDefined(realm) ? realm : AuthRealm.Conventional;
    }

    public static byte[] Write(Credential credential)
    {
        var payload = new CredentialPayload
                      {
                          Realm    = credential.Realm.ToString(),
                          Username = credential.Username,
                          Domain   = credential.Domain,
                          Password = credential.Password,
                          Notes    = credential.Notes
                      };
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    public static CredentialPayload Read(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<CredentialPayload>(bytes) ?? throw new SecretException("The stored credential is empty.");
        }
        catch (JsonException ex)
        {
            throw new SecretException("The stored credential is damaged.", ex);
        }
    }
}
