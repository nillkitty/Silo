using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Silo.Clipboarding;

/// <summary>A drag that is about to start: the data to drag, and whether it may move (every object can be cut) or only duplicate.</summary>
public sealed record DragPayload(object Data, bool CanMove);

/// <summary>
///     The app's clipboard: carries <see cref="ClipboardSession" /> (the copy / cut / paste rules) to the system clipboard and to
///     drag and drop. A copy puts the objects' formats on the system clipboard -- so it pastes into other programs -- together
///     with a token; a paste whose clipboard still carries this app's token is given the real objects (a duplicate or a move
///     of them), any other paste only the formats.
/// </summary>
public static class AppClipboard
{
    private static readonly Action<Exception, string> Report =
        (ex, what) => Serilog.Log.Warning(ex, "Clipboard: {What}", what);

    private static ClipboardSnapshot? _drag;

    // reading the system clipboard is a cross-process call, and the Paste command is asked about on nearly every input event: remember the read
    private static uint                       _cachedSequence;
    private static System.Windows.IDataObject? _cachedData;
    private static string?                     _cachedToken;

    /// <summary>The copy / cut / paste state of the app.</summary>
    public static ClipboardSession Session { get; } = new(Report);

    // ---- asking ------------------------------------------------------------------------------

    public static bool CanCopy(ClipboardContext context) => ClipboardCollector.CanCopy(context.Selection, Report);

    public static bool CanCut(ClipboardContext context) => ClipboardCollector.CanCut(context.Selection, Report);

    /// <summary>Whether a paste (a plain one when <paramref name="mode" /> is null) would be taken by one of the containers.</summary>
    public static bool CanPaste(ClipboardContext context, PasteMode? mode = null)
    {
        if (context.Targets.Count == 0) return false;

        var data = CurrentData();
        return data is not null && Session.CanPaste(data, mode, context.Targets);
    }

    // ---- doing -------------------------------------------------------------------------------

    public static bool Copy(ClipboardContext context) => Begin(ClipboardOperation.Copy, context);

    public static bool Cut(ClipboardContext context) => Begin(ClipboardOperation.Cut, context);

    /// <summary>Pastes into the first container that takes it. Returns whether anything was pasted.</summary>
    public static bool Paste(ClipboardContext context, PasteMode? mode = null)
    {
        var data = CurrentData();
        if (data is null || context.Targets.Count == 0) return false;

        return Session.Paste(data, mode, context.Targets).Handled;
    }

    /// <summary>Gives up the cut that is waiting (Escape): the objects are untouched and lose any "marked for cut" look.</summary>
    public static void CancelCut() => Session.Abandon();

    private static bool Begin(ClipboardOperation operation, ClipboardContext context)
    {
        var result = Session.Begin(operation, context.Selection);
        if (!result.Success) return false;
        if (result.Snapshot is null) return true; // the objects ran the operation themselves

        try
        {
            Place(result.Snapshot);
            return true;
        }
        catch (Exception ex)
        {
            Report(ex, "Putting data on the system clipboard failed");
            Session.Abandon();
            return false;
        }
    }

