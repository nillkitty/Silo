using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using AvalonDock.Themes;
using Silo.Design;
using Silo.Explorer;
using Silo.Hosting;
using Silo.Model;
using Silo.Nav;
using Silo.Remote;
using Silo.Settings;
using Silo.Tabular;
using Silo.Theming;
using Silo.Tools;
using Silo.Ui.Documents;
using Silo.Ui.Windows;

namespace Silo;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window, IWindowLayout
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

    /// <summary>The document tabs in the middle of the window (tables, navigation views...).</summary>
    public DocumentTabs Documents { get; private set; } = null!;

    private ExplorerView   _explorer   = null!;
    private PropertiesView _properties = null!;

    public MainWindow()
    {
        InitializeComponent();

        // Explorer on the left, Properties on the right, documents between them (see the DockHost layout in the XAML)
        Documents   = new DocumentTabs(DockHost);
        _explorer   = new ExplorerView();
        _properties = new PropertiesView();
        ExplorerPane.Content   = _explorer;
        PropertiesPane.Content = _properties;
        _explorer.NodeSelected         += OnExplorerSelected;
        _explorer.NodeOpened           += OnExplorerOpened;
        _explorer.SelectedNodeRefreshed += (_, node) => _properties.Show(node);
        App.SiloClosing                += OnSiloClosing;

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

        // the layout the window starts with, for "Reset Window Layout" (and the first restore, should a Silo open before this)
        Loaded += (_, _) => _defaultDockXml ??= CaptureLayout();

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

        // The window takes the arrangement the Silo remembers, but only the first Silo opened into a window gets to rearrange it
        // (a second Silo opened beside it does not move the window around); every Silo keeps what the window looked like when saved.
        if (_watched is { } silo)
        {
            bool restore = OpenSilo.OpenSilos.Count <= 1;
            Dispatcher.BeginInvoke(new Action(() => WindowPlacements.Track(this, silo, WindowPlacements.Main, "silo:main", this, restore, saveOnClose: false)),
                                   System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    // --- remembered arrangement (the Window table of the Silo, see WindowPlacements) -----------------------------

    /// <summary>The dock layout the window was built with (null until the window has loaded).</summary>
    private string? _defaultDockXml;

    /// <summary>
    ///     The arrangement of the panes as AvalonDock XML: which dock they sit in, their sizes, the floating ones, the hidden ones.
    ///     Documents are left out -- the tabs are opened afresh each time.
    /// </summary>
    public string? CaptureLayout()
    {
        try
        {
            var xml = new StringBuilder();
            using (var writer = new System.IO.StringWriter(xml))
                new XmlLayoutSerializer(DockHost).Serialize(writer);

            var doc = System.Xml.Linq.XDocument.Parse(xml.ToString());
            foreach (var element in doc.Descendants().Where(e => e.Name.LocalName is "LayoutDocument" or "LayoutDocumentFloatingWindow").ToList())
                element.Remove();

            return doc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not capture the dock layout");
            return null;
        }
    }

    /// <summary>
    ///     Puts the panes back where <paramref name="layout" /> says. What the panes show stays; the open tabs are carried over
    ///     into the document pane of the new layout; a pane the layout does not have is closed (the View menu opens it again).
    /// </summary>
    public void RestoreLayout(string layout)
    {
        _defaultDockXml ??= CaptureLayout();

        var fixedPanes = new Dictionary<string, LayoutAnchorable> { ["explorer"] = ExplorerPane, ["properties"] = PropertiesPane, ["toolbox"] = ToolboxPane };
        var contents   = fixedPanes.ToDictionary(p => p.Key, p => p.Value.Content);
        var tools      = ToolBase.PaneTools().ToDictionary(t => "tool:" + t.Name);

        // the tabs are made again by nobody: take them out of the old layout and put them in the new one
        var tabs = DockHost.Layout.Descendents().OfType<LayoutDocument>().Select(d => (Doc: d, Content: d.Content, Selected: d.IsSelected)).ToList();
        foreach (var tab in tabs) tab.Doc.Content = null;

        var placed      = new Dictionary<string, LayoutAnchorable>();
        var placedTools = new List<(ToolBase Tool, LayoutAnchorable Pane)>();
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
                                                          }

                                                          e.Cancel = true; // a document, or something this version does not have
                                                      };

            using var reader = new System.IO.StringReader(layout);
            serializer.Deserialize(reader);
        }
        catch
        {
            foreach (var tab in tabs) tab.Doc.Content = tab.Content;
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

        Documents.Adopt(tabs);
    }

    /// <summary>View -> Reset Window Layout: the panes go back to where a new window has them, and the remembered sizes of windows are forgotten.</summary>
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
            Serilog.Log.Warning(ex, "Resetting the window layout failed");
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
        foreach (var root in AllRoots()) WireNode(root);
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
            selected.IsSelected = false;
            _selectedNode       = null;
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
    ///     hand, now done generically from whichever <see cref="AppNavNode"/> got selected:
    ///     (1) walk its <see cref="AppNavItem.Parent"/> chain up to its top-level node
    ///     so NavRail (the collapsed pane's icon gutter) can highlight the right one, and
    ///     (2) if the node has a real <see cref="AppNavItem.View"/>, build its content and host
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

        // working inside another Silo's branch makes that Silo the active one
        if (SiloOf(root) is { } silo && !ReferenceEquals(App.ActiveSilo, silo))
            App.ActiveSilo = silo;

        // a group (Silo, Schema, ...) has no view of its own: it shows the first one beneath it, e.g. Silo shows Search.
        // The view opens as the preview tab; double-clicking the node pins it (NavTree_DoubleClick).
        if (FirstView(node) is { } view)
            OpenView(view, false);
    }

    /// <summary>The document tab key of a navigation view: views are identified by name, whichever node reached them.</summary>
    private static string ViewKey(AppView view) => "view:" + view.Name;

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
            d = (d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D) ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is not TreeViewItem { DataContext: AppNavNode hit } || !ReferenceEquals(hit, node))
            return;

        if (FirstView(node) is { } view)
        {
            e.Handled = true;
            OpenView(view, true);
        }
    }

    /// <summary>The selected document tab's content, when it adds items to the Edit menu (a table does); else null.</summary>
    public IEditContributor? ActiveEditContributor => Documents.SelectedContent as IEditContributor;

    // --- the Explorer tree and the Properties pane -----------------------------------------------

    /// <summary>A click (or arrow key) in the Explorer: the details go to Properties and the node's view, if any, to the preview tab.</summary>
    private void OnExplorerSelected(object? sender, ExplorerNode? node)
    {
        _properties.Show(node);
        if (node is { DocKey: { } key, Open: { } open })
            Documents.Open(key, node.Header, open, node.Closed, false);
    }

    /// <summary>A double-click (or Enter) in the Explorer keeps the node's view open as a permanent tab.</summary>
    private void OnExplorerOpened(object? sender, ExplorerNode node)
    {
        if (node is { DocKey: { } key, Open: { } open })
            Documents.Open(key, node.Header, open, node.Closed, true);
        else
            node.Activate?.Invoke();
    }

    /// <summary>A Silo is about to close: its table tabs go first (saving what they hold).</summary>
    private void OnSiloClosing(object? sender, OpenSilo silo)
    {
        string prefix = TableEditors.DocPrefix(silo);
        Documents.CloseWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
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

    /// <summary>Whether the Explorer pane (the browsable tree on the left) is showing.</summary>
    public bool IsExplorerOpen => IsOpen(ExplorerPane);

    /// <summary>Whether the Properties pane is showing.</summary>
    public bool IsPropertiesOpen => IsOpen(PropertiesPane);

    /// <summary>Whether the Toolbox pane is showing (closed or hidden panes are not).</summary>
    public bool IsToolboxOpen => IsOpen(ToolboxPane);

    public void SetExplorerOpen(bool open) => SetOpen(ExplorerPane, open, AnchorableShowStrategy.Left);

    public void SetPropertiesOpen(bool open) => SetOpen(PropertiesPane, open, AnchorableShowStrategy.Right);

    public void SetToolboxOpen(bool open) => SetOpen(ToolboxPane, open, AnchorableShowStrategy.Right);

    private static bool IsOpen(LayoutAnchorable pane) => pane.Root is not null && pane.IsVisible;

    /// <summary>Whether a tool's pane is showing, docked or floating.</summary>
    public bool IsToolPaneOpen(LayoutAnchorable pane) => IsOpen(pane);

    /// <summary>
    ///     Opens a tool's pane: the first time it is added to the bottom of the dock area (it can be dragged out into a
    ///     floating window from there, or onto any side), afterwards it returns to wherever it was.
    /// </summary>
    public void ShowToolPane(LayoutAnchorable pane)
    {
        if (pane.Root is null)
        {
            pane.AddToLayout(DockHost, AnchorableShowStrategy.Bottom);
            if (pane.Parent is LayoutAnchorablePane group) group.DockHeight = new GridLength(360);
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
    ///     the full tree would have - <see cref="NavTree_SelectedItemChanged"/> keeps the
    ///     highlight and content pane in sync, since setting <see cref="AppNavNode.IsSelected"/>
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
}
