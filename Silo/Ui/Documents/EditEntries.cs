using System.Windows.Controls;

namespace Silo.Ui.Documents;

/// <summary>
///     One entry a document adds to the main window's Edit menu (a row or column operation of a table, say). Plain data,
///     so a document does not have to build WPF menu items itself: the menu turns entries into items each time it opens.
/// </summary>
public sealed class EditEntry
{
    /// <summary>The text; an underscore marks the access key, as in any menu.</summary>
    public string Header { get; init; } = string.Empty;

    /// <summary>What a click does. Null for a submenu.</summary>
    public Action? Run { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>Shows a check mark beside the entry (the current choice of a set, such as a column's type).</summary>
    public bool IsChecked { get; init; }

    /// <summary>The submenu, when this entry opens one.</summary>
    public IReadOnlyList<EditEntry>? Children { get; init; }

    public bool IsSeparator { get; init; }

    /// <summary>Shortcut text shown on the right (display only: the document handles the key itself).</summary>
    public string? Gesture { get; init; }

    public static EditEntry Divider() => new() { IsSeparator = true };

    public static EditEntry Item(string header, Action run, bool enabled = true, string? gesture = null) =>
        new() { Header = header, Run = run, IsEnabled = enabled, Gesture = gesture };

    /// <summary>An entry that shows a check mark when <paramref name="isChecked" /> (one choice of a set).</summary>
    public static EditEntry Choice(string header, Action run, bool isChecked, bool enabled = true) =>
        new() { Header = header, Run = run, IsEnabled = enabled, IsChecked = isChecked };

    public static EditEntry Submenu(string header, params EditEntry[] children) => new() { Header = header, Children = children };

    /// <summary>A menu element for the entry: a <see cref="Separator" /> or a <see cref="MenuItem" /> (with its submenu).</summary>
    public Control ToMenuElement()
    {
        if (IsSeparator) return new Separator();

        var item = new MenuItem { Header = Header, IsEnabled = IsEnabled, IsChecked = IsChecked };
        if (Gesture is not null) item.InputGestureText = Gesture;

        if (Children is not null)
        {
            foreach (var child in Children) item.Items.Add(child.ToMenuElement());
        }
        else if (Run is { } run)
        {
            item.Click += (_, _) =>
                          {
                              try
                              {
                                  run();
                              }
                              catch (Exception ex)
                              {
                                  Serilog.Log.Warning(ex, "The Edit menu entry {header} failed", Header);
                                  App.Status(ex.Message);
                              }
                          };
        }

        return item;
    }
}

/// <summary>
///     Implemented by a document view (the content of a document tab) that has editing operations of its own. The Edit menu
///     lists them, below the standard Undo / Cut / Copy / Paste, while that document's tab is the selected one.
/// </summary>
public interface IEditContributor
{
    /// <summary>The entries to add, built fresh each time the Edit menu opens (so enabled states are current).</summary>
    IEnumerable<EditEntry> EditEntries();
}
