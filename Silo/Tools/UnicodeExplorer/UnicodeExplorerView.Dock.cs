using System.ComponentModel;
using System.Windows.Controls;
using AvalonDock.Layout;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     The tabs under the list (Collection, Generate, Library) can each leave the explorer: Undock puts the tab's contents in a dock pane of its own,
///     placed beside the explorer's pane (in the main window's dock area, or in the explorer's floating window). From there it can be dragged anywhere
///     AvalonDock allows, including back next to the explorer as a tab or a split. Closing that pane puts the contents back in their tab. The panes are
///     part of the main window's saved dock layout (ContentId <c>tool:unicodeexplorer/&lt;key&gt;</c>, see <see cref="ToolBase.ExtraPaneKeys" />).
/// </summary>
/// <remarks>
///     A part's controls are one element (<see cref="DockPart.Root" />) that moves between <see cref="Decorator" />s: the one in its tab and one made
///     for each pane. A Decorator lets go of its child at once, so the element is never still held by the place it is leaving (a ContentPresenter
///     only lets go on its next layout pass, which a pane that left the layout never gets).
/// </remarks>
public sealed partial class UnicodeExplorerView
{
    internal const string CollectionPaneKey = "collection";
    internal const string GeneratePaneKey   = "generate";
    internal const string LibraryPaneKey    = "library";

    /// <summary>The keys of the parts that can be undocked, in tab order.</summary>
    internal static readonly string[] PartKeys = [CollectionPaneKey, GeneratePaneKey, LibraryPaneKey];

    /// <summary>One tab under the list whose contents can live in a pane of their own.</summary>
    private sealed class DockPart
    {
        public required string                   Key       { get; init; }
        public required string                   Name      { get; init; }   // "Collection"
        public required TabItem                  Tab       { get; init; }
        public required FrameworkElement         Root      { get; init; }   // the controls that move
        public required Func<string>             Title     { get; init; }   // the tab's and pane's title, with a count when it has one
        public required Func<ExplorerSettings, bool>   IsShown  { get; init; }
        public required Action<ExplorerSettings, bool> SetShown { get; init; }
        public          Decorator                Home      { get; } = new();
        public          LayoutAnchorable?        Pane      { get; set; }
        public          bool                     TakingDown { get; set; } // the pane is being taken down on purpose: its Hiding/Closing is not a request to dock back
    }

    private readonly List<DockPart> _parts = [];

    private DockPart Part(string key) => _parts.First(p => p.Key == key);

    /// <summary>Puts a part's controls in its tab (through its holder), with the tab's undock menu; called once per part while the layout is built.</summary>
    private void AddPart(DockPart part)
    {
        _parts.Add(part);
        part.Home.Child  = part.Root;
        part.Tab.Content = part.Home;
        part.Tab.Header  = part.Title();
        part.Tab.ContextMenu = new ContextMenu();
        part.Tab.ContextMenuOpening += (_, _) =>
                                       {
                                           part.Tab.ContextMenu!.Items.Clear();
                                           part.Tab.ContextMenu.Items.Add(UiKit.Item($"Undock {part.Name}", () => Undock(part.Key), CanUndock(part.Key),
                                                                                     $"Move the {part.Name.ToLowerInvariant()} into a pane of its own beside the explorer; drag it anywhere, close it to put it back here"));
                                       };

        // a part showing in a pane of its own keeps the Silo hooked even while the explorer is not showing
        part.Root.Loaded   += OnLoaded;
        part.Root.Unloaded += (_, _) => UnhookSiloWhenHidden();
    }

    /// <summary>Whether any part is showing right now (in its tab or in a pane).</summary>
    private bool AnyPartLoaded => _parts.Any(p => p.Root.IsLoaded);

    internal bool IsUndocked(string key) => Part(key).Pane is not null;

    /// <summary>Undocking needs the main window's dock area (the explorer must be a pane there, docked or floating).</summary>
    private bool CanUndock(string key) => Part(key).Pane is null && Pane is { Root: not null };

    // the collection's own names, used across the explorer
    internal bool IsCollectionUndocked => IsUndocked(CollectionPaneKey);
    private bool  CanUndockCollection  => CanUndock(CollectionPaneKey);
    private void  UndockCollection()   => Undock(CollectionPaneKey);
    private void  DockCollection()     => DockBack(CollectionPaneKey);
    private void  BringCollectionForward() => BringForward(CollectionPaneKey);

    // ---- undock and dock ---------------------------------------------------------------------------------------

