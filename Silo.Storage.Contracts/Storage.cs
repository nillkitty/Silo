namespace Silo.Storage;

/// <summary>
///     A Silo's storage as a client sees it, wherever it lives: in this process, or on a server reached over a protocol.
///     Every call can fail with an <see cref="IOException" /> (or a <see cref="SiloStorageException" /> when the Silo
///     refused) when it is remote, so callers await and handle them; none block.
/// </summary>
public interface ISiloStorage : IAsyncDisposable
{
    /// <summary>Values of any shape kept as text under a key.</summary>
    IStoredStorage Stored { get; }

    /// <summary>The hand-authored tables.</summary>
    ITableStorage Tables { get; }

    /// <summary>Who the Silo is and what it can do.</summary>
    ValueTask<SiloInfo> GetInfoAsync(CancellationToken ct = default);

    /// <summary>
    ///     What changes in the Silo from now on (changes made by anyone, including this client), until cancelled. Replaces
    ///     the in-process change events.
    /// </summary>
    IAsyncEnumerable<StorageChange> ChangesAsync(CancellationToken ct = default);
}

/// <summary>Values kept as text under a key (the async form of <c>IStoredStore</c>).</summary>
public interface IStoredStorage
{
    /// <summary>The item with the key (any case), or null if there is none.</summary>
    ValueTask<StoredItem?> GetAsync(string key, StoredScope scope = StoredScope.Personal, StoredTier tier = StoredTier.Permanent,
                                    CancellationToken ct = default);

    /// <summary>The items whose key starts with <paramref name="keyPrefix" /> (all of them when null or empty), ordered by key.</summary>
    ValueTask<IReadOnlyList<StoredItem>> ListAsync(string? keyPrefix = null, StoredScope scope = StoredScope.Personal,
                                                   StoredTier tier = StoredTier.Permanent, CancellationToken ct = default);

    /// <summary>Stores the value under the key, replacing what was there; returns the item as it is stored now.</summary>
    ValueTask<StoredItem> SetAsync(string key, string typeName, string format, string data, StoredScope scope = StoredScope.Personal,
                                   StoredTier tier = StoredTier.Permanent, CancellationToken ct = default);

    /// <summary>Removes the item; false if there was none.</summary>
    ValueTask<bool> DeleteAsync(string key, StoredScope scope = StoredScope.Personal, StoredTier tier = StoredTier.Permanent,
                                CancellationToken ct = default);
}

/// <summary>The hand-authored tables (the async form of <c>ITableStore</c>).</summary>
public interface ITableStorage
{
    ValueTask<IReadOnlyList<TableItem>> ListAsync(CancellationToken ct = default);

    ValueTask<TableItem?> GetAsync(int id, CancellationToken ct = default);

    /// <summary>Inserts (<c>Id &lt;= 0</c>) or updates a table and returns it with its id assigned.</summary>
    ValueTask<TableItem> SaveAsync(TableItem table, CancellationToken ct = default);

    ValueTask<bool> DeleteAsync(int id, CancellationToken ct = default);
}
