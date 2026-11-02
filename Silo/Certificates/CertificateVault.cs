using System.Security.Cryptography.X509Certificates;
using System.Text;
using Silo.Providers.Crypto;
using Silo.Providers.Data;
using Silo.Storage;

namespace Silo.Certificates;

/// <summary>Raised after certificates or keypairs were added to or removed from a Silo, so the Explorer can show them.</summary>
public static class CertificateEvents
{
    public static event EventHandler? Changed;

    public static void RaiseChanged()
    {
        Changed?.Invoke(null, EventArgs.Empty);
    }
}

/// <summary>A certificate kept in the Silo, with where it came from and whether the Silo holds its private key.</summary>
public sealed record VaultEntry(string Id, X509Certificate2 Certificate, string Source, DateTime Added, bool HasKey)
{
    public string Name => CertificateInfo.DisplayName(Certificate);
}

/// <summary>
///     The certificates and keypairs of one Silo. Certificates (public) are kept as stored items under <c>x509/cert/</c>; a private key is
///     kept as a <c>keypair</c> secret, sealed by the Silo's own encryption, so it is never written in the clear. Written against the
///     stores' interfaces so it can be tested without a Silo.
/// </summary>
public sealed class CertificateVault
{
    public const string CertPrefix = "x509/cert/";
    public const string KeyKind    = "keypair";
    public const string KeyContext = "silo.keypair";

    private readonly ISiloEncryption? _encryption;
    private readonly ISecretStore?    _secrets;
    private readonly IStoredStore     _store;

    public CertificateVault(IStoredStore store, ISecretStore? secrets, ISiloEncryption? encryption)
    {
        _store      = store;
        _secrets    = secrets;
        _encryption = encryption;
    }

    /// <summary>Whether the Silo can keep private keys (its storage keeps secrets).</summary>
    public bool CanKeepKeys => _secrets is not null && _encryption is not null;

    private sealed record StoredCertificate(string Der, string Source, DateTime Added);

    private sealed record KeyPayload(string Pkcs8);

    // ---- certificates ----------------------------------------------------------------------------------------

