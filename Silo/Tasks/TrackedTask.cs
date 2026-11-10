using System.ComponentModel;

namespace Silo.Tasks;

/// <summary>Where a task stands. A task is queued, then runs, then ends in exactly one of the three final states.</summary>
public enum TaskState
{
    /// <summary>Waiting to start.</summary>
    Queued,

    /// <summary>Under way.</summary>
    Running,

    /// <summary>Finished its work.</summary>
    Succeeded,

    /// <summary>Ended with an error (see <see cref="ITrackedTask.Message" />).</summary>
    Failed,

    /// <summary>Stopped before it finished, by the person or by what asked for it.</summary>
    Cancelled
}

/// <summary>
///     The contract of anything the Tasks pane lists: a piece of work with a name, a source, a status, a duration and (when it has them) a
///     count and a size of what it produced. A sync job, a save, an import, an export, a load all implement it -- usually by being, or
///     holding, a <see cref="TrackedTask" /> -- and hand themselves to <see cref="TaskCenter.Queue" />. The pane reads only this interface,
///     so a new kind of work needs no change to it.
/// </summary>
/// <remarks>
///     Properties may change on any thread; <see cref="INotifyPropertyChanged.PropertyChanged" /> is raised on the UI thread. A task must
///     raise it for <see cref="State" /> (the center watches it to see the task end), and for whatever else changes.
/// </remarks>
public interface ITrackedTask : INotifyPropertyChanged
{
    /// <summary>What it is doing, as a person would say it: "Saving Plans.silo", "Importing 3 files".</summary>
    string Name { get; }

    /// <summary>What asked for it, or where it is: "Silo file", "Sync", "Explorer", "Web Lab".</summary>
    string Source { get; }

    TaskState State { get; }

    /// <summary>A line about the task: its progress while it runs ("2 of 5"), how it ended when it has, the error when it failed.</summary>
    string? Message { get; }

    /// <summary>When it was queued (UTC).</summary>
    DateTime QueuedUtc { get; }

    /// <summary>When it began (UTC); null while queued.</summary>
    DateTime? StartedUtc { get; }

    /// <summary>When it ended (UTC); null until it has.</summary>
    DateTime? EndedUtc { get; }

    /// <summary>How long it has run: until now while it runs, until it ended after that, zero while queued.</summary>
    TimeSpan Duration { get; }

    /// <summary>How many results it produced (files imported, objects synced, rows exported); null when that does not apply or is not known.</summary>
    int? ResultCount { get; }

    /// <summary>How many bytes it produced or moved (the size of the file written, the data read); null when that does not apply or is not known.</summary>
    long? ResultBytes { get; }

    /// <summary>Whether <see cref="Cancel" /> can stop it.</summary>
    bool CanCancel { get; }

    /// <summary>Asks the task to stop. Does nothing when it cannot (<see cref="CanCancel" />) or has ended.</summary>
    void Cancel();

    /// <summary>
    ///     The person wants to be told (a toast) when it ends. Set at any time before then; a task that already ended is not announced
    ///     afterwards.
    /// </summary>
    bool NotifyOnCompletion { get; set; }
}

public static class TaskStates
{
    public static bool IsFinished(this TaskState state)
    {
        return state is TaskState.Succeeded or TaskState.Failed or TaskState.Cancelled;
    }

    public static string Describe(this TaskState state)
    {
        return state switch
               {
                   TaskState.Queued    => "Queued",
                   TaskState.Running   => "Running",
                   TaskState.Succeeded => "Completed",
                   TaskState.Failed    => "Failed",
                   _                   => "Cancelled"
               };
    }
}

/// <summary>
///     The usual <see cref="ITrackedTask" />: holds the state and does the bookkeeping (times, cancellation token, change notification), so a
///     piece of work only says what it is doing. Make one with <see cref="TaskCenter.Begin" /> (work that is already going on) or run work
///     through <see cref="TaskCenter.Run(string, string, Func{TrackedTask, Task}, bool)" />.
/// </summary>
public class TrackedTask : ITrackedTask
{
    private readonly CancellationTokenSource? _cancel;
    private readonly object                   _gate = new();

    private TaskState _state = TaskState.Queued;
    private string?   _message;
    private DateTime? _started, _ended;
    private int?      _count;
    private long?     _bytes;
    private bool      _notify;

