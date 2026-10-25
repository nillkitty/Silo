using System.Windows.Controls;
using System.Windows.Media;
using Serilog;
using Silo.Converters;

namespace Silo.Toolbar;

/// <summary>Turns <see cref="NodeCommand" />s into menu entries (for the Node menu and the Explorer's right-click menu).</summary>
public static class CommandMenu
{
    /// <summary>
    ///     One menu entry for the command: its header, icon (when it has one), tooltip, shortcut text, check mark and the
    ///     command
    ///     itself (which enables and disables the entry); bold for the default command, red for a destructive one; a submenu
    ///     holds
    ///     the command's children.
    /// </summary>
    public static MenuItem Item(NodeCommand c)
    {
        var item = new MenuItem
                   {
                       Header           = c.Header,
                       Command          = c.Command,
                       CommandParameter = c.Parameter,
                       ToolTip          = c.ToolTip
                   };

        // always set, even to nothing: inside the menu bar an implicit style would otherwise read an icon off the command
        item.Icon = ToolbarIcons.Find(c.IconKey) is { } geometry ? new AppIcon { Icon = geometry, Size = 16 } : null;

        if (c.Gesture is { Length: > 0 } gesture) item.InputGestureText = gesture;

        if (c.IsChecked is { } isChecked)
        {
            item.IsCheckable = true;
            item.IsChecked   = SafeChecked(isChecked);
        }

        if (c.IsDefault) item.FontWeight     = FontWeights.Bold;
        if (c.IsDestructive) item.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));

        if (c.Children is { Count: > 0 } children)
            foreach (var child in Items(children))
                item.Items.Add(child);

        return item;
    }

    /// <summary>
    ///     Entries for all the commands. Each group with a name starts with its name as a heading; between other groups there
    ///     is a
    ///     separator (the <see cref="ProvidedCommand.LastGroup" /> has no heading, only the separator).
    /// </summary>
    public static IEnumerable<Control> Items(IEnumerable<NodeCommand> commands)
    {
        string? group = null;
        bool    first = true;
        foreach (var c in commands)
        {
            if (first || group != c.Group)
            {
                bool named = !string.IsNullOrEmpty(c.Group) && c.Group != ProvidedCommand.LastGroup;
                if (!first && !named) yield return new Separator();
                if (named) yield return Heading(c.Group!);
            }

            first = false;
            group = c.Group;
            yield return Item(c);
        }
    }

    /// <summary>
    ///     A group's name as a label that cannot be chosen and does not look like an entry: small capitals in the theme's
    ///     accent
    ///     colour on a tinted band across the menu.
    /// </summary>
    public static MenuItem Heading(string title)
    {
        var heading = new MenuItem
                      {
                          Header           = title.ToUpperInvariant(),
                          IsHitTestVisible = false,
                          Focusable        = false,
                          FontSize         = 10.5,
                          FontWeight       = FontWeights.Bold,
                          Margin           = new Thickness(0, 3, 0, 1),
                          Padding          = new Thickness(6, 6, 3, 3)
                      };
        heading.SetResourceReference(Control.ForegroundProperty, "Theme.Accent");
        heading.SetResourceReference(Control.BackgroundProperty, "Theme.BackgroundAlt");
        return heading;
    }

    private static bool SafeChecked(Func<bool> isChecked)
    {
        try
        {
            return isChecked();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading a check mark failed");
            return false;
        }
    }
}