using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Serilog;
using Silo.Design;
using Silo.Ui.Panes;
using Silo.Ui.Windows;

namespace Silo.Diagnostics;

/// <summary>
///     Notices exceptions that point at a bug (a <see cref="NotImplementedException" />, a null dereference, a bad cast...)
///     even when something catches them, such as a binding swallowing a property getter's exception, and lists them in the
///     Problems pane while View ▸ User Level is <see cref="DesignLevel.Debug" />. Exceptions nobody caught are listed too.
/// </summary>
/// <remarks>
///     Each throw site is one line, with a count. Collecting runs at every level; only showing the lines is tied to the
///     Debug level, so switching to it shows what was already seen. Code that throws one of these on purpose and catches
///     it can say so with <see cref="Expecting" />.
/// </remarks>
public static class StrayExceptions
{
    /// <summary>The most throw sites kept; later new ones are only logged.</summary>
    private const int MaxSites = 200;

    /// <summary>Exceptions that are a bug wherever they come from.</summary>
    private static readonly Type[] AlwaysBugs =
    [
        typeof(NotImplementedException), typeof(NullReferenceException), typeof(InvalidCastException),
        typeof(IndexOutOfRangeException), typeof(DivideByZeroException)
    ];

    /// <summary>Exceptions that are a bug when Silo's (or Telefrag's) own code throws them.</summary>
    private static readonly Type[] OwnCodeBugs =
    [
        typeof(ArgumentException), typeof(KeyNotFoundException), typeof(InvalidOperationException),
        typeof(NotSupportedException), typeof(FormatException), typeof(OverflowException)
    ];

    private static readonly AsyncLocal<int> ExpectDepth = new();

    private static readonly object                                Gate  = new();
    private static readonly Dictionary<string, Site>              Sites = new(StringComparer.Ordinal);
    private static readonly List<Site>                            Order = [];
    private static readonly ConditionalWeakTable<Exception, Site> Seen  = new();

    /// <summary>The owner of the lines in <see cref="ProblemList" />.</summary>
    private static readonly object Owner = new();

    [ThreadStatic] private static bool _inHandler;

    private static Dispatcher? _dispatcher;
    private static int         _publishQueued;
    private static bool        _installed;

    /// <summary>Whether the lines are shown now (the design level is Debug).</summary>
    private static bool Showing => AppDesignLevel.Instance.IsDebug;

    /// <summary>Starts watching. Call once, early in <see cref="App.OnStartup" />, on the UI thread.</summary>
    public static void Install(Application app)
    {
        if (_installed) return;

        _installed  = true;
        _dispatcher = app.Dispatcher;

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        AppDomain.CurrentDomain.UnhandledException   += (_, e) => { if (e.ExceptionObject is Exception ex) Report(ex, true); };
        TaskScheduler.UnobservedTaskException        += (_, e) => Report(e.Exception, true);
        app.DispatcherUnhandledException             += (_, e) => Report(e.Exception, true);

        AppDesignLevel.Instance.PropertyChanged += OnDesignLevelChanged;
    }

    /// <summary>
    ///     Marks exceptions thrown until the result is disposed (on this thread and the async code it starts) as expected, for
    ///     code that throws and catches one of the watched kinds on purpose.
    /// </summary>
    public static IDisposable Expecting()
    {
        ExpectDepth.Value++;
        return new ExpectScope();
    }

