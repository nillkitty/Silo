using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using AuthoredTable = Silo.DbModel.Global.AuthoredTable;
using Silo.Commanding;
using Silo.Model;
using Silo.Providers.Action;
using Silo.Tabular;

namespace Silo.Views;

/// <summary>
///     The "Tables" navigation view: the tables authored in the active Silo (Silo -> Add Data -> Tabular). Double-click
///     or press Enter to open one in the editor; right-click for rename, duplicate and delete.
/// </summary>
/// <remarks>Built in code like the other nav views. It refreshes when a table is saved / created / deleted and when the active Silo changes, while on screen.</remarks>
public class TablesView : UserControl
{
    private readonly ListBox   _list;
    private readonly TextBlock _empty;

    public TablesView()
    {
        var root = new DockPanel { Margin = new Thickness(24) };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(header, Dock.Top);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Right);
        buttons.Children.Add(MakeButton("New rich text table", () => TableEditors.CreateAndOpen(ActiveSilo, TableTemplate.RichText)));
        buttons.Children.Add(MakeButton("New plain table", () => TableEditors.CreateAndOpen(ActiveSilo, TableTemplate.PlainText)));
        buttons.Children.Add(MakeButton("New typed table", () => TableEditors.CreateAndOpen(ActiveSilo, TableTemplate.Typed)));
        header.Children.Add(buttons);

        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "Tables", FontSize = 20, FontWeight = FontWeights.SemiBold });
        var hint = new TextBlock { Text = "Tables typed into this Silo. Double-click one to edit it or copy it out as markdown, HTML or TSV.", Margin = new Thickness(0, 2, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        titles.Children.Add(hint);
        header.Children.Add(titles);
        root.Children.Add(header);

        _empty = new TextBlock
                 {
                     Text                = "No tables yet. Use Silo -> Add Data -> Tabular -> Rich Text, or the buttons above.",
                     TextWrapping        = TextWrapping.Wrap,
                     MaxWidth            = 480,
                     HorizontalAlignment = HorizontalAlignment.Left,
                     Margin              = new Thickness(0, 12, 0, 0)
                 };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        DockPanel.SetDock(_empty, Dock.Top);
        root.Children.Add(_empty);

        _list = new ListBox { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _list.SetResourceReference(BackgroundProperty, "Theme.Background");
        _list.MouseDoubleClick += (_, e) =>
                                  {
                                      if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListBoxItem { Tag: AuthoredTable t })
                                      {
                                          e.Handled = true;
                                          Open(t);
                                      }
                                  };
        _list.KeyDown += OnKeyDown;

        // right-click a table for TableActions' commands; the blank area offers the New buttons
        ContextCommands.SetCommands(_list, new CommandList(() =>
        [
            ProvidedCommand.From("tables.newRich", "New _rich text table", () => TableEditors.CreateAndOpen(ActiveSilo, TableTemplate.RichText), placement: CommandPlacement.Menu),
            ProvidedCommand.From("tables.newPlain", "New _plain table", () => TableEditors.CreateAndOpen(ActiveSilo, TableTemplate.PlainText), placement: CommandPlacement.Menu),
            ProvidedCommand.From("tables.newTyped", "New _typed table", () => TableEditors.CreateAndOpen(ActiveSilo, TableTemplate.Typed), placement: CommandPlacement.Menu)
        ]));
        ContextCommands.Attach(_list);
        root.Children.Add(_list);

        Content = root;
        Rebuild();

        Loaded += (_, _) =>
                  {
                      TableEditors.Changed += OnChanged;
                      if (App.Instance is { } app) app.ActiveSiloChanged += OnChanged;
                      Rebuild();
                  };
        Unloaded += (_, _) =>
                    {
                        TableEditors.Changed -= OnChanged;
                        if (App.Instance is { } app) app.ActiveSiloChanged -= OnChanged;
                    };
    }

    private static OpenSilo? ActiveSilo => App.Instance?.ActiveSilo;

    private void OnChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(Rebuild), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        int? keep = (_list.SelectedItem as ListBoxItem)?.Tag is AuthoredTable sel ? sel.Id : null;

        _list.Items.Clear();
        var tables = ActiveSilo?.Data is ITableStore store ? store.ListTables() : [];
        foreach (var t in tables)
        {
            var item = MakeItem(t);
            _list.Items.Add(item);
            if (keep == t.Id) item.IsSelected = true;
        }

        bool any = _list.Items.Count > 0;
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        _list.Visibility  = any ? Visibility.Visible : Visibility.Collapsed;
        if (any && _list.SelectedItem is null) ((ListBoxItem)_list.Items[0]!).IsSelected = true;
    }

    private ListBoxItem MakeItem(AuthoredTable t)
    {
        string summary;
        try
        {
            var s = t.ReadSheet();
            summary = $"{s.Rows.Count} rows × {s.Columns.Count} columns";
        }
        catch
        {
            summary = "unreadable data";
        }

        var grid = new Grid { Margin = new Thickness(4, 6, 4, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new AppIcon { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(AppIcon.IconProperty, "Data1");
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = t.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var sub = new TextBlock { Text = summary, FontSize = 12, Margin = new Thickness(0, 1, 0, 0) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        text.Children.Add(sub);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var when = new TextBlock { Text = t.Modified.ToLocalTime().ToString("g"), Margin = new Thickness(16, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        when.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        Grid.SetColumn(when, 2);
        grid.Children.Add(when);

        // the row stands for the table: its right-click menu comes from TableActions (see ContextCommands)
        return new ListBoxItem { Content = grid, Tag = t, DataContext = t };
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not AuthoredTable t) return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Open(t);
                break;
            case Key.Delete:
                e.Handled = true;
                Delete(t);
                break;
        }
    }

    private static void Open(AuthoredTable t) => TableEditors.OpenExisting(ActiveSilo, t.Id);

    private static void Duplicate(AuthoredTable t) => TableEditors.Duplicate(ActiveSilo, t.Id);

    private static void Delete(AuthoredTable t) => TableEditors.Delete(ActiveSilo, t.Id);

    private static Button MakeButton(string text, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0) };
        button.Click += (_, _) => click();
        return button;
    }
}

/// <summary>The commands of an authored table (in the active Silo): open, duplicate, delete.</summary>
public sealed class TableActions : IActionProvider<AuthoredTable>
{
    public IEnumerable<ProvidedCommand> GetCommands(AuthoredTable t)
    {
        int id = t.Id;
        yield return ProvidedCommand.From("table.open", "_Open", () => TableEditors.OpenExisting(App.Instance?.ActiveSilo, id),
                                          placement: CommandPlacement.Menu) with { IsDefault = true };
        yield return ProvidedCommand.From("table.duplicate", "_Duplicate", () => TableEditors.Duplicate(App.Instance?.ActiveSilo, id),
                                          placement: CommandPlacement.Menu);
        yield return ProvidedCommand.From("table.delete", "D_elete...", () => TableEditors.Delete(App.Instance?.ActiveSilo, id),
                                          group: ProvidedCommand.LastGroup, placement: CommandPlacement.Menu) with { IsDestructive = true };
    }
}
