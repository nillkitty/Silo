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
    [JsonPropertyName("user")]     public string? Username { get; set; }
    [JsonPropertyName("domain")]   public string? Domain   { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("notes")]    public string? Notes    { get; set; }

    public static byte[] Write(Credential credential)
    {
        var payload = new CredentialPayload
                      {
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
