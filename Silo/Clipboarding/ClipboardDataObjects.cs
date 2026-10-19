using System.IO;
using System.Windows;
using Silo.Tabular;

namespace Silo.Clipboarding;

/// <summary>
///     A data object for the system clipboard (or a drag) built from a <see cref="ClipboardSnapshot" />: the formats the objects
///     offered, converted to what Windows programs expect, plus the snapshot's token so the app can tell later that this is
///     still its own copy. Formats offered lazily are rendered the first time a paste asks for them.
/// </summary>
internal sealed class SnapshotDataObject : System.Windows.IDataObject
{
    private readonly DataObject      _inner = new();
    private readonly ClipboardWriter? _writer;
    private readonly HashSet<string> _lazy = new(StringComparer.OrdinalIgnoreCase);
    private readonly object          _gate = new();

    public SnapshotDataObject(ClipboardSnapshot snapshot)
    {
        _writer = snapshot.Writer;
        _inner.SetData(ClipboardFormats.SiloItems, ClipboardTokens.ToStream(snapshot.Token));

        if (_writer is null) return;

        foreach (var format in _writer.Formats)
        {
            if (_writer.IsPending(format)) _lazy.Add(format);
            else Store(format, _writer.Get(format));
        }
    }

    public object? GetData(string format, bool autoConvert)
    {
        Realize(format);
        return _inner.GetData(format, autoConvert);
    }

    public object? GetData(string format) => GetData(format, true);

    public object? GetData(Type format) => GetData(format.FullName ?? format.Name);

    public bool GetDataPresent(string format, bool autoConvert)
    {
        lock (_gate)
        {
            if (_lazy.Contains(format)) return true;
        }

        return _inner.GetDataPresent(format, autoConvert);
    }

    public bool GetDataPresent(string format) => GetDataPresent(format, true);

    public bool GetDataPresent(Type format) => GetDataPresent(format.FullName ?? format.Name);

    public string[] GetFormats(bool autoConvert)
    {
        lock (_gate) return _inner.GetFormats(autoConvert).Union(_lazy, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string[] GetFormats() => GetFormats(true);

    public void SetData(object data) => _inner.SetData(data);

    public void SetData(string format, object data) => _inner.SetData(format, data);

    public void SetData(Type format, object data) => _inner.SetData(format, data);

    public void SetData(string format, object data, bool autoConvert) => _inner.SetData(format, data, autoConvert);

    private void Realize(string format)
    {
        lock (_gate)
        {
            if (!_lazy.Remove(format)) return;
        }

        Store(format, _writer?.Get(format));
    }

    private void Store(string format, object? value)
    {
        var converted = Convert(format, value);
        if (converted is not null) _inner.SetData(format, converted);
    }

    /// <summary>Turns what an object offered into what Windows programs read for that format.</summary>
    internal static object? Convert(string format, object? value)
    {
        if (value is null) return null;

        if (Same(format, ClipboardFormats.Html))
        {
            var html = value as string ?? value.ToString() ?? string.Empty;
            return html.StartsWith("Version:", StringComparison.Ordinal) ? html : SheetClipboard.ToCfHtml(html); // CF_HTML, not a bare fragment
        }

        if (Same(format, ClipboardFormats.Files))
        {
            return value switch
                   {
                       string one                  => new[] { one },
                       IEnumerable<string> many    => many.ToArray(),
                       _                           => value
                   };
        }

        if (Same(format, ClipboardFormats.Text) || Same(format, ClipboardFormats.Rtf) || Same(format, ClipboardFormats.Csv) ||
            Same(format, ClipboardFormats.Json) || Same(format, ClipboardFormats.Url))
            return value as string ?? value.ToString();

        return value;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The clipboard (or a drop) as data from outside the app, or from the app after its own copy is used up: formats only, no objects.</summary>
internal sealed class ExternalClipboardData(System.Windows.IDataObject data, Action<Exception, string>? report = null) : IClipboardData
{
    public ClipboardOperation Operation => ClipboardOperation.Copy;

    public IReadOnlyList<ICopyable> Sources => [];

    public IReadOnlyCollection<string> Formats
    {
        get
        {
            try
            {
                return (data.GetFormats(false) ?? []).Where(f => f != ClipboardFormats.SiloItems).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception ex)
            {
                report?.Invoke(ex, "Listing the clipboard formats failed");
                return [];
            }
        }
    }

    public bool Contains(string format)
    {
        if (string.Equals(format, ClipboardFormats.SiloItems, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            return data.GetDataPresent(format, true);
        }
        catch (Exception ex)
        {
            report?.Invoke(ex, $"Asking the clipboard for '{format}' failed");
            return false;
        }
    }

    public object? Get(string format)
    {
        if (!Contains(format)) return null;

        try
        {
            return data.GetData(format, true);
        }
        catch (Exception ex)
        {
            report?.Invoke(ex, $"Reading '{format}' from the clipboard failed");
            return null;
        }
    }
}

/// <summary>The snapshot token as the bytes of a custom clipboard format (raw bytes need no serialization, whatever the .NET version).</summary>
internal static class ClipboardTokens
{
    public static MemoryStream ToStream(string token) => new(Encoding.UTF8.GetBytes(token));

    /// <summary>The token a data object carries, or null if it carries none (another program's data).</summary>
    public static string? From(System.Windows.IDataObject? data)
    {
        try
        {
            if (data is null || !data.GetDataPresent(ClipboardFormats.SiloItems, false)) return null;

            switch (data.GetData(ClipboardFormats.SiloItems, false))
            {
                case MemoryStream ms:
                    return Encoding.UTF8.GetString(ms.ToArray());
                case string s:
                    return s;
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }
}
