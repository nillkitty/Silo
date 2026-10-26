using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Xml.Linq;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using AvalonDock.Themes;
using Serilog;
using Silo.Clipboarding;
using Silo.Commanding;
using Silo.Design;
using Silo.Explorer;
using Silo.Hosting;
using Silo.Model;
using Silo.Nav;
using Silo.Providers.Data;
using Silo.Remote;
using Silo.Settings;
using Silo.Tabular;
using Silo.TextEditing;
using Silo.Theming;
using Silo.Toolbar;
using Silo.Tools;
using Silo.Ui.Documents;
using Silo.Ui.Windows;
using Silo.ViewModel;

namespace Silo;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window, IWindowLayout
{
    /// <summary>NavColumn's width while the nav pane is expanded - 300 matches its original XAML default.</summary>
    private static readonly GridLength _navExpandedWidth = new(300);

    /// <summary>
    ///     The bottom of the nav pane (NavTreeBottom and NavRailBottom): Inbox, Settings and Profile. They stand apart
    ///     from the Silos, so they are always there and always hug the bottom edge.
    /// </summary>
    private readonly IReadOnlyList<AppNavNode> _bottomRoots = AppNavTree.BuildBottomRoots();

    private readonly AppNavNode _connectionsNode = AppNavTree.BuildConnections();

    /// <summary>
    ///     Where each open document's node (a document that is an <see cref="INodeProvider" />) was put, so closing the
    ///     tab takes it out of the same place.
    /// </summary>
    private readonly Dictionary<INodeProvider, NodeHost> _documentNodes = new(ReferenceEqualityComparer.Instance);

    private readonly AppNavNode     _performanceNode = AppNavTree.BuildPerformance();
    private readonly PropertiesView _properties      = null!;

    private readonly AppNavNode _recentNode = AppNavTree.BuildRecent();

    /// <summary>The nav node (and name-change subscription) of each open Silo.</summary>
    private readonly Dictionary<OpenSilo, SiloNav> _siloNavs = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     The top of the nav pane (NavTree and NavRail share it): "Recent" while no Silo is open, otherwise a node per
    ///     open Silo followed by Connections and Performance. Kept in step with the open Silos by <see cref="SyncNav" />.
    /// </summary>
    private readonly ObservableCollection<AppNavNode> _topRoots = [];

    private bool _closeApproved;

    // --- remembered arrangement (the Window table of the Silo, see WindowPlacements) -----------------------------

    /// <summary>The dock layout the window was built with (null until the window has loaded).</summary>
    private string? _defaultDockXml;

    private bool _navSyncQueued;

    /// <summary>The node currently selected, in whichever of the two nav trees it is.</summary>
    private AppNavNode? _selectedNode;

    /// <summary>
    ///     True while the selection is in the Settings branch: the bottom tree may then take all the room the top tree's
    ///     rows leave, instead of the usual 60% (see <see cref="FitNavTrees" />).
    /// </summary>
    private bool _settingsFocused;

    /// <summary>Top-tree nodes folded away to make room for Settings; unfolded again when the selection leaves it.</summary>
    private readonly List<AppNavNode> _foldedForSettings = [];

    private OpenSilo? _watched;

    /// <summary>What the terminal in the status bar runs (see <see cref="Silo.Ui.MainTerminal" />).</summary>
    private readonly Ui.MainTerminal _terminal = null!;

    /// <summary>The selected document tab's content, when it adds items to the Edit menu (a table does); else null.</summary>
    public IEditContributor? ActiveEditContributor => Documents.SelectedContent as IEditContributor;

    public App App => App.Instance!;

    /// <summary>The document tabs in the middle of the window (tables, navigation views...).</summary>
    public DocumentTabs Documents { get; } = null!;

    /// <summary>The Explorer tree on the left (what the Node menu is about).</summary>
    public ExplorerView ExplorerControl { get; } = null!;

    // --- dock panes (View menu) ----------------------------------------------------------------

    /// <summary>Whether the Explorer pane (the browsable tree on the left) is showing.</summary>
    public bool IsExplorerOpen => IsOpen(ExplorerPane);

    /// <summary>Whether the Properties pane is showing.</summary>
    public bool IsPropertiesOpen => IsOpen(PropertiesPane);

    /// <summary>Whether the preview tab (the one that shows the selected Explorer item) is open.</summary>
    public bool IsSelectedItemOpen => Documents.HasPreview;

    /// <summary>Whether the Toolbox pane is showing (closed or hidden panes are not).</summary>
    public bool IsToolboxOpen => IsOpen(ToolboxPane);

