using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Media;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Microsoft.Win32;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using Silo.Assets;
using Silo.Commanding;
using Silo.DbModel.Sqlite;
using Silo.Design;
using Silo.Extensions;
using Silo.Hosting;
using Silo.Model;
using Silo.Settings;
using Silo.Theming;
using Silo.Ui;
using Silo.UI;
using Silo.ViewModel;
using Telefrag.DI;
using Telefrag.Exceptions;

namespace Silo;

/// <summary>
///     ViewModel for the application
/// </summary>
public partial class App : Application, IContainerHost
{
    /// <summary>
    ///     Help -> About
    /// </summary>
    public static AppCommand About => new AppAboutCommand();

    /// <summary>
    ///     Gegs or changes the active Silo
    /// </summary>
    public OpenSilo? ActiveSilo
    {
        get;
        set
        {
            field = value;
            ActiveSiloChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    ///     Gets the app's current User/Author/Developer/Debug design level (View -> User Level),
    ///     which gates whether the pink design menus (see <see cref="Silo.Design.DesignMenu" />)
    ///     are reachable via Ctrl+right-click.
    /// </summary>
    public AppDesignLevel DesignLevel => AppDesignLevel.Instance;

    /// <summary>
    ///     File -> Exit
    /// </summary>
    public new static AppCommand ExitCommand => new AppExitCommand();

    /// <summary>
    ///     The app instance representing this process
    /// </summary>
    public static App? Instance => Current as App;

    /// <summary>
    ///     What this process was started to do: which kind of process it is (stock / Solo / Remote), the document to open
    ///     and whether it answers "open this" requests from later launches. Decided in <c>Program.Main</c> by
    ///     <see cref="StartupPlanner" /> before this object exists.
    /// </summary>
    public LaunchPlan Launch { get; init; } = new(LaunchMode.Stock, null, false);

    /// <summary>
    ///     In-memory cache of logs
    /// </summary>
    public ObservableCollection<LogEvent> Logs { get; } = [];

    /// <summary>
    ///     File -> New Silo
    /// </summary>
    public static AppCommand NewSiloCommand => new NewSiloCommand();

    /// <summary>
    ///     File -> Open Silo
    /// </summary>
    public static AppCommand OpenSiloCommand => new OpenSiloCommand();

    /// <summary>
    ///     File -> Open from URL
    /// </summary>
    public static AppCommand OpenSiloFromUrlCommand => new OpenSiloFromUrlCommand();

    /// <summary>
    ///     Gets a collection of the open Silos.  This mirrors the actual live registry of
    ///     open silos (<see cref="OpenSilo.OpenSilos" />) rather than a separate, never-populated
    ///     tracking collection, so callers - such as <see cref="Silo.Commanding.AppExitCommand" />,
    ///     which shuts each one down on exit, and the connection diagram tool - see real data.
    /// </summary>
    public ObservableCollection<OpenSilo> OpenSilos => OpenSilo.OpenSilos;


    /// <summary>
    ///     Whether to show the Tolos menu
    /// </summary>
    public static bool ShowToolsMenu { get; set; } = true;

    /// <summary>
    ///     Gets the app's current Light/Dark/System theme setting (View -> Theme).
    /// </summary>
    public AppTheme Theme => AppTheme.Instance;

    /// <summary>
    ///     Gets the ViewModel for the Tools in the Tools menu.
    /// </summary>
    public SiloToolsModel Tools { get; } = new();

    /// <summary>
    ///     Application level logger
    /// </summary>
    protected ILogger Logger { get; } = Log.ForContext<App>();

    private IDatabaseProvider _db => ActiveSilo?.Data;

    /// <summary>
    ///     Application-level components container
    /// </summary>
    public Container Components { get; } = new(nameof(App));

    /// <summary>
    ///     Gets all logs formatted to a single buffer
    /// </summary>
    public string GetLogs()
    {
        StringBuilder sb = new(Logs.Count * 10);
        foreach (LogEvent v in Logs) sb.AppendLine($"{v.Timestamp} [{v.Level}] {v.RenderMessage()}");

        return sb.ToString();
    }

    /// <summary>
    ///     Creates a new Silo file at a path chosen by the user via modal file save dialog.
    /// </summary>
    public OpenSilo? NewSilo()
    {
        var s = new SaveFileDialog
                {
                    Title  = "Create Silo",
                    Filter = "(*.silo)|*.silo"
                };
        if (s.ShowDialog() is true)
        {
            var file = new SiloFile(s.FileName);
            if (OpenSilo.CreateFile(file) is OpenSilo c)
            {
                Logger.Information("Created new silo at '{path}'", c.File.FilePath);
                return c;
            }
        }

        return null;
    }

    /// <summary>
    ///     Closes every open Silo, offering to save the ones with unsaved changes. Returns false -- leaving the remaining
    ///     Silos open -- as soon as the person cancels.
    /// </summary>
    public async Task<bool> CloseAllSilosAsync()
    {
        foreach (var silo in OpenSilo.OpenSilos.ToList())
        {
            ActiveSilo = silo; // so the person sees which Silo is being asked about
            if (!await silo.CloseAsync()) return false;
        }

        return true;
    }

    /// <summary>
    ///     Raised when the active Silo has changed
    /// </summary>
    public event EventHandler? ActiveSiloChanged;

    public event EventHandler<OpenSilo>? SiloOpened;
    public event EventHandler<OpenSilo>? SiloOpening;
    public event EventHandler<OpenSilo>? SiloClosing;
    public event EventHandler?           SiloClosed;

    internal void NotifySiloOpening(object sender, OpenSilo o)
    {
        SiloOpening?.Invoke(sender, o);
    }

    internal void NotifySiloOpened(object sender, OpenSilo o)
    {
        SiloOpened?.Invoke(sender, o);
    }

    internal void NotifySiloClosing(object sender, OpenSilo o)
    {
        SiloClosing?.Invoke(sender, o);
    }

    internal void NotifySiloClosed(object sender, OpenSilo o)
    {
        SiloClosed?.Invoke(sender, EventArgs.Empty);
    }


    public static bool Status(string text)
    {
        if (!Current.Dispatcher.CheckAccess())
            return Current.Dispatcher.Invoke(() => Instance!.InterError(text));
        (Current.MainWindow as MainWindow)?.StatusError.Content = text;
        return false;
    }

    public bool InterError(string error)
    {
        Beep();
        return Status(error);
    }

    public bool Beep()
    {
        try
        {
            SystemSounds.Exclamation.Play();
            return true;
        }
        catch
        {
            /* intentional */
        }

        return false;
    }

    public bool InterAdd<TItem>(TItem item)
    {
        if (ActiveSilo is null)
            return InterError("No silo is active.  Create, open, or select a silo first.");

        if (Exists(item))
            return InterError("Error:  Item not found in silo.");

        // _validateItem(item);
        // _validateChild(item, null);
        return _db.AddItem(item);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _configLogging();
        _discoverRegs();
        AppTheme.Apply();
        AppDesignSettings.Apply();
        AppDesignLevel.Apply();
        SiloFileSettings.Apply();
        DesignMenu.Register();
        ResourceIndex.Instance.Build();

        // Working folders left behind by a crashed Silo process (those still in use are locked and skipped).
        _ = Task.Run(SiloArchive.SweepOrphans);

        base.OnStartup(e);

        // Later launches hand their documents to the stock instance through a named pipe.
        if (Launch.ServeRequests) SingleInstance.StartServer(HandleLaunchRequest);

        // The document this launch is for (decided in Program.Main -- double-click in Explorer, "silo.exe file.silo", a URL) opens
        // once the window is up.
        if (Launch.Target is { } target)
            Dispatcher.BeginInvoke(new Action(async () => await Commanding.OpenSiloCommand.OpenPathAsync(target)),
                                   DispatcherPriority.ApplicationIdle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SingleInstance.StopServer();
        base.OnExit(e);
    }

    /// <summary>A later launch asked this (stock) instance to open a document, or just to come to the front.</summary>
    private void HandleLaunchRequest(string? path)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
                                          {
                                              if (path is not null) await Commanding.OpenSiloCommand.OpenPathAsync(path);
                                              BringToFront();
                                          }));
    }

