using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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
public sealed class AppNavNode : INotifyPropertyChanged
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
        get => _header;
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

    private void _set(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
