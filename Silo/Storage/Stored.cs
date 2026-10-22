using System.ComponentModel;
using Newtonsoft.Json;
using Silo.Providers.Data;

namespace Silo.Storage;

/// <summary>Raised when something is stored, changed or removed through <see cref="Stored{T}" />.</summary>
public sealed class StoredChangedEventArgs(string key, StoredScope scope, bool removed, StoredTier tier = StoredTier.Permanent) : EventArgs
{
    /// <summary>What was stored under.</summary>
    public string Key { get; } = key;

    /// <summary>True when the item was removed, false when it was stored.</summary>
    public bool Removed { get; } = removed;

    /// <summary>Whose it was.</summary>
    public StoredScope Scope { get; } = scope;

    /// <summary>Where it was kept.</summary>
    public StoredTier Tier { get; } = tier;
}

/// <summary>The stored text of an item cannot be read as the type that is asked for.</summary>
public sealed class StoredDataException : Exception
{
    /// <summary>The key of the item.</summary>
    public string Key { get; }

    /// <summary>An unreadable item.</summary>
    public StoredDataException(string key, Type type, Exception inner) :
        base($"What is stored as \"{key}\" cannot be read as a {type.Name}: {inner.Message}", inner)
    {
        Key = key;
    }
}

/// <summary>How values are written to, and read from, the text of a stored item.</summary>
/// <remarks>
///     JSON, with no type names in it: a value is read as the type that asks for it, never as a type the text names (which
///     would let stored text
///     make the program create any type). The type it was written from is recorded beside it, for people and tools.
/// </remarks>
public static class StoredCodec
{
    /// <summary>The format name for JSON.</summary>
    public const string Json = "json";

    private static readonly JsonSerializerSettings Settings = new()
                                                              {
                                                                  TypeNameHandling  = TypeNameHandling.None,
                                                                  Formatting        = Formatting.None,
                                                                  NullValueHandling = NullValueHandling.Include,
                                                                  DateParseHandling = DateParseHandling.None
                                                              };

    /// <summary>The full name of the type for the record: assembly-qualified when it has one.</summary>
    public static string TypeNameOf(Type type)
    {
        return type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
    }

    /// <summary>The value as JSON text.</summary>
    public static string Serialize<T>(T value)
    {
        return JsonConvert.SerializeObject(value, typeof(T), Settings);
    }

    /// <summary>The value the JSON text stands for.</summary>
    /// <exception cref="JsonException">The text is not a <typeparamref name="T" />.</exception>
    public static T? Deserialize<T>(string data)
    {
        return JsonConvert.DeserializeObject<T>(data, Settings);
    }
}

/// <summary>Where <see cref="Stored{T}" /> keeps things unless it is told otherwise.</summary>
public static class Stored
{
    /// <summary>The store of the Silo that is active now; null when none is open. The application sets this when it starts.</summary>
    public static Func<IStoredStore?> DefaultStore { get; set; } = static () => null;

    /// <summary>Raised after any <see cref="Stored{T}" /> stores or removes something. May be raised on any thread.</summary>
    public static event EventHandler<StoredChangedEventArgs>? Changed;

    /// <summary>A <see cref="Stored{T}" /> in the active Silo (asked each time, the Silo can change).</summary>
    public static Stored<T> At<T>(string key, StoredScope scope = StoredScope.Personal, StoredTier tier = StoredTier.Permanent)
    {
        return new Stored<T>(key, scope, static () => DefaultStore(), tier);
    }

    internal static void RaiseChanged(string key, StoredScope scope, bool removed, StoredTier tier = StoredTier.Permanent)
    {
        Changed?.Invoke(null, new StoredChangedEventArgs(key, scope, removed, tier));
    }
}

/// <summary>
///     A <typeparamref name="T" /> kept in the Silo under a key, whatever its shape: it is written as text (JSON), and the
///     row records when, and which
///     type it was written from. The value is read from the Silo each time it is asked for, so what other parts of the
///     program stored shows at once.
/// </summary>
/// <typeparam name="T">Anything JSON can write and read.</typeparam>
public sealed class Stored<T> : INotifyPropertyChanged
{
    private readonly Func<IStoredStore?> _store;

