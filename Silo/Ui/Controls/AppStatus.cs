using System.Windows.Threading;

namespace Silo.Ui;

/// <summary>What the status line is saying.</summary>
public enum StatusKind
{
    /// <summary>Nothing has been reported yet, and nothing is going on.</summary>
    Ready,

    /// <summary>The last result: something was done (or a note for the person).</summary>
    Info,

    /// <summary>Something is being loaded or worked on right now (see <see cref="AppStatus.Begin" />).</summary>
    Busy,

    /// <summary>The last result was a failure.</summary>
    Error
}

/// <summary>
///     What the app is up to, for the status bar at the bottom of the main window (<see cref="AppStatusBar" />): the last result
///     (<see cref="Report" />), the things going on in the background (<see cref="Begin" />), and two slots kept for later -- the last
///     event that fired (<see cref="ReportEvent" />) and the running tasks (<see cref="SetRunningTasks" />). Everything here may be
///     called from any thread; <see cref="Changed" /> is raised on the UI thread, coalesced (many changes in a row make one event).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="App.Status" /> (the long-standing "say something in the status bar") reports here. Code that loads or builds
///         something in the background tells the person with <c>using var op = AppStatus.Instance.Begin("Loading X");</c> and then
///         <see cref="StatusOperation.Done" />: while any operation is running the line reads "Loading X..." (and "(+2 more)"),
///         and when the last one ends it goes back to the last result.
///     </para>
/// </remarks>
public sealed class AppStatus
{
    private readonly object                _gate = new();
    private readonly List<StatusOperation> _operations = [];

    private string?    _lastText;
    private StatusKind _lastKind = StatusKind.Ready;
    private DateTime   _lastAt;
    private string?    _eventText;
    private DateTime   _eventAt;
    private string?    _tasksText;
    private int        _pending;

    /// <summary>The one status line of the app.</summary>
    public static AppStatus Instance { get; } = new();

    /// <summary>The line to show: what is being worked on, or else the last result ("Ready" before there is one).</summary>
    public string Text
    {
        get
        {
            lock (_gate)
            {
                if (_operations.Count == 0) return _lastText ?? "Ready";

                var    latest = _operations[^1];
                string text   = latest.Detail is { Length: > 0 } d ? $"{latest.What}: {d}..." : latest.What + "...";
                return _operations.Count > 1 ? $"{text}  (+{_operations.Count - 1} more)" : text;
            }
        }
    }

    /// <summary>What <see cref="Text" /> is: busy while there are operations, else how the last result went.</summary>
    public StatusKind Kind
    {
        get
        {
            lock (_gate)
            {
                return _operations.Count > 0 ? StatusKind.Busy : _lastKind;
            }
        }
    }

    /// <summary>The longer account for a tooltip: each operation under way, then the last result and when it happened.</summary>
    public string Detail
    {
        get
        {
            lock (_gate)
            {
                var lines = new List<string>();
                foreach (var op in _operations)
                    lines.Add($"{op.What}{(op.Detail is { Length: > 0 } d ? ": " + d : string.Empty)}  (for {Elapsed(op.StartedUtc)})");

                if (_lastText is not null) lines.Add($"Last result ({_lastAt.ToLocalTime():T}): {_lastText}");
                return lines.Count == 0 ? "Nothing is going on." : string.Join(Environment.NewLine, lines);
            }
        }
    }

    /// <summary>The names of what is being worked on (see <see cref="Begin" />), oldest first.</summary>
    public IReadOnlyList<string> Operations
    {
        get
        {
            lock (_gate)
            {
                return _operations.Select(o => o.What).ToList();
            }
        }
    }

    /// <summary>The last event reported with <see cref="ReportEvent" />; null for none yet.</summary>
    public string? LastEvent
    {
        get
        {
            lock (_gate)
            {
                return _eventText;
            }
        }
    }

    /// <summary>When <see cref="LastEvent" /> was reported (UTC).</summary>
    public DateTime LastEventAtUtc
    {
        get
        {
            lock (_gate)
            {
                return _eventAt;
            }
        }
    }

    /// <summary>The running tasks as set with <see cref="SetRunningTasks" />; null for none shown.</summary>
    public string? RunningTasks
    {
        get
        {
            lock (_gate)
            {
                return _tasksText;
            }
        }
    }

    /// <summary>Raised on the UI thread after anything here changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Says how something turned out (or notes something for the person): it stays until the next report.</summary>
    /// <param name="text">What to say.</param>
    /// <param name="isError">Whether it is a failure (shown in red).</param>
    public void Report(string text, bool isError = false)
    {
        lock (_gate)
        {
            _lastText = text;
            _lastKind = isError ? StatusKind.Error : StatusKind.Info;
            _lastAt   = DateTime.UtcNow;
        }

        Raise();
    }