    public TrackedTask(string name, string source, bool canCancel = false)
    {
        Name      = name;
        Source    = source;
        CanCancel = canCancel;
        QueuedUtc = DateTime.UtcNow;
        if (canCancel) _cancel = new CancellationTokenSource();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string Source { get; }

    public bool CanCancel { get; }

    public DateTime QueuedUtc { get; }

    /// <summary>Raised when the task is asked to stop, before the work notices (see <see cref="Token" />).</summary>
    public event Action? CancelRequested;

    /// <summary>Cancelled by <see cref="Cancel" />; the work watches it (and passes it on to what it calls). Never cancelled for a task that cannot be.</summary>
    public CancellationToken Token => _cancel?.Token ?? CancellationToken.None;

    public TaskState State => _state;

    public string? Message => _message;

    public DateTime? StartedUtc => _started;

    public DateTime? EndedUtc => _ended;

    public int? ResultCount => _count;

    public long? ResultBytes => _bytes;

    public bool NotifyOnCompletion
    {
        get => _notify;
        set
        {
            if (_notify == value) return;

            _notify = value;
            Raise();
        }
    }

    public TimeSpan Duration
    {
        get
        {
            if (_started is not { } start) return TimeSpan.Zero;

            return (_ended ?? DateTime.UtcNow) - start;
        }
    }

    /// <summary>True once the task asked to be cancelled (whether or not it has stopped yet).</summary>
    public bool CancelAsked => _cancel?.IsCancellationRequested == true;

    // ---- moving through the states ---------------------------------------------------------------

    /// <summary>It began. Does nothing when it already has, or ended.</summary>
    public void Start(string? message = null)
    {
        lock (_gate)
        {
            if (_state != TaskState.Queued) return;

            _state   = TaskState.Running;
            _started = DateTime.UtcNow;
            if (message is not null) _message = message;
        }

        Raise(nameof(State), nameof(StartedUtc), nameof(Message), nameof(Duration));
    }

    /// <summary>Says how far it has got ("2 of 5"). Starts the task when it has not been.</summary>
    public void Report(string? message, int? count = null, long? bytes = null)
    {
        Start();
        lock (_gate)
        {
            if (_state.IsFinished()) return;

            _message = message;
            if (count is not null) _count = count;
            if (bytes is not null) _bytes = bytes;
        }

        Raise(nameof(Message), nameof(ResultCount), nameof(ResultBytes));
    }

    /// <summary>Records what the task has produced so far (or in the end), without changing its state.</summary>
    public void SetResult(int? count = null, long? bytes = null)
    {
        lock (_gate)
        {
            if (count is not null) _count = count;
            if (bytes is not null) _bytes = bytes;
        }

        Raise(nameof(ResultCount), nameof(ResultBytes));
    }

    /// <summary>It finished its work.</summary>
    public void Succeed(string? message = null, int? count = null, long? bytes = null)
    {
        End(TaskState.Succeeded, message, count, bytes);
    }

    public void Fail(string message)
    {
        End(TaskState.Failed, message, null, null);
    }

    public void Fail(Exception ex)
    {
        End(TaskState.Failed, ex.Message, null, null);
    }

    /// <summary>It stopped before it finished.</summary>
    public void MarkCancelled(string? message = null)
    {
        End(TaskState.Cancelled, message ?? "Cancelled", null, null);
    }

    private void End(TaskState state, string? message, int? count, long? bytes)
    {
        lock (_gate)
        {
            if (_state.IsFinished()) return;

            _started ??= DateTime.UtcNow;
            _ended   =   DateTime.UtcNow;
            _state   =   state;
            if (message is not null || state != TaskState.Succeeded) _message = message;
            if (count is not null) _count = count;
            if (bytes is not null) _bytes = bytes;
        }

        Raise(nameof(State), nameof(EndedUtc), nameof(Message), nameof(ResultCount), nameof(ResultBytes), nameof(Duration));
    }

    // ---- cancelling ------------------------------------------------------------------------------

    /// <summary>Asks the work to stop (see <see cref="Token" />). It ends as <see cref="TaskState.Cancelled" /> when the work notices.</summary>
    public virtual void Cancel()
    {
        if (!CanCancel || _state.IsFinished() || CancelAsked) return;

        lock (_gate)
        {
            if (_state == TaskState.Running) _message = "Cancelling...";
        }

        Raise(nameof(Message));
        try
        {
            _cancel!.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return; // it ended while the request was on its way
        }

        CancelRequested?.Invoke();
        if (_state == TaskState.Queued) MarkCancelled(); // never started: there is no work to notice
    }

    // ---- change notification ---------------------------------------------------------------------

    private void Raise(params string[] names)
    {
        if (names.Length == 0) names = [nameof(NotifyOnCompletion)];

        TaskCenter.OnUi(() =>
                        {
                            foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
                        });
    }

    /// <summary>Tells the pane the duration moved on (it ticks while a task runs).</summary>
    public void RaiseDuration()
    {
        Raise(nameof(Duration));
    }

    public override string ToString()
    {
        return $"{Name} [{State}]";
    }
}
