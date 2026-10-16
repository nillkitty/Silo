using System.Windows.Controls;

namespace Silo.Toolbar;

/// <summary>Turns <see cref="NodeCommand" />s into menu entries (for the Node menu and the Explorer's right-click menu).</summary>
public static class CommandMenu
{
    /// <summary>One menu entry for the command: its header, icon (when it has one), tooltip, and the command itself (which enables and disables the entry).</summary>
    public static MenuItem Item(NodeCommand c)
    {
        var item = new MenuItem
                   {
                       Header           = c.Header,
                       Command          = c.Command,
                       CommandParameter = c.Parameter,
                       ToolTip          = c.ToolTip
                   };

        if (ToolbarIcons.Find(c.IconKey) is { } geometry) item.Icon = new AppIcon { Icon = geometry, Size = 16 };
        return item;
    }

    /// <summary>Entries for all the commands, with a separator between groups.</summary>
    public static IEnumerable<Control> Items(IEnumerable<NodeCommand> commands)
    {
        string? group = null;
        bool    first = true;
        foreach (var c in commands)
        {
            if (!first && group != c.Group) yield return new Separator();

            first = false;
            group = c.Group;
            yield return Item(c);
        }
    }
}
