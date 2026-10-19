namespace Silo.Clipboarding;

/// <summary>What <see cref="ClipboardSession.Begin" /> did.</summary>
/// <param name="Snapshot">The copy to put on the system clipboard; null when every object ran the operation itself, or nothing could be copied.</param>
/// <param name="HandledByItems">How many objects ran the operation themselves.</param>
public sealed record BeginResult(ClipboardSnapshot? Snapshot, int HandledByItems)
{
    /// <summary>Whether the operation did anything.</summary>
    public bool Success => Snapshot is not null || HandledByItems > 0;

    public static BeginResult Nothing { get; } = new(null, 0);
}

/// <summary>
///     The app's copy / cut / paste as a state machine, free of WPF: the current copy, the cut waiting for its paste, and the
///     rules that connect them (a new copy cancels the cut before it; a paste that moves completes it). The system clipboard
///     and the controls are <see cref="AppClipboard" />'s business.
/// </summary>
public sealed class ClipboardSession(Action<Exception, string>? report = null)
{
    private readonly CutTracker _cut = new(report);

    /// <summary>The last copy or cut made in the app and not yet used up or abandoned, or null.</summary>
    public ClipboardSnapshot? Current { get; private set; }

    /// <summary>Whether a cut is waiting for its paste.</summary>
    public bool HasPendingCut => _cut.IsPending;

    /// <summary>
    ///     Copies or cuts <paramref name="selection" />. Nothing happens if it cannot be done (nothing selected, an object
    ///     refuses). A successful one replaces the current copy, cancelling a cut that was waiting.
    /// </summary>
    public BeginResult Begin(ClipboardOperation operation, IReadOnlyList<ICopyable> selection)
    {
        bool can = operation == ClipboardOperation.Cut
                       ? ClipboardCollector.CanCut(selection, report)
                       : ClipboardCollector.CanCopy(selection, report);
        if (!can) return BeginResult.Nothing;

        _cut.Cancel(); // "something else was copied or cut"
        var collected = ClipboardCollector.Collect(operation, selection, report);

        if (collected.Sources.Count == 0)
        {
            Current = null;
            return new BeginResult(null, collected.Handled.Count);
        }

        Current = new ClipboardSnapshot(operation, collected.Sources, collected.Writer);
        if (operation == ClipboardOperation.Cut) _cut.Start(collected.Sources);

        return new BeginResult(Current, collected.Handled.Count);
    }

    /// <summary>
    ///     Pastes <paramref name="data" /> into the first container of <paramref name="targets" /> that takes it. A plain paste
    ///     (<paramref name="mode" /> null) is <see cref="IClipboardData.DefaultMode" />. When it moves what this session cut,
    ///     the cut objects are told, and the copy is used up (a second paste has nothing to move).
    /// </summary>
    public PasteOutcome Paste(IClipboardData data, PasteMode? mode, IEnumerable<IPastable> targets)
    {
        var effective = mode ?? data.DefaultMode;
        var outcome   = ClipboardPaster.Paste(data, effective, targets, report);

        if (outcome.Handled && effective == PasteMode.Move && ReferenceEquals(data, Current) && _cut.IsPending)
        {
            _cut.Complete(outcome.Result);
            Current = null;
        }

        return outcome;
    }

    /// <summary>
    ///     Pastes a dragged copy: a drop that moves tells the dragged objects what the drop did, a drop that duplicates leaves
    ///     them alone. The drag is not the session's current copy and never cancels a pending cut.
    /// </summary>
    public PasteOutcome Drop(ClipboardSnapshot dragged, PasteMode mode, IEnumerable<IPastable> targets)
    {
        var data    = dragged.WithOperation(mode == PasteMode.Move ? ClipboardOperation.Cut : ClipboardOperation.Copy);
        var outcome = ClipboardPaster.Paste(data, mode, targets, report);

        if (outcome.Handled && mode == PasteMode.Move) CutTracker.Complete(data.Sources, outcome.Result, report);

        return outcome;
    }

    /// <summary>Whether <paramref name="data" /> could be pasted into <paramref name="targets" /> (a plain paste when <paramref name="mode" /> is null).</summary>
    public bool CanPaste(IClipboardData data, PasteMode? mode, IEnumerable<IPastable> targets)
    {
        return ClipboardPaster.CanPaste(data, mode ?? data.DefaultMode, targets, report);
    }

    /// <summary>Abandons the cut that is waiting (Escape, or something outside the app replaced the clipboard); a copy is simply forgotten.</summary>
    public void Abandon()
    {
        _cut.Cancel();
        Current = null;
    }
}
