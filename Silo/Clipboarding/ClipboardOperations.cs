namespace Silo.Clipboarding;

/// <summary>What <see cref="ClipboardCollector.Collect" /> gathered.</summary>
/// <param name="Writer">The formats the objects offered.</param>
/// <param name="Sources">The objects whose data (or at least identity) is on the clipboard: those a paste can duplicate or move.</param>
/// <param name="Handled">The objects that ran the operation themselves (<see cref="ICopyable.TryHandleCopy" />); the host does nothing more for them.</param>
public sealed record CollectResult(ClipboardWriter Writer, IReadOnlyList<ICopyable> Sources, IReadOnlyList<ICopyable> Handled);

/// <summary>The copy and cut half of the clipboard: asks each selected object, in order, to contribute.</summary>
public static class ClipboardCollector
{
    /// <summary>Whether a Copy of <paramref name="selection" /> is possible: something is selected and every object agrees.</summary>
    public static bool CanCopy(IReadOnlyList<ICopyable> selection, Action<Exception, string>? report = null)
    {
        return selection.Count > 0 && selection.All(s => Ask(() => s.CanCopy, report));
    }

    /// <summary>
    ///     Whether a Cut of <paramref name="selection" /> is possible: every object is <see cref="ICuttable" /> and agrees. One
    ///     that cannot be cut makes the whole cut impossible, rather than cutting some and copying the rest.
    /// </summary>
    public static bool CanCut(IReadOnlyList<ICopyable> selection, Action<Exception, string>? report = null)
    {
        return selection.Count > 0 && selection.All(s => s is ICuttable c && Ask(() => c.CanCut, report));
    }

    /// <summary>
    ///     Runs the operation over <paramref name="selection" />. An object that takes the operation over is left to it; the
    ///     others offer their formats to one shared writer. An object that throws is reported and left out, so one broken
    ///     object does not lose the rest of the copy. A drag collects with <paramref name="allowTakeOver" /> false: an object
    ///     that would put its own data object on the clipboard has no business doing so when it is merely picked up.
    /// </summary>
    public static CollectResult Collect(ClipboardOperation operation, IReadOnlyList<ICopyable> selection, Action<Exception, string>? report = null,
                                        bool allowTakeOver = true)
    {
        var writer  = new ClipboardWriter(operation, report);
        var request = new ClipboardRequest(operation, writer, selection);
        var sources = new List<ICopyable>();
        var handled = new List<ICopyable>();

        foreach (var item in selection)
        {
            try
            {
                bool took = allowTakeOver &&
                            (operation == ClipboardOperation.Cut && item is ICuttable cuttable
                                 ? cuttable.TryHandleCut(request)
                                 : item.TryHandleCopy(request));
                if (took)
                {
                    handled.Add(item);
                    continue;
                }

                if (operation == ClipboardOperation.Cut && item is ICuttable c) c.Cut(writer);
                else item.Copy(writer);

                sources.Add(item);
            }
            catch (Exception ex)
            {
                report?.Invoke(ex, $"{(operation == ClipboardOperation.Cut ? "Cutting" : "Copying")} {item.GetType().Name} failed");
            }
        }

        return new CollectResult(writer, sources, handled);
    }

    private static bool Ask(Func<bool> question, Action<Exception, string>? report)
    {
        try
        {
            return question();
        }
        catch (Exception ex)
        {
            report?.Invoke(ex, "Asking whether the clipboard can be used failed");
            return false;
        }
    }
}

/// <summary>Which container took a paste, and what it did.</summary>
public sealed record PasteOutcome(PasteResult Result, IPastable? Target)
{
    public static PasteOutcome None { get; } = new(PasteResult.None, null);

    public bool Handled => Result.Handled;
}

/// <summary>The paste half of the clipboard: offers the data to the focused container, then to each one around it.</summary>
public static class ClipboardPaster
{
    /// <summary>Whether any of <paramref name="targets" /> would take <paramref name="data" /> in <paramref name="mode" />.</summary>
    public static bool CanPaste(IClipboardData data, PasteMode mode, IEnumerable<IPastable> targets, Action<Exception, string>? report = null)
    {
        return Candidates(data, targets).Any(t => Ask(t, data, mode, report));
    }

    /// <summary>
    ///     Offers the paste to each container in turn (innermost first) and stops at the first that takes it. A container that is
    ///     one of the objects being pasted is skipped (an item is not pasted into itself); one that throws is reported and skipped.
    /// </summary>
    public static PasteOutcome Paste(IClipboardData data, PasteMode mode, IEnumerable<IPastable> targets, Action<Exception, string>? report = null)
    {
        foreach (var target in Candidates(data, targets))
        {
            try
            {
                if (!target.CanPaste(data, mode)) continue;

                var result = target.Paste(data, mode);
                if (result.Handled) return new PasteOutcome(result, target);
            }
            catch (Exception ex)
            {
                report?.Invoke(ex, $"Pasting into {target.GetType().Name} failed");
            }
        }

        return PasteOutcome.None;
    }

    private static IEnumerable<IPastable> Candidates(IClipboardData data, IEnumerable<IPastable> targets)
    {
        return targets.Where(t => !data.Sources.Any(s => ReferenceEquals(s, t)));
    }

    private static bool Ask(IPastable target, IClipboardData data, PasteMode mode, Action<Exception, string>? report)
    {
        try
        {
            return target.CanPaste(data, mode);
        }
        catch (Exception ex)
        {
            report?.Invoke(ex, $"Asking {target.GetType().Name} whether it can paste failed");
            return false;
        }
    }
}

/// <summary>
///     Remembers which objects are waiting to be cut and tells them how it ended: <see cref="ICuttable.CutCompleted" /> when a
///     paste took them, <see cref="ICuttable.CutCancelled" /> when the cut is abandoned.
/// </summary>
public sealed class CutTracker(Action<Exception, string>? report = null)
{
    private List<ICuttable> _pending = [];

    /// <summary>Whether a cut is waiting for its paste.</summary>
    public bool IsPending => _pending.Count > 0;

    /// <summary>The objects waiting to be cut.</summary>
    public IReadOnlyList<ICuttable> Pending => _pending;

    /// <summary>A cut began: whatever was waiting is cancelled first (something else was cut or copied).</summary>
    public void Start(IEnumerable<ICopyable> sources)
    {
        Cancel();
        _pending = sources.OfType<ICuttable>().ToList();
    }

    /// <summary>A paste took the cut objects: each completes the cut (re-parented already, or removing itself, as the result says).</summary>
    public void Complete(PasteResult result)
    {
        var done = _pending;
        _pending = [];
        Complete(done, result, report);
    }

    /// <summary>The cut is abandoned; the objects are untouched.</summary>
    public void Cancel()
    {
        var abandoned = _pending;
        _pending = [];
        foreach (var c in abandoned)
        {
            try
            {
                c.CutCancelled();
            }
            catch (Exception ex)
            {
                report?.Invoke(ex, $"{c.GetType().Name}.CutCancelled failed");
            }
        }
    }

    /// <summary>Completes a cut that was never pending (a drop that moved objects): tells each cuttable object what the paste did.</summary>
    public static void Complete(IEnumerable<ICopyable> sources, PasteResult result, Action<Exception, string>? report = null)
    {
        foreach (var c in sources.OfType<ICuttable>())
        {
            try
            {
                c.CutCompleted(result);
            }
            catch (Exception ex)
            {
                report?.Invoke(ex, $"{c.GetType().Name}.CutCompleted failed");
            }
        }
    }
}
