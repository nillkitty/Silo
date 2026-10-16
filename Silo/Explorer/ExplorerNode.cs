using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Silo.Design;

namespace Silo.Explorer;

/// <summary>A context-menu entry of an <see cref="ExplorerNode" />.</summary>
public sealed record ExplorerAction(string Header, Action Run);

/// <summary>
///     One node of the Explorer tree (the browsable tree on the left, like SSMS's Object Explorer): a world, a folder, a
///     table, a connection. Selecting a node shows its details in the Properties pane and, when it has a view
///     (<see cref="Open" />), previews that view in a document tab; double-click pins the tab.
/// </summary>
public sealed class ExplorerNode : INotifyPropertyChanged, IOverridableNode
{
    private bool _isExpanded;
    private bool _isSelected;

    public ExplorerNode(string key, string header, string kind, string? iconKey = null)
    {
        Key    = key;
        Header = header;
        Kind   = kind;
        Icon   = iconKey is null ? null : FindIcon(iconKey);
    }

    /// <summary>Unique within the tree and stable across rebuilds (so expansion and selection survive a refresh).</summary>
    public string Key { get; }

    public string Header { get; }

    /// <summary>What the node is, in words: "World", "Table", "Connection"...</summary>
    public string Kind { get; }

    public Geometry? Icon { get; }

    public ObservableCollection<ExplorerNode> Children { get; } = [];

    /// <summary>Opens expanded the first time the tree is shown for a Silo.</summary>
    public bool ExpandByDefault { get; init; }

    /// <summary>The key of the document tab this node shows; two nodes with the same key share one tab. Null: the node has no view.</summary>
    public string? DocKey { get; init; }

    /// <summary>
    ///     Changes whenever something shown for the node (its details) changes without its name changing -- a table's
    ///     modified time, say -- so the tree knows to refresh the Properties pane.
    /// </summary>
    public string? Stamp { get; init; }

    /// <summary>What double-click / Enter does for a node that has no document view (opening a diagram in the canvas, say).</summary>
    public Action? Activate { get; init; }

    /// <summary>Builds the node's view (for a document tab).</summary>
    public Func<UIElement>? Open { get; init; }

    /// <summary>Called when the tab is closed or replaced, e.g. to save pending edits.</summary>
    public Action<UIElement>? Closed { get; init; }

    /// <summary>The label / value rows the Properties pane shows for the node.</summary>
    public Func<IEnumerable<(string Name, string Value)>>? Details { get; init; }

    /// <summary>The right-click menu.</summary>
    public Func<IEnumerable<ExplorerAction>>? Actions { get; init; }

    /// <summary>
    ///     The commands the node's model offers (<see cref="ICommandProvider" />): they join <see cref="Actions" /> in the Node menu
    ///     and the right-click menu, and show as toolbar buttons while the node is selected.
    /// </summary>
    public ICommandProvider? Commands { get; init; }

    /// <summary>
    ///     Called when the node is expanded, for a node whose children are read on demand (a folder's contents): starts the
    ///     load, whose result reaches the tree as a change of the node's source (see <see cref="ProvidedNodes" />).
    /// </summary>
    public Func<Task>? LoadChildren { get; init; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && value) StartLoadingChildren();
        }
    }

    private async void StartLoadingChildren()
    {
        if (LoadChildren is not { } load) return;

        try
        {
            await load();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Loading the children of the Explorer node {Key} failed", Key);
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public IEnumerable<ExplorerNode> Descendants()
    {
        yield return this;
        foreach (var child in Children)
        foreach (var d in child.Descendants())
            yield return d;
    }

    // ---- author-mode customisation (see NodeOverrides) -----------------------------------------

    /// <summary>The node above this one; set by <see cref="LinkParents" /> once the tree is built (null for a top-level node).</summary>
    public ExplorerNode? Parent { get; private set; }

    /// <summary>Points every node below this one at its parent.</summary>
    public void LinkParents()
    {
        foreach (var child in Children)
        {
            child.Parent = this;
            child.LinkParents();
        }
    }

    /// <summary>The text the tree shows: the author's own label, else <see cref="Header" />.</summary>
    public string Display => LabelOverride ?? Header;

    /// <summary>The icon size shown: this node's own override, else <see cref="DefaultIconSize" />.</summary>
    public double IconSize => IconSizeOverride ?? DefaultIconSize;

    public string OverrideKey => NodeOverrides.ExplorerPrefix + Key;

    public string OriginalName => Header;

    public string KindText => Kind;

    public string PathText
    {
        get
        {
            var parts = new List<string>();
            for (ExplorerNode? n = this; n is not null; n = n.Parent) parts.Insert(0, n.Header);
            return string.Join(" / ", parts);
        }
    }

    public double DefaultIconSize => 16;

    public string DesignTypeName => nameof(ExplorerNode);

    public string? LabelOverride
    {
        get => NodeOverrides.Get(OverrideKey)?.Label;
        set => NodeOverrides.Update(OverrideKey, o => o.Label = value);
    }

    public double? IconSizeOverride
    {
        get => NodeOverrides.Get(OverrideKey)?.IconSize;
        set => NodeOverrides.Update(OverrideKey, o => o.IconSize = value);
    }

    public bool IsHidden
    {
        get => NodeOverrides.Get(OverrideKey)?.Hidden == true;
        set => NodeOverrides.Update(OverrideKey, o => o.Hidden = value);
    }

    /// <summary>Opens the node's view (true: as a permanent tab, false: as the preview tab). Set by the tree.</summary>
    public Action<bool>? Opener { get; set; }

    /// <summary>Called after the overrides changed: tells the tree to redraw this node's text, icon and visibility.</summary>
    public void RefreshOverrides()
    {
        foreach (var name in new[] { nameof(Display), nameof(IconSize), nameof(IsHidden), nameof(LabelOverride), nameof(IconSizeOverride) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public IEnumerable<DesignMenuItem> OpenMenuItems()
    {
        var items = new List<DesignMenuItem>();

        if (Opener is { } open && DocKey is not null)
        {
            items.Add(DesignMenuItem.Run("Open (preview tab)", () => open(false)));
            items.Add(DesignMenuItem.Run("Open (keep as a tab)", () => open(true)));
        }

        // the node's ordinary right-click menu, so the author-mode menu offers everything the plain one does
        IEnumerable<ExplorerAction> actions = [];
        try
        {
            actions = Actions?.Invoke() ?? [];
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Building the actions of an Explorer node failed");
        }

        foreach (var action in actions)
        {
            var run = action.Run;
            items.Add(DesignMenuItem.Run(action.Header.Replace("_", string.Empty), run));
        }

        return items;
    }

    public IEnumerable<DesignMenuItem> DesignMenuItems() => NodeDesignMenu.Build(this);

    private static Geometry? FindIcon(string key)
    {
        return Application.Current?.TryFindResource(key) as Geometry;
    }

    private bool Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
