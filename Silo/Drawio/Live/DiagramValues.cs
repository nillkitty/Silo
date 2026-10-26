using System.Collections.Concurrent;
using System.Linq;
using Silo.Drawio.Mx;

namespace Silo.Drawio.Live;

/// <summary>
///     A content source whose generated layer grows or changes a little at a time (a trace that adds a hop, a scan that finds
///     a host). Besides <see cref="IDiagramContentSource.Changed" /> (rebuild everything) it reports small structural changes
///     as <see cref="DiagramEdit" />s, which a pane applies to the open diagram in place, without reloading it.
/// </summary>
/// <remarks>
///     <see cref="IDiagramContentSource.Build" /> stays the source of truth: it must always return the whole current layer
///     (every edit reported so far included), because a pane rebuilds from it whenever it reloads (theme change, options,
///     a pane that has no plugin) or when an edit could not be applied in place. Edits may be raised from any thread.
/// </remarks>
public interface IIncrementalDiagramSource : IDiagramContentSource
{
    /// <summary>Raised with edits to the generated layer: add, update or remove generated cells.</summary>
    event Action<IReadOnlyList<DiagramEdit>>? EditsReady;
}

/// <summary>
///     A general-purpose <see cref="IDiagramDataProvider" />: a set of named values under one scheme that code fills in
///     (<c>{trace:h3.rtt}</c>), with change notification batched per call. Get one with <see cref="For" />, which registers
///     it with <see cref="DiagramData" /> the first time a scheme is asked for.
/// </summary>
public sealed class DiagramValues : IDiagramDataProvider
{
    private static readonly ConcurrentDictionary<string, DiagramValues> Schemes = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    private DiagramValues(string scheme)
    {
        Scheme = scheme.Required();
    }

    /// <inheritdoc />
    public string Scheme { get; }

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>The value set for <paramref name="scheme" />, created and registered with <see cref="DiagramData" /> on first use.</summary>
    public static DiagramValues For(string scheme)
    {
        bool created = false;
        var values = Schemes.GetOrAdd(scheme, s =>
                                              {
                                                  created = true;
                                                  return new DiagramValues(s);
                                              });
        if (created) DiagramData.Register(values);
        return values;
    }

    /// <inheritdoc />
    public bool TryGet(string path, out object? value)
    {
        return _values.TryGetValue(path, out value);
    }

    /// <summary>The keys currently set.</summary>
    public IReadOnlyList<string> Keys => _values.Keys.ToList();

    /// <summary>Sets one value; raises <see cref="Changed" /> only if it differs from the current one.</summary>
    public void Set(string key, object? value)
    {
        if (SetCore(key, value)) Changed?.Invoke();
    }

    /// <summary>Sets several values with at most one <see cref="Changed" />.</summary>
    public void SetMany(IEnumerable<KeyValuePair<string, object?>> values)
    {
        bool changed = false;
        foreach (var (key, value) in values) changed |= SetCore(key, value);
        if (changed) Changed?.Invoke();
    }

    /// <summary>Removes every value whose key starts with <paramref name="prefix" />.</summary>
    public void RemovePrefix(string prefix)
    {
        bool changed = false;
        foreach (string key in _values.Keys)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                changed |= _values.TryRemove(key, out _);

        if (changed) Changed?.Invoke();
    }

    /// <summary>Removes every value.</summary>
    public void Clear()
    {
        if (_values.IsEmpty) return;
        _values.Clear();
        Changed?.Invoke();
    }

    private bool SetCore(string key, object? value)
    {
        if (_values.TryGetValue(key, out var old) && Equals(old, value)) return false;
        _values[key] = value;
        return true;
    }
}