    /// <summary>Restores and activates the main window.</summary>
    public void BringToFront()
    {
        if (MainWindow is not { } w) return;

        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        w.Topmost = true; // the usual trick to get past foreground-lock rules
        w.Topmost = false;
        w.Focus();
    }

    private void _configLogging()
    {
        var l = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console().WriteTo.Debug().CreateLogger();

        Log.Logger = l;
        SelfLog.Enable(msg => Trace.WriteLine($"[Serilog]: {msg}"));
        l.Debug("Logging configured");
    }

    private void _discoverRegs()
    {
        List<Assembly?> a =
        [
            Assembly.GetEntryAssembly(),
            Assembly.GetCallingAssembly(),
            Assembly.GetExecutingAssembly()
        ];
        a.Where(x => x != null).Distinct().ToList().ForEach(x => DiscoverRegistrations(x));
    }

    public void DiscoverRegistrations(Assembly? a, bool critical = false)
    {
        if (a is null) return;
        List<Type> tt = null;
        try
        {
            tt = a.GetExportedTypes().ToList();
        }
        catch (Exception ex) when (!critical)
        {
            Log.Error(ex, "Failed to get types from assembly '{a}'", a);
            return;
        }

        foreach (var t in tt)
        {
            if (t.IsAbstract) continue;
            if (t.IsInterface) continue;
            if (!t.IsPublic) continue;

            var ca = t.GetCustomAttribute<RegisterAttribute>(true);
            var ta = t.GetCustomAttribute<TransientAttribute>(true);
            var sa = t.GetCustomAttribute<SingletonAttribute>(true);
            if (ca is { } aa)
            {
                var r = Components.RegisterSingleton(aa.ServiceType, t, aa.Key, null,
                                                     aa.IsSingleton ? ComponentLifetime.Singleton : ComponentLifetime.Transient);
                if (r != null)
                    Log.Debug("Registered component '{tn}' from attribute (as {in})", _base(t).ShortDisplayName(),
                              aa.ServiceType.ShortDisplayName());
            }
            else if (ta != null)
            {
                var r = Components.RegisterSingleton(ta.ServiceType, t, ta.Name, null, ComponentLifetime.Transient);
                if (r != null)
                    Log.Debug("Registered transient '{tn}' from attribute (as {in})", _base(t).ShortDisplayName(),
                              ta.ServiceType.ShortDisplayName());
            }
            else if (sa != null)
            {
                var r = Components.RegisterSingleton(sa.ServiceType, t, sa.Name, null, ComponentLifetime.Singleton);
                if (r != null)
                    Log.Debug("Registered singleton '{tn}' from attribute (as {in})", _base(t).ShortDisplayName(),
                              sa.ServiceType.ShortDisplayName());
            }
        }
    }

