using System.Collections.ObjectModel;
using System.Media;
using System.Reflection;
using System.Windows;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Win32;
using Serilog;
using Serilog.Events;
using Silo.Commanding;
using Silo.Connectors;
using Silo.Model;
using Silo.Ui;
using Silo.Ui.Windows;
using Telefrag.Collections;
using Telefrag.DI;
using Telefrag.Exceptions;

namespace Silo;

/// <summary>
/// ViewModel for the application
/// </summary>
public partial class App : Application, IContainerHost
{
    private readonly FragTable<OpenSilo, string> _silos = [];

    /// <summary>
    /// Application level logger
    /// </summary>
    protected ILogger Logger { get; } = Log.ForContext<App>();

    /// <summary>
    /// In-memory cache of logs
    /// </summary>
    public ObservableCollection<Serilog.Events.LogEvent> Logs { get; } = [];

    /// <summary>
    /// Gets a collection of the open Silos
    /// </summary>
    public IEnumerable<OpenSilo> OpenSilos => _silos.Values;

    /// <summary>
    /// Application-level components container
    /// </summary>
    public Container Components { get; } = new(nameof(App));

    /// <summary>
    /// File -> New Silo
    /// </summary>
    public static AppCommand NewSiloCommand => new NewSiloCommand();

    /// <summary>
    /// File -> Open Silo
    /// </summary>
    public static AppCommand OpenSiloCommand => new OpenSiloCommand();

    /// <summary>
    /// File -> Exit
    /// </summary>
    public static new AppCommand ExitCommand => new AppExitCommand();

    /// <summary>
    /// Help -> About
    /// </summary>
    public static AppCommand About => new AppAboutCommand();

    /// <summary>
    /// Gets all logs formatted to a single buffer
    /// </summary>
    public string GetLogs()
    {
        StringBuilder sb = new(Logs.Count * 10);
        foreach (Serilog.Events.LogEvent v in Logs)
        {
            sb.AppendLine($"{v.Timestamp} [{v.Level}] {v.RenderMessage()}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Creates a new Silo file at a path chosen by the user via modal file save dialog.
    /// </summary>
    public OpenSilo? NewSilo()
    {
        var s = new SaveFileDialog()
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


    public static App? Instance => Application.Current as App;

    public OpenSilo? ActiveSilo
    {
        get;
        set
        {
            field = value;
            ActiveSiloChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler?           ActiveSiloChanged;
    public event EventHandler<OpenSilo>? SiloOpened;
    public event EventHandler<OpenSilo>? SiloOpening;
    public event EventHandler<OpenSilo>? SiloClosing;
    public event EventHandler?           SiloClosed;

    internal void NotifySiloOpening(object sender, OpenSilo o) =>
        SiloOpening?.Invoke(sender, o);

    internal void NotifySiloOpened(object sender, OpenSilo o) =>
        SiloOpened?.Invoke(sender, o);

    internal void NotifySiloClosing(object sender, OpenSilo o) =>
        SiloClosing?.Invoke(sender, o);

    internal void NotifySiloClosed(object sender, OpenSilo o) =>
        SiloClosed?.Invoke(sender, EventArgs.Empty);


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

    private IDatabaseProvider _db => ActiveSilo?.Data;

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

        base.OnStartup(e);
    }

    private void _configLogging()
    {
        var l = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console().WriteTo.Debug().CreateLogger();

        Log.Logger = l;
        Serilog.Debugging.SelfLog.Enable(msg => System.Diagnostics.Trace.WriteLine($"[Serilog]: {msg}"));
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
        a.Distinct().ToList().ForEach(x => DiscoverRegistrations(x, false));
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

            var ca = t.GetCustomAttribute<RegisterAttribute>();
            if (ca is { } aa)
            {
                var r = Components.RegisterSingleton(aa.ServiceType, t, aa.Key, null,
                                                     aa.IsSingleton ? ComponentLifetime.Singleton : ComponentLifetime.Transient);
                if (r != null)
                {
                    Log.Debug("Registered component '{tn}' from attribute (as {in})", _base(t).ShortDisplayName(),
                              aa.ServiceType.ShortDisplayName());
                }
            }
        }
    }

    private Type _base(Type type)
    {
        if (!type.IsConstructedGenericType)
            return type;

        return type.GetGenericTypeDefinition();
    }

    public bool Exists<TItem>(TItem item) => _db.ItemExists(item);

    /// <summary>
    /// Requires an app-level component
    /// </summary>
    public static TService Require<TService>() where TService : class
    {
        return Resolve<TService>() ??
               throw new RequiredComponentMissingException($"Component '{typeof(TService)}' is required but is not a registered" +
                                                           $" service, component, or resource.");
    }

    /// <summary>
    /// Resolves an app-level component
    /// </summary>
    public static TService? Resolve<TService>() where TService : class
    {
        return App.Instance?.Components?.Resolve<TService>();
    }

    /// <summary>
    /// Invoked when an unhandled exception has occured off the primary thread
    /// </summary>
    public void Unhandled(Exception exception)
    {
        throw exception;
    }

    public static void RunSafe(Action action)
    {
        action.Required();
        if (App.Current is { Dispatcher: var d })
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
};