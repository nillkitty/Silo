using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Silo.Design;

namespace Silo.Nav;

/// <summary>
///     One node of the data-bound navigation tree shown in <c>MainWindow.xaml</c>'s NavTree and
///     NavRail: an <see cref="AppNavItem" /> plus its child nodes, plus the small bit of mutable
///     presentation state (selection/expansion/active-root highlight) those two controls bind
///     to.
/// </summary>
/// <remarks>
///     Deliberately a separate type from <see cref="AppNavItem" /> rather than adding a
///     <c>Children</c> property there. <see cref="AppNavItem" /> already points <em>up</em> via
///     <see cref="AppNavItem.Parent" />; if it also pointed <em>down</em> via a <c>Children</c>
///     property on the same record, the two references would form a cycle reachable from a
///     single object -- and a record's generated (structural) <c>Equals</c>/<c>GetHashCode</c>
///     walks every property, so the first thing that compares or hashes two nodes (a TreeView's
///     item-container generator, a <c>HashSet</c>, even a debugger "quick watch") would recurse
///     forever. Keeping the parent link on <see cref="AppNavItem" /> and the child link here, on
///     a different object, means neither direction is ever reachable from the other.
/// </remarks>
public sealed class AppNavNode : INotifyPropertyChanged, IOverridableNode
{
    public AppNavItem Item { get; }

    /// <summary>
    ///     The child nodes. Observable, so a group can gain or lose children while it is on screen (the Silo's Worlds
    ///     node, say, once its worlds are listed); the tree follows along.
    /// </summary>
    public ObservableCollection<AppNavNode> Children { get; }

    public AppNavNode(AppNavItem item, IReadOnlyList<AppNavNode>? children = null)
    {
        Item     = item;
        Children = children is null ? [] : new ObservableCollection<AppNavNode>(children);
        _header  = item.Text;

        // the default icon size follows the Author-mode slider; weak, so nodes of closed Silos can be collected
        PropertyChangedEventManager.AddHandler(AppDesignSettings.Instance, OnDesignSettingsChanged, nameof(AppDesignSettings.IconSize));
    }

    private void OnDesignSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconSize)));
    }

    private string _header;
    private bool   _isSelected;
    private bool   _isExpanded;
    private bool   _isActiveRoot;
    private bool   _isEmphasized;

    /// <summary>
    ///     The text shown for the node. Starts as <see cref="AppNavItem.Text" />; a root that stands for an open Silo
    ///     changes it as Silos open and close (see <c>MainWindow.xaml.cs</c>'s <c>SyncNav</c>).
    /// </summary>
    public string Header
    {
        get => LabelOverride ?? _header;
        set
        {
            if (_header == value)
                return;

            _header = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Header)));
        }
    }

    /// <summary>Shown in the accent colour: the node of the Silo that is currently active.</summary>
    public bool IsEmphasized
    {
        get => _isEmphasized;
        set => _set(ref _isEmphasized, value);
    }

    /// <summary>Two-way bound to TreeViewItem.IsSelected via NavTree's ItemContainerStyle.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => _set(ref _isSelected, value);
    }

    /// <summary>Two-way bound to TreeViewItem.IsExpanded via NavTree's ItemContainerStyle.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => _set(ref _isExpanded, value);
    }

    /// <summary>
    ///     Only meaningful on a root node (one whose <see cref="AppNavItem.Parent" /> is
    ///     <see langword="null" />) -- bound to NavRail's collapsed-pane icon buttons, true for
    ///     whichever root's subtree contains the current selection. See
    ///     <c>MainWindow.xaml.cs</c>'s <c>NavTree_SelectedItemChanged</c>.
    /// </summary>
    public bool IsActiveRoot
    {
        get => _isActiveRoot;
        set => _set(ref _isActiveRoot, value);
    }

    // ---- author-mode customisation (see NodeOverrides) -----------------------------------------

    /// <summary>The icon size shown: this node's own override, else the app-wide Icon Size setting.</summary>
    public double IconSize => IconSizeOverride ?? AppDesignSettings.Instance.IconSize;

    /// <summary>The key in <see cref="NodeOverrides" />: the texts of the node and its ancestors (the same for every Silo).</summary>
    public string OverrideKey => NodeOverrides.NavPrefix + PathText.Replace(" / ", "/");

    public string OriginalName => Item.Text;

    public string KindText => Children.Count > 0 ? "Navigation group" : Item.View is not null ? "Navigation view" : "Navigation item";

    public string PathText
    {
        get
        {
            var parts = new List<string>();
            for (AppNavItem? i = Item; i is not null; i = i.Parent) parts.Insert(0, i.Text);
            return string.Join(" / ", parts);
        }
    }

    public double DefaultIconSize => AppDesignSettings.Instance.IconSize;

    public string DesignTypeName => nameof(AppNavNode);

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

    /// <summary>Opens the node's view (true: as a permanent tab, false: as the preview tab). Set by the window for nodes that have a view.</summary>
    public Action<bool>? Opener { get; set; }

    /// <summary>Called after the overrides changed: tells the tree to redraw this node's text, icon and visibility.</summary>
    public void RefreshOverrides()
    {
        foreach (var name in new[] { nameof(Header), nameof(IconSize), nameof(IsHidden), nameof(LabelOverride), nameof(IconSizeOverride) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public IEnumerable<DesignMenuItem> OpenMenuItems()
    {
        if (Opener is not { } open) return [];

        return [DesignMenuItem.Run("Open (preview tab)", () => open(false)), DesignMenuItem.Run("Open (keep as a tab)", () => open(true))];
    }

    public IEnumerable<DesignMenuItem> DesignMenuItems() => NodeDesignMenu.Build(this);

    private void _set(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
