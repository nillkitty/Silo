namespace Silo.Clipboarding;

/// <summary>
///     One copy or cut as a paste sees it (<see cref="IClipboardData" />): how it happened, the objects that were copied, and
///     the formats. The same instance serves every paste of it inside the app; the system clipboard only ever holds the
///     formats, plus <see cref="Token" /> so a paste can tell this copy is still what is on it.
/// </summary>
public sealed class ClipboardSnapshot : IClipboardData
{
    private readonly IReadOnlyList<string>   _formats;
    private readonly Func<string, object?>   _get;

    public ClipboardSnapshot(ClipboardOperation operation, IReadOnlyList<ICopyable> sources, ClipboardWriter writer)
        : this(operation, sources, writer.Formats, writer.Get, Guid.NewGuid().ToString("N"), writer)
    {
    }

    private ClipboardSnapshot(ClipboardOperation operation, IReadOnlyList<ICopyable> sources, IReadOnlyList<string> formats,
                              Func<string, object?> get, string token, ClipboardWriter? writer)
    {
        Operation = operation;
        Sources   = sources;
        _formats  = formats;
        _get      = get;
        Token     = token;
        Writer    = writer;
    }

    /// <summary>Identifies this copy on the system clipboard (and in a drag).</summary>
    public string Token { get; }

    /// <summary>What the formats were collected into; the host turns it into a data object.</summary>
    public ClipboardWriter? Writer { get; }

    /// <inheritdoc />
    public ClipboardOperation Operation { get; }

    /// <inheritdoc />
    public IReadOnlyList<ICopyable> Sources { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Formats => Sources.Count == 0 ? _formats : [.._formats, ClipboardFormats.SiloItems];

    /// <inheritdoc />
    public bool Contains(string format)
    {
        if (string.Equals(format, ClipboardFormats.SiloItems, StringComparison.OrdinalIgnoreCase)) return Sources.Count > 0;

        return _formats.Contains(format, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public object? Get(string format)
    {
        if (string.Equals(format, ClipboardFormats.SiloItems, StringComparison.OrdinalIgnoreCase)) return Sources.Count > 0 ? Sources : null;

        return Contains(format) ? _get(format) : null;
    }

    /// <summary>
    ///     The same copy seen as a different operation. A drag does not know whether it moves or duplicates until it is dropped:
    ///     the drop asks for the matching view of it.
    /// </summary>
    public ClipboardSnapshot WithOperation(ClipboardOperation operation)
    {
        return operation == Operation ? this : new ClipboardSnapshot(operation, Sources, _formats, _get, Token, Writer);
    }
}

/// <summary>
///     What the person is working with, for the clipboard commands: what a Copy or Cut would take, and the containers that
///     are offered a Paste, innermost first. Resolved from the focus by <c>ClipboardFocus</c>, or handed over by a control
///     that knows (<see cref="IClipboardSurface" />).
/// </summary>
public sealed record ClipboardContext(IReadOnlyList<ICopyable> Selection, IReadOnlyList<IPastable> Targets)
{
    /// <summary>Nothing selected, nowhere to paste.</summary>
    public static ClipboardContext Empty { get; } = new([], []);

    /// <summary>Joins two contexts: this one's selection if it has one (else the other's), the targets of both, this one's first.</summary>
    public ClipboardContext Then(ClipboardContext outer)
    {
        var targets = new List<IPastable>(Targets);
        foreach (var t in outer.Targets)
            if (!targets.Any(x => ReferenceEquals(x, t)))
                targets.Add(t);

        return new ClipboardContext(Selection.Count > 0 ? Selection : outer.Selection, targets);
    }
}

/// <summary>A control that knows better than the focus walk what is selected and which containers should be offered a paste.</summary>
public interface IClipboardSurface
{
    /// <summary>What a Copy, Cut or Paste means at this control right now.</summary>
    ClipboardContext GetClipboardContext();
}