    /// <summary>
    ///     NavColumn's width while collapsed - wide enough for the header bar's toggle button to remain clickable (see the
    ///     Grid in MainWindow.xaml's nav cell), so re-expanding is always reachable, and for the rail's icons, which follow
    ///     the live "Icon Size" setting (icon + the rail button's padding and accent bar).
    /// </summary>
    private static GridLength NavCollapsedWidth => new(Math.Max(32, AppDesignSettings.Instance.IconSize + 16));

    private void ApplyDockTheme()
    {
        DockHost.Theme = AppTheme.Instance.IsEffectivelyDark ? new Vs2013DarkTheme() : new Vs2013LightTheme();
    }

    /// <summary>
    ///     Author-mode file settings, reached with Ctrl+right-click anywhere in the window (see <see cref="DesignMenu" />
    ///     ).
    /// </summary>
    private void AddFileDesignMenu()
    {
        var settings = SiloFileSettings.Instance;
        var items    = DesignMenu.EnsureItems(this);

        var policy = new ComboBox
                     {
                         ItemsSource = Enum.GetValues<ExternalChangePolicy>(), SelectedItem = settings.ConflictPolicy,
                         MinWidth    = 110
                     };
        policy.SelectionChanged += (_, _) =>
                                   {
                                       if (policy.SelectedItem is ExternalChangePolicy p) settings.ConflictPolicy = p;
                                   };
        settings.PropertyChanged += (_, e) =>
                                    {
                                        if (e.PropertyName == nameof(SiloFileSettings.ConflictPolicy))
                                            policy.SelectedItem = settings.ConflictPolicy;
                                    };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text  = "If the file changed on disk: ", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(policy);
        items.Add(new DesignMenuItem { MinLevel = DesignLevel.Author, Content = row });

        var archives = new CheckBox { Content = "New Silos use the archive format", IsChecked = settings.NewSilosAreArchives };
        archives.Click += (_, _) => settings.NewSilosAreArchives = archives.IsChecked == true;
        items.Add(new DesignMenuItem { MinLevel = DesignLevel.Author, Content = archives });

        items.Add(DesignMenuItem.Slider("Offer archive conversion above", DesignLevel.Author, 8, 1024, () => settings.ExplodeThresholdMb,
                                        v => settings.ExplodeThresholdMb = (int)v, "{0:0} MB"));
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
        foreach (var target in targets) await OpenSiloCommand.OpenPathAsync(target);
    }

