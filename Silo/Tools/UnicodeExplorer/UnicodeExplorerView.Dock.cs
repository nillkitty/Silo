using System.ComponentModel;
using System.Windows.Controls;
using AvalonDock.Layout;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     The collection can leave its tab: Undock Collection puts it in a dock pane of its own, placed beside the explorer's pane (in the main
///     window's dock area, or in the explorer's floating window). From there it can be dragged anywhere AvalonDock allows, including back next to
///     the explorer as a tab or a split. Closing that pane puts the collection back in its tab. The pane is part of the main window's saved dock
///     layout (ContentId <c>tool:unicodeexplorer/collection</c>, see <see cref="ToolBase.ExtraPaneKeys" />).
/// </summary>
/// <remarks>
///     The collection's controls are one element (<see cref="_trayRoot" />) that moves between two <see cref="Decorator" />s: the one in the tab and
///     one made for each pane. A Decorator lets go of its child at once, so the element is never still held by the place it is leaving (a
///     ContentPresenter only lets go on its next layout pass, which a pane that left the layout never gets).
/// </remarks>
public sealed partial class UnicodeExplorerView
{
    internal const string CollectionPaneKey = "collection";

    private          FrameworkElement  _trayRoot = null!;     // the collection's controls (BuildTray)
    private readonly Decorator         _trayHome = new();     // their place in the Collection tab
    private          LayoutAnchorable? _collectionPane;       // the pane they are in while undocked
    private          bool              _closingCollectionPane; // the pane is being taken down on purpose: its Hiding/Closing is not a request to dock back

    /// <summary>Whether the collection is in a pane of its own.</summary>
    internal bool IsCollectionUndocked => _collectionPane is not null;

    /// <summary>Undocking needs the main window's dock area (the explorer must be a pane there, docked or floating).</summary>
    private bool CanUndockCollection => _collectionPane is null && Pane is { Root: not null };

    // ---- undock and dock ---------------------------------------------------------------------------------------

    /// <summary>Edit/View/tab menu: moves the collection into a pane of its own beside the explorer, or shows that pane when it already is.</summary>
    private void UndockCollection()
    {
        if (_collectionPane is { } open)
        {
            if (!open.IsVisible) open.Show();
            open.IsActive = true;
            return;
        }

        if (Pane is not { Root: not null } explorer)
        {
            Say("The collection can be undocked when the explorer is a pane in the main window.");
            return;
        }

        var pane = MakeCollectionPane(TakeCollectionForPane());
        PlaceBeside(explorer, pane);
        AdoptCollectionPane(pane);
        pane.IsSelected = true;
        pane.IsActive   = true;
        Say("The collection is in a pane of its own: drag its title to move it, close it to put it back in its tab.");
    }

    /// <summary>Takes the collection out of its pane and puts it back in its tab.</summary>
    private void DockCollection()
    {
        if (_collectionPane is not { } pane) return;

        _collectionPane = null;
        _closingCollectionPane = true;
        try
        {
            if (pane.Content is Decorator carrier) carrier.Child = null;
            pane.Content = null;

            var root = pane.Root;
            if (pane.Parent is { } parent) parent.RemoveChild(pane);
            else root?.Hidden.Remove(pane);
            root?.CollectGarbage(); // the pane group it leaves empty goes too
        }
        finally
        {
            _closingCollectionPane = false;
        }

        GoHome();
        _settings.ShowCollection = true;
        SaveSettings();
        ApplyPanes();
        _bottomTabs.SelectedItem = _collectionTab;
        Say("The collection is back in its tab.");
    }

    /// <summary>Puts the collection's controls back in the tab (wherever they are now).</summary>
    private void GoHome()
    {
        Detach();
        _trayHome.Child = _trayRoot;
    }

    /// <summary>Takes the collection's controls out of the tab or the pane they are in.</summary>
    private void Detach()
    {
        if (_trayRoot.Parent is Decorator holder) holder.Child = null;
    }

    /// <summary>Shows the collection wherever it is: its pane (brought forward) or its tab (selected).</summary>
    private void BringCollectionForward()
    {
        if (_collectionPane is { } pane)
        {
            if (!pane.IsVisible) pane.Show();
            pane.IsSelected = true;
            return;
        }

        _bottomTabs.SelectedItem = _collectionTab;
    }

