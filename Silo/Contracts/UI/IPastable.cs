namespace Silo.Contracts.UI;

/// <summary>
///     A container that can receive what is on the clipboard: a copy is pasted as a duplicate, a cut as a move. The same
///     two interactions back drag-and-drop (drag = move, Ctrl+drag = duplicate), so a container only implements this once.
/// </summary>
/// <remarks>
///     The host offers the paste to the focused item first and, when it answers with <see cref="PasteResult.None" />,
///     to each parent container in turn, so a container gets whatever its children do not want.
/// </remarks>
public interface IPastable
{
    /// <summary>
    ///     The clipboard formats this container understands (see <see cref="ClipboardFormats" />), most preferred first.
    ///     Empty means it only accepts in-app objects (<see cref="IClipboardData.Sources" />).
    /// </summary>
    IReadOnlyList<string> PasteFormats => [];

    /// <summary>
    ///     Whether <see cref="Paste" /> could take this data in this mode; drives the Paste menu item and Ctrl+V. The
    ///     default says yes when the data has in-app objects or one of <see cref="PasteFormats" />, and refuses a
    ///     <see cref="PasteMode.Move" /> of something that was merely copied.
    /// </summary>
    bool CanPaste(IClipboardData data, PasteMode mode)
    {
        if (mode == PasteMode.Move && data.Operation != ClipboardOperation.Cut) return false;

        return data.Sources.Count > 0 || PasteFormats.Any(data.Contains);
    }

    /// <summary>
    ///     Takes the data into the container. <paramref name="mode" /> is the interaction asked for:
    ///     <see cref="PasteMode.Duplicate" /> (leave the original, add a copy) or <see cref="PasteMode.Move" /> (the
    ///     original relocates). A container that cannot honour a move -- the objects come from another Silo or another
    ///     program -- duplicates instead and says so in the result; the cut source then removes itself (see
    ///     <see cref="ICuttable.CutCompleted" />). Return <see cref="PasteResult.None" /> when nothing was pasted.
    /// </summary>
    PasteResult Paste(IClipboardData data, PasteMode mode);
}
