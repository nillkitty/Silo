using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using Serilog;
using Silo.Ui;

namespace Silo.Tasks;

/// <summary>
///     The list of tasks the Tasks pane shows, and the way work gets into it. Anything that implements <see cref="ITrackedTask" /> is handed to
///     <see cref="Queue" />; work that has not been written as a task goes through <see cref="Run(string, string, Func{TrackedTask, Task}, bool)" />
///     (it runs, and is tracked) or <see cref="Begin" /> (it is already going on, and tells the center how it ends). The first task queued in a
///     session shows the pane. A task whose <see cref="ITrackedTask.NotifyOnCompletion" /> is set is announced with a toast when it ends.
/// </summary>
/// <remarks>
///     Everything here may be called from any thread. <see cref="Tasks" /> and the events are for the UI thread.
/// </remarks>
public static class TaskCenter
{
    /// <summary>How many ended tasks are kept for the pane to show (the oldest go first); running ones are never dropped.</summary>
    public const int MaxFinished = 200;

    /// <summary>How long deferred work (expanding an Explorer node) may run before it shows up in the Tasks pane.</summary>
    public static readonly TimeSpan DeferredAfter = TimeSpan.FromMilliseconds(500);

    private static readonly object Gate = new();

    private static readonly HashSet<ITrackedTask> Watched = new(ReferenceEqualityComparer.Instance);

    private static bool _revealed;

    /// <summary>Every task, in the order they were queued: running and queued ones, then (as they end) the ones that are over. Change it only through this class.</summary>
    public static ObservableCollection<ITrackedTask> Tasks { get; } = [];

    /// <summary>How many tasks are queued or running.</summary>
    public static int ActiveCount
    {
        get
        {
            lock (Gate) return Watched.Count(t => !t.State.IsFinished());
        }
    }

    /// <summary>Raised (on the UI thread) when a task was queued.</summary>
    public static event Action<ITrackedTask>? TaskQueued;

    /// <summary>Raised (on the UI thread) when a task ended, whichever way.</summary>
    public static event Action<ITrackedTask>? TaskEnded;

    /// <summary>What shows the pane the first time a task is queued. The Tasks tool sets it; tests may replace it.</summary>
    public static Action? Reveal { get; set; } = () => RevealPane.Request?.Invoke();

    /// <summary>What announces an ended task whose person asked for it: a toast. Tests may replace it.</summary>
    public static Action<ITrackedTask> Toast { get; set; } = TaskToast.Show;

    // ---- queueing --------------------------------------------------------------------------------

    /// <summary>Puts a task in the list (once; queueing it again does nothing) and starts watching it. The first one of the session shows the pane.</summary>
    public static void Queue(ITrackedTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        lock (Gate)
        {
            if (!Watched.Add(task)) return;
        }

        task.PropertyChanged += OnTaskChanged;

        bool first;
        lock (Gate)
        {
            first     = !_revealed;
            _revealed = true;
        }

        OnUi(() =>
             {
                 Tasks.Add(task);
                 Trim();
                 UpdateStatusBar();
                 TaskQueued?.Invoke(task);
                 if (first) Safely(Reveal);

                 // a task that ended before it was queued (a deferred one that failed fast) is over already
                 if (task.State.IsFinished()) Ended(task);
             });
    }

    /// <summary>
    ///     Starts a task for work that is already going on (it runs on the caller's thread, or wherever): the task is queued and running; the caller
    ///     ends it with <see cref="TrackedTask.Succeed" />, <see cref="TrackedTask.Fail(string)" /> or <see cref="TrackedTask.MarkCancelled" />.
    /// </summary>
    public static TrackedTask Begin(string name, string source, bool canCancel = false)
    {
        var task = new TrackedTask(name, source, canCancel);
        Queue(task);
        task.Start();
        return task;
    }

