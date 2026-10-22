namespace Silo.Providers.Data;

/// <summary>Whose a stored item is.</summary>
public enum StoredScope
{
    /// <summary>The current person's own: only they see it (a row with their user).</summary>
    Personal,

    /// <summary>The Silo's: everyone who opens the Silo sees it (a row without a user).</summary>
    Shared
}

/// <summary>How long a stored item is meant to last: where in the Silo it is kept.</summary>
public enum StoredTier
{
    /// <summary>
    ///     The Silo's own, kept with it: the main (global, Z) database. For what is made on purpose, like a constant or a
    ///     shortcut key.
    /// </summary>
    Permanent,

    /// <summary>
    ///     Remembered for the session: the scratch (X) database, which is what the Silo keeps of "where I was" when it
    ///     closes. For what just happens to be worth remembering.
    /// </summary>
    Session
}

/// <summary>
///     An item kept by <see cref="IStoredStore" />: a key, the value written as text, and what is known about where it
///     came from.
/// </summary>
/// <param name="Key">What it is stored under (any case finds it).</param>
/// <param name="TypeName">
///     The assembly-qualified name of the type the value was written from. Information only: reading
///     never creates a type from it.
/// </param>
/// <param name="Format">
///     How <paramref name="Data" /> is written; <c>json</c> for everything written by
///     <c>Stored&lt;T&gt;</c>.
/// </param>
/// <param name="Data">The value as text.</param>
/// <param name="Created">When the item was first stored (UTC).</param>
/// <param name="Modified">When it last changed (UTC).</param>
/// <param name="Scope">Whose it is.</param>
public sealed record StoredRecord(
    string      Key,
    string      TypeName,
    string      Format,
    string      Data,
    DateTime    Created,
    DateTime    Modified,
    StoredScope Scope);

/// <summary>
///     Keeps values of any shape in the Silo as text (the <c>Stored</c> table, one row per key and person, in the main
///     database for a <see cref="StoredTier.Permanent" /> item and in the scratch one for a
///     <see cref="StoredTier.Session" /> item): the value is written by whoever stores it
///     -- see <c>Stored&lt;T&gt;</c> -- and the table records when, and from which type.
/// </summary>
/// <remarks>
///     A <see cref="StoredScope.Personal" /> item belongs to the current person once that person is one of the Silo's
///     Users; until then it is kept
///     as a shared one. Storing a value that is not what is stored already is a real change to the Silo: it raises
///     <see cref="IDatabaseChanges.Changed" />.
/// </remarks>
public interface IStoredStore
{
    /// <summary>The item with the key (any case), or null if there is none.</summary>
    StoredRecord? GetStored(string key, StoredScope scope = StoredScope.Personal, StoredTier tier = StoredTier.Permanent);

    /// <summary>
    ///     The items whose key starts with <paramref name="keyPrefix" /> (any case; all of them when null or empty),
    ///     ordered by key.
    /// </summary>
    IReadOnlyList<StoredRecord> ListStored(string?    keyPrefix = null, StoredScope scope = StoredScope.Personal,
                                           StoredTier tier      = StoredTier.Permanent);

    /// <summary>
    ///     Stores the value under the key, replacing what was there. Nothing changes (not even the modified time) if it is
    ///     already stored just so.
    /// </summary>
    /// <returns>The item as it is stored now.</returns>
    StoredRecord SetStored(string     key, string typeName, string format, string data, StoredScope scope = StoredScope.Personal,
                           StoredTier tier = StoredTier.Permanent);

    /// <summary>Removes the item; false if there was none.</summary>
    bool DeleteStored(string key, StoredScope scope = StoredScope.Personal, StoredTier tier = StoredTier.Permanent);
}