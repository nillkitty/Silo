using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Silo.Clipboarding;
using Silo.Labs;
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

    /// <summary>Lists the selected Explorer node's commands.</summary>
    private void NodeMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;

        NodeMenu.Items.Clear();

        var node = NodeCommandHub.Current;
        try
        {
            // opening the node's view (what a click and a double-click do)
            if (node is { Opener: { } open, DocKey: not null })
            {
                NodeMenu.Items.Add(Item("_Open (preview tab)", () => open(false)));
                NodeMenu.Items.Add(Item("Open (_keep as a tab)", () => open(true)));
                NodeMenu.Items.Add(new Separator());
            }

            // the node's commands: exactly what its right-click menu lists (see ContextCommands), Refresh (F5) among them
            foreach (var entry in CommandMenu.Items(NodeCommandHub.CommandsFor(node)))
                NodeMenu.Items.Add(entry);

            // then Cut / Copy / Paste, as in the right-click menu
            if (Main?.ExplorerControl is { } explorer)
            {
                var clipboard = ClipboardCommands.Items(explorer.GetClipboardContext()).ToList();
                if (clipboard.Count > 0)
                {
                    if (NodeMenu.Items.Count > 0 && NodeMenu.Items[^1] is not Separator) NodeMenu.Items.Add(new Separator());
                    foreach (var item in clipboard) NodeMenu.Items.Add(item);
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Building the Node menu failed");
        }

        // never leave it empty: a menu item with no items cannot open again
        if (NodeMenu.Items.Count == 0)
            NodeMenu.Items.Add(new MenuItem { Header = node is null ? "(select a node in the Explorer)" : "(no commands)", IsEnabled = false });
    }

    // ---- Analysis ------------------------------------------------------------------------------

    /// <summary>
    ///     Lists the Labs the container knows, to open one on its own; below them the Labs that can study what is selected in the
    ///     Explorer; below those the Lab windows that are open.
    /// </summary>
    private void AnalysisMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;

        AnalysisMenu.Items.Clear();

        var labs = LabHost.Labs;
        foreach (var lab in labs)
        {
            var l = lab;
            var item = new MenuItem { Header = "Open " + l.Name.Replace("_", "__"), ToolTip = l.Description };

            // always set, even to nothing: inside the menu bar an implicit style would otherwise read an icon off the command
            item.Icon = ToolbarIcons.Find(l.IconKey) is { } geometry ? new AppIcon { Icon = geometry, Size = 16 } : null;
            item.Click += (_, _) => LabHost.Open(l);
            AnalysisMenu.Items.Add(item);
        }

        if (labs.Count == 0) AnalysisMenu.Items.Add(new MenuItem { Header = "(no labs)", IsEnabled = false });

        // the selected node, in the Labs that take it
        var selected = NodeCommandHub.Current;
        var accepting = selected?.Model is { } model ? LabHost.LabsAccepting(model).ToList() : [];
        if (accepting.Count > 0)
        {
            AnalysisMenu.Items.Add(new Separator());
            foreach (var (lab, value) in accepting)
            {
                var l = lab;
                var v = value;
                var item = new MenuItem { Header = $"Open \"{selected!.Display.Replace("_", "__")}\" in {l.Name.Replace("_", "__")}" };
                item.Icon  = ToolbarIcons.Find(l.IconKey) is { } geometry ? new AppIcon { Icon = geometry, Size = 16 } : null;
                item.Click += (_, _) => LabHost.Open(l, v);
                AnalysisMenu.Items.Add(item);
            }
        }

        // the windows that are open
        var windows = LabHost.Windows;
        if (windows.Count > 0)
        {
            AnalysisMenu.Items.Add(new Separator());
            foreach (var window in windows)
            {
                var w = window;
                var item = new MenuItem { Header = "Window: " + Shorten(w.Title), ToolTip = w.Title };
                item.Click += (_, _) =>
                              {
                                  if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                                  w.Activate();
                              };
                AnalysisMenu.Items.Add(item);
            }
        }
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
            var entry = new MenuItem { Header = "Window: " + Shorten(w.Title), ToolTip = w.Title };
            entry.Items.Add(Item("_Show", w.Reveal));
            entry.Items.Add(Item("_Return to tab", w.ReturnToTab));
            entry.Items.Add(Item("_Close", w.Close));

            ViewMenu.Items.Add(entry);
            _windowItems.Add(entry);
        }
    }

    /// <summary>The longest a window's name is in a menu; a page's title can be a whole sentence.</summary>
    private const int WindowNameLimit = 40;

    /// <summary>A window's title for a menu: cut at <see cref="WindowNameLimit" /> characters with an ellipsis, and with "_" doubled (it would make the next letter an access key).</summary>
    internal static string Shorten(string? title, int limit = WindowNameLimit)
    {
        string text = (title ?? string.Empty).Trim();
        if (text.Length > limit) text = text[..(limit - 1)].TrimEnd() + "\u2026";
        return text.Replace("_", "__");
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