    /// <summary>
    ///     As <see cref="Begin" />, for work that is usually over in a blink (expanding a folder): the task only appears in the list when it is still
    ///     going after <paramref name="after" />, or when it fails. Work that finishes sooner leaves no trace.
    /// </summary>
    public static TrackedTask BeginDeferred(string name, string source, TimeSpan? after = null, bool canCancel = false)
    {
        var task = new TrackedTask(name, source, canCancel);
        task.Start();

        var timer = new System.Threading.Timer(_ =>
                                               {
                                                   if (!task.State.IsFinished()) Queue(task);
                                               }, null, after ?? DeferredAfter, Timeout.InfiniteTimeSpan);

        task.PropertyChanged += (_, e) =>
                                {
                                    if (e.PropertyName != nameof(ITrackedTask.State) || !task.State.IsFinished()) return;

                                    timer.Dispose();
                                    if (task.State == TaskState.Failed) Queue(task);
                                };
        return task;
    }

    /// <summary>
    ///     Runs <paramref name="work" /> as a task: queued, running while it runs, and ended by how it ends (returns: completed; throws: failed;
    ///     cancelled: cancelled). The work reports through the task it is given, and watches <see cref="TrackedTask.Token" /> when
    ///     <paramref name="canCancel" />. A failure is rethrown to the caller after it was recorded.
    /// </summary>
    public static async Task Run(string name, string source, Func<TrackedTask, Task> work, bool canCancel = false)
    {
        await Run<object?>(name, source, async t =>
                                         {
                                             await work(t).ConfigureAwait(false);
                                             return null;
                                         }, canCancel).ConfigureAwait(false);
    }

    /// <inheritdoc cref="Run(string, string, Func{TrackedTask, Task}, bool)" />
    public static async Task<T> Run<T>(string name, string source, Func<TrackedTask, Task<T>> work, bool canCancel = false)
    {
        var task = new TrackedTask(name, source, canCancel);
        Queue(task);
        task.Start();
        try
        {
            var result = await work(task).ConfigureAwait(false);
            task.Succeed();
            return result;
        }
        catch (OperationCanceledException)
        {
            task.MarkCancelled();
            throw;
        }
        catch (Exception ex)
        {
            task.Fail(ex);
            throw;
        }
    }

    // ---- housekeeping ----------------------------------------------------------------------------

    /// <summary>Takes the ended tasks out of the list.</summary>
    public static void ClearFinished()
    {
        OnUi(() =>
             {
                 foreach (var t in Tasks.Where(t => t.State.IsFinished()).ToList()) Forget(t);
                 UpdateStatusBar();
             });
    }

    private static void Forget(ITrackedTask task)
    {
        Tasks.Remove(task);
        task.PropertyChanged -= OnTaskChanged;
        lock (Gate) Watched.Remove(task);
    }

    private static void Trim()
    {
        var finished = Tasks.Where(t => t.State.IsFinished()).ToList();
        for (int i = 0; i < finished.Count - MaxFinished; i++) Forget(finished[i]);
    }

    private static void OnTaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ITrackedTask task || e.PropertyName != nameof(ITrackedTask.State)) return;

        UpdateStatusBar();
        if (task.State.IsFinished()) Ended(task);
    }

    private static readonly HashSet<ITrackedTask> Announced = new(ReferenceEqualityComparer.Instance);

    private static void Ended(ITrackedTask task)
    {
        lock (Gate)
        {
            if (!Announced.Add(task)) return;
        }

        TaskEnded?.Invoke(task);
        if (task.NotifyOnCompletion) Safely(() => Toast(task));
        OnUi(Trim);
    }

    private static void UpdateStatusBar()
    {
        int active = ActiveCount;
        AppStatus.Instance.SetRunningTasks(active == 0 ? null : active == 1 ? "1 task running" : $"{active} tasks running");
    }

    private static void Safely(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "A task notification failed");
        }
    }

    /// <summary>Runs <paramref name="action" /> on the UI thread (now when already there or when there is no UI: a test, a command-line run).</summary>
    internal static void OnUi(Action action)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
        catch
        {
            // shutting down
        }
    }

    /// <summary>Forgets every task and that the pane was shown, so the next task shows it again (for tests).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _revealed = false;
            Watched.Clear();
            Announced.Clear();
        }

        Tasks.Clear();
    }
}

/// <summary>The hook the Tasks tool fills in so the center can show the pane without knowing about it.</summary>
internal static class RevealPane
{
    public static Action? Request { get; set; }
}
