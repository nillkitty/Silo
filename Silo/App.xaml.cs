using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Media;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Serializer.Xml;
using AvalonDock.Themes;
using Microsoft.Win32;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using Silo.Assets;
using Silo.Bindings;
using Silo.Commanding;
using Silo.Connectors;
using Silo.DbModel.Sqlite;
using Silo.Design;
using Silo.Extensions;
using Silo.Hosting;
using Silo.Hotkeys;
using Silo.Model;
using Silo.Providers;
using Silo.Providers.Data.Validation;
using Silo.Providers.Format;
using Silo.Settings;
using Silo.Theming;
using Silo.Tools.UnicodeExplorer;
using Silo.Ui;
using Silo.Ui.Windows;
using Silo.ViewModel;
using Telefrag.DI;
using Telefrag.Exceptions;

namespace Silo;

/// <summary>
///     ViewModel for the application
/// </summary>
public partial class App : Application, IContainerHost
{
    // set while this thread is the one discovering the registrations, so what it does with the container does not wait for itself
    [ThreadStatic] private static bool _registering;

    private readonly Container _components   = new(nameof(App));
    private readonly object    _registerGate = new();

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

    public static IConnection? LocalConnection { get; } = new LocalConnection();

    /// <summary>
    ///     In-memory cache of logs
    /// </summary>
    public ObservableCollection<LogEvent> Logs { get; } = [];

    /// <summary>
    ///     File -> New Silo
    /// </summary>
    public static AppCommand NewSiloCommand => new NewSiloCommand();

    /// <summary>
    ///     The Explorer nodes that belong to the app rather than to a Silo: the ones tools contribute
    ///     (see <see cref="INodeProvider" />). A Silo's own are in <see cref="OpenSilo.Nodes" />.
    /// </summary>
    public NodeHost Nodes { get; } = new();

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

    /// <summary>Whether <see cref="RegistrationsReady" /> has completed, i.e. asking for a component will not wait.</summary>
    public bool RegistrationsComplete => RegistrationsReady.IsCompleted;

    /// <summary>
    ///     Completes when the registrations declared by attributes (<c>[Singleton]</c>, <c>[Transient]</c>, <c>[Register]</c>)
    ///     are in
    ///     the container. Started in <see cref="OnStartup" />; completed already when nothing was started. Never faults: a
    ///     failure
    ///     is logged and the container keeps whatever was registered.
    /// </summary>
    public Task RegistrationsReady { get; private set; } = Task.CompletedTask;


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

    public static Version? Version { get; } = Assembly.GetExecutingAssembly().GetName().Version;

    /// <summary>
    ///     Application level logger
    /// </summary>
    protected ILogger Logger { get; } = Log.ForContext<App>();

    private IDatabaseProvider _db => ActiveSilo?.Data;

    /// <summary>
    ///     Application-level components container. The registrations the app's attributes declare are discovered by reflection
    ///     in
    ///     the background at startup (see <see cref="RegistrationsReady" />); asking for the container -- and so for any
    ///     component
    ///     through <see cref="Resolve{TService}" /> or <see cref="Require{TService}" /> -- waits for that to finish if it has
    ///     not,
    ///     and costs nothing once it has. Code that does not need a component right away can avoid even that wait with
    ///     <see cref="ResolveAsync{TService}" /> or <see cref="WhenRegistered" />.
    /// </summary>
    public Container Components
    {
        get
        {
            WaitForRegistrations();
            return _components;
        }
    }

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
                    Filter = Formats.Of<SiloFileFormat>().Filter()
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


    /// <summary>
    ///     Says something in the status bar (<see cref="AppStatus" />): the last result, kept until the next. Safe from any
    ///     thread.
    ///     Always returns false, so a failing command can end with <c>return App.Status("...")</c>.
    /// </summary>
    public static bool Status(string text)
    {
        AppStatus.Instance.Report(text);
        return false;
    }

