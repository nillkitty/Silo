using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;
using Silo.Model;

namespace Silo.Ui;

/// <summary>
///     The status bar at the bottom of the main window. From the left: the status line (the last result, or what is being
///     loaded
///     right now, with a progress bar while something is), a place for the last event that fired and one for the running
///     tasks (both
///     empty until something reports them: see <see cref="AppStatus" />), then the active Silo's name, how many Silos are
///     open and the
///     process's memory use with the garbage collector's counts (right-click for "Collect garbage now").
/// </summary>
/// <remarks>
///     A control that holds the bar rather than a <see cref="StatusBar" /> subclass: the theme's implicit style for
///     <c>StatusBar</c> matches the exact type only. The bar listens to the app only while it is loaded.
/// </remarks>
public sealed class AppStatusBar : UserControl
{
    private const double MiB = 1024.0 * 1024.0;

    private static readonly Brush ErrorBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C)));

    private readonly ProgressBar _busy = new()
                                         {
                                             IsIndeterminate   = true, Width = 70, Height = 8, Margin = new Thickness(0, 0, 8, 0),
                                             VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed
                                         };

    private readonly TextBlock _count  = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _event  = Muted("Event: -");
    private readonly TextBlock _memory = new() { VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _silo   = new() { VerticalAlignment = VerticalAlignment.Center };

    private readonly TextBlock _status = new()
                                         {
                                             TextTrimming      = TextTrimming.CharacterEllipsis,
                                             VerticalAlignment = VerticalAlignment.Center
                                         };

    private readonly TextBlock       _tasks = Muted("Tasks: -");
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private          bool            _listening;

    private OpenSilo? _watched;

    private UIElement Build()
    {
        var grid = new Grid { Margin                            = new Thickness(0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 5; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var statusCell = new DockPanel { Margin = new Thickness(8, 2, 8, 2), LastChildFill = true };
        DockPanel.SetDock(_busy, Dock.Left);
        statusCell.Children.Add(_busy);
        statusCell.Children.Add(_status);
        Place(grid, statusCell, 0, false);

        _event.MaxWidth     = 300;
        _event.TextTrimming = TextTrimming.CharacterEllipsis;
        Place(grid, Cell(_event), 1, true);
        Place(grid, Cell(_tasks), 2, true);
        Place(grid, Cell(_silo),  3, true);
        Place(grid, Cell(_count), 4, true);

        var memory  = Cell(_memory);
        var menu    = new ContextMenu();
        var collect = new MenuItem { Header = "Collect garbage now" };
        collect.Click += (_, _) => CollectGarbage();
        menu.Items.Add(collect);
        memory.ContextMenu = menu;
        Place(grid, memory, 5, true);

        var bar = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = grid, MinHeight = 22 };
        bar.SetResourceReference(Border.BackgroundProperty,  "Theme.BackgroundAlt");
        bar.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        return bar;
    }

    private static void Place(Grid grid, UIElement element, int column, bool divider)
    {
        if (divider)
        {
            var line = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = element };
            line.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
            element = line;
        }

        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static FrameworkElement Cell(TextBlock text)
    {
        text.Margin = new Thickness(8, 2, 8, 2);
        return text;
    }

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        return t;
    }

    private void StartListening()
    {
        if (_listening) return;

        _listening                           =  true;
        AppStatus.Instance.Changed           += OnStatusChanged;
        OpenSilo.OpenSilos.CollectionChanged += OnSilosChanged;
        if (App.Instance is { } app) app.ActiveSiloChanged += OnActiveSiloChanged;

        WatchActiveSilo();
        RefreshStatus();
        RefreshSilos();
        RefreshMemory();
        _timer.Start();
    }

    private void StopListening()
    {
        if (!_listening) return;

        _listening = false;
        _timer.Stop();
        AppStatus.Instance.Changed           -= OnStatusChanged;
        OpenSilo.OpenSilos.CollectionChanged -= OnSilosChanged;
        if (App.Instance is { } app) app.ActiveSiloChanged -= OnActiveSiloChanged;
        if (_watched is not null) _watched.StateChanged    -= OnSiloStateChanged;
        _watched = null;
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        RefreshStatus();
    }

    private void OnSilosChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshSilos();
    }

    private void OnActiveSiloChanged(object? sender, EventArgs e)
    {
        WatchActiveSilo();
    }

    private void OnSiloStateChanged(object? sender, EventArgs e)
    {
        RefreshSilos();
    }

    private void WatchActiveSilo()
    {
        if (_watched is not null) _watched.StateChanged -= OnSiloStateChanged;
        _watched = App.Instance?.ActiveSilo;
        if (_watched is not null) _watched.StateChanged += OnSiloStateChanged;
        RefreshSilos();
    }

    private void RefreshStatus()
    {
        var status = AppStatus.Instance;
        var kind   = status.Kind;

        _status.Text     = status.Text;
        _status.ToolTip  = status.Detail;
        _busy.Visibility = kind == StatusKind.Busy ? Visibility.Visible : Visibility.Collapsed;

        if (kind == StatusKind.Error) _status.Foreground = ErrorBrush;
        else _status.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");

        if (status.LastEvent is { } evt)
        {
            _event.Text    = "Event: " + evt;
            _event.ToolTip = $"{evt}  ({status.LastEventAtUtc.ToLocalTime():T})";
            _event.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        }
        else
        {
            _event.Text    = "Event: -";
            _event.ToolTip = "The last event that fired (nothing reports events yet)";
            _event.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        }

        if (status.RunningTasks is { } tasks)
        {
            _tasks.Text    = "Tasks: " + tasks;
            _tasks.ToolTip = tasks;
            _tasks.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        }
        else
        {
            _tasks.Text    = "Tasks: -";
            _tasks.ToolTip = "The running tasks (nothing reports tasks yet)";
            _tasks.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        }
    }

    private void RefreshSilos()
    {
        if (_watched is { } silo)
        {
            string name  = silo.Name ?? "(untitled)";
            string flags = silo.IsUnsynced ? " (not uploaded)" : string.Empty;
            _silo.Text    = $"{name}{(silo.IsDirty ? "*" : string.Empty)}{flags}";
            _silo.ToolTip = silo.FilePath is { Length: > 0 } path ? $"Active Silo: {path}" : "Active Silo";
            _silo.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        }
        else
        {
            _silo.Text    = "No active Silo";
            _silo.ToolTip = "Create, open or select a Silo";
            _silo.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        }

        int n = OpenSilo.OpenSilos.Count;
        _count.Text = n switch { 0 => "No Silos", 1 => "1 Silo", _ => $"{n} Silos" };
        _count.ToolTip = n == 0
                             ? "No Silos are open"
                             : string.Join(Environment.NewLine, OpenSilo.OpenSilos.Select(s => s.Name ?? "(untitled)"));
    }

    private void RefreshMemory()
    {
        try
        {
            long workingSet = Environment.WorkingSet;
            long managed    = GC.GetTotalMemory(false);
            int  gen0       = GC.CollectionCount(0);
            int  gen1       = GC.CollectionCount(1);
            int  gen2       = GC.CollectionCount(2);

            _memory.Text = $"{workingSet / MiB:0} MB  |  managed {managed / MiB:0} MB  |  GC {gen0}/{gen1}/{gen2}";

            var  info = GC.GetGCMemoryInfo();
            long priv;
            using (var process = Process.GetCurrentProcess())
            {
                priv = process.PrivateMemorySize64;
            }

            _memory.ToolTip = string.Join(Environment.NewLine, $"Working set        {workingSet / MiB,10:N1} MB",
                                          $"Private bytes      {priv / MiB,10:N1} MB", $"Managed heap       {managed / MiB,10:N1} MB",
                                          $"  at last GC       {info.HeapSizeBytes / MiB,10:N1} MB (fragmented {info.FragmentedBytes / MiB:N1} MB)",
                                          $"  committed        {info.TotalCommittedBytes / MiB,10:N1} MB",
                                          $"Allocated overall  {GC.GetTotalAllocatedBytes() / MiB,10:N1} MB",
                                          $"Collections        gen0 {gen0}, gen1 {gen1}, gen2 {gen2}",
                                          $"Last GC            #{info.Index} (gen {info.Generation}), {info.PauseTimePercentage:0.#}% of time paused",
                                          $"Mode               {(GCSettings.IsServerGC ? "server" : "workstation")}, {GCSettings.LatencyMode}",
                                          string.Empty, "Right-click: Collect garbage now");
        }
        catch (Exception ex)
        {
            _memory.Text = "memory: n/a";
            Log.Debug(ex, "Reading the process's memory use failed");
        }
    }

    private void CollectGarbage()
    {
        long before = GC.GetTotalMemory(false);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long after = GC.GetTotalMemory(false);

        AppStatus.Instance.Report($"Collected garbage: managed heap {before / MiB:0.#} MB -> {after / MiB:0.#} MB");
        RefreshMemory();
    }

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public AppStatusBar()
    {
        Content     =  Build();
        _timer.Tick += (_, _) => RefreshMemory();

        Loaded   += (_, _) => StartListening();
        Unloaded += (_, _) => StopListening();
    }
}