namespace Silo.Contracts.Providers.Data;

/// <summary>
///     Represents the relationship between the Silo and the current user.
/// </summary>
public interface ISiloUserContext
{
    /// <summary>
    ///     Gets whether this user can author the silo
    /// </summary>
    bool IsAuthorable { get; }

    /// <summary>
    ///     Gets whether this ussr can develop for this Silo
    /// </summary>
    bool IsDeveloper { get; }

    /// <summary>
    ///     Gets whether this user owns the silo
    /// </summary>
    bool IsOwner { get; }

    /// <summary>
    ///     Gets whether this user can read/see/open the silo
    /// </summary>
    bool IsReadable { get; }

    /// <summary>
    ///     Gets whether this silo must open solo
    /// </summary>
    bool IsSoloSilo { get; }

    /// <summary>
    ///     Gets whether this user can write to the silo
    /// </summary>
    bool IsWritable { get; }
}