    /// <summary>Forgets every line (the Problems pane empties).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Sites.Clear();
            Order.Clear();
        }

        QueuePublish();
    }

    private static void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (_inHandler) return;

        _inHandler = true;
        try
        {
            var ex = e.Exception;
            if (ExpectDepth.Value > 0 || !IsUnexpected(ex)) return;

            Report(ex, false);
        }
        catch
        {
            /* never let the watcher be the problem */
        }
        finally
        {
            _inHandler = false;
        }
    }

    /// <summary>Whether <paramref name="ex" /> is a kind that means a bug where it was thrown.</summary>
    private static bool IsUnexpected(Exception ex)
    {
        var type = ex.GetType();
        foreach (var t in AlwaysBugs)
            if (t.IsAssignableFrom(type)) return true;

        foreach (var t in OwnCodeBugs)
            if (t.IsAssignableFrom(type)) return IsOwn(ex.TargetSite?.DeclaringType);

        return false;
    }

    private static bool IsOwn(Type? type)
    {
        string? name = type?.Assembly.GetName().Name;
        return name is not null &&
               (name.StartsWith("Silo", StringComparison.Ordinal) || name.StartsWith("Telefrag", StringComparison.Ordinal));
    }

    private static void Report(Exception ex, bool unhandled)
    {
        bool  isNew = false;
        Site? site;
        lock (Gate)
        {
            if (Seen.TryGetValue(ex, out var known))
            {
                // the same exception again: rethrown, or now unhandled
                if (!unhandled || known.Unhandled) return;

                known.Unhandled = true;
                site            = known;
            }
            else
            {
                string key = $"{ex.GetType().FullName}|{ex.TargetSite?.DeclaringType?.FullName}.{ex.TargetSite?.Name}";
                if (!Sites.TryGetValue(key, out site))
                {
                    if (Sites.Count >= MaxSites)
                    {
                        Log.Debug(ex, "Stray exception (not listed: {max} sites already)", MaxSites);
                        return;
                    }

                    site = new Site(ex, unhandled);
                    Sites.Add(key, site);
                    Order.Add(site);
                    isNew = true;
                }

                site!.Count++;
                site.Last      =  DateTime.Now;
                site.Unhandled |= unhandled;
                Seen.AddOrUpdate(ex, site);
            }
        }

        if (isNew)
            Log.Warning(ex, "{kind} {type} at {where}: {message}", unhandled ? "Unhandled" : "Stray", ex.GetType().Name,
                        site!.Where, ex.Message);

        QueuePublish();
    }

    /// <summary>
    ///     The call stack where <paramref name="ex" /> was thrown. At first chance the handler runs on the throwing thread,
    ///     on top of the throwing frame, and the exception's own trace has only that frame, so the current stack is taken and
    ///     this class's frames are dropped. For an exception that reached an unhandled-exception event its own trace is used.
    /// </summary>
    private static StackFrame[] Capture(Exception ex, bool unhandled)
    {
        try
        {
            if (!unhandled)
            {
                var frames = new StackTrace(true).GetFrames();
                int last   = Array.FindLastIndex(frames, f => f.GetMethod()?.DeclaringType is { } t && IsThisClass(t));
                if (last >= 0 && last < frames.Length - 1) return frames[(last + 1)..];
            }

            return new StackTrace(ex, true).GetFrames();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsThisClass(Type type)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
            if (t == typeof(StrayExceptions)) return true;

        return false;
    }

    private static void OnDesignLevelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppDesignLevel.IsDebug)) QueuePublish();
    }

    /// <summary>Republishes the lines on the UI thread, once for any number of reports in between.</summary>
    private static void QueuePublish()
    {
        if (_dispatcher is not { HasShutdownStarted: false } dispatcher) return;
        if (Interlocked.Exchange(ref _publishQueued, 1) == 1) return;

        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, Publish);
        }
        catch
        {
            _publishQueued = 0;
        }
    }

    private static void Publish()
    {
        Interlocked.Exchange(ref _publishQueued, 0);

        if (!Showing)
        {
            ProblemList.Clear(Owner);
            return;
        }

        List<Problem> problems;
        lock (Gate)
        {
            problems = Order.Select(s => s.ToProblem()).ToList();
        }

        ProblemList.Publish(Owner, problems);
    }

    /// <summary>One place exceptions were thrown from, with how often.</summary>
    private sealed class Site
    {
        private readonly StackFrame[] _frames;

        public Site(Exception first, bool unhandled)
        {
            _frames    = Capture(first, unhandled);
            Type       = first.GetType();
            Message    = first.Message;
            FirstSeen  = DateTime.Now;
            ThreadName = global::System.Threading.Thread.CurrentThread is { } t ? t.Name ?? $"#{t.ManagedThreadId}" : "?";
            Frame      = PickFrame(_frames);

            var method = Frame?.GetMethod() ?? first.TargetSite;
            Where = method is null ? "(unknown)" : $"{method.DeclaringType?.Name}.{method.Name}";
        }

        public int      Count     { get; set; }
        public DateTime FirstSeen { get; }
        public DateTime Last      { get; set; }
        public string   Message   { get; }
        public string   ThreadName { get; }
        public Type     Type      { get; }
        public bool     Unhandled { get; set; }
        public string   Where     { get; }

        /// <summary>The frame to point at: the first one in Silo's own code, else the throwing one.</summary>
        private StackFrame? Frame { get; }

        private static StackFrame? PickFrame(StackFrame[] frames)
        {
            return frames.FirstOrDefault(f => IsOwn(f.GetMethod()?.DeclaringType) && f.GetFileName() is not null) ??
                   frames.FirstOrDefault(f => IsOwn(f.GetMethod()?.DeclaringType)) ??
                   frames.FirstOrDefault();
        }

        public Problem ToProblem()
        {
            string times = Count > 1 ? $" (×{Count})" : string.Empty;
            string how   = Unhandled ? "Unhandled" : "Caught";
            string? file = Frame?.GetFileName();
            int     line = Frame?.GetFileLineNumber()   ?? 0;
            int     col  = Frame?.GetFileColumnNumber() ?? 0;

            return new Problem(Unhandled || Type != typeof(NotImplementedException) ? ProblemSeverity.Error : ProblemSeverity.Warning,
                               $"{how} {Type.Name} in {Where}: {Message}{times}",
                               file is null ? Where : Path.GetFileName(file),
                               line > 0 ? line : null,
                               col  > 0 ? col : null,
                               "Exception",
                               ShowDetails);
        }

        private void ShowDetails()
        {
            var text = new StringBuilder();
            text.AppendLine($"{Type.FullName}: {Message}");
            text.AppendLine();
            text.AppendLine($"{(Unhandled ? "Unhandled" : "Caught by something")}; seen {Count} time(s), first {FirstSeen:T}, last {Last:T}, first on thread {ThreadName}.");
            text.AppendLine();
            foreach (var frame in _frames)
            {
                var method = frame.GetMethod();
                text.Append("   at ").Append(method?.DeclaringType?.FullName).Append('.').Append(method?.Name ?? "?");
                if (frame.GetFileName() is { } file) text.Append(" in ").Append(file).Append(':').Append(frame.GetFileLineNumber());
                text.AppendLine();
            }

            AlertDialog.Show(text.ToString(), "Stray exception");
        }
    }

    private sealed class ExpectScope : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;

            _done = true;
            ExpectDepth.Value--;
        }
    }
}
