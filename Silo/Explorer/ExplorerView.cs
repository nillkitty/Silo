using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Serilog;
using Silo.Bindings;
using Silo.Clipboarding;
using Silo.Commanding;
using Silo.Connectors;
using Silo.Converters;
using Silo.Design;
using Silo.Drawio;
using Silo.Model;
using Silo.Tabular;
using Silo.Toolbar;
using Silo.Ui;
using Typing;

namespace Silo.Explorer;

/// <summary>
///     The Explorer pane: a browsable tree of the active Silo's worlds, folders and objects (think SSMS Object Explorer or
///     VS Code's Explorer), with MMC-style behaviour -- selecting a node shows what it is (Properties) and previews its
///     view,
///     double-click or Enter opens the view in a tab of its own. Its background is the editor surface colour, deliberately
///     unlike the navigation flyout beside it, which is chrome.
/// </summary>
public sealed class ExplorerView : UserControl, IClipboardSurface
{
    private static readonly KeyGestureConverter Gestures = new();
    private readonly        TextBlock           _hint;
    private readonly        HashSet<OpenSilo>   _hooked = new(ReferenceEqualityComparer.Instance);

    private readonly ExplorerStateSet                   _remembered = new();
    private readonly ObservableCollection<ExplorerNode> _roots      = [];
    private readonly DispatcherTimer                    _saveTimer;
    private readonly DispatcherTimer                    _settleTimer;
    private readonly TextBlock                          _title;
    private readonly TreeView                           _tree;
    private          (Point Start, ExplorerNode Node)?  _dragArm;
    private          bool                               _freshSelection;
    private          string?                            _lastSelectedKey;
    private          bool                               _rebuilding;
    private          bool                               _refreshQueued;
    private          string                             _signature = string.Empty;

    private OpenSilo? _silo;
    private bool      _userChoseSelection;

    /// <summary>
    ///     The keys of the document tabs the person is keeping open (set by the window that holds the tabs); the nodes whose
    ///     documents these
    ///     are come back open the next time the Silo is opened.
    /// </summary>
    public Func<IReadOnlyCollection<string>>? PinnedDocuments { get; set; }

    /// <summary>The node selected in the tree now, or null.</summary>
    public ExplorerNode? SelectedNode => _tree.SelectedItem as ExplorerNode;

    /// <summary>A document tab was opened, closed or kept: the remembered state is out of date.</summary>
    public void DocumentsChanged()
    {
        QueueSaveState();
    }

    /// <summary>A node was selected (null: the selection went away).</summary>
    public event EventHandler<ExplorerNode?>? NodeSelected;

    /// <summary>The tree was rebuilt (a table was saved or renamed...) and the selected node, now a new object, still exists.</summary>
    public event EventHandler<ExplorerNode>? SelectedNodeRefreshed;

    /// <summary>A node was double-clicked or Enter was pressed on it.</summary>
    public event EventHandler<ExplorerNode>? NodeOpened;

