namespace Silo.Contracts.UI;

/// <summary>
///     Something that can be cut: copied now and removed once a paste has actually taken it somewhere. Nothing is
///     removed at the moment of the cut -- the object stays where it is, so cancelling or never pasting loses nothing.
/// </summary>
/// <remarks>
///     <para>
///         Cut starts like <see cref="ICopyable.Copy" /> (the same formats go on the clipboard, with
///         <see cref="IClipboardWriter.Operation" /> set to <see cref="ClipboardOperation.Cut" /> so an object can tell
///         the difference), and finishes when a paste is done: the host then calls <see cref="CutCompleted" /> on each
///         cut object with what the paste did, or <see cref="CutCancelled" /> if the cut is abandoned.
///     </para>
///     <para>
///         When a paste is not into the app (text pasted into another program), nobody calls back and the objects
///         simply stay; only a paste the host knows about completes a cut.
///     </para>
/// </remarks>
public interface ICuttable : ICopyable
{
    /// <summary>Whether cutting is possible right now (e.g. the item is not read-only). Defaults to <see cref="ICopyable.CanCopy" />.</summary>
    bool CanCut => CanCopy;

    /// <summary>Adds this object's data to the clipboard for a cut. By default the same as <see cref="ICopyable.Copy" />.</summary>
    void Cut(IClipboardWriter clipboard) => Copy(clipboard);

    /// <summary>Takes the cut over, like <see cref="ICopyable.TryHandleCopy" />. By default defers to it.</summary>
    bool TryHandleCut(ClipboardRequest request) => TryHandleCopy(request);

    /// <summary>
    ///     A paste finished, so the cut can complete. What the object must do depends on <see cref="PasteResult.Effect" />:
    ///     <see cref="PasteEffect.Moved" /> means the destination took this very object (it was re-parented), so there is
    ///     nothing to delete -- just detach from the old place; <see cref="PasteEffect.Copied" /> means the destination
    ///     made a copy, so this original must now remove itself. Also the moment to clear any "marked for cut" look.
    /// </summary>
    void CutCompleted(PasteResult result)
    {
    }

    /// <summary>
    ///     The cut was abandoned (something else was copied or cut, or the user pressed Escape). Clear any "marked for
    ///     cut" look; the object is untouched.
    /// </summary>
    void CutCancelled()
    {
    }
}
