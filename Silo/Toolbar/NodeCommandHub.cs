using System.Windows.Input;
using Silo.Explorer;

namespace Silo.Toolbar;

/// <summary>A command of the node selected in the Explorer, as the Node menu, the Explorer's right-click menu and the toolbar show it.</summary>
/// <param name="Id">Identifies the command (within the node).</param>
/// <param name="Header">Menu-style text (an underscore marks an access key).</param>
/// <param name="Command">What runs.</param>
/// <param name="Parameter">Passed to the command.</param>
/// <param name="IconKey">A geometry resource key; null for a text-only entry (the node's built-in actions have no icon).</param>
/// <param name="ToolTip">Explanation, if any.</param>
/// <param name="Group">Commands of the same group sit together.</param>
/// <param name="OnToolbar">Whether a toolbar button is made for it.</param>
public sealed record NodeCommand(
    string   Id,
    string   Header,
    ICommand Command,
    object?  Parameter,
    string?  IconKey,
    string?  ToolTip,
    string?  Group,
    bool     OnToolbar);

/// <summary>
///     Tracks the node that is selected in the Explorer and works out its commands: those it offers through
///     <see cref="ICommandProvider" /> (<see cref="ExplorerNode.Commands" />) followed by its built-in actions
///     (<see cref="ExplorerNode.Actions" />, text only).
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
            if (node.Commands is { } provider)
            {
                foreach (var c in provider.GetCommands())
                    result.Add(new NodeCommand(c.Id, c.Header, c.Command, c.Parameter, c.IconKey, c.ToolTip, c.Group ?? "commands",
                                               c.Placement.HasFlag(CommandPlacement.Toolbar)));
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Getting the commands of the Explorer node {Key} failed", node.Key);
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
            result.Add(new NodeCommand("action:" + n++, action.Header, new Silo.Commanding.DelegateCommand(_ => run()), null, null, null, "actions", true));
        }

        return result;
    }
}
