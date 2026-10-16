using System.Linq;
using Silo.Contracts.Providers.Crypto;
using Silo.Contracts.Providers.Data;
using Silo.Model;

namespace Silo.Credentials;

/// <summary>A credential as a list shows it: nothing secret but what a person would see anyway (the account name).</summary>
/// <param name="Account">The user name (with its domain); null when the credential cannot be opened here or has no user name.</param>
/// <param name="IsReadable">False when the credential cannot be opened here (it was protected for another person or computer).</param>
public sealed record CredentialSummary(int Id, string Name, string? Account, DateTime Modified, bool IsReadable);

/// <summary>
///     Lists, reads, saves and deletes the credentials of one Silo: the rows of kind <c>credential</c> in its <see cref="ISecretStore" />, sealed
///     by its <see cref="ISiloEncryption" />. Written against the two interfaces so it can be tested without a Silo.
/// </summary>
public sealed class CredentialService
{
    public const string Kind = "credential";

    private readonly ISecretStore    _store;
    private readonly ISiloEncryption _encryption;

    public CredentialService(ISecretStore store, ISiloEncryption encryption)
    {
        _store      = store ?? throw new ArgumentNullException(nameof(store));
        _encryption = encryption ?? throw new ArgumentNullException(nameof(encryption));
    }

    /// <summary>Raised (on the thread that made the change) after a credential was saved or deleted in any Silo.</summary>
    public static event EventHandler? Changed;

    /// <summary>The protector that locks this Silo's secrets, for display.</summary>
    public string ProviderId => _encryption.ProviderId;

    /// <summary>The saved credentials, by name. Nothing is shown that is not shown anyway; a credential that cannot be opened is listed as such.</summary>
    public IReadOnlyList<CredentialSummary> List()
    {
        var result = new List<CredentialSummary>();
        foreach (var row in _store.GetSecrets(Kind).OrderBy(r => r.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                var c = ToCredential(row);
                result.Add(new CredentialSummary(row.Id, row.Key, c.Account, row.Modified, true));
            }
            catch (SecretException)
            {
                result.Add(new CredentialSummary(row.Id, row.Key, null, row.Modified, false));
            }
        }

        return result;
    }

    /// <summary>The credential with its password, or null if there is none with that id.</summary>
    /// <exception cref="SecretException">It cannot be opened here.</exception>
    public Credential? Get(int id)
    {
        var row = _store.GetSecret(id);
        return row is { } r && r.Kind == Kind ? ToCredential(r) : null;
    }

    /// <summary>The credential with that name (any case), or null.</summary>
    /// <exception cref="SecretException">It cannot be opened here.</exception>
    public Credential? Find(string name)
    {
        var row = _store.GetSecrets(Kind).LastOrDefault(r => string.Equals(r.Key, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        return row is null ? null : ToCredential(row);
    }

    /// <summary>The reason the credential cannot be saved (see <see cref="CredentialRules" />), or null.</summary>
    public string? Validate(Credential credential)
    {
        var others = _store.GetSecrets(Kind).Select(r => (r.Id, Name: r.Key));
        return CredentialRules.Check(credential, others);
    }

    /// <summary>Saves the credential (a new one when its <see cref="Credential.Id" /> is 0). Returns it as saved.</summary>
    /// <exception cref="InvalidOperationException">It does not pass <see cref="Validate" />.</exception>
    /// <exception cref="SecretException">It cannot be sealed.</exception>
    public Credential Save(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        if (Validate(credential) is { } problem) throw new InvalidOperationException(problem);

        var now = DateTime.UtcNow;
        var row = new SecretRecord(credential.Id, credential.Name.Trim(), Kind, string.Empty,
                                   _encryption.Encrypt(CredentialPayload.Write(credential), CredentialPayload.Context),
                                   credential.Id == 0 ? now : credential.Created, now);

        var saved = _store.SaveSecret(row);
        Changed?.Invoke(this, EventArgs.Empty);

        var result = credential.Clone();
        result.Id       = saved.Id;
        result.Name     = saved.Key;
        result.Created  = saved.Created;
        result.Modified = saved.Modified;
        return result;
    }

    public bool Delete(int id)
    {
        var row = _store.GetSecret(id);
        if (row is null || row.Kind != Kind) return false;

        bool removed = _store.DeleteSecret(id);
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    private Credential ToCredential(SecretRecord row)
    {
        var payload = CredentialPayload.Read(_encryption.Decrypt(row.Data, CredentialPayload.Context));
        return new Credential
               {
                   Id       = row.Id,
                   Name     = row.Key,
                   Realm    = payload.ReadRealm(),
                   Username = payload.Username,
                   Domain   = payload.Domain,
                   Password = payload.Password,
                   Notes    = payload.Notes,
                   Created  = row.Created,
                   Modified = row.Modified
               };
    }
}
