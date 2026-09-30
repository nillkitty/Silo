using System.Windows;
using System.Windows.Controls;
using Silo.Design;

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

    public App App => App.Instance!;

    public MainWindow()
    {
        InitializeComponent();

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
    ///     Tracks which top-level nav group (<see cref="SiloRootItem"/> or
    ///     <see cref="ConnectionsRootItem"/>) contains the current selection, so NavRail -
    ///     the collapsed pane's icon gutter - can highlight it (see
    ///     <see cref="_updateNavRailHighlight"/>). Walks up via
    ///     <see cref="ItemsControl.ItemsControlFromItemContainer"/> rather than the visual
    ///     tree, since for a XAML-declared (non-data-bound) TreeView like this one, each
    ///     TreeViewItem's logical parent in the items hierarchy IS either another
    ///     TreeViewItem or the TreeView itself.
    /// </summary>
    private void NavTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var current = e.NewValue as TreeViewItem;
        while (current != null && ItemsControl.ItemsControlFromItemContainer(current) is TreeViewItem parent)
            current = parent;

        _activeTopLevel = current;
        _updateNavRailHighlight();
    }

    private TreeViewItem? _activeTopLevel;

    private void _updateNavRailHighlight()
    {
        SiloRailButton.IsChecked        = ReferenceEquals(_activeTopLevel, SiloRootItem);
        ConnectionsRailButton.IsChecked = ReferenceEquals(_activeTopLevel, ConnectionsRootItem);
    }

    /// <summary>
    ///     Selecting a group from NavRail while collapsed picks up exactly where clicking
    ///     it in the full tree would have - <see cref="NavTree_SelectedItemChanged"/> keeps
    ///     the highlight in sync, since setting <see cref="TreeViewItem.IsSelected"/> raises
    ///     the same TreeView.SelectedItemChanged event a mouse click would.
    /// </summary>
    private void SiloRailButton_Click(object sender, RoutedEventArgs e)
    {
        SiloRootItem.IsSelected = true;
    }

    private void ConnectionsRailButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectionsRootItem.IsSelected = true;
    }
}