    /// <summary>
    ///     Records the last event that fired (a Silo opened, a theme changed...). Nothing reports events yet: the status bar has a
    ///     place for it.
    /// </summary>
    public void ReportEvent(string text)
    {
        lock (_gate)
        {
            _eventText = text;
            _eventAt   = DateTime.UtcNow;
        }

        Raise();
    }

    /// <summary>
    ///     Sets what the "tasks" place of the status bar shows (the number or names of running tasks); null or empty to clear it.
    ///     Nothing sets it yet. (<see cref="Begin" /> is not this: those are the things being loaded, shown in the status line.)
    /// </summary>
    public void SetRunningTasks(string? text)
    {
        lock (_gate)
        {
            _tasksText = string.IsNullOrWhiteSpace(text) ? null : text;
        }

        Raise();
    }

    /// <summary>
    ///     Starts telling the person that <paramref name="what" /> ("Loading components") is being worked on. End it with
    ///     <see cref="StatusOperation.Done" /> or <see cref="StatusOperation.Fail" />, or just dispose it.
    /// </summary>
    public StatusOperation Begin(string what)
    {
        var op = new StatusOperation(this, what);
        lock (_gate)
        {
            _operations.Add(op);
        }

        Raise();
        return op;
    }

    /// <summary>Runs <paramref name="task" /> as an operation: shown while it runs, and its failure is reported (and rethrown).</summary>
    public async Task Track(string what, Task task, string? done = null)
    {
        using var op = Begin(what);
        try
        {
            await task.ConfigureAwait(false);
            op.Done(done);
        }
        catch (Exception ex)
        {
            op.Fail($"{what} failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>As <see cref="Track(string, Task, string?)" /> for a task with a result.</summary>
    public async Task<T> Track<T>(string what, Task<T> task, string? done = null)
    {
        using var op = Begin(what);
        try
        {
            var result = await task.ConfigureAwait(false);
            op.Done(done);
            return result;
        }
        catch (Exception ex)
        {
            op.Fail($"{what} failed: {ex.Message}");
            throw;
        }
    }

    internal void End(StatusOperation op, string? result, bool failed)
    {
        lock (_gate)
        {
            if (!_operations.Remove(op)) return;

            if (result is not null)
            {
                _lastText = result;
                _lastKind = failed ? StatusKind.Error : StatusKind.Info;
                _lastAt   = DateTime.UtcNow;
            }
        }

        Raise();
    }

    internal void Touch()
    {
        Raise();
    }

    // One notification at a time is queued, so a burst of changes (every step of a load) costs the UI one refresh.
    private void Raise()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;

        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            Volatile.Write(ref _pending, 0); // no UI (a command-line run, a test): nobody is listening
            return;
        }

        try
        {
            dispatcher.BeginInvoke(new Action(() =>
                                              {
                                                  Volatile.Write(ref _pending, 0);
                                                  Changed?.Invoke(this, EventArgs.Empty);
                                              }), DispatcherPriority.Background);
        }
        catch
        {
            Volatile.Write(ref _pending, 0); // shutting down
        }
    }

    private static string Elapsed(DateTime startedUtc)
    {
        var span = DateTime.UtcNow - startedUtc;
        return span.TotalSeconds < 1 ? $"{span.TotalMilliseconds:0} ms" : $"{span.TotalSeconds:0.#} s";
    }
}

/// <summary>One thing being worked on, as <see cref="AppStatus.Begin" /> hands it out.</summary>
public sealed class StatusOperation : IDisposable
{
    private readonly AppStatus _owner;
    private          string?   _detail;

    internal StatusOperation(AppStatus owner, string what)
    {
        _owner = owner;
        What   = what;
    }

    /// <summary>What is being worked on ("Loading components").</summary>
    public string What { get; }

    /// <summary>A step or a count to show beside <see cref="What" /> ("12 of 40"); changes show at once.</summary>
    public string? Detail
    {
        get => _detail;
        set
        {
            _detail = value;
            _owner.Touch();
        }
    }

    /// <summary>When it began (UTC).</summary>
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>It is done. <paramref name="result" />, when given, becomes the last result ("Components loaded in 210 ms").</summary>
    public void Done(string? result = null)
    {
        _owner.End(this, result, false);
    }

    /// <summary>It failed; <paramref name="message" /> becomes the last result, shown as an error.</summary>
    public void Fail(string message)
    {
        _owner.End(this, message, true);
    }

    /// <summary>Ends the operation with no result of its own (as <see cref="Done" /> with none); ending it twice does nothing.</summary>
    public void Dispose()
    {
        _owner.End(this, null, false);
    }
}
