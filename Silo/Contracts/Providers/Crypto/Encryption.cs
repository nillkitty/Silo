namespace Silo.Providers.Crypto;

/// <summary>
///     How a Silo keeps its secrets (credentials and the like): it seals them with a data key of its own, which is stored
///     only after a
///     pluggable <see cref="ISecretProtector" /> has locked it. The sealed bytes are what is written to the Silo's
///     <c>Secret</c> table.
/// </summary>
public interface ISiloEncryption
{
    /// <summary>The <see cref="ISecretProtector.Id" /> that locks the keys of this Silo for the current person.</summary>
    string ProviderId { get; }

    /// <summary>
    ///     Seals the bytes with the current data key (making one the first time). <paramref name="context" /> names what
    ///     kind of secret it is and must be the same when opening.
    /// </summary>
    /// <exception cref="SecretException">The key cannot be made or unlocked.</exception>
    byte[] Encrypt(byte[] plaintext, string context);

    /// <summary>Opens bytes that <see cref="Encrypt" /> sealed, with whichever key sealed them.</summary>
    /// <exception cref="SecretException">
    ///     They cannot be opened here (the key was locked for someone else, or they were
    ///     changed).
    /// </exception>
    byte[] Decrypt(byte[] sealedData, string context);

    /// <summary>
    ///     Locks every data key again with another protector, without touching the secrets themselves. Returns how many
    ///     keys were moved.
    /// </summary>
    /// <exception cref="SecretException">A key cannot be unlocked with its present protector, or there is no such protector.</exception>
    int ChangeProvider(string providerId);
}