    /// <summary>Every certificate, by name.</summary>
    public IReadOnlyList<VaultEntry> List()
    {
        var withKeys = KeyRows().Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries  = new List<VaultEntry>();
        foreach (var row in _store.ListStored(CertPrefix, StoredScope.Shared))
            if (Read(row, withKeys) is { } entry)
                entries.Add(entry);

        return entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Certificate.NotAfter).ToList();
    }

    public VaultEntry? Find(string id)
    {
        return _store.GetStored(CertPrefix + id, StoredScope.Shared) is { } row ? Read(row, KeyRows().Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)) : null;
    }

    private static VaultEntry? Read(StoredRecord row, HashSet<string> withKeys)
    {
        try
        {
            var stored = StoredCodec.Deserialize<StoredCertificate>(row.Data)!;
            var id     = row.Key[CertPrefix.Length..];
            return new VaultEntry(id, X509CertificateLoader.LoadCertificate(Convert.FromBase64String(stored.Der)), stored.Source, stored.Added, withKeys.Contains(id));
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or FormatException or System.Security.Cryptography.CryptographicException or NullReferenceException)
        {
            return null; // not readable as a certificate: leave it out rather than fail the whole list
        }
    }

    /// <summary>Keeps a certificate. False when the Silo already had it (nothing changes, not even where it came from).</summary>
    public bool Add(X509Certificate2 cert, string source)
    {
        var id = CertificateInfo.ThumbprintSha256(cert).ToLowerInvariant();
        if (_store.GetStored(CertPrefix + id, StoredScope.Shared) is not null) return false;

        var stored = new StoredCertificate(Convert.ToBase64String(cert.RawData), source, DateTime.UtcNow);
        _store.SetStored(CertPrefix + id, StoredCodec.TypeNameOf(typeof(StoredCertificate)), StoredCodec.Json, StoredCodec.Serialize(stored), StoredScope.Shared);
        CertificateEvents.RaiseChanged();
        return true;
    }

    /// <summary>
    ///     Keeps a keypair: its certificate, and its private key sealed in the Silo. A keypair for a certificate the Silo already holds adds the
    ///     key to it.
    /// </summary>
    /// <exception cref="NotSupportedException">The Silo cannot keep secrets.</exception>
    public VaultEntry AddKeyPair(KeyPair pair, string source)
    {
        if (!CanKeepKeys) throw new NotSupportedException("This Silo's storage cannot keep private keys.");

        var id = CertificateInfo.ThumbprintSha256(pair.Certificate).ToLowerInvariant();
        Add(pair.Certificate, source);

        var sealedKey = _encryption!.Encrypt(Encoding.UTF8.GetBytes(StoredCodec.Serialize(new KeyPayload(Convert.ToBase64String(pair.Pkcs8)))), KeyContext);
        var existing  = KeyRows().FirstOrDefault(r => string.Equals(r.Key, id, StringComparison.OrdinalIgnoreCase));
        var now       = DateTime.UtcNow;
        _secrets!.SaveSecret(new SecretRecord(existing?.Id ?? 0, id, KeyKind, string.Empty, sealedKey, existing?.Created ?? now, now));

        CertificateEvents.RaiseChanged();
        return Find(id)!;
    }

    /// <summary>The certificate with its private key attached, ready to sign with; null when the Silo holds no key for it.</summary>
    /// <exception cref="SecretException">The key cannot be opened here (it was sealed for another person or computer).</exception>
    public X509Certificate2? OpenKeyPair(string id)
    {
        return GetKeyPair(id)?.Open();
    }

    /// <summary>The keypair (certificate and exportable key) for exporting; null when the Silo holds no key for it.</summary>
    public KeyPair? GetKeyPair(string id)
    {
        var row   = KeyRows().FirstOrDefault(r => string.Equals(r.Key, id, StringComparison.OrdinalIgnoreCase));
        var entry = Find(id);
        if (row is null || entry is null || _encryption is null) return null;

        var payload = StoredCodec.Deserialize<KeyPayload>(Encoding.UTF8.GetString(_encryption.Decrypt(row.Data, KeyContext)))!;
        return new KeyPair(entry.Certificate, Convert.FromBase64String(payload.Pkcs8));
    }

    /// <summary>Removes the certificate and any private key held for it. False when there was nothing.</summary>
    public bool Remove(string id)
    {
        var removed = _store.DeleteStored(CertPrefix + id, StoredScope.Shared);
        foreach (var row in KeyRows().Where(r => string.Equals(r.Key, id, StringComparison.OrdinalIgnoreCase)).ToList())
            removed |= _secrets!.DeleteSecret(row.Id);

        if (removed) CertificateEvents.RaiseChanged();
        return removed;
    }

    /// <summary>Removes only the private key, leaving the (public) certificate.</summary>
    public bool RemoveKey(string id)
    {
        var any = false;
        foreach (var row in KeyRows().Where(r => string.Equals(r.Key, id, StringComparison.OrdinalIgnoreCase)).ToList())
            any |= _secrets!.DeleteSecret(row.Id);

        if (any) CertificateEvents.RaiseChanged();
        return any;
    }

    private IReadOnlyList<SecretRecord> KeyRows()
    {
        return _secrets?.GetSecrets(KeyKind) ?? [];
    }

    // ---- issued certificates and revocation ------------------------------------------------------------------

    private const string IssuedPrefix = "x509/issued/", CrlPrefix = "x509/crl/", ReplacesPrefix = "x509/replaces/";

    private sealed record CrlState(long Number);

    private sealed record ReplacesState(string Old);

    private static string IssuedKey(string issuerId, string serial) => $"{IssuedPrefix}{issuerId}/{serial}";

    /// <summary>Remembers that the authority <paramref name="issuerId" /> issued <paramref name="cert" />. Recording the same certificate again changes nothing.</summary>
    public IssuedRecord RecordIssued(string issuerId, X509Certificate2 cert)
    {
        var serial = RevocationLists.SerialOf(cert);
        if (_store.GetStored(IssuedKey(issuerId, serial), StoredScope.Shared) is { } existing && ReadIssued(existing) is { } known) return known;

        var record = new IssuedRecord(serial, cert.Subject, CertificateInfo.ThumbprintSha256(cert).ToLowerInvariant(), cert.NotBefore.ToUniversalTime(),
                                      cert.NotAfter.ToUniversalTime(), DateTime.UtcNow) { IssuerId = issuerId };
        WriteIssued(record);
        return record;
    }

    private void WriteIssued(IssuedRecord record)
    {
        _store.SetStored(IssuedKey(record.IssuerId, record.Serial), StoredCodec.TypeNameOf(typeof(IssuedRecord)), StoredCodec.Json, StoredCodec.Serialize(record), StoredScope.Shared);
        CertificateEvents.RaiseChanged();
    }

    private static IssuedRecord? ReadIssued(StoredRecord row)
    {
        try
        {
            var issuer = row.Key[IssuedPrefix.Length..].Split('/')[0];
            return StoredCodec.Deserialize<IssuedRecord>(row.Data)! with { IssuerId = issuer };
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or NullReferenceException)
        {
            return null;
        }
    }

    /// <summary>The certificates the authority issued (that the Silo knows of), newest first.</summary>
    public IReadOnlyList<IssuedRecord> Issued(string issuerId)
    {
        return _store.ListStored($"{IssuedPrefix}{issuerId}/", StoredScope.Shared).Select(ReadIssued).OfType<IssuedRecord>().OrderByDescending(r => r.IssuedUtc).ToList();
    }

    /// <summary>The record of the certificate with this SHA-256 thumbprint, whichever authority issued it; null when it was not issued here.</summary>
    public IssuedRecord? FindIssued(string thumbprintSha256)
    {
        return _store.ListStored(IssuedPrefix, StoredScope.Shared).Select(ReadIssued).OfType<IssuedRecord>()
                     .FirstOrDefault(r => string.Equals(r.Thumbprint, thumbprintSha256, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Marks an issued certificate revoked. (The Silo cannot tell anyone: <see cref="BuildCrl" /> makes the list to publish.) Null when the
    ///     certificate is not known.
    /// </summary>
    public IssuedRecord? Revoke(string issuerId, string serial, X509RevocationReason reason)
    {
        if (_store.GetStored(IssuedKey(issuerId, serial), StoredScope.Shared) is not { } row || ReadIssued(row) is not { } record) return null;
        if (record.Revoked) return record;

        var revoked = record with { Revoked = true, RevokedUtc = DateTime.UtcNow, Reason = reason.ToString() };
        WriteIssued(revoked);
        return revoked;
    }

    /// <summary>The next CRL number of the authority (each list it signs is numbered one higher than the last).</summary>
    public long NextCrlNumber(string issuerId)
    {
        var key  = CrlPrefix + issuerId;
        var last = _store.GetStored(key, StoredScope.Shared) is { } row ? StoredCodec.Deserialize<CrlState>(row.Data)!.Number : 0;
        var next = last + 1;
        _store.SetStored(key, StoredCodec.TypeNameOf(typeof(CrlState)), StoredCodec.Json, StoredCodec.Serialize(new CrlState(next)), StoredScope.Shared);
        return next;
    }

    /// <summary>The signed revocation list (DER) of an authority, holding every certificate it issued that is revoked.</summary>
    public byte[] BuildCrl(string issuerId, X509Certificate2 issuerWithKey, int validDays = 30)
    {
        return RevocationLists.Build(issuerWithKey, Issued(issuerId), NextCrlNumber(issuerId), validDays);
    }

    // ---- replacing keys --------------------------------------------------------------------------------------

    /// <summary>Remembers that the keypair <paramref name="newId" /> replaced <paramref name="oldId" />.</summary>
    public void SetReplaces(string newId, string oldId)
    {
        _store.SetStored(ReplacesPrefix + newId, StoredCodec.TypeNameOf(typeof(ReplacesState)), StoredCodec.Json, StoredCodec.Serialize(new ReplacesState(oldId)), StoredScope.Shared);
        CertificateEvents.RaiseChanged();
    }

    /// <summary>The id of the keypair this one replaced; null when it replaced none.</summary>
    public string? Replaces(string id)
    {
        return _store.GetStored(ReplacesPrefix + id, StoredScope.Shared) is { } row ? StoredCodec.Deserialize<ReplacesState>(row.Data)!.Old : null;
    }

    /// <summary>
    ///     The authority in this vault that issued <paramref name="cert" /> and whose private key is held (so it can sign again); null when the
    ///     certificate is self-signed or its issuer is not here.
    /// </summary>
    public VaultEntry? IssuerOf(X509Certificate2 cert)
    {
        if (CertificateInfo.IsSelfSigned(cert)) return null;

        var aki = cert.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().FirstOrDefault()?.KeyIdentifier;
        foreach (var e in List().Where(e => e.HasKey && CertificateInfo.CanIssue(e.Certificate)))
        {
            if (!e.Certificate.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData)) continue;

            var ski = e.Certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifierBytes;
            if (aki is { } a && ski is { } s && !a.Span.SequenceEqual(s.Span)) continue;

            return e;
        }

        return null;
    }
}