    private void WatchActiveSilo()
    {
        if (_watched is not null) _watched.StateChanged -= OnSiloStateChanged;
        _watched = App.ActiveSilo;
        if (_watched is not null) _watched.StateChanged += OnSiloStateChanged;
        UpdateTitle();

        // The window takes the arrangement the Silo remembers, but only the first Silo opened into a window gets to rearrange it
        // (a second Silo opened beside it does not move the window around); every Silo keeps what the window looked like when saved.
        if (_watched is { } silo)
        {
            bool restore = OpenSilo.OpenSilos.Count <= 1;
            // the window's own frame is kept apart from any Silo (MainWindowState); the Silo's only stands in until there is one
            bool frameKept = MainWindowState.HasSaved;
            Dispatcher.BeginInvoke(new Action(() => WindowPlacements.Track(this, silo, WindowPlacements.Main, "silo:main", this, restore,
                                                                           false, !frameKept)), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    ///     View -> Reset Window Layout: the panes go back to where a new window has them, and the remembered sizes of
    ///     windows are forgotten.
    /// </summary>
    public void ResetWindowLayout()
    {
        try
        {
            _defaultDockXml ??= CaptureLayout();
            if (_defaultDockXml is { } layout) RestoreLayout(layout);

            if (App.ActiveSilo?.Data is IWindowStore store) store.ClearWindows();
            App.Status("The window layout was reset");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Resetting the window layout failed");
            App.Status("The window layout could not be reset: " + ex.Message);
        }
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

    /// <summary>
    ///     Closing the window while a Silo has unsaved changes asks whether to save first (Save / Don't save / Cancel); the
    ///     Silos are closed properly so their working folders are cleaned up. With nothing open the window just closes.
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        // text editors with unsaved files (or new documents) ask first; texts kept in a Silo are written into it as it closes
        if (!_closeApproved && !e.Cancel && !TextEditors.ConfirmCloseAll())
        {
            e.Cancel = true;
            return;
        }

        if (_closeApproved || e.Cancel || OpenSilo.OpenSilos.Count == 0)
        {
            // it is going: remember where it was (also when it was maximized or minimized)
            if (!e.Cancel) MainWindowState.Save(this);
            return;
        }

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

        // Settings may have been picked from the rail while the trees were hidden (nothing could be measured then)
        if (!collapsed && _settingsFocused) QueueSettingsFit();
    }

    /// <summary>
    ///     Settings can expand upward from the bottom edge; this keeps it from squeezing out the top tree by capping the
    ///     bottom tree at 60% of the pane (it scrolls beyond that).
    /// </summary>
    private void NavHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitNavTrees();
    }

    /// <summary>
    ///     Sizes the bottom tree: normally at most 60% of the pane; while Settings is the focus, everything the top
    ///     tree's rows leave over (so the unfolded Settings branch shows whole when it fits).
    /// </summary>
    private void FitNavTrees()
    {
        var usual = Math.Max(120, NavHost.ActualHeight * 0.6);
        if (!_settingsFocused || NavTree.Visibility != Visibility.Visible)
        {
            NavTreeBottom.MaxHeight = usual;
            return;
        }

        NavTreeBottom.MaxHeight = Math.Max(usual, NavAvailableHeight() - ContentHeight(NavTree));
    }

    /// <summary>The height both trees share: the pane minus its header bar.</summary>
    private double NavAvailableHeight()
    {
        return Math.Max(0, NavHost.ActualHeight - NavHost.RowDefinitions[0].ActualHeight);
    }

    /// <summary>How tall a nav tree's rows are, all of them, whether or not they fit (its scroll viewer's extent).</summary>
    private static double ContentHeight(TreeView tree)
    {
        if (FindDescendant<ScrollViewer>(tree) is not { } scroll) return tree.ActualHeight;

        // item-based scrolling reports its extent in items, not pixels: ask the items for their size instead
        if (scroll.CanContentScroll && FindDescendant<ItemsPresenter>(scroll) is { } items) return items.DesiredSize.Height + 4;

        return scroll.ExtentHeight + 4;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }

        return null;
    }

    /// <summary>
    ///     Selecting Settings unfolds it and every group beneath it. If the whole branch does not fit below the top
    ///     tree, the top tree's nodes (the Silos, Connections) are folded to make room; they unfold again when the
    ///     selection moves back out of Settings (<see cref="LeaveSettings" />).
    /// </summary>
    private void UnfoldSettings(AppNavNode settings)
    {
        ExpandAll(settings);
        QueueSettingsFit();
    }

    private static void ExpandAll(AppNavNode node)
    {
        if (node.Children.Count == 0) return;

        node.IsExpanded = true;
        foreach (var child in node.Children) ExpandAll(child);
    }

    /// <summary>
    ///     Measures once the unfolded rows exist (after layout), folds the top tree if Settings still would not fit, then
    ///     sizes the bottom tree (after the next layout, when the folded top tree has shrunk).
    /// </summary>
    private void QueueSettingsFit()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_settingsFocused || NavTree.Visibility != Visibility.Visible) return;