    private Type _base(Type type)
    {
        if (!type.IsConstructedGenericType)
            return type;

        return type.GetGenericTypeDefinition();
    }

    public bool Exists<TItem>(TItem item)
    {
        return _db.ItemExists(item);
    }

    /// <summary>
    ///     Requires an app-level component
    /// </summary>
    [DebuggerStepThrough]
    [DebuggerHidden]
    public static TService Require<TService>() where TService : class
    {
        return Resolve<TService>() ??
               throw new RequiredComponentMissingException($"Component '{typeof(TService)}' is required but is not a registered" +
                                                           $" service, component, or resource.");
    }

    /// <summary>
    ///     Resolves an app-level component
    /// </summary>
    [DebuggerStepThrough]
    [DebuggerHidden]
    public static TService? Resolve<TService>() where TService : class
    {
        return Instance?.Components?.Resolve<TService>();
    }

    /// <summary>
    ///     Invoked when an unhandled exception has occured off the primary thread
    /// </summary>
    [DebuggerStepThrough]
    [DebuggerHidden]
    public void Unhandled(Exception e)
    {
        Log.Error(e, "Unhandled exception: {e}", e);
        ExceptionDispatchInfo.Capture(e).Throw();
        throw new Exception("Unhandled exception in handler:  {e}", e);
    }

    public static void RunSafe(Action action)
    {
        action.Required();
        if (Current is { Dispatcher: var d })
        {
            if (d.CheckAccess())
                action();
            else
                d.Invoke(action);
        }
        else
        {
            action();
        }
    }
}