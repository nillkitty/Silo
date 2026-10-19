using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Silo.Storage;
using Silo.Ui.Windows;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     The Unicode Explorer: search, filter and page through every character, see everything known about one (name, category, block, script, tags,
///     every encoding and Alt code), collect characters in a tray, and save the tray to a file or to the Silo (constants, hotkeys).
/// </summary>
/// <remarks>
///     Built in code, like the rest of the app's UI. The class is split by area: this file is the frame and the always-on-top handling,
///     <c>.Search</c> the search, filters and paging, <c>.Inspector</c> the character details, <c>.Tray</c> the collection and files, <c>.Silo</c> the
///     constants and hotkeys.
/// </remarks>
public sealed partial class UnicodeExplorerView : UserControl
{
    private const string AlwaysOnTopKey = "unicodeexplorer:alwaysontop";

    private readonly CheckBox      _alwaysOnTop = new() { Content = "Always on top", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock     _status      = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DispatcherTimer _statusClear = new() { Interval = TimeSpan.FromSeconds(6) };

    private readonly TabControl _bottomTabs = new() { Padding = new Thickness(4), Margin = new Thickness(0) };
    private readonly TabItem    _collectionTab = new() { Header = "Collection" };
    private readonly TabItem    _savedTab      = new() { Header = "Saved" };
    private SavedItemsPanel     _saved = null!;

    private bool _topmostWanted;
    private bool _topmostLoading;

    public UnicodeExplorerView()
    {
        _statusClear.Tick += (_, _) =>
                             {
                                 _statusClear.Stop();
                                 _status.Text = string.Empty;
                             };

        Content = BuildLayout();

        Loaded   += OnLoaded;
        Unloaded += (_, _) => UnhookSilo();
        PresentationSource.AddSourceChangedHandler(this, (_, _) => ApplyAlwaysOnTop());
        IsVisibleChanged += (_, _) => ApplyAlwaysOnTop();
    }

    // ---- frame ---------------------------------------------------------------------------------------------------

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(6) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                           // search + filters
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 }); // browser + inspector
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                           // splitter
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(210), MinHeight = 110 });     // collection and saved
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                           // status

        var top = BuildSearchBar();
        Grid.SetRow(top, 0);
        root.Children.Add(top);

        var middle = new Grid();
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340), MinWidth = 220 });

        var browser = BuildBrowser();
        Grid.SetColumn(browser, 0);
        middle.Children.Add(browser);

        var vsplit = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        vsplit.SetResourceReference(BackgroundProperty, "Theme.Border");
        Grid.SetColumn(vsplit, 1);
        middle.Children.Add(vsplit);

        var inspector = BuildInspector();
        Grid.SetColumn(inspector, 2);
        middle.Children.Add(inspector);

        Grid.SetRow(middle, 1);
        root.Children.Add(middle);

        var hsplit = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        hsplit.SetResourceReference(BackgroundProperty, "Theme.Border");
        Grid.SetRow(hsplit, 2);
        root.Children.Add(hsplit);

        _collectionTab.Content = BuildTray();
        _saved                 = BuildSaved();
        _savedTab.Content      = _saved;
        _bottomTabs.Items.Add(_collectionTab);
        _bottomTabs.Items.Add(_savedTab);
        Grid.SetRow(_bottomTabs, 3);
        root.Children.Add(_bottomTabs);

        _status.Margin = new Thickness(2, 4, 2, 0);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        Grid.SetRow(_status, 4);
        root.Children.Add(_status);

        return root;
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
        _alwaysOnTop.Checked   += (_, _) => SetAlwaysOnTop(true);
        _alwaysOnTop.Unchecked += (_, _) => SetAlwaysOnTop(false);
        ApplyAlwaysOnTop();

        _ = StartAsync();
    }

    private bool _loadedOnce;

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

    // ---- always on top ---------------------------------------------------------------------------------------------

    /// <summary>The window the explorer is in right now, when that is a floating one of its own (not the main window the dock area is in).</summary>
    private Window? FloatingWindow
    {
        get
        {
            var w = Window.GetWindow(this);
            return w is not null && !ReferenceEquals(w, Application.Current?.MainWindow) ? w : null;
        }
    }

    private void LoadAlwaysOnTopChoice()
    {
        try
        {
            _topmostLoading = true;
            _topmostWanted  = Stored.At<bool>(AlwaysOnTopKey).Get();
            _alwaysOnTop.IsChecked = _topmostWanted;
        }
        catch (Exception)
        {
            // no Silo open or the value is unreadable: off
        }
        finally
        {
            _topmostLoading = false;
        }
    }

    private void SetAlwaysOnTop(bool on)
    {
        if (_topmostLoading) return;

        _topmostWanted = on;
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
    ///     Makes the window the explorer floats in stay above others (or not). The choice is the person's, but it only means something while
    ///     the explorer floats: docked in the main window the box is disabled, and when it is floated again the choice is applied to the new window.
    /// </summary>
    private void ApplyAlwaysOnTop()
    {
        var floating = FloatingWindow;
        _alwaysOnTop.IsEnabled = floating is not null;
        _alwaysOnTop.ToolTip   = floating is null
                                     ? "Drag the Unicode Explorer out of the main window to float it; then it can stay above other windows."
                                     : "Keep this window above other windows (including other applications).";

        if (floating is not null && floating.Topmost != _topmostWanted) floating.Topmost = _topmostWanted;
    }
}
