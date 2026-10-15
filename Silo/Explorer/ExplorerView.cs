using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Silo.Design;
using Silo.Drawio;
using Silo.Model;
using Silo.Tabular;

namespace Silo.Explorer;

/// <summary>
///     The Explorer pane: a browsable tree of the active Silo's worlds, folders and objects (think SSMS Object Explorer or
///     VS Code's Explorer), with MMC-style behaviour -- selecting a node shows what it is (Properties) and previews its view,
///     double-click or Enter opens the view in a tab of its own. Its background is the editor surface colour, deliberately
///     unlike the navigation flyout beside it, which is chrome.
/// </summary>
public sealed class ExplorerView : UserControl
{
    private readonly ObservableCollection<ExplorerNode> _roots = [];
    private readonly TreeView                           _tree;
    private readonly TextBlock                          _title;
    private readonly TextBlock                          _hint;

    private OpenSilo? _silo;
    private string    _signature = string.Empty;
    private string?   _lastSelectedKey;
    private bool      _rebuilding;
    private bool      _freshSelection;
    private bool      _refreshQueued;

    public ExplorerView()
    {
        _title = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 6, 8, 6), TextTrimming = TextTrimming.CharacterEllipsis };

        var header = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = _title };
        header.SetResourceReference(Border.BackgroundProperty, "Theme.BackgroundAlt");
        header.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        DockPanel.SetDock(header, Dock.Top);

        _hint = new TextBlock { Text = "Open or create a Silo to browse it here.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        DockPanel.SetDock(_hint, Dock.Top);

        _tree = new TreeView { BorderThickness = new Thickness(0), ItemsSource = _roots, ItemTemplate = MakeTemplate(), ItemContainerStyle = MakeContainerStyle() };
        _tree.SelectedItemChanged        += OnSelectedItemChanged;
        _tree.MouseDoubleClick           += OnDoubleClick;
        _tree.PreviewMouseLeftButtonDown += (_, _) => _freshSelection = false;
        _tree.PreviewMouseLeftButtonUp   += OnLeftUp;
        _tree.KeyDown                    += OnKeyDown;
        _tree.PreviewMouseRightButtonDown += OnRightClick;
        _tree.ContextMenu                =  new ContextMenu();
        _tree.ContextMenuOpening         += OnContextMenuOpening;

        var root = new DockPanel();
        root.Children.Add(header);
        root.Children.Add(_hint);
        root.Children.Add(_tree);
        Content = root;

        Loaded += (_, _) => Refresh(false);

        if (App.Instance is { } app)
            app.ActiveSiloChanged += (_, _) => QueueRefresh();
        TableEditors.Changed += (_, _) => QueueRefresh();
        DiagramEvents.Changed += (_, _) => QueueRefresh();
        NodeOverrides.Changed += (_, _) =>
                                 {
                                     foreach (var node in _roots.SelectMany(r => r.Descendants()).ToList())
                                         node.RefreshOverrides();
                                 };
        Refresh(true);
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

        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var icon = new FrameworkElementFactory(typeof(AppIcon));
        icon.SetBinding(AppIcon.IconProperty, new Binding(nameof(ExplorerNode.Icon)));
        icon.SetBinding(AppIcon.SizeProperty, new Binding(nameof(ExplorerNode.IconSize)));
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(icon);

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ExplorerNode.Display)));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(text);

        template.VisualTree = row;
        return template;
    }

    private Style MakeContainerStyle()
    {
        var style = new Style(typeof(TreeViewItem), TryFindResource(typeof(TreeViewItem)) as Style);
        style.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding(nameof(ExplorerNode.IsSelected)) { Mode = BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(ExplorerNode.IsExpanded)) { Mode = BindingMode.TwoWay }));

        // a node the author has hidden (Ctrl+right-click -> Hide this node) takes no room
        var hidden = new DataTrigger { Binding = new Binding(nameof(ExplorerNode.IsHidden)), Value = true };
        hidden.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
        style.Triggers.Add(hidden);
        return style;
    }

    // ---- content -----------------------------------------------------------------------------

    private void QueueRefresh()
    {
        if (_refreshQueued) return;

        _refreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
                                          {
                                              _refreshQueued = false;
                                              Refresh(false);
                                          }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRefresh();

    /// <summary>Rebuilds the tree from the active Silo, keeping what is expanded and selected. Does nothing if nothing visible changed.</summary>
    public void Refresh(bool force)
    {
        var silo = App.Instance?.ActiveSilo;

        if (!ReferenceEquals(silo, _silo))
        {
            if (_silo is not null) _silo.Connections.CollectionChanged -= OnConnectionsChanged;
            if (silo is not null) silo.Connections.CollectionChanged += OnConnectionsChanged;
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
            Serilog.Log.Error(ex, "Building the Explorer tree failed");
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
            }
        }

        string signature = string.Join("\n", built.SelectMany(r => r.Descendants()).Select(n => n.Key + "|" + n.Header + "|" + n.Stamp));
        if (!force && signature == _signature) return;
        _signature = signature;

        var all      = _roots.SelectMany(r => r.Descendants()).ToList();
        var expanded = newSilo ? [] : all.Where(n => n.IsExpanded).Select(n => n.Key).ToHashSet();
        var selected = newSilo ? null : all.FirstOrDefault(n => n.IsSelected)?.Key;

        _rebuilding = true;
        try
        {
            _roots.Clear();
            foreach (var r in built) _roots.Add(r);

            foreach (var n in built.SelectMany(r => r.Descendants()))
            {
                n.IsExpanded = newSilo ? n.ExpandByDefault : expanded.Contains(n.Key) || (n.ExpandByDefault && !all.Any(o => o.Key == n.Key));
                n.IsSelected = n.Key == selected;
            }
        }
        finally
        {
            _rebuilding = false;
        }

        _title.Text         = silo is null ? "EXPLORER" : $"EXPLORER — {silo.Name ?? "Untitled"}";
        _hint.Visibility    = silo is null ? Visibility.Visible : Visibility.Collapsed;
        _tree.Visibility    = silo is null ? Visibility.Collapsed : Visibility.Visible;

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

        if (e.OriginalSource is DependencyObject d && FindItem(d) is { DataContext: ExplorerNode node } && ReferenceEquals(node, _tree.SelectedItem))
            NodeSelected?.Invoke(this, node);
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // every enclosing item sees the same double-click; only the innermost, i.e. the one under the pointer, acts
        if (e.OriginalSource is DependencyObject d && FindItem(d) is { DataContext: ExplorerNode node } && ReferenceEquals(node, _tree.SelectedItem))
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

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = _tree.ContextMenu!;
        menu.Items.Clear();

        IEnumerable<ExplorerAction> actions = [];
        try
        {
            actions = (_tree.SelectedItem as ExplorerNode)?.Actions?.Invoke() ?? [];
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Building an Explorer menu failed");
        }

        foreach (var a in actions)
        {
            var item = new MenuItem { Header = a.Header };
            var run  = a.Run;
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0) e.Handled = true; // nothing to offer: no menu at all
    }

    private static TreeViewItem? FindItem(DependencyObject d)
    {
        while (d is not null and not TreeViewItem)
            d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }
}
