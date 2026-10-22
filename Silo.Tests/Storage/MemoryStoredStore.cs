using Silo.Providers.Data;

namespace Silo.Tests.Storage;

/// <summary>
///     An <see cref="IStoredStore" /> in memory that behaves as the SQL one does: keys are not case sensitive,
///     personal and shared items are apart.
/// </summary>
public sealed class MemoryStoredStore : IStoredStore
{
    private readonly Dictionary<(StoredTier, StoredScope, string), StoredRecord> _items = new();
    private          DateTime                                                    _now   = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    public int Writes { get; private set; }

    public void Tick()
    {
        _now = _now.AddMinutes(1);
    }

    public StoredRecord? GetStored(string key, StoredScope scope = StoredScope.Personal, StoredTier tier = StoredTier.Permanent)
    {
        return _items.GetValueOrDefault((tier, scope, key.ToUpperInvariant()));
    }

    public IReadOnlyList<StoredRecord> ListStored(string?    keyPrefix = null, StoredScope scope = StoredScope.Personal,
                                                  StoredTier tier      = StoredTier.Permanent)
    {
        return _items
              .Where(kv => kv.Key.Item1 == tier && kv.Key.Item2 == scope &&
                           (string.IsNullOrEmpty(keyPrefix) || kv.Value.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase)))
              .Select(kv => kv.Value)
              .OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
              .ToList();
    }

    public StoredRecord SetStored(string     key, string typeName, string format, string data, StoredScope scope = StoredScope.Personal,
                                  StoredTier tier = StoredTier.Permanent)
    {
        var id       = (tier, scope, key.ToUpperInvariant());
        var existing = _items.GetValueOrDefault(id);
        if (existing is not null && existing.TypeName == typeName && existing.Format == format && existing.Data == data) return existing;

        Tick();
        Writes++;
        var record = new StoredRecord(key, typeName, format, data, existing?.Created ?? _now, _now, scope);
        _items[id] = record;
        return record;
    }

    public bool DeleteStored(string key, StoredScope scope = StoredScope.Personal, StoredTier tier = StoredTier.Permanent)
    {
        bool removed = _items.Remove((tier, scope, key.ToUpperInvariant()));
        if (removed) Writes++;
        return removed;
    }
}