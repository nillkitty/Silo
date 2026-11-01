using System.Windows.Controls;
using Serilog;
using Silo.Clipboarding;
using Silo.Commanding;
using Silo.Converters;
using Silo.Labs;
using Silo.Tabular;
using Silo.TextEditing;
using Silo.Toolbar;
using Silo.Ui.Documents;
using Silo.Ui.Panes;

namespace Silo.Ui;

/// <summary>
///     Interaction logic for MenuBar.xaml
/// </summary>
public partial class MenuBar : Menu
{
    /// <summary>The longest a window's name is in a menu; a page's title can be a whole sentence.</summary>
    private const int WindowNameLimit = 40;

    /// <summary>The items listing the pop-out windows in the View menu.</summary>
    private readonly List<Control> _windowItems = [];

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    // ---- the menus made from the layout -------------------------------------------------------------

    /// <summary>The menus made from the layout (<see cref="MenuSettings" />), by id.</summary>
    private readonly Dictionary<string, MenuItem> _menus = [];

    /// <summary>
    ///     Makes the menu bar's menus from the layout: each is filled in from the groups it lists each time it opens, so what components add
    ///     (<see cref="MenuCommands" />) and what the selection and the open Silo make available is always current. The View menu is the one made in XAML.
    /// </summary>
    private void BuildMenus()
    {
        var native = Items.OfType<MenuItem>().ToDictionary(m => m.Name, m => m);
        Items.Clear();
        _menus.Clear();

        foreach (var def in MenuSettings.Layout.Menus)
        {
            if (def.Native)
            {
                if (native.TryGetValue(def.Id + "Menu", out var existing)) Items.Add(existing);
                continue;
            }

            var menu = new MenuItem { Header = def.Header, Tag = def };

            // an empty menu item cannot open, so SubmenuOpened would never come: this stand-in is replaced each time the menu opens
            menu.Items.Add(new MenuItem { Header = "(loading)", IsEnabled = false });
            menu.SubmenuOpened += GroupMenu_SubmenuOpened;
            _menus[def.Id]     =  menu;
            Items.Add(menu);
        }
    }

    private void GroupMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        // nested submenus raise this too, and it bubbles up here
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not MenuItem { Tag: MenuDef def } menu) return;

        var ctx = new MenuContext(Main, NodeCommandHub.Current);
        menu.Items.Clear();
        try
        {
            FillMenu(menu, def, ctx);

            // the active document's own editing entries, below the standard ones
            if (def.Id == MenuGroups.EditMenu) AddDocumentEditEntries(menu);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Building the {menu} menu failed", def.Id);
        }

        // never leave it empty: a menu item with no items cannot open again
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = def.EmptyText ?? "(nothing here)", IsEnabled = false });
    }

    /// <summary>Puts the layout's groups in the menu: each block, a separator between blocks that have entries, a block that is a submenu as one.</summary>
    internal static void FillMenu(MenuItem menu, MenuDef def, MenuContext ctx)
    {
        foreach (var section in def.Sections)
        {
            var entries = MenuCommands.Entries(section.Group == MenuGroups.Default ? def.Id : section.Group, ctx);
            if (entries.Count == 0) continue;

            if (menu.Items.Count > 0 && menu.Items[^1] is not Separator) menu.Items.Add(new Separator());

            if (section.Submenu is { Length: > 0 } header)
            {
                var sub = new MenuItem { Header = header };
                foreach (var entry in entries) sub.Items.Add(entry);
                menu.Items.Add(sub);
            }
            else
            {
                foreach (var entry in entries) menu.Items.Add(entry);
            }
        }

        while (menu.Items.Count > 0 && menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
    }

    private void AddDocumentEditEntries(MenuItem editMenu)
    {
        var entries = new List<EditEntry>();
        try
        {
            if (Main?.ActiveEditContributor is { } contributor) entries.AddRange(contributor.EditEntries());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Building the document's Edit entries failed");
        }

        if (entries.Count > 0 && editMenu.Items.Count > 0) editMenu.Items.Add(new Separator());
        foreach (var entry in entries) editMenu.Items.Add(entry.ToMenuElement());
    }
    // ---- View ----------------------------------------------------------------------------------

    /// <summary>
    ///     Refreshes the pane check marks from what is actually showing (a pane can be closed with its own X), and the
    ///     list of windows.
    /// </summary>
    private void ViewMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        // nested submenus (Theme, User Level) raise this too, and it bubbles up here
        if (!ReferenceEquals(e.OriginalSource, sender) || Main is not { } main)
            return;

        ViewExplorer.IsChecked     = main.IsExplorerOpen;
        ViewProperties.IsChecked   = main.IsPropertiesOpen;
        ViewToolbox.IsChecked      = main.IsToolboxOpen;
        ViewProblems.IsChecked     = ProblemsTool.Current?.IsChecked ?? false;
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
            entry.Items.Add(Item("_Show",          w.Reveal));
            entry.Items.Add(Item("_Return to tab", w.ReturnToTab));
            entry.Items.Add(Item("_Close",         w.Close));

            ViewMenu.Items.Add(entry);
            _windowItems.Add(entry);
        }
    }

    /// <summary>
    ///     A window's title for a menu: cut at <see cref="WindowNameLimit" /> characters with an ellipsis, and with "_"
    ///     doubled (it would make the next letter an access key).
    /// </summary>
    internal static string Shorten(string? title, int limit = WindowNameLimit)
    {
        string text                   = (title ?? string.Empty).Trim();
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
        {
            main.SetExplorerOpen(item.IsChecked);
        }
        else if (ReferenceEquals(item, ViewProperties))
        {
            main.SetPropertiesOpen(item.IsChecked);
        }
        else if (ReferenceEquals(item, ViewToolbox))
        {
            main.SetToolboxOpen(item.IsChecked);
        }
        else if (ReferenceEquals(item, ViewProblems))
        {
            if (ProblemsTool.Current is not { } problems) return;
            if (item.IsChecked) problems.Show();
            else if (problems.IsChecked) problems.Toggle();
            item.IsChecked = problems.IsChecked;
        }
        else if (ReferenceEquals(item, ViewSelectedItem))
        {
            main.SetSelectedItemOpen(item.IsChecked);
            item.IsChecked = main.IsSelectedItemOpen; // nothing selected: nothing opened
        }
    }

    private void Terminal_Click(object sender, RoutedEventArgs e)
    {
        Main?.StatusStrip.Terminal.FocusInput();
    }

    private void TerminalLog_Click(object sender, RoutedEventArgs e)
    {
        Main?.ShowTerminalLog();
    }

    public MenuBar()
    {
        InitializeComponent();
        BuildMenus();
        MenuSettings.Changed += (_, _) => Dispatcher.InvokeAsync(BuildMenus);

        // MenuBar derives from Menu, and an implicit style only applies to its exact target type, so the themed Menu
        // style (background, text colour) is asked for explicitly
        SetResourceReference(StyleProperty, typeof(Menu));
    }
}