    // ---- what the dock layout calls (through UnicodeExplorerTool) ----------------------------------------------

    /// <summary>The collection's controls, taken out of wherever they are, in a holder of their own for a pane to show.</summary>
    internal UIElement TakeCollectionForPane()
    {
        Detach();
        return new Decorator { Child = _trayRoot };
    }

    /// <summary>
    ///     The pane that shows the collection now (one this view made, or one a saved layout put back), or null when there is none: the collection then
    ///     goes back in its tab. A pane the layout keeps hidden is not used, so the collection never ends up out of sight.
    /// </summary>
    internal void AdoptCollectionPane(LayoutAnchorable? pane)
    {
        if (pane is { IsHidden: true })
        {
            _collectionPane = pane;
            DockCollection();
            return;
        }

        _collectionPane = pane;
        if (pane is null)
        {
            if (!ReferenceEquals(_trayHome.Child, _trayRoot)) GoHome();
            ApplyPanes();
            return;
        }

        pane.Title = CollectionPaneTitle;
        pane.CanHide = true;
        pane.CanClose = false;
        pane.Hiding -= OnCollectionPaneHiding;
        pane.Hiding += OnCollectionPaneHiding;
        pane.Closing -= OnCollectionPaneClosing;
        pane.Closing += OnCollectionPaneClosing;
        ApplyPanes();
    }

    private LayoutAnchorable MakeCollectionPane(UIElement content)
    {
        return new LayoutAnchorable
               {
                   Title          = CollectionPaneTitle,
                   ContentId      = Pane is { } p ? p.ContentId + "/" + CollectionPaneKey : "tool:unicodeexplorer/" + CollectionPaneKey,
                   Content        = content,
                   CanFloat       = true,
                   CanHide        = true,
                   CanClose       = false,
                   FloatingWidth  = 560,
                   FloatingHeight = 280
               };
    }

    private string CollectionPaneTitle => _tray.Count == 0 ? "Collection" : $"Collection ({_tray.Count:N0})";

    /// <summary>Keeps the pane's title in step with the count (RefreshTray calls it).</summary>
    private void UpdateCollectionPaneTitle()
    {
        if (_collectionPane is { } pane) pane.Title = CollectionPaneTitle;
    }

    /// <summary>
    ///     Places the new pane in a group of its own right after the explorer's group, in the same container: to the right of it when that container
    ///     lays its parts out side by side, below it when it stacks them. Works the same in the main dock area and in a floating window.
    /// </summary>
    private static void PlaceBeside(LayoutAnchorable explorer, LayoutAnchorable pane)
    {
        var group = new LayoutAnchorablePane(pane);

        if (explorer.Parent is LayoutAnchorablePane explorerGroup && explorerGroup.Parent is ILayoutOrientableGroup container)
        {
            if (container.Orientation == Orientation.Horizontal) group.DockWidth = new GridLength(320);
            else group.DockHeight = new GridLength(220);

            container.InsertChildAt(container.IndexOfChild(explorerGroup) + 1, group);
            return;
        }

        // the explorer is somewhere else (an auto-hide side, say): next to its group as a tab, or floating
        if (explorer.Parent is LayoutAnchorablePane tabs)
        {
            tabs.Children.Add(pane);
            return;
        }

        if (explorer.Root?.Manager is { } manager)
        {
            pane.AddToLayout(manager, AnchorableShowStrategy.Bottom);
            pane.Float();
        }
    }

    // ---- the pane's own X ---------------------------------------------------------------------------------------

    private void OnCollectionPaneHiding(object? sender, CancelEventArgs e)
    {
        if (_closingCollectionPane || !ReferenceEquals(sender, _collectionPane)) return;

        // closing the pane means "put it back": done once AvalonDock has finished with the click
        e.Cancel = true;
        Dispatcher.BeginInvoke(DockCollection);
    }

    private void OnCollectionPaneClosing(object? sender, CancelEventArgs e)
    {
        OnCollectionPaneHiding(sender, e);
    }
}
