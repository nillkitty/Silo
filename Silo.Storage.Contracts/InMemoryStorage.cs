using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Silo.Storage;

/// <summary>
///     A Silo's storage held in memory: the reference behaviour of <see cref="ISiloStorage" /> (case-insensitive keys, scopes and
///     tiers kept apart, ids assigned on first save, changes announced), for tests and for trying a client without a Silo.
/// </summary>
public sealed class InMemoryStorage(string name = "In-memory Silo") : ISiloStorage, IStoredStorage, ITableStorage
{
    private readonly object _lock = new();
    private readonly Dictionary<(string Key, StoredScope Scope, StoredTier Tier), StoredItem> _stored = new();
    private readonly SortedDictionary<int, TableItem> _tables = new();
    private readonly List<Channel<StorageChange>> _listeners = new();
    private int _nextTableId = 1;

    public IStoredStorage Stored => this;

    public ITableStorage Tables => this;

    public ValueTask<SiloInfo> GetInfoAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(new SiloInfo(name, SiloWire.SchemaId, ["stored", "tables"]));

    public async IAsyncEnumerable<StorageChange> ChangesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        Channel<StorageChange> channel = Channel.CreateUnbounded<StorageChange>();
        lock (_lock) _listeners.Add(channel);
        try
        {
            await foreach (StorageChange change in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return change;
        }
        finally
        {
            lock (_lock) _listeners.Remove(channel);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void Announce(StorageChange change)
    {
        lock (_lock)
            foreach (Channel<StorageChange> l in _listeners)
                l.Writer.TryWrite(change);
    }

    private static (string, StoredScope, StoredTier) Id(string key, StoredScope scope, StoredTier tier) => (key.ToLowerInvariant(), scope, tier);

    // ----- stored

    ValueTask<StoredItem?> IStoredStorage.GetAsync(string key, StoredScope scope, StoredTier tier, CancellationToken ct)
    {
        lock (_lock) return ValueTask.FromResult(_stored.GetValueOrDefault(Id(key, scope, tier)));
    }

    ValueTask<IReadOnlyList<StoredItem>> IStoredStorage.ListAsync(string? keyPrefix, StoredScope scope, StoredTier tier, CancellationToken ct)
    {
        lock (_lock)
        {
            IReadOnlyList<StoredItem> items = _stored.Where(p => p.Key.Scope == scope && p.Key.Tier == tier)
                                                     .Select(p => p.Value)
                                                     .Where(i => string.IsNullOrEmpty(keyPrefix) || i.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                                                     .OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
                                                     .ToList();
            return ValueTask.FromResult(items);
        }
    }

    ValueTask<StoredItem> IStoredStorage.SetAsync(string key, string typeName, string format, string data, StoredScope scope, StoredTier tier,
                                                  CancellationToken ct)
    {
        StoredItem result;
        StorageChange? change = null;
        lock (_lock)
        {
            var id = Id(key, scope, tier);
            if (_stored.TryGetValue(id, out StoredItem? existing))
            {
                if (existing.TypeName == typeName && existing.Format == format && existing.Data == data)
                    return ValueTask.FromResult(existing);    // nothing changes, not even the modified time

                result = existing with { TypeName = typeName, Format = format, Data = data, Modified = DateTime.UtcNow };
                change = new StorageChange(StorageChangeKind.Changed, "stored", key);
            }
            else
            {
                DateTime now = DateTime.UtcNow;
                result = new StoredItem(key, typeName, format, data, now, now, scope);
                change = new StorageChange(StorageChangeKind.Added, "stored", key);
            }

            _stored[id] = result;
        }

        Announce(change);
        return ValueTask.FromResult(result);
    }

    ValueTask<bool> IStoredStorage.DeleteAsync(string key, StoredScope scope, StoredTier tier, CancellationToken ct)
    {
        bool removed;
        lock (_lock)
        {
            var id = Id(key, scope, tier);
            removed = _stored.Remove(id);
        }

        if (removed) Announce(new StorageChange(StorageChangeKind.Removed, "stored", key));
        return ValueTask.FromResult(removed);
    }

    // ----- tables

    ValueTask<IReadOnlyList<TableItem>> ITableStorage.ListAsync(CancellationToken ct)
    {
        lock (_lock) return ValueTask.FromResult<IReadOnlyList<TableItem>>(_tables.Values.ToList());
    }

    ValueTask<TableItem?> ITableStorage.GetAsync(int id, CancellationToken ct)
    {
        lock (_lock) return ValueTask.FromResult(_tables.GetValueOrDefault(id));
    }

    ValueTask<TableItem> ITableStorage.SaveAsync(TableItem table, CancellationToken ct)
    {
        TableItem saved;
        StorageChange change;
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            if (table.Id <= 0)
            {
                saved  = table with { Id = _nextTableId++, Created = now, Modified = now };
                change = new StorageChange(StorageChangeKind.Added, "tables", saved.Id.ToString());
            }
            else
            {
                if (!_tables.TryGetValue(table.Id, out TableItem? existing))
                    throw new KeyNotFoundException($"There is no table {table.Id}");

                saved  = table with { Created = existing.Created, Modified = now };
                change = new StorageChange(StorageChangeKind.Changed, "tables", saved.Id.ToString());
            }

            _tables[saved.Id] = saved;
        }

        Announce(change);
        return ValueTask.FromResult(saved);
    }

    ValueTask<bool> ITableStorage.DeleteAsync(int id, CancellationToken ct)
    {
        bool removed;
        lock (_lock) removed = _tables.Remove(id);

        if (removed) Announce(new StorageChange(StorageChangeKind.Removed, "tables", id.ToString()));
        return ValueTask.FromResult(removed);
    }
}