    /// <summary>Moves a part into a pane of its own beside the explorer, or shows that pane when it already is.</summary>
    private void Undock(string key)
    {
        var part = Part(key);
        if (part.Pane is { } open)
        {
            if (!open.IsVisible) open.Show();
            open.IsActive = true;
            return;
        }

        if (Pane is not { Root: not null } explorer)
        {
            Say($"The {part.Name.ToLowerInvariant()} can be undocked when the explorer is a pane in the main window.");
            return;
        }

        var pane = MakePane(part, TakePartForPane(key));
        PlaceBeside(explorer, pane);
        AdoptPartPane(key, pane);
        pane.IsSelected = true;
        pane.IsActive   = true;
        Say($"The {part.Name.ToLowerInvariant()} is in a pane of its own: drag its title to move it, close it to put it back in its tab.");
    }

    /// <summary>Takes a part out of its pane and puts it back in its tab.</summary>
    private void DockBack(string key)
    {
        var part = Part(key);
        if (part.Pane is not { } pane) return;

        part.Pane       = null;
        part.TakingDown = true;
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
            part.TakingDown = false;
        }

        GoHome(part);
        part.SetShown(_settings, true);
        SaveSettings();
        ApplyPanes();
        _bottomTabs.SelectedItem = part.Tab;
        Say($"The {part.Name.ToLowerInvariant()} is back in its tab.");
    }

    /// <summary>Puts a part's controls back in its tab (wherever they are now).</summary>
    private static void GoHome(DockPart part)
    {
        Detach(part);
        part.Home.Child = part.Root;
    }

    /// <summary>Takes a part's controls out of the tab or the pane they are in.</summary>
    private static void Detach(DockPart part)
    {
        if (part.Root.Parent is Decorator holder) holder.Child = null;
    }

    /// <summary>Shows a part wherever it is: its pane (brought forward) or its tab (selected).</summary>
    private void BringForward(string key)
    {
        var part = Part(key);
        if (part.Pane is { } pane)
        {
            if (!pane.IsVisible) pane.Show();
            pane.IsSelected = true;
            return;
        }

        if (!part.IsShown(_settings))
        {
            part.SetShown(_settings, true);
            SaveSettings();
            ApplyPanes();
        }

        _bottomTabs.SelectedItem = part.Tab;
    }

    // ---- what the dock layout calls (through UnicodeExplorerTool) ----------------------------------------------

    /// <summary>A part's controls, taken out of wherever they are, in a holder of their own for a pane to show (null: no such part).</summary>
    internal UIElement? TakePartForPane(string key)
    {
        if (_parts.FirstOrDefault(p => p.Key == key) is not { } part) return null;

        Detach(part);
        return new Decorator { Child = part.Root };
    }

    /// <summary>
    ///     The pane that shows a part now (one this view made, or one a saved layout put back), or null when there is none: the part then goes back in
    ///     its tab. A pane the layout keeps hidden is not used, so the part never ends up out of sight.
    /// </summary>
    internal void AdoptPartPane(string key, LayoutAnchorable? pane)
    {
        if (_parts.FirstOrDefault(p => p.Key == key) is not { } part) return;

        if (pane is { IsHidden: true })
        {
            part.Pane = pane;
            DockBack(key);
            return;
        }

        part.Pane = pane;
        if (pane is null)
        {
            if (!ReferenceEquals(part.Home.Child, part.Root)) GoHome(part);
            ApplyPanes();
            return;
        }

        pane.Title    = part.Title();
        pane.CanHide  = true;
        pane.CanClose = false;
        pane.Hiding  -= OnPartPaneHiding;
        pane.Hiding  += OnPartPaneHiding;
        pane.Closing -= OnPartPaneHiding;
        pane.Closing += OnPartPaneHiding;
        ApplyPanes();
    }

    private LayoutAnchorable MakePane(DockPart part, UIElement? content)
    {
        return new LayoutAnchorable
               {
                   Title          = part.Title(),
                   ContentId      = (Pane is { } p ? p.ContentId : "tool:unicodeexplorer") + "/" + part.Key,
                   Content        = content,
                   CanFloat       = true,
                   CanHide        = true,
                   CanClose       = false,
                   FloatingWidth  = 560,
                   FloatingHeight = 300
               };
    }

    /// <summary>Keeps a part's tab header and pane title in step with its count.</summary>
    private void UpdatePartTitle(string key)
    {
        if (_parts.FirstOrDefault(p => p.Key == key) is not { } part) return;

        string title = part.Title();
        part.Tab.Header = title;
        if (part.Pane is { } pane) pane.Title = title;
    }

    /// <summary>The collection's title changed (RefreshTray calls it).</summary>
    private void UpdateCollectionPaneTitle() => UpdatePartTitle(CollectionPaneKey);

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

    // ---- a pane's own X -------------------------------------------------------------------------------------------

    private void OnPartPaneHiding(object? sender, CancelEventArgs e)
    {
        if (_parts.FirstOrDefault(p => ReferenceEquals(p.Pane, sender)) is not { TakingDown: false } part) return;

        // closing the pane means "put it back": done once AvalonDock has finished with the click
        e.Cancel = true;
        Dispatcher.BeginInvoke(() => DockBack(part.Key));
    }
}
