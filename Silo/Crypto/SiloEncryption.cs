using System.Linq;
using Silo.Contracts.Providers.Crypto;
using Silo.Contracts.Providers.Data;

namespace Silo.Crypto;

/// <summary>
///     <see cref="ISiloEncryption" /> over a Silo's <see cref="ISecretStore" />: secrets are sealed with a random data key
///     (<see cref="SecretEnvelope" />) and the data key is kept in the store as a <c>datakey</c> row that a pluggable
///     <see cref="ISecretProtector" /> has locked. The first seal makes the key, with the default protector.
/// </summary>
/// <remarks>
///     A person's own key wins over the Silo's shared one. A secret names the key that sealed it (its row id), so a new key does not
///     orphan old secrets. Unlocked keys are kept in memory for the life of this object; <see cref="Dispose" /> clears them.
/// </remarks>
public sealed class SiloEncryption : ISiloEncryption, IDisposable
{
    public const string KeyKind = "datakey";
    public const string KeyName = "$datakey";

    private readonly object                                _lock = new();
    private readonly ISecretStore                          _store;
    private readonly Dictionary<string, ISecretProtector>  _protectors;
    private readonly string                                _defaultId;
    private readonly Dictionary<int, byte[]>               _keys = new();

    public SiloEncryption(ISecretStore store, IEnumerable<ISecretProtector> protectors, string? defaultProviderId = null)
    {
        _store      = store ?? throw new ArgumentNullException(nameof(store));
        _protectors = (protectors ?? throw new ArgumentNullException(nameof(protectors))).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _defaultId  = defaultProviderId ?? SecretProtectors.DefaultId;

        if (!_protectors.ContainsKey(_defaultId)) throw new ArgumentException($"There is no secret protector called '{_defaultId}'.", nameof(defaultProviderId));
    }

    public string ProviderId
    {
        get
        {
            lock (_lock)
            {
                return ActiveRecord()?.Provider is { Length: > 0 } id ? id : _newKeyProvider ?? _defaultId;
            }
        }
    }

    public byte[] Encrypt(byte[] plaintext, string context)
    {
        lock (_lock)
        {
            var (id, key) = ActiveKey();
            return SecretEnvelope.Seal(key, id, plaintext, context);
        }
    }

    public byte[] Decrypt(byte[] sealedData, string context)
    {
        int id = SecretEnvelope.KeyIdOf(sealedData);
        lock (_lock)
        {
            return SecretEnvelope.Open(KeyById(id), sealedData, context);
        }
    }

    public int ChangeProvider(string providerId)
    {
        if (!_protectors.TryGetValue(providerId ?? string.Empty, out var target))
            throw new SecretException($"There is no secret protector called '{providerId}'.");

        lock (_lock)
        {
            var rows = _store.GetSecrets(KeyKind);
            if (rows.Count == 0)
            {
                // nothing to move yet: the first key will be locked by the new protector
                _newKeyProvider = target.Id;
                return 0;
            }

            // unlock every key first, so that a key that cannot be opened leaves all of them as they were
            var opened = rows.Select(r => (Row: r, Key: Unlock(r))).ToList();

            int moved = 0;
            foreach (var (row, key) in opened)
            {
                if (row.Provider == target.Id) continue;

                _store.SaveSecret(row with { Provider = target.Id, Data = target.Protect(key), Modified = DateTime.UtcNow });
                moved++;
            }

            _newKeyProvider = target.Id;
            return moved;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var key in _keys.Values) Array.Clear(key);
            _keys.Clear();
        }
    }

    // ---- keys ------------------------------------------------------------------------------------

    private string? _newKeyProvider;

    private SecretRecord? ActiveRecord() => _store.GetSecrets(KeyKind).LastOrDefault();

    private (int Id, byte[] Key) ActiveKey()
    {
        if (ActiveRecord() is { } row) return (row.Id, KeyById(row.Id, row));

        var provider = _protectors[_newKeyProvider ?? _defaultId];
        var key      = SecretEnvelope.NewKey();
        var saved    = _store.SaveSecret(new SecretRecord(0, KeyName, KeyKind, provider.Id, provider.Protect(key), DateTime.UtcNow, DateTime.UtcNow));
        _keys[saved.Id] = key;
        return (saved.Id, key);
    }

    private byte[] KeyById(int id, SecretRecord? known = null)
    {
        if (_keys.TryGetValue(id, out var cached)) return cached;

        var row = known ?? _store.GetSecret(id);
        if (row is null || row.Kind != KeyKind)
            throw new SecretException("The key that this secret was sealed with is not in this Silo (or belongs to another person).");

        var key = Unlock(row);
        _keys[id] = key;
        return key;
    }

    private byte[] Unlock(SecretRecord row)
    {
        if (!_protectors.TryGetValue(row.Provider ?? string.Empty, out var protector))
            throw new SecretException($"These secrets are protected by '{row.Provider}', which is not available in this copy of Silo.");

        var key = protector.Unprotect(row.Data);
        if (key.Length != SecretEnvelope.KeySize) throw new SecretException("The stored key is damaged.");
        return key;
    }
}
