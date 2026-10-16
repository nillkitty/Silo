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
///     Tracks the node that is selected in the Explorer and works out its commands: those it offers itself through
///     <see cref="ICommandProvider" /> (<see cref="ExplorerNode.Commands" />), those the
///     <see cref="Silo.Providers.Action.IActionProvider{TNodeType}" />s offer for its model (<see cref="ExplorerNode.Model" />),
///     then its built-in actions (<see cref="ExplorerNode.Actions" />, text only). They are put together by group, the groups in
///     the order they were first mentioned, except <see cref="ProvidedCommand.LastGroup" />, which is always last.
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

    /// <summary>The commands of <paramref name="node" />; empty when there is no node or it offers none.</summary>
    public static IReadOnlyList<NodeCommand> CommandsFor(ExplorerNode? node)
    {
        var result = new List<NodeCommand>();
        if (node is null) return result;

        try
        {
            foreach (var c in node.Commands?.GetCommands() ?? [])
                result.Add(NodeCommand.From(c));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Getting the commands of the Explorer node {Key} failed", node.Key);
        }

        try
        {
            foreach (var c in ActionProviders.CommandsFor(node.Model))
                result.Add(NodeCommand.From(c));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Getting the provided commands of the Explorer node {Key} failed", node.Key);
        }

        // the node's built-in actions come last, as text-only entries
        IEnumerable<ExplorerAction> actions = [];
        try
        {
            actions = node.Actions?.Invoke().ToList() ?? [];
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Building the actions of the Explorer node {Key} failed", node.Key);
        }

        int n = 0;
        foreach (var action in actions)
        {
            var run = action.Run;
            result.Add(new NodeCommand("action:" + n++, action.Header, new Silo.Commanding.DelegateCommand(_ => run()), null, null, null, NodeCommand.ActionsGroup, true));
        }

        // one block per group, in the order the groups first appear (the last group, if any, after all the rest), each keeping its
        // commands' order
        return result.GroupBy(c => c.Group).OrderBy(g => g.Key == ProvidedCommand.LastGroup ? 1 : 0).SelectMany(g => g).ToList();
    }
}