    private static HierarchicalDataTemplate MakeTemplate()
    {
        var template = new HierarchicalDataTemplate(typeof(ExplorerNode)) { ItemsSource = new Binding(nameof(ExplorerNode.Children)) };

        // icon, then the text taking what is left of the row and trimmed with an ellipsis: a long name never makes the tree scroll sideways
        var row = new FrameworkElementFactory(typeof(DockPanel));

        // the icon, with a small badge in its lower-right corner for the database the thing is kept in (nothing for a node with no realm)
        var iconHost = new FrameworkElementFactory(typeof(Grid));
        iconHost.SetValue(MarginProperty,            new Thickness(0, 0, 6, 0));
        iconHost.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        iconHost.SetValue(DockPanel.DockProperty,    Dock.Left);

        var icon = new FrameworkElementFactory(typeof(AppIcon));
        icon.SetBinding(AppIcon.IconProperty, new Binding(nameof(ExplorerNode.Icon)));
        icon.SetBinding(AppIcon.SizeProperty, new Binding(nameof(ExplorerNode.IconSize)));
        iconHost.AppendChild(icon);

        var badge = new FrameworkElementFactory(typeof(RealmBadge));
        badge.SetBinding(RealmBadge.RealmProperty,    new Binding(nameof(ExplorerNode.Realm)));
        badge.SetBinding(RealmBadge.WorldKeyProperty, new Binding(nameof(ExplorerNode.WorldKey)));
        badge.SetBinding(RealmBadge.SizeProperty,     new Binding(nameof(ExplorerNode.BadgeSize)));
        badge.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
        badge.SetValue(VerticalAlignmentProperty,   VerticalAlignment.Bottom);
        badge.SetValue(MarginProperty,              new Thickness(0, 0, -3, -3));
        iconHost.AppendChild(badge);
        row.AppendChild(iconHost);

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ExplorerNode.Display)));
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetBinding(ToolTipProperty, new Binding(nameof(ExplorerNode.Display)));
        row.AppendChild(text);

        template.VisualTree = row;
        return template;
    }

    private Style MakeContainerStyle()
    {
        var style = new Style(typeof(TreeViewItem), TryFindResource(typeof(TreeViewItem)) as Style);
        style.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty,
                                     new Binding(nameof(ExplorerNode.IsSelected)) { Mode = BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty,
                                     new Binding(nameof(ExplorerNode.IsExpanded)) { Mode = BindingMode.TwoWay }));

        // a node the author has hidden (Ctrl+right-click -> Hide this node) takes no room
        var hidden = new DataTrigger { Binding = new Binding(nameof(ExplorerNode.IsHidden)), Value = true };
        hidden.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        style.Triggers.Add(hidden);
        return style;
    }

    // ---- content -----------------------------------------------------------------------------

    private void QueueRefresh()
    {
        if (_refreshQueued) return;

        _refreshQueued = true;

        // The first build of the tree for a Silo that has just become active is the heavy one (every node, and what is remembered about
        // them): the status bar says so now, and gets to show it before the UI thread is taken up by the work.
        var building = App.Instance?.ActiveSilo is { } active && !ReferenceEquals(active, _silo)
                           ? AppStatus.Instance.Begin($"Building the Explorer for {active.Name ?? "the Silo"}")
                           : null;

        Dispatcher.BeginInvoke(new Action(async () =>
                                          {
                                              _refreshQueued = false;
                                              try
                                              {
                                                  if (building is not null) await Dispatcher.Yield(DispatcherPriority.Background);
                                                  Refresh(false);
                                              }
                                              finally
                                              {
                                                  building?.Dispose();
                                              }
                                          }), DispatcherPriority.Background);
    }

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueRefresh();
    }

    // nodes can be contributed from any thread (a connection initializing in the background, say)
    private void OnNodesChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(QueueRefresh));
    }

    /// <summary>
    ///     Rebuilds the tree from the active Silo, keeping what is expanded and selected. Does nothing if nothing visible
    ///     changed.
    /// </summary>
    public void Refresh(bool force)
    {
        var silo = App.Instance?.ActiveSilo;

        if (!ReferenceEquals(silo, _silo))
        {
            if (_silo is not null)
            {
                SaveState(); // while the tree on screen is still this Silo's
                _silo.Connections.CollectionChanged -= OnConnectionsChanged;
                _silo.Nodes.Changed                 -= OnNodesChanged;
                _silo.ConnectionsRestored           -= OnConnectionsRestored;
            }

            _settleTimer.Stop();
            _saveTimer.Stop();
            _remembered.Clear();
            _userChoseSelection = false;

            if (silo is not null)
            {
                silo.Connections.CollectionChanged += OnConnectionsChanged;
                silo.Nodes.Changed                 += OnNodesChanged;
                silo.ConnectionsRestored           += OnConnectionsRestored;
                LoadRemembered(silo);
            }

            force            = true;
            _lastSelectedKey = null;
        }

        bool newSilo = !ReferenceEquals(silo, _silo);
        _silo = silo;

        IReadOnlyList<ExplorerNode> built;
        try
        {
            built = silo is null ? [] : ExplorerTree.Build(silo);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Building the Explorer tree failed");
            built = [];
        }

        foreach (var root in built)
        {
            root.LinkParents();
            foreach (var n in root.Descendants())
            {
                var node = n;
                node.Opener = pin =>
                              {
                                  if (pin) NodeOpened?.Invoke(this, node);
                                  else NodeSelected?.Invoke(this, node);
                              };
                node.RebuildTree = () => Refresh(true);
            }
        }

        string signature = string.Join("\n", built.SelectMany(r => r.Descendants()).Select(n => n.Key + "|" + n.Header + "|" + n.Stamp));
        if (!force && signature == _signature) return;
        _signature = signature;

        var all      = _roots.SelectMany(r => r.Descendants()).ToList();
        var before   = all.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        var expanded = newSilo ? [] : all.Where(n => n.IsExpanded).Select(n => n.Key).ToHashSet();
        var selected = newSilo ? null : all.FirstOrDefault(n => n.IsSelected)?.Key;
        var restored = new List<NodeStateRecord>();

        _rebuilding = true;
        try
        {
            _roots.Clear();
            foreach (var r in built) _roots.Add(r);

            foreach (var n in built.SelectMany(r => r.Descendants()))
            {
                // a node the Silo remembered, appearing for the first time (it can be long after the tree was first built: a
                // connection that is still reconnecting, a folder read when it is expanded), takes its remembered state
                var saved = _remembered.Take(n.Key);
                if (saved is not null) restored.Add(saved);

                n.IsExpanded = saved is not null ? saved.Expanded :
                               newSilo ? n.ExpandByDefault : expanded.Contains(n.Key) || (n.ExpandByDefault && !before.Contains(n.Key));
                n.IsSelected      =  n.Key == selected;
                n.PropertyChanged += OnNodeChanged;
            }
        }
        finally
        {
            _rebuilding = false;
        }

        _title.Text      = silo is null ? "EXPLORER" : $"EXPLORER — {silo.Name ?? "Untitled"}";
        _hint.Visibility = silo is null ? Visibility.Visible : Visibility.Collapsed;
        _tree.Visibility = silo is null ? Visibility.Collapsed : Visibility.Visible;

        // the selection can have vanished with a deleted table
        var current = selected is null ? null : built.SelectMany(r => r.Descendants()).FirstOrDefault(n => n.Key == selected);
        if (selected is not null && current is null)
        {
            _lastSelectedKey = null;
            NodeSelected?.Invoke(this, null);
        }
        else if (current is not null && !newSilo)
        {
            SelectedNodeRefreshed?.Invoke(this, current);
        }

        if (restored.Count > 0) ShowRestored(restored, selected is not null);
    }

    // ---- remembering how the tree was left -----------------------------------------------------

    /// <summary>Reads what the Silo remembered, and makes sure the state is written before the Silo is saved or closed.</summary>
    private void LoadRemembered(OpenSilo silo)
    {
        try
        {
            if (silo.RememberExplorerTree && silo.Data is INodeStateStore store) _remembered.Load(store.GetNodeStates());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading the remembered Explorer state failed");
        }

        if (_hooked.Add(silo))
            silo.SaveHooks.Add(() =>
                               {
                                   if (ReferenceEquals(_silo, silo)) SaveState();
                                   return Task.CompletedTask;
                               });
    }

    /// <summary>
    ///     Opens, in permanent tabs, the documents of the nodes that were open when the Silo was last used, then selects the
    ///     node that was
    ///     selected -- unless the person has chosen something themselves meanwhile. Done a moment later, when the window has
    ///     finished
    ///     setting itself up and the nodes it is about to open exist in the tree that is then showing.
    /// </summary>
    private void ShowRestored(IReadOnlyList<NodeStateRecord> restored, bool somethingIsSelected)
    {
        var open   = restored.Where(r => r.Open).Select(r => r.Key).ToList();
        var select = somethingIsSelected || _userChoseSelection ? null : restored.FirstOrDefault(r => r.Selected)?.Key;
        if (open.Count == 0 && select is null) return;

        var silo   = _silo;
        var status = open.Count > 0 ? AppStatus.Instance.Begin("Reopening documents") : null;
        Dispatcher.BeginInvoke(new Action(async () =>
                                          {
                                              try
                                              {
                                                  if (!ReferenceEquals(silo, _silo)) return;

                                                  var nodes = _roots.SelectMany(r => r.Descendants())
                                                                    .ToDictionary(n => n.Key, n => n, StringComparer.Ordinal);
                                                  int done = 0;
                                                  foreach (var key in open)
                                                  {
                                                      // each tab takes a while to build: say which, and let the window breathe between them
                                                      status!.Detail = $"{++done} of {open.Count}";
                                                      await Dispatcher.Yield(DispatcherPriority.Background);
                                                      if (!ReferenceEquals(silo, _silo)) return;

                                                      if (nodes.TryGetValue(key, out var node) && node.DocKey is not null &&
                                                          node.Open is not null)
                                                          NodeOpened?.Invoke(this, node);
                                                  }

                                                  if (select is not null && !_userChoseSelection && _tree.SelectedItem is null &&
                                                      nodes.TryGetValue(select, out var chosen))
                                                      chosen.IsSelected = true;

                                                  status?.Done($"Reopened {open.Count} document{(open.Count == 1 ? string.Empty : "s")}");
                                              }
                                              finally
                                              {
                                                  status?.Dispose();
                                              }
                                          }), DispatcherPriority.Background);
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_rebuilding && e.PropertyName is nameof(ExplorerNode.IsExpanded) or nameof(ExplorerNode.IsSelected)) QueueSaveState();
    }

    private void QueueSaveState()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    ///     Writes how the tree is now: the nodes that are expanded, open in a tab or selected (and the default-open nodes that
    ///     were closed),
    ///     together with what the Silo remembered for nodes that have not come back yet. Does not make the Silo "modified".
    /// </summary>
    private void SaveState()
    {
        _saveTimer.Stop();

        var silo = _silo;
        if (silo is not { RememberExplorerTree: true } || silo.Data is not INodeStateStore store || silo.IsClosing ||
            !OpenSilo.OpenSilos.Contains(silo)) return;

        try
        {
            var pinned = (PinnedDocuments?.Invoke() ?? []).ToHashSet(StringComparer.Ordinal);
            var live   = new List<NodeStateRecord>();
            foreach (var n in _roots.SelectMany(r => r.Descendants()))
            {
                if (n.Kind == "Placeholder") continue; // "Loading..." and the like are not nodes of the tree

                bool open = n.DocKey is not null && n.Open is not null && pinned.Contains(n.DocKey);
                if (n.IsExpanded || open || n.IsSelected || n.ExpandByDefault)
                    live.Add(new NodeStateRecord(n.Key, n.Header, n.IsExpanded, open, n.IsSelected));
            }

            store.SaveNodeStates(_remembered.Merge(live));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Remembering the state of the Explorer failed");
        }
    }

    private void OnConnectionsRestored(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
                                          {
                                              _settleTimer.Stop();
                                              if (_remembered.Count > 0) _settleTimer.Start();
                                          }));
    }

    /// <summary>
    ///     The connections have had their chance to come back and a while has passed: remembered nodes that never reappeared
    ///     (a deleted table,
    ///     a folder that is gone) are forgotten, except those under a connection that has not come back yet.
    /// </summary>
    private void Settle()
    {
        _settleTimer.Stop();

        var keep    = _roots.SelectMany(r => r.Descendants()).Where(n => n.Model is PendingConnection).Select(n => n.Key).ToList();
        int dropped = _remembered.DropUnmatched(keep);
        if (dropped > 0) Log.Debug("Forgot {Count} remembered Explorer node(s) that did not come back", dropped);
    }

    // ---- interaction -------------------------------------------------------------------------

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_rebuilding || e.NewValue is not ExplorerNode node) return;

        // a rebuild re-selects the same node later, when its container exists: that is not a new selection
        if (node.Key == _lastSelectedKey) return;

        _lastSelectedKey = node.Key;
        _freshSelection  = true;
        NodeSelected?.Invoke(this, node);
    }

    /// <summary>Clicking the node that is already selected shows its view again (e.g. after its tab was closed).</summary>
    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_freshSelection)
        {
            _freshSelection = false;
            return;
        }

        if (e.OriginalSource is DependencyObject d && FindItem(d) is { DataContext: ExplorerNode node } &&
            ReferenceEquals(node, _tree.SelectedItem))
            NodeSelected?.Invoke(this, node);
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // every enclosing item sees the same double-click; only the innermost, i.e. the one under the pointer, acts
        if (e.OriginalSource is DependencyObject d && FindItem(d) is { DataContext: ExplorerNode node } &&
            ReferenceEquals(node, _tree.SelectedItem))
        {
            e.Handled = true;
            NodeOpened?.Invoke(this, node);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _tree.SelectedItem is ExplorerNode node)
        {
            e.Handled = true;
            NodeOpened?.Invoke(this, node);
        }
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindItem(d) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    /// <summary>A key that is a shortcut of one of the selected node's commands (F5, Ctrl+Shift+C...) runs it.</summary>
    private void OnShortcut(object sender, KeyEventArgs e)
    {
        // Ctrl+C / X / V are Copy, Cut and Paste when the selected node's model takes part in the clipboard (a node command that
        // also uses Ctrl+C, such as "Copy URI", gives way to it)
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.C or Key.X or Key.V &&
            ClipboardCommands.Claims(e.Key, GetClipboardContext()))
            return;

        // typing a letter to jump to an item is not a shortcut: ask the node only for a function key, Delete or a modified key
        bool candidate = Keyboard.Modifiers != ModifierKeys.None || e.Key is >= Key.F1 and <= Key.F24 or Key.Delete;
        if (!candidate || _tree.SelectedItem is not ExplorerNode node) return;

        try
        {
            foreach (var c in NodeCommandHub.CommandsFor(node).SelectMany(c => c.SelfAndDescendants()))
            {
                if (c.Gesture is not { Length: > 0 } text || Gestures.ConvertFromInvariantString(text) is not KeyGesture gesture) continue;
                if (!gesture.Matches(_tree, e)            || !c.Command.CanExecute(c.Parameter)) continue;

                e.Handled = true;
                c.Command.Execute(c.Parameter);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Running an Explorer shortcut failed");
        }
    }

    /// <summary>
    ///     A node's model is what is copied and cut; the models of the node and of every node above it are offered a paste,
    ///     the
    ///     node's own first.
    /// </summary>
    private static ClipboardContext ContextFor(ExplorerNode? node)
    {
        if (node is null) return ClipboardContext.Empty;

        IReadOnlyList<ICopyable> selection = node.Model is ICopyable copyable ? [copyable] : [];
        return new ClipboardContext(selection, TargetsFor(node));
    }

    private static IReadOnlyList<IPastable> TargetsFor(ExplorerNode? node)
    {
        var targets = new List<IPastable>();
        for (var n = node; n is not null; n = n.Parent)
            if (n.Model is IPastable p && !targets.Any(t => ReferenceEquals(t, p)))
                targets.Add(p);

        return targets;
    }

    private void OnDragArm(object sender, MouseButtonEventArgs e)
    {
        _dragArm = e.OriginalSource is DependencyObject d && FindItem(d) is { DataContext: ExplorerNode { Model: ICopyable } node }
                       ? (e.GetPosition(this), node)
                       : null;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragArm is not { } arm) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragArm = null;
            return;
        }

        var now = e.GetPosition(this);
        if (Math.Abs(now.X - arm.Start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - arm.Start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragArm = null;
        var payload = AppClipboard.BeginDrag(ContextFor(arm.Node));
        if (payload is null) return;

        try
        {
            DragDrop.DoDragDrop(_tree, payload.Data, payload.CanMove ? DragDropEffects.Move | DragDropEffects.Copy : DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Dragging an Explorer node failed");
        }
        finally
        {
            AppClipboard.EndDrag();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = AppClipboard.DragEffect(e.Data, TargetsAt(e.OriginalSource), e.AllowedEffects,
                                            (e.KeyStates & DragDropKeyStates.ControlKey) != 0);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            e.Effects = AppClipboard.Drop(e.Data, TargetsAt(e.OriginalSource), e.AllowedEffects,
                                          (e.KeyStates & DragDropKeyStates.ControlKey) != 0);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Dropping onto an Explorer node failed");
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private static IReadOnlyList<IPastable> TargetsAt(object source)
    {
        return source is DependencyObject d && FindItem(d) is { DataContext: ExplorerNode node } ? TargetsFor(node) : [];
    }

    public ExplorerView()
    {
        _title = new TextBlock
                 {
                     FontWeight   = FontWeights.SemiBold, Margin = new Thickness(8, 6, 8, 6),
                     TextTrimming = TextTrimming.CharacterEllipsis
                 };

        var header = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = _title };
        header.SetResourceReference(Border.BackgroundProperty,  "Theme.BackgroundAlt");
        header.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        DockPanel.SetDock(header, Dock.Top);

        _hint = new TextBlock
                {
                    Text   = "Open or create a Silo to browse it here.", TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12)
                };
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        DockPanel.SetDock(_hint, Dock.Top);

        _tree = new TreeView
                {
                    BorderThickness    = new Thickness(0), ItemsSource = _roots, ItemTemplate = MakeTemplate(),
                    ItemContainerStyle = MakeContainerStyle()
                };
        ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);
        _tree.SelectedItemChanged += OnSelectedItemChanged;
        _tree.MouseDoubleClick    += OnDoubleClick;
        _tree.PreviewMouseLeftButtonDown += (_, _) =>
                                            {
                                                _freshSelection     = false;
                                                _userChoseSelection = true; // from now on a remembered selection does not override theirs
                                            };
        _tree.PreviewKeyDown              += (_, _) => _userChoseSelection = true;
        _tree.PreviewMouseLeftButtonUp    += OnLeftUp;
        _tree.KeyDown                     += OnKeyDown;
        _tree.PreviewKeyDown              += OnShortcut;
        _tree.PreviewMouseRightButtonDown += OnRightClick;

        // Ctrl+right-click empty space in the tree (Author level or above) for the Explorer-wide icon size
        foreach (var item in ExplorerNode.AllIconsMenuItems()) DesignMenu.EnsureItems(_tree).Add(item);
        // the right-click menu: the node's commands (see ContextCommands), then Cut / Copy / Paste when its model takes part
        ContextCommands.Attach(_tree,
                               target => ReferenceEquals(target, _tree) ? [] : ClipboardCommands.MenuItems(_tree, GetClipboardContext()));

        // dragging a node that can be copied moves it (Ctrl: duplicates it) into a container that accepts it; see Clipboarding/
        _tree.AllowDrop                  =  true;
        _tree.PreviewMouseLeftButtonDown += OnDragArm;
        _tree.PreviewMouseMove           += OnDragMove;
        _tree.DragOver                   += OnDragOver;
        _tree.Drop                       += OnDrop;

        var root = new DockPanel();
        root.Children.Add(header);
        root.Children.Add(_hint);
        root.Children.Add(_tree);
        Content = root;

        // how the tree was left is written shortly after it changes (and always with the Silo's save, see HookSaving)
        _saveTimer        =  new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick   += (_, _) => SaveState();
        _settleTimer      =  new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        _settleTimer.Tick += (_, _) => Settle();

        Loaded += (_, _) => Refresh(false);

        if (App.Instance is { } app)
        {
            app.ActiveSiloChanged += (_, _) => QueueRefresh();
            app.Nodes.Changed     += OnNodesChanged; // the tools' nodes
        }

        TableEditors.Changed      += (_, _) => QueueRefresh();
        DiagramEvents.Changed     += (_, _) => QueueRefresh();
        DataBindingEvents.Changed += (_, _) => QueueRefresh();
        DefinitionEvents.Changed  += (_, _) => QueueRefresh();
        NodeOverrides.Changed += (_, _) =>
                                 {
                                     foreach (var node in _roots.SelectMany(r => r.Descendants()).ToList())
                                         node.RefreshOverrides();
                                 };
        Refresh(true);
    }

    // ---- clipboard, and drag and drop ----------------------------------------------------------

    /// <summary>What Copy, Cut and Paste mean for the selected node (see <see cref="ClipboardFocus" />).</summary>
    public ClipboardContext GetClipboardContext()
    {
        return ContextFor(SelectedNode);
    }

    private static TreeViewItem? FindItem(DependencyObject d)
    {
        while (d is not null and not TreeViewItem)
            d = d is Visual || d is Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }
}