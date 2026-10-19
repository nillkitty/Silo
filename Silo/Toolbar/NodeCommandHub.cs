using System.Windows.Input;
using Silo.Commanding;
using Silo.Explorer;

namespace Silo.Toolbar;

/// <summary>A command of the node selected in the Explorer, as the Node menu, the Explorer's right-click menu and the toolbar show it.</summary>
/// <param name="Id">Identifies the command (within the node).</param>
/// <param name="Header">Menu-style text (an underscore marks an access key).</param>
/// <param name="Command">What runs.</param>
/// <param name="Parameter">Passed to the command.</param>
/// <param name="IconKey">A geometry resource key; null for a text-only entry (the node's built-in actions have no icon).</param>
/// <param name="ToolTip">Explanation, if any.</param>
/// <param name="Group">
///     Commands of the same group sit together, under the group's name as a heading in the menus (no heading when it is null
///     or empty).
/// </param>
/// <param name="OnToolbar">Whether a toolbar button is made for it.</param>
/// <param name="Gesture">The keyboard shortcut as text ("F5"), shown in the menus and handled while the Explorer has the focus.</param>
/// <param name="IsChecked">For a switch: whether it is on now (the menu entry is then checkable).</param>
/// <param name="IsDefault">Shown in bold: it is what the node does by default.</param>
/// <param name="IsDestructive">Shown in red.</param>
/// <param name="Children">A submenu's entries.</param>
public sealed record NodeCommand(
    string   Id,
    string   Header,
    ICommand Command,
    object?  Parameter,
    string?  IconKey,
    string?  ToolTip,
    string?  Group,
    bool     OnToolbar,
    string?  Gesture       = null,
    Func<bool>? IsChecked  = null,
    bool     IsDefault     = false,
    bool     IsDestructive = false,
    IReadOnlyList<NodeCommand>? Children = null)
{
    /// <summary>The group of the node's built-in actions: nameless (no heading) and apart from the provided commands.</summary>
    public const string ActionsGroup = "";

    /// <summary>This command and the ones in its submenu, depth first.</summary>
    public IEnumerable<NodeCommand> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children ?? [])
        foreach (var d in child.SelfAndDescendants())
            yield return d;
    }

    /// <summary>The node command for a provided one (its submenu too).</summary>
    public static NodeCommand From(ProvidedCommand c)
    {
        return new NodeCommand(c.Id, c.Header, c.Command, c.Parameter, c.IconKey, c.ToolTip, c.Group, c.Placement.HasFlag(CommandPlacement.Toolbar),
                               c.Gesture, c.IsChecked, c.IsDefault, c.IsDestructive, c.Children?.Select(From).ToList());
    }
}

/// <summary>
///     Tracks the node that is selected in the Explorer and works out its commands with <see cref="ContextCommands" />, so the
///     Node menu, the toolbar and the node's right-click menu always agree.
/// </summary>
public static class NodeCommandHub
{
    /// <summary>The selected node, or null.</summary>
    public static ExplorerNode? Current { get; private set; }

    /// <summary>Raised when the selected node (or the node object standing for it after the tree was rebuilt) changed.</summary>
    public static event EventHandler? Changed;

    /// <summary>Called by the main window when the Explorer's selection changes or its tree is rebuilt.</summary>
    public static void SetCurrent(ExplorerNode? node)
    {
        Current = node;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    ///     The commands of <paramref name="node" />; empty when there is no node or it offers none. The same list the node's
    ///     right-click menu shows (see <see cref="ContextCommands.For" />).
    /// </summary>
    public static IReadOnlyList<NodeCommand> CommandsFor(ExplorerNode? node)
    {
        return node is null ? [] : ContextCommands.For(node);
    }
}
