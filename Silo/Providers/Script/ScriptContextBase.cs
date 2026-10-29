using System.Text;

namespace Silo.Providers.Script;

/// <summary>The outcome of one <see cref="IScriptContext.RunAsync" /> call.</summary>
public sealed class ScriptResult : IScriptResult
{
    public bool IsFailed     => !IsSuccessful;
    public bool IsSuccessful { get; private init; }
    public bool IsTimeout    { get; private init; }

    /// <summary>The value the script evaluated to, if any.</summary>
    public object? Value { get; private init; }

    /// <summary>The exception that stopped the script, if any.</summary>
    public Exception? Exception { get; private init; }

    /// <summary>Compile/parse/runtime error messages reported by the runtime.</summary>
    public IReadOnlyList<string> Errors { get; private init; } = [];

    public static ScriptResult Ok(object? value = null)
    {
        return new ScriptResult { IsSuccessful = true, Value = value };
    }

    public static ScriptResult Failed(Exception? ex, params string[] errors)
    {
        return new ScriptResult { Exception = ex, Errors = errors };
    }

    public static ScriptResult TimedOut(TimeSpan after)
    {
        return new ScriptResult
        {
            IsTimeout = true,
            Exception = new TimeoutException($"Script did not complete within {after}."),
            Errors    = [$"Script did not complete within {after}."]
        };
    }
}

/// <summary>
///     What a script sees as <c>host</c> (F#, PowerShell variable <c>$host</c> is untouched; see each context) or as
///     top-level members (C#): a way to send progress/warnings/errors/output to the context's events.
/// </summary>
public sealed class ScriptHost
{
    private readonly ScriptContextBase _context;

    internal ScriptHost(ScriptContextBase context)
    {
        _context = context;
    }

    /// <summary>Variables added with <see cref="IScriptContext.AddVariable" />.</summary>
    public IDictionary<string, object> Vars { get; } = new Dictionary<string, object>();

    public void Progress(object progress)
    {
        _context.RaiseProgress(progress);
    }

    public void Warning(object warning)
    {
        _context.RaiseWarning(warning);
    }

    public void Error(object error)
    {
        _context.RaiseError(error);
    }

    public void WriteLine(object? text)
    {
        _context.RaiseText(text?.ToString() ?? "");
    }

    public void Output(object value)
    {
        _context.RaiseObject(value);
    }
}

/// <summary>
///     Shared plumbing for in-process script contexts: one script at a time, optional timeout, and the
///     event raising. Events are raised on a thread-pool thread; UI subscribers must marshal.
/// </summary>
public abstract class ScriptContextBase : IScriptContext, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected ScriptContextBase()
    {
        Host = new ScriptHost(this);
    }

    protected ScriptHost Host { get; }

    /// <summary>How long a script may run before <see cref="IScriptResult.IsTimeout" />. Null for no limit.</summary>
    public TimeSpan? Timeout { get; set; }

    public abstract bool AddVariable(string name, object value);

    public async Task<IScriptResult> RunAsync(string script)
    {
        await _gate.WaitAsync();
        try
        {
            ScriptStarting?.Invoke(this, script);

            ScriptResult result;
            using var cts = new CancellationTokenSource();
            try
            {
                var task = Task.Run(() => ExecuteAsync(script, cts.Token));
                if (Timeout is { } limit && await Task.WhenAny(task, Task.Delay(limit)) != task)
                {
                    cts.Cancel();
                    _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    result = ScriptResult.TimedOut(limit);
                    RaiseError(result.Exception!);
                }
                else
                {
                    result = await task;
                }
            }
            catch (Exception ex)
            {
                RaiseError(ex);
                result = ScriptResult.Failed(ex, ex.Message);
            }

            ScriptCompleted?.Invoke(this, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public event EventHandler<object>?        ProgressEvent;
    public event EventHandler<object>?        WarningEvent;
    public event EventHandler<object>?        ErrorEvent;
    public event EventHandler<string>?        TextOutput;
    public event EventHandler<object>?        ObjectOutput;
    public event EventHandler<string>?        ScriptStarting;
    public event EventHandler<IScriptResult>? ScriptCompleted;

    public virtual void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>Runs the script. Report problems through the Raise* methods; return the overall result.</summary>
    protected abstract Task<ScriptResult> ExecuteAsync(string script, CancellationToken cancel);

    internal void RaiseProgress(object o)
    {
        ProgressEvent?.Invoke(this, o);
    }

    internal void RaiseWarning(object o)
    {
        WarningEvent?.Invoke(this, o);
    }

    internal void RaiseError(object o)
    {
        ErrorEvent?.Invoke(this, o);
    }

    internal void RaiseText(string s)
    {
        TextOutput?.Invoke(this, s);
    }

    internal void RaiseObject(object o)
    {
        ObjectOutput?.Invoke(this, o);
    }

    /// <summary>A <see cref="TextWriter" /> which raises <paramref name="onLine" /> for each completed line.</summary>
    protected sealed class LineWriter(Action<string> onLine) : TextWriter
    {
        private readonly StringBuilder _line = new();
        public override  Encoding      Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                Flush();
            }
            else if (value != '\r')
            {
                _line.Append(value);
            }
        }

        public override void Flush()
        {
            if (_line.Length == 0)
            {
                return;
            }

            var s = _line.ToString();
            _line.Clear();
            onLine(s);
        }
    }
}
