namespace Silo.Clipboarding;

/// <summary>
///     Collects what <see cref="ICopyable" /> objects offer during one copy or cut: the data per format, in the order it was
///     offered (richest first), some of it rendered only when a paste asks for it. Free of WPF so the whole collecting step can
///     be tested; <see cref="AppClipboard" /> carries the result to the system clipboard.
/// </summary>
public sealed class ClipboardWriter : IClipboardWriter
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string>              _order   = [];
    private readonly Action<Exception, string>? _report;
    private readonly object                    _gate = new();

    public ClipboardWriter(ClipboardOperation operation, Action<Exception, string>? report = null)
    {
        Operation = operation;
        _report   = report;
    }

    /// <inheritdoc />
    public ClipboardOperation Operation { get; }

    /// <summary>The formats offered, in the order they were first offered.</summary>
    public IReadOnlyList<string> Formats
    {
        get
        {
            lock (_gate) return _order.ToList();
        }
    }

    /// <summary>Whether any offered format is still waiting to be rendered.</summary>
    public bool HasLazy
    {
        get
        {
            lock (_gate) return _entries.Values.Any(e => e.Render is not null);
        }
    }

    /// <inheritdoc />
    public void Set(string format, object? data)
    {
        Check(format);
        lock (_gate)
        {
            if (data is null) Remove(format);
            else Put(format, new Entry(data, null));
        }
    }

    /// <inheritdoc />
    public void SetLazy(string format, Func<object?> render)
    {
        Check(format);
        ArgumentNullException.ThrowIfNull(render);
        lock (_gate) Put(format, new Entry(null, render));
    }

    /// <inheritdoc />
    public bool Contains(string format)
    {
        lock (_gate) return _entries.ContainsKey(format);
    }

    /// <summary>Whether <paramref name="format" /> was offered lazily and has not been rendered yet.</summary>
    public bool IsPending(string format)
    {
        lock (_gate) return _entries.TryGetValue(format, out var e) && e.Render is not null;
    }

    /// <summary>
    ///     The data in <paramref name="format" />, rendering it now (once) if it was offered lazily. A render that throws is
    ///     reported and leaves the format without data.
    /// </summary>
    public object? Get(string format)
    {
        Func<object?> render;
        lock (_gate)
        {
            if (!_entries.TryGetValue(format, out var entry)) return null;
            if (entry.Render is null) return entry.Value;

            render = entry.Render;
            entry.Render = null; // at most once, even if the render re-enters
        }

        object? value = null;
        try
        {
            value = render();
        }
        catch (Exception ex)
        {
            _report?.Invoke(ex, $"Rendering the clipboard format '{format}' failed");
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(format, out var entry) && entry.Render is null) entry.Value = value;
        }

        return value;
    }

    private void Put(string format, Entry entry)
    {
        if (!_entries.ContainsKey(format)) _order.Add(format);
        _entries[format] = entry;
    }

    private void Remove(string format)
    {
        if (_entries.Remove(format)) _order.RemoveAll(f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase));
    }

    private static void Check(string format)
    {
        if (string.IsNullOrWhiteSpace(format)) throw new ArgumentException("A clipboard format needs a name.", nameof(format));
    }

    private sealed class Entry(object? value, Func<object?>? render)
    {
        public object?         Value  { get; set; } = value;
        public Func<object?>?  Render { get; set; } = render;
    }
}
