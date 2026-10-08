using System.Windows;
using System.Windows.Controls;
using Silo.Design;
using Silo.Nav;

namespace Silo;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>NavColumn's width while the nav pane is expanded - 300 matches its original XAML default.</summary>
    private static readonly GridLength _navExpandedWidth = new(300);

    /// <summary>
    ///     NavColumn's width while collapsed - just wide enough for the header bar's
    ///     toggle button to remain clickable (see the Grid in MainWindow.xaml's nav cell),
    ///     so re-expanding is always reachable.
    /// </summary>
    private static readonly GridLength _navCollapsedWidth = new(32);

    /// <summary>
    ///     The nav tree's top-level nodes (Silo, Connections, Settings, Performance, Inbox,
    ///     Profile - see <see cref="AppNavTree.BuildRoots"/>). NavTree and NavRail are both
    ///     bound to this same list; <see cref="NavTree_SelectedItemChanged"/> walks a selected
    ///     node's <see cref="AppNavItem.Parent"/> chain back to whichever of these it descends
    ///     from to drive NavRail's highlight.
    /// </summary>
    private readonly IReadOnlyList<AppNavNode> _navRoots;

    public App App => App.Instance!;

    public MainWindow()
    {
        InitializeComponent();

        _navRoots = AppNavTree.BuildRoots();
        NavTree.ItemsSource = _navRoots;
        NavRail.ItemsSource = _navRoots;

        // Ctrl+right-click the nav tree (in Author mode or above) for a pink design
        // menu with a live "Icon Size" slider - see Design/DesignMenu.cs.
        DesignMenu.EnsureItems(NavTree).Add(DesignMenuItem.Slider(
            "Icon Size", DesignLevel.Author,
            min: 12, max: 64,
            getValue: () => AppDesignSettings.Instance.IconSize,
            setValue: v => AppDesignSettings.Instance.IconSize = v,
            format: "{0:0} px"));
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
        bool collapsed = NavCollapseToggle.IsChecked == true;
        NavColumn.Width  = collapsed ? _navCollapsedWidth : _navExpandedWidth;
        NavTree.Visibility        = collapsed ? Visibility.Collapsed : Visibility.Visible;
        NavHeaderText.Visibility  = collapsed ? Visibility.Collapsed : Visibility.Visible;
        // The collapsed-state icon gutter (NavRail) only makes sense, and only fits,
        // while the tree itself is hidden - the two share the same Grid cell.
        NavRail.Visibility        = collapsed ? Visibility.Visible : Visibility.Collapsed;
        NavCollapseToggle.ToolTip = collapsed ? "Expand navigation pane" : "Collapse navigation pane";
    }

    /// <summary>
    ///     The two jobs MainWindow's old per-item TreeViewItem/RailButton pairs used to do by
    ///     hand, now done generically from whichever <see cref="AppNavNode"/> got selected:
    ///     (1) walk its <see cref="AppNavItem.Parent"/> chain up to one of <see cref="_navRoots"/>
    ///     so NavRail (the collapsed pane's icon gutter) can highlight the right one, and
    ///     (2) if the node has a real <see cref="AppNavItem.View"/>, build its content and host
    ///     it in <see cref="SelectedItemPane"/>. A node with no view (a group header like
    ///     "Schema", or a not-yet-wired setting) leaves whatever content was already showing
    ///     rather than clearing it to something misleading.
    /// </summary>
    private void NavTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not AppNavNode node)
            return;

        var root = node.Item;
        while (root.Parent is { } parent)
            root = parent;

        foreach (var candidate in _navRoots)
            candidate.IsActiveRoot = ReferenceEquals(candidate.Item, root);

        if (node.Item.View is { } view)
        {
            SelectedItemPane.Content = view.ContentBuilder();
            SelectedItemPane.Title   = view.Name;
        }
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
            node.IsSelected = true;
    }
}
