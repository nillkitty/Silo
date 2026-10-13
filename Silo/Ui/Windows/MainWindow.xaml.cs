using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AvalonDock.Layout;
using AvalonDock.Themes;
using Silo.Design;
using Silo.Hosting;
using Silo.Model;
using Silo.Nav;
using Silo.Remote;
using Silo.Settings;
using Silo.Theming;

namespace Silo;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>NavColumn's width while the nav pane is expanded - 300 matches its original XAML default.</summary>
    private static readonly GridLength _navExpandedWidth = new(300);

    /// <summary>
    ///     NavColumn's width while collapsed - wide enough for the header bar's toggle button to remain clickable (see the
    ///     Grid in MainWindow.xaml's nav cell), so re-expanding is always reachable, and for the rail's icons, which follow
    ///     the live "Icon Size" setting (icon + the rail button's padding and accent bar).
    /// </summary>
    private static GridLength NavCollapsedWidth => new(Math.Max(32, AppDesignSettings.Instance.IconSize + 16));

    /// <summary>
    ///     The top of the nav pane (NavTree and NavRail share it): "Recent" while no Silo is open, otherwise a node per
    ///     open Silo followed by Connections and Performance. Kept in step with the open Silos by <see cref="SyncNav" />.
    /// </summary>
    private readonly ObservableCollection<AppNavNode> _topRoots = [];

    /// <summary>
    ///     The bottom of the nav pane (NavTreeBottom and NavRailBottom): Inbox, Settings and Profile. They stand apart
    ///     from the Silos, so they are always there and always hug the bottom edge.
    /// </summary>
    private readonly IReadOnlyList<AppNavNode> _bottomRoots = AppNavTree.BuildBottomRoots();

    private readonly AppNavNode _recentNode      = AppNavTree.BuildRecent();
    private readonly AppNavNode _connectionsNode = AppNavTree.BuildConnections();
    private readonly AppNavNode _performanceNode = AppNavTree.BuildPerformance();

    /// <summary>The nav node (and name-change subscription) of each open Silo.</summary>
    private readonly Dictionary<OpenSilo, SiloNav> _siloNavs = new(ReferenceEqualityComparer.Instance);

    private sealed record SiloNav(AppNavNode Node, EventHandler Renamed);

    /// <summary>The node currently selected, in whichever of the two nav trees it is.</summary>
    private AppNavNode? _selectedNode;

    private bool _navSyncQueued;

    public App App => App.Instance!;

    public MainWindow()
    {
        InitializeComponent();

        NavTree.ItemsSource       = _topRoots;
        NavRail.ItemsSource       = _topRoots;
        NavTreeBottom.ItemsSource = _bottomRoots;
        NavRailBottom.ItemsSource = _bottomRoots;

        // The pane starts closed - only the icon gutter shows until the person opens it.
        NavCollapseToggle.IsChecked = true;
        ApplyNavState(true);

        SyncNav();
        OpenSilo.OpenSilos.CollectionChanged += OnOpenSilosChanged;

        // Ctrl+right-click the nav tree (in Author mode or above) for a pink design
        // menu with a live "Icon Size" slider - see Design/DesignMenu.cs.
        DesignMenu.EnsureItems(NavTree).Add(DesignMenuItem.Slider(
            "Icon Size", DesignLevel.Author,
            min: 12, max: 64,
            getValue: () => AppDesignSettings.Instance.IconSize,
            setValue: v => AppDesignSettings.Instance.IconSize = v,
            format: "{0:0} px"));

        // The collapsed rail's width tracks the icon size while the slider is dragged.
        AppDesignSettings.Instance.PropertyChanged += (_, e) =>
                                                      {
                                                          if (e.PropertyName == nameof(AppDesignSettings.IconSize) &&
                                                              NavCollapseToggle.IsChecked == true)
                                                              NavColumn.Width = NavCollapsedWidth;
                                                      };

        // AvalonDock draws its own chrome (tabs, title bars, splitters) from a theme object, not from the palette brushes,
        // so it is switched alongside the palette.
        ApplyDockTheme();
        AppTheme.Instance.PropertyChanged += (_, e) =>
                                             {
                                                 if (e.PropertyName == nameof(AppTheme.IsEffectivelyDark)) ApplyDockTheme();
                                             };

        AddFileDesignMenu();

        // Ctrl+N / Ctrl+O / Ctrl+S ... -- the keys shown beside the menu items (see Commanding/Shortcuts.cs).
        Commanding.Shortcuts.Install(this);

        // Drag a .silo file (or a link to one) onto the window to open it: a Solo Silo or a URL then gets a process of its own.
        AllowDrop =  true;
        DragOver  += OnDragOverSilo;
        Drop      += OnDropSilo;

        // Window title: "<file>[*] - Silo" for the active Silo; the star marks unsaved changes.
        App.ActiveSiloChanged += (_, _) =>
                                 {
                                     WatchActiveSilo();
                                     QueueNavSync();
                                 };
        WatchActiveSilo();
    }

    private void ApplyDockTheme()
    {
        DockHost.Theme = AppTheme.Instance.IsEffectivelyDark ? new Vs2013DarkTheme() : new Vs2013LightTheme();
    }

    /// <summary>Author-mode file settings, reached with Ctrl+right-click anywhere in the window (see <see cref="DesignMenu" />).</summary>
    private void AddFileDesignMenu()
    {
        var settings = SiloFileSettings.Instance;
        var items    = DesignMenu.EnsureItems(this);

        var policy = new ComboBox { ItemsSource = Enum.GetValues<ExternalChangePolicy>(), SelectedItem = settings.ConflictPolicy, MinWidth = 110 };
        policy.SelectionChanged += (_, _) =>
                                   {
                                       if (policy.SelectedItem is ExternalChangePolicy p) settings.ConflictPolicy = p;
                                   };
        settings.PropertyChanged += (_, e) =>
                                    {
                                        if (e.PropertyName == nameof(SiloFileSettings.ConflictPolicy)) policy.SelectedItem = settings.ConflictPolicy;
                                    };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "If the file changed on disk: ", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(policy);
        items.Add(new DesignMenuItem { MinLevel = DesignLevel.Author, Content = row });

        var archives = new CheckBox { Content = "New Silos use the archive format", IsChecked = settings.NewSilosAreArchives };
        archives.Click += (_, _) => settings.NewSilosAreArchives = archives.IsChecked == true;
        items.Add(new DesignMenuItem { MinLevel = DesignLevel.Author, Content = archives });

        items.Add(DesignMenuItem.Slider("Offer archive conversion above", DesignLevel.Author, 8, 1024,
                                        () => settings.ExplodeThresholdMb, v => settings.ExplodeThresholdMb = (int)v, "{0:0} MB"));
    }

    private static bool TryGetDropped(IDataObject data, out List<string> targets)
    {
        targets = [];
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            targets.AddRange(files.Where(f => f.EndsWith(".silo", StringComparison.OrdinalIgnoreCase)));
        else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text &&
                 SiloLocation.IsRemote(text.Trim()))
            targets.Add(text.Trim());

        return targets.Count > 0;
    }

    private void OnDragOverSilo(object sender, DragEventArgs e)
    {
        if (!TryGetDropped(e.Data, out _)) return;

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnDropSilo(object sender, DragEventArgs e)
    {
        if (!TryGetDropped(e.Data, out var targets)) return;

        e.Handled = true;
        foreach (var target in targets) await Commanding.OpenSiloCommand.OpenPathAsync(target);
    }

    private OpenSilo? _watched;

    private void WatchActiveSilo()
    {
        if (_watched is not null) _watched.StateChanged -= OnSiloStateChanged;
        _watched = App.ActiveSilo;
        if (_watched is not null) _watched.StateChanged += OnSiloStateChanged;
        UpdateTitle();
    }

    private void OnSiloStateChanged(object? sender, EventArgs e)
    {
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        string app = AppIdentity.AppName;
        string mode = App.Launch.Mode switch
                      {
                          LaunchMode.Solo   => " [Solo]",
                          LaunchMode.Remote => " [Remote]",
                          _                 => ""
                      };
        string flags = _watched is { IsUnsynced: true } ? " (not uploaded)" : "";
        Title = _watched is { Name: { } name } ? $"{name}{(_watched.IsDirty ? "*" : "")}{flags} - {app}{mode}" : app + mode;
    }

    private bool _closeApproved;

    /// <summary>
    ///     Closing the window while a Silo has unsaved changes asks whether to save first (Save / Don't save / Cancel); the
    ///     Silos are closed properly so their working folders are cleaned up. With nothing open the window just closes.
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeApproved || e.Cancel || OpenSilo.OpenSilos.Count == 0) return;

        e.Cancel = true; // decided asynchronously below
        if (await App.CloseAllSilosAsync())
        {
            _closeApproved = true;

            // Not Close() directly: when nothing needed a prompt the await above finishes synchronously, so this is still
            // inside the closing event, and a window cannot be closed (or shown) while it is closing.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    /// <summary>
    ///     Replaces Telerik RadNavigationView's IsPaneOpen/DisplayMode/AutoChangeDisplayMode -
    ///     the simplest equivalent of "close/collapse the nav pane": shrink NavColumn to a
    ///     narrow rail and hide the tree and header label, keeping only this toggle button
    ///     visible so the pane can always be reopened. No intermediate "compact/icon-only"
    ///     rail state (RadNavigationView's Compact mode) - just expanded/collapsed.
    /// </summary>
    private void NavCollapseToggle_Click(object sender, RoutedEventArgs e)
    {
        ApplyNavState(NavCollapseToggle.IsChecked == true);
    }

    private void ApplyNavState(bool collapsed)
    {
        NavColumn.Width           = collapsed ? NavCollapsedWidth : _navExpandedWidth;
        NavTree.Visibility        = collapsed ? Visibility.Collapsed : Visibility.Visible;
        NavTreeBottom.Visibility  = collapsed ? Visibility.Collapsed : Visibility.Visible;
        NavHeaderText.Visibility  = collapsed ? Visibility.Collapsed : Visibility.Visible;
        // The collapsed-state icon gutters (NavRail, NavRailBottom) only make sense, and only fit,
        // while the trees themselves are hidden - each pair shares a Grid cell.
        NavRail.Visibility        = collapsed ? Visibility.Visible : Visibility.Collapsed;
        NavRailBottom.Visibility  = collapsed ? Visibility.Visible : Visibility.Collapsed;
        NavCollapseToggle.ToolTip = collapsed ? "Expand navigation pane" : "Collapse navigation pane";
    }

    /// <summary>
    ///     Settings can expand upward from the bottom edge; this keeps it from squeezing out the top tree by capping the
    ///     bottom tree at 60% of the pane (it scrolls beyond that).
    /// </summary>
    private void NavHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        NavTreeBottom.MaxHeight = Math.Max(120, NavHost.ActualHeight * 0.6);
    }

    // --- nav tree contents ---------------------------------------------------------------------

    private void OnOpenSilosChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueNavSync();
    }

    /// <summary>Coalesces the several events one open/close produces into a single <see cref="SyncNav" />.</summary>
    private void QueueNavSync()
    {
        if (_navSyncQueued)
            return;

        _navSyncQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
                                          {
                                              _navSyncQueued = false;
                                              SyncNav();
                                          }));
    }

    /// <summary>
    ///     Makes the top of the nav tree match the open Silos: just "Recent" when none is open; otherwise a node per
    ///     Silo (named "Silo" when it is the only one, else after its file) then Connections and Performance. The
    ///     active Silo's node is shown in the accent colour and expanded, the others collapsed. Nodes of Silos that
    ///     stay open are kept, so their expansion state survives.
    /// </summary>
    private void SyncNav()
    {
        var silos  = OpenSilo.OpenSilos.ToList();
        var active = App.ActiveSilo;

        foreach (var gone in _siloNavs.Keys.Where(s => !silos.Contains(s)).ToList())
        {
            gone.StateChanged -= _siloNavs[gone].Renamed;
            _siloNavs.Remove(gone);
        }

        var desired = new List<AppNavNode>();
        if (silos.Count == 0)
        {
            desired.Add(_recentNode);
        }
        else
        {
            foreach (var silo in silos)
            {
                if (!_siloNavs.TryGetValue(silo, out var nav))
                {
                    EventHandler renamed = (_, _) => UpdateSiloHeaders();
                    silo.StateChanged += renamed;
                    _siloNavs[silo]   =  nav = new SiloNav(AppNavTree.BuildSilo(), renamed);
                }

                desired.Add(nav.Node);
            }

            desired.Add(_connectionsNode);
            desired.Add(_performanceNode);
        }

        // edit the list in place (rather than replacing it) so the tree keeps its scroll position and expansion
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < _topRoots.Count && ReferenceEquals(_topRoots[i], desired[i]))
                continue;

            var existing = _topRoots.IndexOf(desired[i]);
            if (existing > i)
                _topRoots.Move(existing, i);
            else
                _topRoots.Insert(i, desired[i]);
        }

        while (_topRoots.Count > desired.Count)
            _topRoots.RemoveAt(_topRoots.Count - 1);

        UpdateSiloHeaders();
        foreach (var (silo, nav) in _siloNavs)
        {
            var isActive = ReferenceEquals(silo, active);
            nav.Node.IsEmphasized = isActive;
            if (isActive != nav.Node.IsExpanded && (isActive || silos.Count > 1))
                nav.Node.IsExpanded = isActive; // the active Silo opens, the others fold away
        }

        // a selection inside a Silo that has been closed no longer exists
        if (_selectedNode is { } selected && !AllRoots().Contains(RootNodeOf(selected)))
        {
            selected.IsSelected      = false;
            _selectedNode            = null;
            SelectedItemPane.Content = null;
            SelectedItemPane.Title   = "Selected Item";
        }

        // as in the navigation design, the active Silo (or "Recent") is the highlighted row, unless the person is
        // already working somewhere that is not in another Silo's branch
        var target = silos.Count == 0                                          ? _recentNode
                     : active is not null && _siloNavs.TryGetValue(active, out var activeNav) ? activeNav.Node
                                                                                : null;
        if (target is not null &&
            (_selectedNode is null || (SiloOf(RootNodeOf(_selectedNode)) is { } owner && !ReferenceEquals(owner, active))))
            Select(target);
    }

    /// <summary>"Silo" for a lone open Silo, the file name when there are several.</summary>
    private void UpdateSiloHeaders()
    {
        foreach (var (silo, nav) in _siloNavs)
            nav.Node.Header = _siloNavs.Count == 1 ? "Silo" : silo.Name ?? "Untitled";
    }

    private IEnumerable<AppNavNode> AllRoots()
    {
        return _topRoots.Concat(_bottomRoots);
    }

    /// <summary>The top-level node above <paramref name="node" /> (the node itself, if it is one).</summary>
    private AppNavNode? RootNodeOf(AppNavNode node)
    {
        var root = node.Item;
        while (root.Parent is { } parent)
            root = parent;

        return AllRoots().FirstOrDefault(r => ReferenceEquals(r.Item, root));
    }

    /// <summary>The open Silo a top-level node stands for; null for Recent, Connections, Settings and the rest.</summary>
    private OpenSilo? SiloOf(AppNavNode? root)
    {
        return root is null ? null : _siloNavs.FirstOrDefault(kv => ReferenceEquals(kv.Value.Node, root)).Key;
    }

    /// <summary>
    ///     The two jobs MainWindow's old per-item TreeViewItem/RailButton pairs used to do by
    ///     hand, now done generically from whichever <see cref="AppNavNode"/> got selected:
    ///     (1) walk its <see cref="AppNavItem.Parent"/> chain up to its top-level node
    ///     so NavRail (the collapsed pane's icon gutter) can highlight the right one, and
    ///     (2) if the node has a real <see cref="AppNavItem.View"/>, build its content and host
    ///     it in <see cref="SelectedItemPane"/>. A node with no view (a group header like
    ///     "Schema", or a not-yet-wired setting) leaves whatever content was already showing
    ///     rather than clearing it to something misleading.
    /// </summary>
    private void NavTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is AppNavNode node)
            OnSelected(node);
    }

    /// <summary>
    ///     Selects a node from code. The trees only raise their selection event once the node's container exists, and
    ///     while the pane is collapsed (it starts that way) nothing has been generated yet, so the selection is also
    ///     applied directly here; <see cref="OnSelected" /> ignores it the second time round.
    /// </summary>
    private void Select(AppNavNode node)
    {
        node.IsSelected = true;
        OnSelected(node);
    }

    private void OnSelected(AppNavNode node)
    {
        if (ReferenceEquals(_selectedNode, node))
            return;

        // there are two trees (top and bottom) but only one selection: clear the other tree's highlight
        if (_selectedNode is { } previous)
            previous.IsSelected = false;
        _selectedNode = node;

        var root = RootNodeOf(node);
        foreach (var candidate in AllRoots())
            candidate.IsActiveRoot = ReferenceEquals(candidate, root);

        // working inside another Silo's branch makes that Silo the active one
        if (SiloOf(root) is { } silo && !ReferenceEquals(App.ActiveSilo, silo))
            App.ActiveSilo = silo;

        // a group (Silo, Schema, ...) has no view of its own: it shows the first one beneath it, e.g. Silo shows Search
        if (FirstView(node) is { } view)
        {
            SelectedItemPane.Content = view.ContentBuilder();
            SelectedItemPane.Title   = view.Name;
        }
    }

    /// <summary>The view of <paramref name="node" />, else of the first node under it (depth first) that has one.</summary>
    private static AppView? FirstView(AppNavNode node)
    {
        if (node.Item.View is { } own)
            return own;

        foreach (var child in node.Children)
            if (FirstView(child) is { } found)
                return found;

        return null;
    }

    // --- dock panes (View menu) ----------------------------------------------------------------

    /// <summary>Whether the Toolbox pane is showing (closed or hidden panes are not).</summary>
    public bool IsToolboxOpen => IsOpen(ToolboxPane);

    /// <summary>Whether the Selected Item pane -- where the navigation's views show -- is showing.</summary>
    public bool IsSelectedItemOpen => IsOpen(SelectedItemPane);

    public void SetToolboxOpen(bool open) => SetOpen(ToolboxPane, open);

    public void SetSelectedItemOpen(bool open) => SetOpen(SelectedItemPane, open);

    private static bool IsOpen(LayoutAnchorable pane) => pane.Root is not null && pane.IsVisible;

    /// <summary>
    ///     Hides a pane, or brings it back: a hidden pane returns to where it was, one that was closed outright
    ///     (no longer in the layout at all) is added to the layout again.
    /// </summary>
    private void SetOpen(LayoutAnchorable pane, bool open)
    {
        if (open == IsOpen(pane))
            return;

        if (!open)
        {
            pane.Hide();
            return;
        }

        if (pane.Root is null)
            pane.AddToLayout(DockHost, AnchorableShowStrategy.Most);
        else
            pane.Show();

        pane.IsActive = true;
    }

    /// <summary>
    ///     Selecting a root from NavRail while collapsed picks up exactly where clicking it in
    ///     the full tree would have - <see cref="NavTree_SelectedItemChanged"/> keeps the
    ///     highlight and content pane in sync, since setting <see cref="AppNavNode.IsSelected"/>
    ///     (two-way bound to TreeViewItem.IsSelected via NavTree's ItemContainerStyle) raises
    ///     the same TreeView.SelectedItemChanged event a mouse click in the tree would. Only
    ///     ever invoked for a root node, and roots are always realized (NavTree doesn't
    ///     virtualize its first level), so the binding is live by the time this runs.
    /// </summary>
    private void NavRailButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AppNavNode node)
            Select(node);
    }
}
