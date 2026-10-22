namespace Silo.Providers.Data;

/// <summary>One saved connection: what it is called and where it points.</summary>
/// <param name="Id">The row's id.</param>
/// <param name="DisplayName">What the person (or the dialog) called it.</param>
/// <param name="SourceUri">The destination, when it has one.</param>
public sealed record ConnectionRecord(long Id, string DisplayName, Uri? SourceUri);

/// <summary>
///     Remembers the connections made in a Silo (the <c>Connection</c> table, one row per destination), so they can be
///     listed and, later, restored when the Silo is opened. Saving is a real change to the Silo: it raises
///     <see cref="IDatabaseChanges.Changed" />, unlike remembered window positions.
/// </summary>
public interface IConnectionStore
{
    /// <summary>Every saved connection, oldest first.</summary>
    IReadOnlyList<ConnectionRecord> GetConnections();

    /// <summary>Saves a connection; one already saved for the same destination is renamed instead of added twice.</summary>
    ConnectionRecord SaveConnection(string displayName, Uri? sourceUri);

    /// <summary>Forgets a saved connection.</summary>
    void ForgetConnection(long id);
}