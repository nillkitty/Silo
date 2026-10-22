using AvalonDock.Layout;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Silo.Storage;
using Silo.Ui.Windows;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     The Unicode Explorer: search, filter and scroll through every character, see everything known about one (name, category, block, script, tags,
///     every encoding and Alt code), collect characters in a tray, and save the tray to a file or to the Silo (constants, hotkeys).
/// </summary>
/// <remarks>
///     Built in code, like the rest of the app's UI. The class is split by area: this file is the frame and the always-on-top handling,
///     <c>.Search</c> the search, filters and selection (the list itself is the virtualizing <c>ResultsView</c>), <c>.Inspector</c> the character details,
///     <c>.Tray</c> the collection and files, <c>.Silo</c> the constants and hotkeys, <c>.Colors</c> the color marks and view choices, <c>.Generate</c> the
///     text styles.
/// </remarks>
public sealed partial class UnicodeExplorerView : UserControl
{
    private const string AlwaysOnTopKey = "unicodeexplorer:alwaysontop";

    private readonly TextBlock     _status      = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DispatcherTimer _statusClear = new() { Interval = TimeSpan.FromSeconds(6) };

    private readonly TabControl _bottomTabs = new() { Padding = new Thickness(4), Margin = new Thickness(0) };
    private readonly TabItem    _collectionTab = new() { Header = "Collection" };
    private readonly TabItem    _generateTab   = new() { Header = "Generate" };
    private readonly TabItem    _savedTab      = new() { Header = "Saved" };
    private SavedItemsPanel     _saved = null!;

    private Grid         _rootGrid = null!;   // search bar, browser and details, collection tabs, status
    private Grid         _middle   = null!;   // browser | splitter | details
    private UIElement    _browser  = null!;
    private UIElement    _inspector = null!;
    private GridSplitter _vsplit   = null!;
    private GridSplitter _hsplit   = null!;

    private bool _topmostWanted;

    public UnicodeExplorerView()
    {
        _statusClear.Tick += (_, _) =>
                             {
                                 _statusClear.Stop();
                                 _status.Text = string.Empty;
                             };

        Content = BuildLayout();

        PreviewKeyDown += OnViewKeyDown;
        Loaded   += OnLoaded;
        Unloaded += (_, _) => UnhookSiloWhenHidden();

        // the collection may be showing in a pane of its own while the explorer is not: either one showing keeps the Silo hooked
        _trayRoot.Loaded   += OnLoaded;
        _trayRoot.Unloaded += (_, _) => UnhookSiloWhenHidden();
        PresentationSource.AddSourceChangedHandler(this, (_, _) => ApplyAlwaysOnTop());
        IsVisibleChanged += (_, _) => ApplyAlwaysOnTop();
    }

    // ---- frame ---------------------------------------------------------------------------------------------------

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(6) };
        _rootGrid = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                           // search
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 }); // browser + inspector
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                           // splitter
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DefaultBottomHeight), MinHeight = 110 }); // collection and saved
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                           // status

        var top = BuildSearchBar();
        Grid.SetRow(top, 0);
        root.Children.Add(top);

        var middle = new Grid();
        _middle = middle;
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DefaultInspectorWidth), MinWidth = 220 });

        _browser = BuildBrowser();
        Grid.SetColumn(_browser, 0);
        middle.Children.Add(_browser);

        _vsplit = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        _vsplit.SetResourceReference(BackgroundProperty, "Theme.Border");
        Grid.SetColumn(_vsplit, 1);
        middle.Children.Add(_vsplit);

        _inspector = BuildInspector();
        Grid.SetColumn(_inspector, 2);
        middle.Children.Add(_inspector);

        Grid.SetRow(middle, 1);
        root.Children.Add(middle);

        _hsplit = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        _hsplit.SetResourceReference(BackgroundProperty, "Theme.Border");
        Grid.SetRow(_hsplit, 2);
        root.Children.Add(_hsplit);

        _trayRoot              = (FrameworkElement)BuildTray(); // a DockPanel
        _trayHome.Child        = _trayRoot;
        _collectionTab.Content = _trayHome; // the collection can be undocked into a pane of its own (see .Dock)
        _collectionTab.ContextMenu = new ContextMenu();
        _collectionTab.ContextMenuOpening += (_, _) =>
                                             {
                                                 _collectionTab.ContextMenu!.Items.Clear();
                                                 _collectionTab.ContextMenu.Items.Add(UiKit.Item("Undock Collection", UndockCollection, CanUndockCollection,
                                                                                                 "Move the collection into a pane of its own beside the explorer; drag it anywhere, close it to put it back here"));
                                             };
        _saved                 = BuildSaved();
        _savedTab.Content      = _saved;
        _generateTab.Content   = BuildGenerate();
        _bottomTabs.Items.Add(_collectionTab);
        _bottomTabs.Items.Add(_generateTab);
        _bottomTabs.Items.Add(_savedTab);
        Grid.SetRow(_bottomTabs, 3);
        root.Children.Add(_bottomTabs);

        _status.Margin = new Thickness(2, 4, 2, 0);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        Grid.SetRow(_status, 4);
        root.Children.Add(_status);

        // the menu bar is above everything, edge to edge
        var menu = BuildMenuBar();
        var frame = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        frame.Children.Add(menu);
        frame.Children.Add(root);
        return frame;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookSilo(); // every time it is shown: it lets go again when it is not (see Unloaded)
        if (_loadedOnce)
        {
            ApplyAlwaysOnTop();
            return;
        }

        _loadedOnce = true;
        LoadAlwaysOnTopChoice();
        ApplyAlwaysOnTop();

        _ = StartAsync();
    }

    private bool _loadedOnce;

    /// <summary>
    ///     Lets go of the Silo once neither the explorer nor its (undocked) collection is showing. Checked after the layout settles: moving the
    ///     collection between its tab and its pane unloads and loads it again.
    /// </summary>
    private void UnhookSiloWhenHidden()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                                                          {
                                                              if (!IsLoaded && !_trayRoot.IsLoaded) UnhookSilo();
                                                          });
    }

    /// <summary>Shows a message in the status line for a few seconds.</summary>
    private void Say(string message)
    {
        _status.Text = message;
        _statusClear.Stop();
        _statusClear.Start();
    }

    private void Copy(string text, string what)
    {
        Say(UiKit.TryCopy(text) ? $"Copied {what}: {Shorten(text)}" : "The clipboard is busy; nothing was copied.");
    }

    private static string Shorten(string text, int max = 60)
    {
        string one = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return one.Length <= max ? one : one[..(max - 1)] + "\u2026";
    }

    // ---- floating and always on top ------------------------------------------------------------------------------

    /// <summary>The window the explorer is in right now, when that is a floating one of its own (not the main window the dock area is in).</summary>
    private Window? FloatingWindow
    {
        get
        {
            var w = Window.GetWindow(this);
            return w is not null && !ReferenceEquals(w, Application.Current?.MainWindow) ? w : null;
        }
    }

    /// <summary>The explorer's pane in the main window's dock area; null when the tool is not hosted there.</summary>
    private LayoutAnchorable? Pane => ToolBase.PaneTools().OfType<UnicodeExplorerTool>().FirstOrDefault()?.GetPane();

    /// <summary>Whether the explorer is floating in a window of its own.</summary>
    private bool IsFloating => FloatingWindow is not null || Pane?.IsFloating == true;

    private void ToggleFloating()
    {
        if (Pane is not { } pane) return;

        if (IsFloating) pane.Dock();
        else pane.Float();
    }

    /// <summary>Tool &gt; Exit: closes the explorer (its pane or its window).</summary>
    private void ExitExplorer()
    {
        var tool = ToolBase.PaneTools().OfType<UnicodeExplorerTool>().FirstOrDefault();
        if (tool is { IsChecked: true }) tool.Toggle();
        else if (Window.GetWindow(this) is { } w && !ReferenceEquals(w, Application.Current?.MainWindow)) w.Close();
    }

    /// <summary>View &gt; Reset Position &amp; State: the floating window at its starting size, in the middle of the main window, normal and not on top.</summary>
    private void ResetPositionAndState()
    {
        const double width = 900, height = 640;
        var main = Application.Current?.MainWindow;
        double left = main is null ? 100 : main.Left + Math.Max(0, (main.ActualWidth - width) / 2);
        double top  = main is null ? 100 : main.Top + Math.Max(0, (main.ActualHeight - height) / 2);

        if (Pane is { } pane)
        {
            pane.FloatingWidth  = width;
            pane.FloatingHeight = height;
            pane.FloatingLeft   = left;
            pane.FloatingTop    = top;
        }

        if (FloatingWindow is { } w)
        {
            w.WindowState = WindowState.Normal;
            w.Width       = width;
            w.Height      = height;
            w.Left        = left;
            w.Top         = top;
        }

        SetAlwaysOnTop(false);
        Say("The window is back at its starting size and place.");
    }

    private void LoadAlwaysOnTopChoice()
    {
        try
        {
            _topmostWanted = Stored.At<bool>(AlwaysOnTopKey).Get();
        }
        catch (Exception)
        {
            // no Silo open or the value is unreadable: off
        }
    }

    /// <summary>The person's choice to keep the explorer above other windows. It floats in a window of its own for that; the choice is kept.</summary>
    private void SetAlwaysOnTop(bool on)
    {
        _topmostWanted = on;
        if (on && !IsFloating) ToggleFloating();
        ApplyAlwaysOnTop();

        try
        {
            var stored = Stored.At<bool>(AlwaysOnTopKey);
            if (stored.IsAvailable) stored.Set(on);
        }
        catch (Exception)
        {
            // the choice still holds for this session
        }
    }

    /// <summary>
    ///     Makes the window the explorer floats in stay above others (or not). The choice only means something while the explorer floats; when it is
    ///     floated again the choice is applied to the new window.
    /// </summary>
    private void ApplyAlwaysOnTop()
    {
        var floating = FloatingWindow;
        if (floating is not null && floating.Topmost != _topmostWanted) floating.Topmost = _topmostWanted;
    }
}
