namespace Silo.Contracts.UI;

/// <summary>How an object got onto the clipboard.</summary>
public enum ClipboardOperation
{
    /// <summary>Copied: the original stays.</summary>
    Copy,

    /// <summary>Cut: the original goes away once a paste has taken it (see <see cref="ICuttable" />).</summary>
    Cut
}

/// <summary>What a paste (or a drop) is asked to do.</summary>
public enum PasteMode
{
    /// <summary>Add a copy and leave the original (Copy, then Paste).</summary>
    Duplicate,

    /// <summary>Relocate the original (Cut, then Paste).</summary>
    Move
}

/// <summary>What a paste actually did.</summary>
public enum PasteEffect
{
    /// <summary>Nothing was pasted (not handled, or declined).</summary>
    None,

    /// <summary>The destination made copies; the originals are untouched, and a cut's originals must now remove themselves.</summary>
    Copied,

    /// <summary>The destination took the original objects themselves; nothing is left behind to delete.</summary>
    Moved
}

/// <summary>The outcome of <see cref="IPastable.Paste" />.</summary>
/// <param name="Effect">What happened.</param>
/// <param name="Items">The objects now in the destination (the copies, or the moved originals).</param>
public readonly record struct PasteResult(PasteEffect Effect, IReadOnlyList<object> Items)
{
    /// <summary>Nothing pasted; the host offers the paste to the next container.</summary>
    public static PasteResult None => new(PasteEffect.None, []);

    /// <summary>Whether anything was pasted.</summary>
    public bool Handled => Effect != PasteEffect.None;

    /// <summary>Copies were made.</summary>
    public static PasteResult Copied(params object[] items) => new(PasteEffect.Copied, items);

    /// <summary>The originals were moved.</summary>
    public static PasteResult Moved(params object[] items) => new(PasteEffect.Moved, items);
}

/// <summary>
///     Format names for clipboard data. The first group are the standard Windows formats (the same strings as
///     <c>System.Windows.DataFormats</c>, so they reach other programs); the rest are Silo's own.
/// </summary>
public static class ClipboardFormats
{
    public const string Text    = "UnicodeText";
    public const string Html    = "HTML Format";
    public const string Rtf     = "Rich Text Format";
    public const string Csv     = "CSV";
    public const string Bitmap  = "Bitmap";
    public const string Files   = "FileDrop";
    public const string Json    = "application/json";
    public const string Url     = "UniformResourceLocator";

    /// <summary>The objects themselves, for pasting within the app (<see cref="IClipboardData.Sources" />).</summary>
    public const string SiloItems = "Silo.Items";
}

/// <summary>Collects the formats an <see cref="ICopyable" /> contributes to a copy or cut.</summary>
public interface IClipboardWriter
{
    /// <summary>Whether this is a copy or a cut.</summary>
    ClipboardOperation Operation { get; }

    /// <summary>Offers <paramref name="data" /> under <paramref name="format" />, replacing any earlier data in that format.</summary>
    void Set(string format, object? data);

    /// <summary>
    ///     Offers a format whose data is produced only when a paste asks for it (large exports, remote content), so
    ///     copying stays instant. <paramref name="render" /> runs at most once.
    /// </summary>
    void SetLazy(string format, Func<object?> render);

    /// <summary>Whether a format has been offered already (e.g. by another object in the same copy).</summary>
    bool Contains(string format);
}

/// <summary>The clipboard as a paste sees it.</summary>
public interface IClipboardData
{
    /// <summary>How the data got here: decides whether a paste is a duplicate or a move by default.</summary>
    ClipboardOperation Operation { get; }

    /// <summary>The paste mode a plain Paste means: <see cref="PasteMode.Move" /> after a cut, else <see cref="PasteMode.Duplicate" />.</summary>
    PasteMode DefaultMode => Operation == ClipboardOperation.Cut ? PasteMode.Move : PasteMode.Duplicate;

    /// <summary>
    ///     The objects that were copied or cut, when that happened in this app; empty for data from another program.
    ///     Moving needs these: they are the real objects to relocate.
    /// </summary>
    IReadOnlyList<ICopyable> Sources { get; }

    /// <summary>The names of the formats available.</summary>
    IReadOnlyCollection<string> Formats { get; }

    /// <summary>Whether a format is available.</summary>
    bool Contains(string format);

    /// <summary>The data in a format (rendering it now if it was offered lazily), or null if there is none.</summary>
    object? Get(string format);

    /// <summary>The data in a format as <typeparamref name="T" />, or default if there is none or it is something else.</summary>
    T? Get<T>(string format) => Get(format) is T value ? value : default;
}

/// <summary>
///     Passed to <see cref="ICopyable.TryHandleCopy" /> and <see cref="ICuttable.TryHandleCut" /> so an object can run
///     the operation itself.
/// </summary>
/// <param name="Operation">Copy or cut.</param>
/// <param name="Writer">Where the clipboard data goes, if the object wants to use the host's clipboard after all.</param>
/// <param name="Selection">Everything being copied or cut together (this object included), in selection order.</param>
public sealed record ClipboardRequest(ClipboardOperation Operation, IClipboardWriter Writer, IReadOnlyList<ICopyable> Selection);