    private static void Place(ClipboardSnapshot snapshot)
    {
        var data = new SnapshotDataObject(snapshot);

        // data left on the clipboard when the app exits has to be rendered now, so only a copy without lazy formats may persist
        bool persist = snapshot.Writer?.HasLazy != true;

        for (int attempt = 0;; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, persist);
                break;
            }
            catch (COMException) when (attempt < 4) // another program has the clipboard open for a moment
            {
                Thread.Sleep(25);
            }
        }

        _cachedSequence = 0;
    }

    /// <summary>
    ///     What a paste would paste: this app's own copy while the system clipboard still carries its token, else whatever is on the
    ///     system clipboard (formats only). Finding someone else's data where a cut was waiting abandons the cut.
    /// </summary>
    internal static IClipboardData? CurrentData()
    {
        var (data, token) = ReadSystem();
        var current       = Session.Current;

        if (data is null)
        {
            if (current is not null) Session.Abandon();
            return null;
        }

        if (current is not null && token == current.Token) return current;

        if (current is not null) Session.Abandon(); // another program (or another copy) replaced it
        return new ExternalClipboardData(data, Report);
    }

    private static (System.Windows.IDataObject? Data, string? Token) ReadSystem()
    {
        uint sequence = GetClipboardSequenceNumber();
        if (sequence != 0 && sequence == _cachedSequence && _cachedData is not null) return (_cachedData, _cachedToken);

        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null) return (null, null);

            _cachedSequence = sequence;
            _cachedData     = data;
            _cachedToken    = ClipboardTokens.From(data);
            return (data, _cachedToken);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            return (null, null); // the clipboard is busy: try again on the next ask
        }
    }

    // ---- drag and drop -----------------------------------------------------------------------

    /// <summary>Starts a drag of the selection, or returns null when it cannot be dragged. Pair with <see cref="EndDrag" />.</summary>
    public static DragPayload? BeginDrag(ClipboardContext context)
    {
        if (!CanCopy(context)) return null;

        var collected = ClipboardCollector.Collect(ClipboardOperation.Copy, context.Selection, Report, allowTakeOver: false);
        if (collected.Sources.Count == 0) return null;

        var snapshot = new ClipboardSnapshot(ClipboardOperation.Copy, collected.Sources, collected.Writer);
        _drag = snapshot;
        return new DragPayload(new SnapshotDataObject(snapshot), ClipboardCollector.CanCut(collected.Sources, Report));
    }

    public static void EndDrag() => _drag = null;

    /// <summary>
    ///     The effect a drop of <paramref name="data" /> would have on <paramref name="targets" />: Move (a plain drag of this app's
    ///     objects that can be cut), Copy (Ctrl, or data that cannot move), or None.
    /// </summary>
    public static DragDropEffects DragEffect(System.Windows.IDataObject data, IReadOnlyList<IPastable> targets, DragDropEffects allowed, bool ctrl)
    {
        return Resolve(data, targets, allowed, ctrl, perform: false);
    }

    /// <summary>Drops <paramref name="data" /> into the first container that takes it; the effect it had, or None.</summary>
    public static DragDropEffects Drop(System.Windows.IDataObject data, IReadOnlyList<IPastable> targets, DragDropEffects allowed, bool ctrl)
    {
        return Resolve(data, targets, allowed, ctrl, perform: true);
    }

    private static DragDropEffects Resolve(System.Windows.IDataObject data, IReadOnlyList<IPastable> targets, DragDropEffects allowed, bool ctrl, bool perform)
    {
        if (targets.Count == 0) return DragDropEffects.None;

        var dragged = _drag;
        if (dragged is not null && ClipboardTokens.From(data) == dragged.Token)
        {
            bool move = !ctrl && allowed.HasFlag(DragDropEffects.Move);
            if (!move && !allowed.HasFlag(DragDropEffects.Copy)) return DragDropEffects.None;

            var mode = move ? PasteMode.Move : PasteMode.Duplicate;
            if (perform)
                return Session.Drop(dragged, mode, targets).Handled ? Effect(move) : DragDropEffects.None;

            var view = dragged.WithOperation(move ? ClipboardOperation.Cut : ClipboardOperation.Copy);
            return Session.CanPaste(view, mode, targets) ? Effect(move) : DragDropEffects.None;
        }

        // from another program: it can only be copied in
        if (!allowed.HasFlag(DragDropEffects.Copy)) return DragDropEffects.None;

        var external = new ExternalClipboardData(data, Report);
        if (!perform) return Session.CanPaste(external, PasteMode.Duplicate, targets) ? DragDropEffects.Copy : DragDropEffects.None;

        return Session.Paste(external, PasteMode.Duplicate, targets).Handled ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private static DragDropEffects Effect(bool move) => move ? DragDropEffects.Move : DragDropEffects.Copy;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