    /// <summary>Whether something is stored under the key.</summary>
    public bool Exists => Record is not null;

    /// <summary>Whether there is a place to keep it (a Silo is open).</summary>
    public bool IsAvailable => _store() is not null;

    /// <summary>What it is stored under.</summary>
    public string Key { get; }

    /// <summary>When it last changed (UTC); null if nothing is stored.</summary>
    public DateTime? Modified => Record?.Modified;

    /// <summary>The item as it is stored, or null if nothing is.</summary>
    public StoredRecord? Record => _store()?.GetStored(Key, Scope, Tier);

    /// <summary>Whose it is.</summary>
    public StoredScope Scope { get; }

    /// <summary>The name of the type the stored value was written from; null if nothing is stored.</summary>
    public string? StoredTypeName => Record?.TypeName;

    /// <summary>Whether it is kept with the Silo or only remembered for the session.</summary>
    public StoredTier Tier { get; }

    /// <summary>The stored value (null when nothing is stored); setting it stores it.</summary>
    /// <exception cref="InvalidOperationException">Setting it with no Silo open.</exception>
    public T? Value
    {
        get => Get();
        set => Set(value!);
    }

    /// <summary>
    ///     Reads the stored value; false when nothing is stored or what is stored cannot be read as a
    ///     <typeparamref name="T" />.
    /// </summary>
    public bool TryGet(out T? value)
    {
        value = default;
        var record = Record;
        if (record is null) return false;

        try
        {
            value = StoredCodec.Deserialize<T>(record.Data);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The stored value; <paramref name="fallback" /> when nothing is stored.</summary>
    /// <exception cref="StoredDataException">Something is stored but cannot be read as a <typeparamref name="T" />.</exception>
    public T? Get(T? fallback = default)
    {
        var record = Record;
        if (record is null) return fallback;

        try
        {
            return StoredCodec.Deserialize<T>(record.Data);
        }
        catch (JsonException ex)
        {
            throw new StoredDataException(Key, typeof(T), ex);
        }
    }

    /// <summary>Stores the value, replacing what was there. Nothing changes if it is already stored just so.</summary>
    /// <exception cref="InvalidOperationException">No Silo is open to keep it in.</exception>
    public void Set(T value)
    {
        var store  = _store() ?? throw new InvalidOperationException("No Silo is open to keep it in.");
        var before = store.GetStored(Key, Scope, Tier);
        var after  = store.SetStored(Key, StoredCodec.TypeNameOf(typeof(T)), StoredCodec.Json, StoredCodec.Serialize(value), Scope, Tier);

        if (before is not null && before.Modified == after.Modified && before.Data == after.Data) return;
        Raise(false);
    }

    /// <summary>Removes the stored value; false if there was none.</summary>
    public bool Delete()
    {
        var store = _store();
        if (store is null || !store.DeleteStored(Key, Scope, Tier)) return false;

        Raise(true);
        return true;
    }

    /// <summary>Tells whoever shows the value that it may have changed (by someone else).</summary>
    public void Refresh()
    {
        RaiseProperties();
    }

    private void Raise(bool removed)
    {
        RaiseProperties();
        Stored.RaiseChanged(Key, Scope, removed, Tier);
    }

    private void RaiseProperties()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Exists)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Modified)));
    }

    /// <param name="key">What to store under (any case finds it).</param>
    /// <param name="scope">Whose it is.</param>
    /// <param name="store">Where to keep it, asked each time; null for nowhere.</param>
    /// <param name="tier">Whether it is kept with the Silo or only remembered for the session.</param>
    public Stored(string key, StoredScope scope, Func<IStoredStore?> store, StoredTier tier = StoredTier.Permanent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key    = key;
        Scope  = scope;
        Tier   = tier;
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>A <see cref="Stored{T}" /> in a given store.</summary>
    public Stored(string key, StoredScope scope, IStoredStore store, StoredTier tier = StoredTier.Permanent) :
        this(key, scope, () => store, tier)
    {
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;
}