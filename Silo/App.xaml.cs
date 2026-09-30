using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Media;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using Silo.Commanding;
using Silo.Connectors;
using Silo.Design;
using Silo.Extensions;
using Silo.Model;
using Silo.Theming;
using Silo.Ui;
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


    public static App? Instance => Current as App;

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
    ///     Gets a collection of the open Silos.  This mirrors the actual live registry of
    ///     open silos (<see cref="OpenSilo.OpenSilos" />) rather than a separate, never-populated
    ///     tracking collection, so callers - such as <see cref="Silo.Commanding.AppExitCommand" />,
    ///     which shuts each one down on exit, and the connection diagram tool - see real data.
    /// </summary>
    public ObservableCollection<OpenSilo> OpenSilos => OpenSilo.OpenSilos;

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

    public event EventHandler?           ActiveSiloChanged;
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
        DesignMenu.Register();

        base.OnStartup(e);
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
        a.Distinct().ToList().ForEach(x => DiscoverRegistrations(x));
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
    public static TService Require<TService>() where TService : class
    {
        return Resolve<TService>() ??
               throw new RequiredComponentMissingException($"Component '{typeof(TService)}' is required but is not a registered" +
                                                           $" service, component, or resource.");
    }

    /// <summary>
    ///     Resolves an app-level component
    /// </summary>
    public static TService? Resolve<TService>() where TService : class
    {
        return Instance?.Components?.Resolve<TService>();
    }

    /// <summary>
    ///     Invoked when an unhandled exception has occured off the primary thread
    /// </summary>
    public void Unhandled(Exception e)
    {
        throw new Exception("Unhandled exception in handler", e);
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