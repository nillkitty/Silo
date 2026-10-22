namespace Silo.Providers.Crypto;

/// <summary>
///     Something that can lock a small piece of data (a Silo's data key) so that only the right person, on the right
///     computer or with
///     the right passphrase, can unlock it again. It is the pluggable part of how a Silo keeps secrets: the Silo's secrets
///     are sealed with
///     a random key (AES-256-GCM, see <c>SecretEnvelope</c>), and that key is stored only after a protector has locked it.
/// </summary>
/// <remarks>
///     Register an implementation with <c>[Singleton(typeof(ISecretProtector))]</c>. The Silo records the
///     <see cref="Id" /> of the protector
///     next to each key it locked, so protectors can be mixed and a Silo can be moved from one to another
///     (<see cref="ISiloEncryption.ChangeProvider" />). An <see cref="Id" /> must never change once released, and must not
///     be reused.
/// </remarks>
public interface ISecretProtector
{
    /// <summary>One sentence on who can open what it locked.</summary>
    string Description { get; }

    /// <summary>What to call it in the interface.</summary>
    string DisplayName { get; }

    /// <summary>A short, stable, lower-case name that is stored with the keys it locked: <c>dpapi-user</c>.</summary>
    string Id { get; }

    /// <summary>
    ///     False when this computer cannot use the protector at all (it needs an operating system service that is not
    ///     there).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Locks the data.</summary>
    /// <exception cref="SecretException">The data cannot be locked.</exception>
    byte[] Protect(byte[] data);

    /// <summary>Unlocks data that <see cref="Protect" /> locked.</summary>
    /// <exception cref="SecretException">
    ///     The data cannot be unlocked here: it was locked for someone else, or on another
    ///     computer, or it is damaged.
    /// </exception>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>A secret could not be locked or unlocked. The message says why in words a person can act on.</summary>
public class SecretException : Exception
{
    public SecretException(string message) : base(message)
    {
    }

    public SecretException(string message, Exception inner) : base(message, inner)
    {
    }
}