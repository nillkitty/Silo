using System.Windows.Input;

namespace Silo.Contracts.Core;

/// <summary>Where a <see cref="ProvidedCommand" /> is offered.</summary>
[Flags]
public enum CommandPlacement
{
    /// <summary>As a button on the main toolbar.</summary>
    Toolbar = 1,

    /// <summary>As an entry in the Node menu and the Explorer's right-click menu (nodes), or the Tools toolbar group (tools).</summary>
    Menu = 2,

    /// <summary>Both.</summary>
    Both = Toolbar | Menu
}

/// <summary>
///     One command a node or a tool offers through <see cref="ICommandProvider" />.
/// </summary>
/// <param name="Id">Identifies the command within its provider; stable across runs (the toolbar remembers buttons by it).</param>
/// <param name="Header">The text of the menu entry (a leading "_" before a letter makes it the access key) and the toolbar button's tooltip.</param>
/// <param name="Command">What runs. Its <c>CanExecute</c> decides whether the button / entry is enabled.</param>
/// <param name="Parameter">Passed to the command.</param>
/// <param name="IconKey">The key of an icon geometry (for instance "Plug"; see Resources/NavIcons.xaml). Null: the toolbar shows the header as text.</param>
/// <param name="ToolTip">Optional longer explanation.</param>
/// <param name="Group">Commands with the same group sit together; a separator goes between groups.</param>
/// <param name="Placement">Where the command is offered; by default everywhere.</param>
public sealed record ProvidedCommand(
    string           Id,
    string           Header,
    ICommand         Command,
    object?          Parameter = null,
    string?          IconKey   = null,
    string?          ToolTip   = null,
    string?          Group     = null,
    CommandPlacement Placement = CommandPlacement.Both)
{
    /// <summary>A command that runs <paramref name="run" />, enabled whenever <paramref name="canRun" /> (if given) says so.</summary>
    public static ProvidedCommand From(string  id,           string header, Action run, string? iconKey = null, Func<bool>? canRun = null,
                                       string? toolTip = null, string? group = null,
                                       CommandPlacement placement = CommandPlacement.Both)
    {
        return new ProvidedCommand(id, header, new Silo.Commanding.DelegateCommand(_ => run(), canRun is null ? null : _ => canRun()), null, iconKey,
                                   toolTip, group, placement);
    }
}

/// <summary>
///     Implemented by a node's model (a connection's item, a document...) -- the object a <see cref="Silo.ViewModel.SiloNode" />
///     holds as its <c>DataContext</c> -- to offer commands for the node: they appear in the Node menu and the Explorer's
///     right-click menu, and as buttons on the toolbar while the node is selected in the Explorer.
///     Implemented by an <see cref="ITool" />, its commands get a first-class place on the toolbar, ahead of the Tools drop-down
///     (which holds every other tool, to open or close it).
/// </summary>
/// <remarks>
///     <see cref="GetCommands" /> is called whenever the selection changes or a menu opens, so it should be quick and
///     must not have side effects. Return the same command objects' behaviour each time; the toolbar identifies a command by
///     <see cref="ProvidedCommand.Id" />, not by instance.
/// </remarks>
public interface ICommandProvider
{
    /// <summary>The commands to offer right now.</summary>
    IEnumerable<ProvidedCommand> GetCommands();
}