    /// <summary>Beeps and shows <paramref name="error" /> in the status bar, in red. Returns false.</summary>
    public bool InterError(string error)
    {
        Beep();
        AppStatus.Instance.Report(error, true);
        return false;
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

    /// <summary>
    ///     WPF matches an implicit style (Theme.Styles.xaml's <c>TargetType=Window</c> one) on the exact type only, so
    ///     MainWindow, FormWindow, ConnectionWindow and every other Window subclass would miss it and keep the stock white
    ///     background whatever the theme. Making that style the default <c>Style</c> of <see cref="Window" /> itself reaches
    ///     all of them. Must run before the first window is created.
    /// </summary>
    private void _themeDerivedWindows()
    {
        try
        {
            if (TryFindResource(typeof(Window)) is Style windowStyle)
                FrameworkElement.StyleProperty.OverrideMetadata(typeof(Window), new FrameworkPropertyMetadata(windowStyle));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not apply the themed Window style to window subclasses");
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _configLogging();

        // Exceptions that point at a bug (NotImplementedException, null dereferences...), caught or not: listed in the Problems
        // pane at View > User Level > Debug (Diagnostics/StrayExceptions.cs).
        global::Silo.Diagnostics.StrayExceptions.Install(this);

        // The reflection that finds the DI registrations, the action providers and the assemblies' resources is the slow part of
        // starting up and nothing on the way to the first window needs it: it runs in the background, and whatever does need a
        // component waits for it in Components (and so Resolve/Require).
        PreloadUiAssemblies();
        StartBackgroundDiscovery();
        AppTheme.Apply();
        _themeDerivedWindows();
        TitleBars.Install();
        AppDesignSettings.Apply();
        AppDesignLevel.Apply();
        SiloFileSettings.Apply();
        DesignMenu.Register();

        // Ctrl+Shift+hover: a pink tip showing what an element's right-click menu is built from (Commanding/ContextMenuInspector.cs)
        ContextMenuInspector.Start();
        ResourceIndex.Instance.BuildDictionary(); // the assemblies' resources are being indexed in the background

        // Shortcut keys kept in the Silo (the Unicode Explorer's hotkeys, for one): Windows hot keys that work in every application.
        GlobalHotkeyService.Current.Start();

        // Palettes (the Unicode Explorer's small always-visible windows of characters): the ones marked "Show on repo load" open with their Silo.
        PaletteService.Current.Start();

        // Data bindings ({bind:Name} in diagrams): sampled while their Silo is open.
        DataBindingService.Current.Start();

        // Working folders left behind by a crashed Silo process (those still in use are locked and skipped).
        _ = Task.Run(SiloArchive.SweepOrphans);

        base.OnStartup(e);

        // Later launches hand their documents to the stock instance through a named pipe.
        if (Launch.ServeRequests) SingleInstance.StartServer(HandleLaunchRequest);

        // The document this launch is for (decided in Program.Main -- double-click in Explorer, "silo.exe file.silo", a URL) opens
        // once the window is up.
        // The status bar says so from the start, not from when the window has settled (the open is queued for idle time).
        if (Launch.Target is { } target)
        {
            var opening = AppStatus.Instance.Begin("Opening " + Path.GetFileName(target.TrimEnd('/', '\\')));
            Dispatcher.BeginInvoke(new Action(async () => await Commanding.OpenSiloCommand.OpenPathAsync(target, opening)),
                                   DispatcherPriority.ApplicationIdle);
        }
        else if (Launch.Mode == LaunchMode.Stock && SiloFileSettings.Instance.ReopenLastSilo)
        {
            var reopening = RecentSilos.Instance.Items.FirstOrDefault() is { IsRemote: false, Exists: true } last
                                ? AppStatus.Instance.Begin("Reopening " + Path.GetFileName(last.Target))
                                : null;
            Dispatcher.BeginInvoke(new Action(async () => await ReopenLastSiloAsync(reopening)), DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>
    ///     Asks every registered tool that is an <see cref="INodeProvider" /> for the node it adds to the Explorer. Done once,
    ///     at startup, on the UI thread as soon as the registrations are discovered (the nodes appear in the Explorer then):
    ///     tools
    ///     belong to the app, not to a Silo.
    /// </summary>
    private void RegisterToolNodes()
    {
        try
        {
            foreach (var tool in Resolve<IEnumerable<ITool>>() ?? [])
                if (tool is INodeProvider provider)
                    Nodes.Add(provider);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Collecting the tools' Explorer nodes failed");
        }
    }

    /// <summary>
    ///     Starting with no document to open: reopens the Silo that was open last. Only a local file that is still there
    ///     and is an ordinary (non-Solo) Silo qualifies -- a URL or a Solo Silo would need a process of its own, which is
    ///     not something to start behind the person's back. Keeps people from ending up with several scattered Silos.
    /// </summary>
    private async Task ReopenLastSiloAsync(StatusOperation? operation = null)
    {
        try
        {
            if (RecentSilos.Instance.Items.FirstOrDefault() is not { IsRemote: false, Exists: true } last ||
                SiloProbe.Probe(last.Target).IsSolo)
            {
                operation?.Dispose();
                return;
            }

            await Commanding.OpenSiloCommand.OpenPathAsync(last.Target, operation);
        }
        catch (Exception ex)
        {
            operation?.Fail("Reopening the last Silo failed: " + ex.Message);
            Log.Warning(ex, "Reopening the last Silo failed");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        GlobalHotkeyService.Current.Stop();
        DataBindingService.Current.Stop();
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

    /// <summary>
    ///     Starts the startup reflection on background threads: the attribute-declared registrations (and, once they are in,
    ///     the
    ///     tools' Explorer nodes, on the UI thread), the action-provider scan and the index of the assemblies' resources.
    ///     Returns at once.
    /// </summary>
    /// <summary>
    ///     Loads the embedded assemblies the main window's XAML uses, here on the UI thread, before the background discovery
    ///     starts.
    /// </summary>
    /// <remarks>
    ///     Costura loads each embedded assembly from its bytes, and every such load gets a load context of its own. When two
    ///     threads
    ///     ask for the same assembly at once (the discovery's <c>GetCustomAttributes</c> loading a type that mentions
    ///     AvalonDock, and
    ///     the main window's BAML) each can get its own copy, and the window then fails with "[A]AvalonDock.DockingManager
    ///     cannot be
    ///     cast to [B]AvalonDock.DockingManager". Loaded once here, both threads find the same copy.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PreloadUiAssemblies()
    {
        _ = typeof(DockingManager).Assembly;
        _ = typeof(Vs2013DarkTheme).Assembly;
        _ = typeof(XmlLayoutSerializer).Assembly;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StartBackgroundDiscovery()
    {
        // Chosen here, on the calling thread: GetCallingAssembly means something else on a pool thread.
        var assemblies = new List<Assembly?>
                         {
                             Assembly.GetEntryAssembly(),
                             Assembly.GetCallingAssembly(),
                             Assembly.GetExecutingAssembly()
                         }.Where(x => x != null)
                          .Distinct()
                          .ToList();

        var dispatcher = Dispatcher;
        var clock      = Stopwatch.StartNew();

        // each step shows in the status bar while it runs (see AppStatus.Begin)
        var status         = AppStatus.Instance;
        var loadingParts   = status.Begin("Loading components");
        var indexingAssets = status.Begin("Indexing resources");
        var findingActions = status.Begin("Finding action providers");

        RegistrationsReady = Task.Run(() =>
                                      {
                                          _registering = true;
                                          try
                                          {
                                              foreach (var assembly in assemblies) DiscoverRegistrations(assembly);
                                              loadingParts.Done($"Components loaded in {clock.ElapsedMilliseconds} ms");
                                          }
                                          catch (Exception ex)
                                          {
                                              Log.Error(ex, "Discovering the component registrations failed");
                                              loadingParts.Fail("Loading components failed: " + ex.Message);
                                          }
                                          finally
                                          {
                                              _registering = false;
                                              Log.Debug("Component registrations discovered in {ms} ms", clock.ElapsedMilliseconds);
                                          }
                                      });

        // What is built from the registrations goes on from there, on the UI thread: the tools may make WPF objects.
        _ = RegistrationsReady.ContinueWith(_ =>
                                            {
                                                try
                                                {
                                                    dispatcher.BeginInvoke(new Action(RegisterToolNodes), DispatcherPriority.Loaded);
                                                }
                                                catch (Exception ex)
                                                {
                                                    Log.Debug(ex, "The tools' Explorer nodes could not be scheduled (shutting down?)");
                                                }
                                            }, TaskScheduler.Default);

        // Independent of the registrations; each does its reflection once, early, and is waited for only by whoever needs it.
        _ = Task.Run(ActionProviders.Prewarm)
                .ContinueWith(_ => findingActions.Done($"Action providers found in {clock.ElapsedMilliseconds} ms"), TaskScheduler.Default);
        _ = ResourceIndex.Instance.BuildAssembliesAsync()
                         .ContinueWith(_ => indexingAssets.Done($"Resources indexed in {clock.ElapsedMilliseconds} ms"),
                                       TaskScheduler.Default);
    }

    /// <summary>
    ///     Waits for the registrations to be discovered, if they are not yet. Not when this is the thread discovering
    ///     them.
    /// </summary>
    private void WaitForRegistrations()
    {
        var task = RegistrationsReady;
        if (task.IsCompleted || _registering) return;

        var waited = Stopwatch.StartNew();
        task.Wait();
        Log.Debug("Something needed a component and waited {ms} ms for the registrations to be discovered", waited.ElapsedMilliseconds);
    }

    /// <summary>
    ///     Runs <paramref name="action" /> on the UI thread once the registrations are discovered -- at once (but still queued
    ///     on the
    ///     dispatcher) if they already are. For work that wants a component but need not hold anything up.
    /// </summary>
    public static void WhenRegistered(Action action)
    {
        action.Required();
        if (Instance is not { } app)
        {
            action();
            return;
        }

        var dispatcher = app.Dispatcher;
        _ = app.RegistrationsReady.ContinueWith(_ => dispatcher.BeginInvoke(action), TaskScheduler.Default);
    }

    /// <summary>
    ///     As <see cref="Resolve{TService}" /> without holding the calling thread: waits for the registrations
    ///     asynchronously.
    /// </summary>
    public static async Task<TService?> ResolveAsync<TService>() where TService : class
    {
        if (Instance is not { } app) return null;

        await app.RegistrationsReady;
        return app._components.Resolve<TService>();
    }

    /// <summary>
    ///     Registers the components the exported types of <paramref name="a" /> declare with attributes. Startup does this for
    ///     the app's own assemblies in the background; call it for another one (a plug-in) whenever it is loaded.
    /// </summary>
    public void DiscoverRegistrations(Assembly? a, bool critical = false)
    {
        if (a is null) return;

        lock (_registerGate)
        {
            DiscoverRegistrationsCore(a, critical);
        }
    }

    private void DiscoverRegistrationsCore(Assembly a, bool critical)
    {
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

            var ca = t.GetCustomAttributes<RegisterAttribute>(true)?.FirstOrDefault();
            var ta = t.GetCustomAttributes<TransientAttribute>(true)?.FirstOrDefault();
            var sa = t.GetCustomAttributes<SingletonAttribute>(true)?.FirstOrDefault();
            if (ca is { } aa)
            {
                var r = _components.RegisterSingleton(aa.ServiceType, t, aa.Key, null,
                                                      aa.IsSingleton ? ComponentLifetime.Singleton : ComponentLifetime.Transient);
                if (r != null)
                    Log.Debug("Registered component '{tn}' from attribute (as {in})", _base(t).ShortDisplayName(),
                              aa.ServiceType.ShortDisplayName());
            }
            else if (ta != null)
            {
                var r = _components.RegisterSingleton(ta.ServiceType, t, ta.Name, null, ComponentLifetime.Transient);
                if (r != null)
                    Log.Debug("Registered transient '{tn}' from attribute (as {in})", _base(t).ShortDisplayName(),
                              ta.ServiceType.ShortDisplayName());
            }
            else if (sa != null)
            {
                var r = _components.RegisterSingleton(sa.ServiceType, t, sa.Name, null, ComponentLifetime.Singleton);
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

    public static OpenSilo? RequireActiveSilo()
    {
        if (Instance?.ActiveSilo is not { } silo)
        {
            Alert("Creating this item requires an open, active Silo");
            return null;
        }

        return silo;
    }

    /// <summary>
    ///     Tells the person something went wrong (or needs their attention) in a window whose text they can select and copy.
    ///     Safe to call from any thread: it is shown on the UI thread and returns when the person closes it. With no UI (a
    ///     command-line run) the message is only logged.
    /// </summary>
    /// <param name="message">What to say.</param>
    /// <param name="title">The window's title; the application's name when omitted.</param>
    public static void Alert(string message, string? title = null)
    {
        Log.Warning("Alert: {message}", message);

        if (Current is not { Dispatcher: var dispatcher })
        {
            Console.Error.WriteLine(message);
            return;
        }

        if (dispatcher.CheckAccess())
            AlertDialog.Show(message, title);
        else
            dispatcher.Invoke(() => AlertDialog.Show(message, title));
    }

    /// <summary>
    ///     Like <see cref="Alert(string, string?)" /> for an exception: <paramref name="what" /> (what was being done),
    ///     then the exception's message.
    /// </summary>
    public static void Alert(Exception ex, string what)
    {
        Alert($"{what}\n\n{ex.Message}");
    }

    /// <summary>
    ///     Resolves all registered implementations of <typeparamref name="TComponent" />, and
    ///     throws an exception if none are available.
    /// </summary>
    /// <typeparam name="TComponent">The service type being resolved.</typeparam>
    public static List<TComponent> RequireAny<TComponent>()
    {
        var tt = typeof(TComponent);
        if (ResolveAll<TComponent>() is not [_, ..] x)
            throw new RequiredComponentMissingException($"A required service, '{tt}', is unavilable.");

        return x;
    }

    /// <summary>
    ///     Resolves all registered implementations of <typeparamref name="TComponent" />
    /// </summary>
    public static List<TComponent> ResolveAll<TComponent>()
    {
        var ie = Resolve<IEnumerable<TComponent>>() ?? [];

        return ie.ToList();
    }
}