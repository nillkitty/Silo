using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Silo.Tabular;
using Silo.Toolbar;
using Silo.Ui.Documents;

namespace Silo.Ui;

/// <summary>
/// Interaction logic for MenuBar.xaml
/// </summary>
public partial class MenuBar : Menu
{
    /// <summary>The items added to the Edit menu for the active document (removed again each time the menu opens).</summary>
    private readonly List<Control> _editItems = [];

    /// <summary>The items listing the pop-out windows in the View menu.</summary>
    private readonly List<Control> _windowItems = [];

    public MenuBar()
    {
        InitializeComponent();

        // MenuBar derives from Menu, and an implicit style only applies to its exact target type, so the themed Menu
        // style (background, text colour) is asked for explicitly
        SetResourceReference(StyleProperty, typeof(Menu));

        // the Node menu is only available while the selected Explorer node has commands
        NodeCommandHub.Changed += (_, _) => RefreshNodeMenuEnabled();
    }

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    // ---- Edit ----------------------------------------------------------------------------------

    /// <summary>Adds the active document's own editing entries below the standard ones.</summary>
    private void EditMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        // nested submenus (Column type...) raise this too, and it bubbles up here
        if (!ReferenceEquals(e.OriginalSource, sender))
            return;

        foreach (var item in _editItems) EditMenu.Items.Remove(item);
        _editItems.Clear();

        var entries = new List<EditEntry>();
        try
        {
            if (Main?.ActiveEditContributor is { } contributor) entries.AddRange(contributor.EditEntries());
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Building the document's Edit entries failed");
        }

        EditDynamicSeparator.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var entry in entries)
        {
            var element = entry.ToMenuElement();
            EditMenu.Items.Add(element);
            _editItems.Add(element);
        }
    }

    private void PasteNewTable_Click(object sender, RoutedEventArgs e)
    {
        TablePaste.AsNewTable(App.Instance?.ActiveSilo);
    }

    // ---- Node ----------------------------------------------------------------------------------

    private void RefreshNodeMenuEnabled()
    {
        NodeMenu.IsEnabled = NodeCommandHub.CommandsFor(NodeCommandHub.Current).Count > 0;
    }

    /// <summary>Lists the selected Explorer node's commands.</summary>
    private void NodeMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;

        NodeMenu.Items.Clear();
        foreach (var entry in CommandMenu.Items(NodeCommandHub.CommandsFor(NodeCommandHub.Current)))
            NodeMenu.Items.Add(entry);

        if (NodeMenu.Items.Count == 0) NodeMenu.Items.Add(new MenuItem { Header = "(no commands)", IsEnabled = false });
    }

    // ---- View ----------------------------------------------------------------------------------

    /// <summary>Refreshes the pane check marks from what is actually showing (a pane can be closed with its own X), and the list of windows.</summary>
    private void ViewMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        // nested submenus (Theme, User Level) raise this too, and it bubbles up here
        if (!ReferenceEquals(e.OriginalSource, sender) || Main is not { } main)
            return;

        ViewExplorer.IsChecked   = main.IsExplorerOpen;
        ViewProperties.IsChecked = main.IsPropertiesOpen;
        ViewToolbox.IsChecked    = main.IsToolboxOpen;
        ViewSelectedItem.IsChecked = main.IsSelectedItemOpen;
        ViewToolbar.IsChecked      = ToolbarSettings.Visible;

        RefreshWindowItems();
    }

    /// <summary>Lists the tables that were popped out into windows, each with Show / Return to tab / Close.</summary>
    private void RefreshWindowItems()
    {
        foreach (var item in _windowItems) ViewMenu.Items.Remove(item);
        _windowItems.Clear();

        var windows = TableEditors.PopOutWindows.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        ViewWindowsSeparator.Visibility = windows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var window in windows)
        {
            var w = window;

            // "_" would make the next letter an access key
            var entry = new MenuItem { Header = "Window: " + w.Title.Replace("_", "__") };
            entry.Items.Add(Item("_Show", w.Reveal));
            entry.Items.Add(Item("_Return to tab", w.ReturnToTab));
            entry.Items.Add(Item("_Close", w.Close));

            ViewMenu.Items.Add(entry);
            _windowItems.Add(entry);
        }
    }

    private static MenuItem Item(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    private void ViewToolbar_Click(object sender, RoutedEventArgs e)
    {
        // IsCheckable has already flipped the check mark: it now says what the person asked for
        ToolbarSettings.Visible = ViewToolbar.IsChecked;
    }

    private void CustomizeToolbar_Click(object sender, RoutedEventArgs e)
    {
        CustomizeToolbarWindow.ShowFor(Main);
    }

    private void ResetToolbar_Click(object sender, RoutedEventArgs e)
    {
        ToolbarSettings.Reset();
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        Main?.ResetWindowLayout();
    }

    private void ViewPane_Click(object sender, RoutedEventArgs e)
    {
        if (Main is not { } main || sender is not MenuItem item)
            return;

        // IsCheckable has already flipped the check mark: it now says what the person asked for
        if (ReferenceEquals(item, ViewExplorer))
            main.SetExplorerOpen(item.IsChecked);
        else if (ReferenceEquals(item, ViewProperties))
            main.SetPropertiesOpen(item.IsChecked);
        else if (ReferenceEquals(item, ViewToolbox))
            main.SetToolboxOpen(item.IsChecked);
        else if (ReferenceEquals(item, ViewSelectedItem))
        {
            main.SetSelectedItemOpen(item.IsChecked);
            item.IsChecked = main.IsSelectedItemOpen; // nothing selected: nothing opened
        }
    }
}