            if (ContentHeight(NavTreeBottom) + ContentHeight(NavTree) > NavAvailableHeight())
                foreach (var root in _topRoots.Where(r => r.IsExpanded))
                {
                    root.IsExpanded = false;
                    if (!_foldedForSettings.Contains(root)) _foldedForSettings.Add(root);
                }

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitNavTrees));
        }));
    }

    /// <summary>The selection left Settings: the bottom tree goes back to its usual share and the folded nodes unfold.</summary>
    private void LeaveSettings()
    {
        // a Silo that stopped being the active one meanwhile stays folded, as SyncNav would have left it
        foreach (var node in _foldedForSettings.Where(n => _topRoots.Contains(n)))
            if (SiloOf(node) is not { } silo || ReferenceEquals(silo, App.ActiveSilo))
                node.IsExpanded = true;
        _foldedForSettings.Clear();
        FitNavTrees();
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
        foreach (var root in AllRoots()) WireNode(root);
        foreach (var (silo, nav) in _siloNavs)
        {
            var isActive = ReferenceEquals(silo, active);
            nav.Node.IsEmphasized = isActive;
            if (isActive != nav.Node.IsExpanded && (isActive || silos.Count > 1))
            {
                if (isActive && _settingsFocused)
                {
                    // Settings has the room for now: the active Silo opens when the selection leaves it
                    if (!_foldedForSettings.Contains(nav.Node)) _foldedForSettings.Add(nav.Node);
                }
                else
                {
                    nav.Node.IsExpanded = isActive; // the active Silo opens, the others fold away
                }
            }
        }

        _foldedForSettings.RemoveAll(n => !_topRoots.Contains(n));
        if (_settingsFocused) QueueSettingsFit();

        // a selection inside a Silo that has been closed no longer exists
        if (_selectedNode is { } selected && !AllRoots().Contains(RootNodeOf(selected)))
        {
            selected.IsSelected = false;
            _selectedNode       = null;
        }

        // as in the navigation design, the active Silo (or "Recent") is the highlighted row, unless the person is
        // already working somewhere that is not in another Silo's branch
        var target = silos.Count == 0                                                       ? _recentNode :
                     active is not null && _siloNavs.TryGetValue(active, out var activeNav) ? activeNav.Node : null;
        if (target is not null &&
            (_selectedNode is null || (SiloOf(RootNodeOf(_selectedNode)) is { } owner && !ReferenceEquals(owner, active))))
            Select(target);
    }

    /// <summary>Gives every node that has a view a way to open it (for its author-mode menu's "Open" items).</summary>
    private void WireNode(AppNavNode node)
    {
        if (FirstView(node) is not null)
            node.Opener = pin =>
                          {
                              if (FirstView(node) is { } view) OpenView(view, pin);
                          };

        foreach (var child in node.Children) WireNode(child);
    }

    private static void RefreshOverrides(AppNavNode node)
    {
        node.RefreshOverrides();
        foreach (var child in node.Children) RefreshOverrides(child);
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
    ///     hand, now done generically from whichever <see cref="AppNavNode" /> got selected:
    ///     (1) walk its <see cref="AppNavItem.Parent" /> chain up to its top-level node
    ///     so NavRail (the collapsed pane's icon gutter) can highlight the right one, and
    ///     (2) if the node has a real <see cref="AppNavItem.View" />, build its content and host
    ///     it in a document tab (previewed on a click, pinned by a double-click). A node with no view (a group header like
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

        // Settings: selecting its root unfolds the whole branch (and folds the top tree if need be); moving out of the
        // branch gives the room back
        var inSettings = root is not null && _bottomRoots.Contains(root) && root.Item.Text == AppNavTree.SettingsText;
        var wasInSettings = _settingsFocused;
        _settingsFocused = inSettings;
        if (inSettings && ReferenceEquals(node, root))
            UnfoldSettings(root);
        else if (wasInSettings && !inSettings)
            LeaveSettings();

        // working inside another Silo's branch makes that Silo the active one
        if (SiloOf(root) is { } silo && !ReferenceEquals(App.ActiveSilo, silo))
            App.ActiveSilo = silo;

        // a group (Silo, Schema, ...) has no view of its own: it shows the first one beneath it, e.g. Silo shows Search.
        // The view opens as the preview tab; double-clicking the node pins it (NavTree_DoubleClick).
        if (FirstView(node) is { } view)
            OpenView(view, false);
    }

    /// <summary>The document tab key of a navigation view: views are identified by name, whichever node reached them.</summary>
    private static string ViewKey(AppView view)
    {
        return view.Key ?? "view:" + view.Name;
    }

    private void OpenView(AppView view, bool pin)
    {
        Documents.Open(ViewKey(view), view.Name, view.ContentBuilder, null, pin);
    }

    /// <summary>Double-clicking a navigation node keeps its view open as a permanent tab.</summary>
    private void NavTree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView { SelectedItem: AppNavNode node } || e.OriginalSource is not DependencyObject source)
            return;

        // only a double-click on the selected row itself, not on the empty space or the scroll bar
        var d = source;
        while (d is not null and not TreeViewItem)
            d = d is Visual || d is Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is not TreeViewItem { DataContext: AppNavNode hit } || !ReferenceEquals(hit, node))
            return;

        if (FirstView(node) is { } view)
        {
            e.Handled = true;
            OpenView(view, true);
        }
    }

    // --- the terminal (status bar) ----------------------------------------------------------------

    /// <summary>The menu bar at the top (what the terminal's <c>menu</c> lists and clicks).</summary>
    internal Silo.Ui.MenuBar? MainMenu => FindDescendant<Silo.Ui.MenuBar>(this);

    /// <summary>View > Terminal Log, the terminal's <c>log</c>, a click on the status line.</summary>
    public void ShowTerminalLog()
    {
        StatusStrip.Terminal.ShowLog();
    }

    public void ClearTerminalLog()
    {
        StatusStrip.Terminal.ClearLog();
    }

    /// <summary>Every node of the navigation with its path as shown ("Settings / Silo Settings / Snapshots"), for the terminal's <c>nav</c>.</summary>
    internal IEnumerable<string> NavPaths()
    {
        return NavWalk().Select(e => e.Path);
    }

    /// <summary>
    ///     Selects the navigation node at <paramref name="path" /> (segments split by <c>/</c>, <c>&gt;</c> or <c>" / "</c>, compared
    ///     without case, spaces or punctuation; the end of a path is enough when only one node has it), unfolding its parents.
    ///     Returns the node's full path, or null when there is no such node.
    /// </summary>
    internal string? Navigate(string path)
    {
        static string Squeeze(string text) => new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        var wanted = path.Replace('>', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Squeeze).ToArray();
        if (wanted.Length == 0) return null;

        var all   = NavWalk().ToList();
        var exact = all.Where(e => e.Segments.Select(Squeeze).SequenceEqual(wanted)).ToList();
        var hits  = exact.Count > 0 ? exact : all.Where(e => e.Segments.Length >= wanted.Length && e.Segments[^wanted.Length..].Select(Squeeze).SequenceEqual(wanted)).ToList();
        if (hits.Count == 0) return null;
        if (hits.Count > 1) throw new InvalidOperationException($"'{path}' could be: {string.Join("; ", hits.Select(h => h.Path))}");

        var (found, _, _, ancestors) = hits[0];
        foreach (var ancestor in ancestors) ancestor.IsExpanded = true;
        Select(found);
        return hits[0].Path;
    }

    private IEnumerable<(AppNavNode Node, string Path, string[] Segments, AppNavNode[] Ancestors)> NavWalk()
    {
        IEnumerable<(AppNavNode, string, string[], AppNavNode[])> Walk(AppNavNode node, string[] above, AppNavNode[] ancestors)
        {
            if (node.IsHidden) yield break;

            string[] segments = [.. above, node.Header];
            yield return (node, string.Join(" / ", segments), segments, ancestors);
            foreach (var child in node.Children)
            foreach (var inner in Walk(child, segments, [.. ancestors, node]))
                yield return inner;
        }

        return AllRoots().SelectMany(root => Walk(root, [], []));
    }

    // --- the Explorer tree and the Properties pane -----------------------------------------------

    /// <summary>
    ///     A click (or arrow key) in the Explorer: the details go to Properties and the node's view, if any, to the
    ///     preview tab.
    /// </summary>
    /// <summary>
    ///     A document opened in a tab: if it is an <see cref="INodeProvider" /> its node joins the Explorer, under the
    ///     active Silo.
    /// </summary>
    private void OnDocumentContentOpened(object? sender, UIElement content)
    {
        if (content is not INodeProvider provider) return;

        var host                                                     = App.ActiveSilo?.Nodes ?? App.Nodes;
        if (host.Add(provider) is not null) _documentNodes[provider] = host;
    }

    private void OnDocumentContentReleased(object? sender, UIElement content)
    {
        if (content is not INodeProvider provider) return;

        if (_documentNodes.Remove(provider, out var host)) host.Remove(provider);
    }

    private void OnExplorerSelected(object? sender, ExplorerNode? node)
    {
        _properties.Show(node);
        NodeCommandHub.SetCurrent(node); // the Node menu, the toolbar's node buttons and the right-click menu follow the selection
        if (node is not null) ShowNodeView(node, false);
    }

    /// <summary>
    ///     Shows the view of an Explorer node in a document tab (the preview tab, or a permanent one when
    ///     <paramref name="pin" />): the node's own view if it has one, otherwise a generic one made from what the node
    ///     says about itself. Selecting something in the navigation flyout previews in the same tab and so replaces it.
    /// </summary>
    private void ShowNodeView(ExplorerNode node, bool pin)
    {
        string key  = node.DocKey ?? "node:" + node.Key;
        var    open = node.Open   ?? (() => NodeViews.Build(node));
        Documents.Open(key, node.Header, open, node.Closed, pin);
    }

    /// <summary>A double-click (or Enter) in the Explorer keeps the node's view open as a permanent tab.</summary>
    private void OnExplorerOpened(object? sender, ExplorerNode node)
    {
        if (node.DocKey is not null && node.Open is not null)
            ShowNodeView(node, true);
        else if (node.Activate is { } activate)
            activate();
        else
            ShowNodeView(node, true);
    }

    /// <summary>A Silo is about to close: its table tabs go first (saving what they hold).</summary>
    private void OnSiloClosing(object? sender, OpenSilo silo)
    {
        string prefix = TableEditors.DocPrefix(silo);
        Documents.CloseWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
        TextEditors.CloseFor(silo);
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

    /// <summary>
    ///     Shows or closes the preview tab. Showing it previews the item now selected in the Explorer; with nothing selected
    ///     there is nothing to show.
    /// </summary>
    public void SetSelectedItemOpen(bool open)
    {
        if (!open)
        {
            Documents.ClosePreview();
            return;
        }

        if (ExplorerControl.SelectedNode is { } node) ShowNodeView(node, false);
        else App.Status("Select something in the Explorer to see it here");
    }

    public void SetExplorerOpen(bool open)
    {
        SetOpen(ExplorerPane, open, AnchorableShowStrategy.Left);
    }

    public void SetPropertiesOpen(bool open)
    {
        SetOpen(PropertiesPane, open, AnchorableShowStrategy.Right);
    }

    public void SetToolboxOpen(bool open)
    {
        SetOpen(ToolboxPane, open, AnchorableShowStrategy.Right);
    }

    private static bool IsOpen(LayoutAnchorable pane)
    {
        return pane.Root is not null && pane.IsVisible;
    }

    /// <summary>Whether a tool's pane is showing, docked or floating.</summary>
    public bool IsToolPaneOpen(LayoutAnchorable pane)
    {
        return IsOpen(pane);
    }

    /// <summary>
    ///     Opens a tool's pane: the first time it is added to the bottom of the dock area (it can be dragged out into a
    ///     floating window from there, or onto any side), afterwards it returns to wherever it was.
    /// </summary>
    public void ShowToolPane(LayoutAnchorable pane, double height = 360)
    {
        if (pane.Root is null)
        {
            pane.AddToLayout(DockHost, AnchorableShowStrategy.Bottom);
            if (pane.Parent is LayoutAnchorablePane group) group.DockHeight = new GridLength(height);
        }
        else if (!pane.IsVisible)
        {
            pane.Show();
        }

        pane.IsSelected = true;
        pane.IsActive   = true;
    }

    public void HideToolPane(LayoutAnchorable pane)
    {
        if (IsOpen(pane)) pane.Hide();
    }

    /// <summary>
    ///     Hides a pane, or brings it back: a hidden pane returns to where it was, one that was closed outright
    ///     (no longer in the layout at all) is added to the layout again.
    /// </summary>
    private void SetOpen(LayoutAnchorable pane, bool open, AnchorableShowStrategy strategy)
    {
        if (open == IsOpen(pane))
            return;

        if (!open)
        {
            pane.Hide();
            return;
        }

        if (pane.Root is null)
            pane.AddToLayout(DockHost, strategy);
        else
            pane.Show();

        pane.IsActive = true;
    }

    /// <summary>
    ///     Selecting a root from NavRail while collapsed picks up exactly where clicking it in
    ///     the full tree would have - <see cref="NavTree_SelectedItemChanged" /> keeps the
    ///     highlight and content pane in sync, since setting <see cref="AppNavNode.IsSelected" />
    ///     (two-way bound to TreeViewItem.IsSelected via NavTree's ItemContainerStyle) raises
    ///     the same TreeView.SelectedItemChanged event a mouse click in the tree would. Only
    ///     ever invoked for a root node, and roots are always realized (NavTree doesn't
    ///     virtualize its first level), so the binding is live by the time this runs.
    /// </summary>
    private void NavRailButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppNavNode node)
            return;

        // clicking the rail button of the branch that is already active opens the flyout (the full tree)
        bool wasActive = node.IsActiveRoot;
        Select(node);
        if (wasActive && NavCollapseToggle.IsChecked == true)
        {
            NavCollapseToggle.IsChecked = false;
            ApplyNavState(false);
        }
    }

    public MainWindow()
    {
        InitializeComponent();

        // where it was left: placed before it is shown, so it does not jump; kept again whenever it is maximized or restored
        MainWindowState.Apply(this);
        StateChanged += (_, _) =>
                        {
                            if (IsLoaded && WindowState != WindowState.Minimized) MainWindowState.Save(this);
                        };

        // Explorer on the left, Properties on the right, documents between them (see the DockHost layout in the XAML)
        Documents                 =  new DocumentTabs(DockHost);
        Documents.ContentOpened   += OnDocumentContentOpened;
        Documents.ContentReleased += OnDocumentContentReleased;
        ExplorerControl           =  new ExplorerView { PinnedDocuments = () => Documents.PinnedKeys };

        // the documents kept open are part of how the Silo's tree is remembered (see ExplorerView)
        Documents.ContentOpened      += (_, _) => ExplorerControl.DocumentsChanged();
        Documents.ContentReleased    += (_, _) => ExplorerControl.DocumentsChanged();
        Documents.PinnedChanged      += (_, _) => ExplorerControl.DocumentsChanged();
        _properties                  =  new PropertiesView();
        ExplorerPane.Content         =  ExplorerControl;
        PropertiesPane.Content       =  _properties;
        ExplorerControl.NodeSelected += OnExplorerSelected;
        ExplorerControl.NodeOpened   += OnExplorerOpened;
        ExplorerControl.SelectedNodeRefreshed += (_, node) =>
                                                 {
                                                     _properties.Show(node);
                                                     NodeCommandHub
                                                        .SetCurrent(node); // the tree was rebuilt: the selected node is a new object
                                                 };
        App.SiloClosing += OnSiloClosing;

        NavTree.ItemsSource       = _topRoots;
        NavRail.ItemsSource       = _topRoots;
        NavTreeBottom.ItemsSource = _bottomRoots;
        NavRailBottom.ItemsSource = _bottomRoots;

        // author-mode node customisations (label, icon size, hidden) are re-read by every node when they change
        NodeOverrides.Changed += (_, _) =>
                                 {
                                     foreach (var root in AllRoots()) RefreshOverrides(root);
                                 };

        // The pane starts closed - only the icon gutter shows until the person opens it.
        NavCollapseToggle.IsChecked = true;
        ApplyNavState(true);

        SyncNav();
        OpenSilo.OpenSilos.CollectionChanged += OnOpenSilosChanged;

        // Right-click a nav node: its commands (Open, Open as a tab, and whatever is registered for its item) - see
        // Commanding/ContextCommands.cs.
        ContextCommands.Attach(NavTree);
        ContextCommands.Attach(NavTreeBottom);

        // Ctrl+right-click the nav tree (in Author mode or above) for a pink design
        // menu with a live "Icon Size" slider - see Design/DesignMenu.cs.
        DesignMenu.EnsureItems(NavTree)
                  .Add(DesignMenuItem.Slider("Icon Size", DesignLevel.Author, 12, 64, () => AppDesignSettings.Instance.IconSize,
                                             v => AppDesignSettings.Instance.IconSize = v, "{0:0} px"));

        // The collapsed rail's width tracks the icon size while the slider is dragged.
        AppDesignSettings.Instance.PropertyChanged += (_, e) =>
                                                      {
                                                          if (e.PropertyName              == nameof(AppDesignSettings.IconSize) &&
                                                              NavCollapseToggle.IsChecked == true)
                                                              NavColumn.Width = NavCollapsedWidth;
                                                      };

        // AvalonDock draws its own chrome (tabs, title bars, splitters) from a theme object, not from the palette brushes,
        // so it is switched alongside the palette.
        ApplyDockTheme();

        // the layout the window starts with, for "Reset Window Layout" (and the first restore, should a Silo open before this)
        Loaded += (_, _) => _defaultDockXml ??= CaptureLayout();

        AppTheme.Instance.PropertyChanged += (_, e) =>
                                             {
                                                 if (e.PropertyName == nameof(AppTheme.IsEffectivelyDark)) ApplyDockTheme();
                                             };

        AddFileDesignMenu();

        // Ctrl+N / Ctrl+O / Ctrl+S ... -- the keys shown beside the menu items (see Commanding/Shortcuts.cs).
        Shortcuts.Install(this);

        // the terminal in the status bar, as in the Lab windows; Ctrl+` puts the cursor in it
        _terminal                      = new Ui.MainTerminal(this);
        StatusStrip.Terminal.Terminal  = _terminal;
        InputBindings.Add(new KeyBinding(new DelegateCommand(_ => StatusStrip.Terminal.FocusInput()), Key.OemTilde, ModifierKeys.Control));
        ClipboardCommands.Install(this); // Edit > Cut / Copy / Paste, Ctrl+X / C / V, Escape ends a cut

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

    /// <summary>
    ///     The arrangement of the panes as AvalonDock XML: which dock they sit in, their sizes, the floating ones, the hidden
    ///     ones.
    ///     Documents are left out -- the tabs are opened afresh each time.
    /// </summary>
    public string? CaptureLayout()
    {
        try
        {
            var xml = new StringBuilder();
            using (var writer = new StringWriter(xml))
            {
                new XmlLayoutSerializer(DockHost).Serialize(writer);
            }

            var doc = XDocument.Parse(xml.ToString());
            foreach (var element in doc.Descendants()
                                       .Where(e => e.Name.LocalName is "LayoutDocument" or "LayoutDocumentFloatingWindow")
                                       .ToList())
                element.Remove();

            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not capture the dock layout");
            return null;
        }
    }

    /// <summary>
    ///     Puts the panes back where <paramref name="layout" /> says. What the panes show stays; the open tabs are carried
    ///     over
    ///     into the document pane of the new layout; a pane the layout does not have is closed (the View menu opens it again).
    /// </summary>
    public void RestoreLayout(string layout)
    {
        _defaultDockXml ??= CaptureLayout();

        var fixedPanes = new Dictionary<string, LayoutAnchorable>
                         { ["explorer"] = ExplorerPane, ["properties"] = PropertiesPane, ["toolbox"] = ToolboxPane };
        var contents = fixedPanes.ToDictionary(p => p.Key, p => p.Value.Content);
        var tools    = ToolBase.PaneTools().ToDictionary(t => "tool:" + t.Name);
        var extras   = tools.Values.SelectMany(t => t.ExtraPaneKeys.Select(k => (Tool: t, Key: k))).ToDictionary(x => x.Tool.ExtraPaneId(x.Key));

        // the tabs are made again by nobody: take them out of the old layout and put them in the new one
        var tabs = DockHost.Layout.Descendents().OfType<LayoutDocument>().Select(d => (Doc: d, d.Content, Selected: d.IsSelected)).ToList();
        foreach (var tab in tabs) tab.Doc.Content = null;

        var placed      = new Dictionary<string, LayoutAnchorable>();
        var placedTools = new List<(ToolBase Tool, LayoutAnchorable Pane)>();
        var placedExtra = new Dictionary<string, LayoutAnchorable>();
        try
        {
            var serializer = new XmlLayoutSerializer(DockHost);
            serializer.LayoutSerializationCallback += (_, e) =>
                                                      {
                                                          if (e.Model is LayoutAnchorable { ContentId: { } id } anchorable)
                                                          {
                                                              if (contents.TryGetValue(id, out var content))
                                                              {
                                                                  e.Content  = content;
                                                                  placed[id] = anchorable;
                                                                  return;
                                                              }

                                                              if (tools.TryGetValue(id, out var tool))
                                                              {
                                                                  e.Content = tool.BuildPaneContent();
                                                                  placedTools.Add((tool, anchorable));
                                                                  return;
                                                              }

                                                              // a part of a tool undocked into a pane of its own
                                                              if (extras.TryGetValue(id, out var extra) && !placedExtra.ContainsKey(id) &&
                                                                  extra.Tool.TakeExtraPaneContent(extra.Key) is { } part)
                                                              {
                                                                  e.Content       = part;
                                                                  placedExtra[id] = anchorable;
                                                                  return;
                                                              }
                                                          }

                                                          e.Cancel = true; // a document, or something this version does not have
                                                      };

            using var reader = new StringReader(layout);
            serializer.Deserialize(reader);
        }
        catch
        {
            foreach (var tab in tabs) tab.Doc.Content = tab.Content;
            foreach (var (_, extra) in extras) extra.Tool.AdoptExtraPane(extra.Key, null); // parts that were taken out go back home
            throw;
        }

        // the panes of the new layout are the ones the rest of this window talks to; a pane the layout left out starts closed
        foreach (var (id, old) in fixedPanes)
        {
            var pane = placed.GetValueOrDefault(id) ?? new LayoutAnchorable { Title = old.Title, ContentId = id, Content = contents[id] };
            switch (id)
            {
                case "explorer":   ExplorerPane   = pane; break;
                case "properties": PropertiesPane = pane; break;
                default:           ToolboxPane    = pane; break;
            }
        }

        foreach (var (tool, pane) in placedTools) tool.AdoptPane(pane);
        foreach (var tool in tools.Values.Where(t => placedTools.All(p => p.Tool != t))) tool.ResetPane();
        foreach (var (id, extra) in extras) extra.Tool.AdoptExtraPane(extra.Key, placedExtra.GetValueOrDefault(id));

        Documents.Adopt(tabs);
    }

    private sealed record SiloNav(AppNavNode Node, EventHandler Renamed);
}