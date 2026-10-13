namespace Silo.Contracts.UI;

/// <summary>
///     Something that can be copied to the clipboard. An object offers as many formats as it likes (its text, an
///     HTML table, JSON, a file, a bitmap, ...) through <see cref="Copy" />, or takes the whole operation over with
///     <see cref="TryHandleCopy" />.
/// </summary>
/// <remarks>
///     However the copy is done, the host also records the object itself (see <see cref="IClipboardData.Sources" />),
///     so a paste inside the app can duplicate it faithfully without going through any of the formats. An
///     implementation that adds no formats is therefore still copyable within the app.
/// </remarks>
public interface ICopyable
{
    /// <summary>Whether copying is possible right now (e.g. something is selected). Drives the Copy menu item and Ctrl+C.</summary>
    bool CanCopy => true;

    /// <summary>
    ///     The format names <see cref="Copy" /> will offer (see <see cref="ClipboardFormats" />), so a UI can describe
    ///     what is on the clipboard without rendering anything. May be empty when it is not known up front.
    /// </summary>
    IReadOnlyCollection<string> CopyFormats => [];

    /// <summary>
    ///     Adds this object's data to the clipboard: call <see cref="IClipboardWriter.Set" /> once per format, richest
    ///     format first. Use <see cref="IClipboardWriter.SetLazy" /> for a format that is costly to produce, so it is
    ///     only rendered if a paste asks for it. Several objects copied together each get a turn at the same writer.
    /// </summary>
    void Copy(IClipboardWriter clipboard)
    {
    }

    /// <summary>
    ///     Lets the object run the clipboard operation itself -- put a pre-built data object on the system clipboard,
    ///     push to a remote clipboard, copy files in the background, and so on. Return true if it did; the host then
    ///     does not call <see cref="Copy" /> for it. Return false (the default) to have <see cref="Copy" /> used.
    /// </summary>
    bool TryHandleCopy(ClipboardRequest request) => false;
}
