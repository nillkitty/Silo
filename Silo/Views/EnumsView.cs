using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Serilog;
using Silo.Commanding;
using Silo.Definitions;
using Silo.Model;
using Silo.Providers.Data;
using Silo.Providers.Definitions;

namespace Silo.Views;

/// <summary>One line of the enums list.</summary>
public sealed class EnumRow(EnumRecord record)
{
    public string     Description => Record.Description ?? string.Empty;
    public string     Flags       => DefinitionText.Describe(Record.Flags);
    public int        Id          => Record.Id;
    public string     Name        => Record.Name;
    public EnumRecord Record      { get; } = record;
    public string     Type        => Record.Type.ToString();
    public int        Values      => Record.Values.Count;
}

/// <summary>
///     The "Enums" view: the enums defined in the active Silo, with New, Edit and Delete. Double-click or press Enter to
///     edit one (its values are
///     edited in the same dialog). Other parts of the Silo refer to an enum as <c>enum:Name</c>.
/// </summary>
/// <remarks>
///     Built in code like the other views. It refreshes when an enum is saved or deleted and when the active Silo
///     changes, while on screen.
/// </remarks>
public class EnumsView : UserControl
{
    private readonly Button                        _delete;
    private readonly Button                        _edit;
    private readonly TextBlock                     _empty;
    private readonly TextBlock                     _footer;
    private readonly ListView                      _list;
    private readonly Button                        _new;
    private readonly ObservableCollection<EnumRow> _rows = [];

    private static OpenSilo? ActiveSilo => App.Instance?.ActiveSilo;

    private void OnChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(Rebuild), DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        int? keep = (_list.SelectedItem as EnumRow)?.Id;

        _rows.Clear();
        if (ActiveSilo?.Data is IEnumStore store)
            try
            {
                foreach (var e in store.GetEnums()) _rows.Add(new EnumRow(e));
                _footer.Text = "Stored in this Silo, in the metaverse database.";
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "The enums could not be listed");
                _footer.Text = "The enums could not be read: " + ex.Message;
            }
        else
            _footer.Text = ActiveSilo is null ? "No Silo is open." : "This Silo's storage cannot keep enums.";

        bool any = _rows.Count > 0;
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        _list.Visibility  = any ? Visibility.Visible : Visibility.Collapsed;
        if (keep is { } id && _rows.FirstOrDefault(r => r.Id == id) is { } row) _list.SelectedItem = row;
        else if (any) _list.SelectedIndex                                                          = 0;

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool silo = ActiveSilo?.Data is IEnumStore;
        _new.IsEnabled    = silo;
        _edit.IsEnabled   = silo && _list.SelectedItem is EnumRow;
        _delete.IsEnabled = silo && _list.SelectedItem is EnumRow;
    }

    private void New()
    {
        DefinitionActions.NewEnum(ActiveSilo);
    }

    private void Edit()
    {
        if (_list.SelectedItem is EnumRow row) DefinitionActions.EditEnum(ActiveSilo, row.Id);
    }

    private void Delete()
    {
        if (_list.SelectedItem is EnumRow row) DefinitionActions.DeleteEnum(ActiveSilo, row.Id);
    }

    private static Button MakeButton(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    public EnumsView()
    {
        var root = new DockPanel { Margin = new Thickness(24) };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(header, Dock.Top);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Right);
        _new    = MakeButton("New...",  New);
        _edit   = MakeButton("Edit...", Edit);
        _delete = MakeButton("Delete",  Delete);
        buttons.Children.Add(_new);
        buttons.Children.Add(_edit);
        buttons.Children.Add(_delete);
        header.Children.Add(buttons);

        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "Enums", FontSize = 20, FontWeight = FontWeights.SemiBold });
        var hint = new TextBlock
                   {
                       Text =
                           "Named sets of known values (a status, a set of flags) defined in this Silo. Refer to one as enum:Name.",
                       Margin = new Thickness(0, 2, 0, 0)
                   };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        titles.Children.Add(hint);
        header.Children.Add(titles);
        root.Children.Add(header);

        _footer = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        _footer.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        DockPanel.SetDock(_footer, Dock.Bottom);
        root.Children.Add(_footer);

        _empty = new TextBlock
                 {
                     Text                = "No enums yet. Use the New button above.",
                     TextWrapping        = TextWrapping.Wrap,
                     MaxWidth            = 480,
                     HorizontalAlignment = HorizontalAlignment.Left,
                     Margin              = new Thickness(0, 12, 0, 0)
                 };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        DockPanel.SetDock(_empty, Dock.Top);
        root.Children.Add(_empty);

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "Name", Width = 180, DisplayMemberBinding = new Binding(nameof(EnumRow.Name)) });
        grid.Columns.Add(new GridViewColumn
                         { Header = "Values are", Width = 90, DisplayMemberBinding = new Binding(nameof(EnumRow.Type)) });
        grid.Columns.Add(new GridViewColumn
                         { Header = "Known values", Width = 90, DisplayMemberBinding = new Binding(nameof(EnumRow.Values)) });
        grid.Columns.Add(new GridViewColumn { Header = "Flags", Width = 160, DisplayMemberBinding = new Binding(nameof(EnumRow.Flags)) });
        grid.Columns.Add(new GridViewColumn
                         { Header = "Description", Width = 320, DisplayMemberBinding = new Binding(nameof(EnumRow.Description)) });

        _list = new ListView { View = grid, ItemsSource = _rows, BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Single };
        _list.SetResourceReference(BackgroundProperty, "Theme.Background");
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, e) =>
                                  {
                                      if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListViewItem)
                                      {
                                          e.Handled = true;
                                          Edit();
                                      }
                                  };
        _list.KeyDown += (_, e) =>
                         {
                             if (e.Key == Key.Enter)
                             {
                                 e.Handled = true;
                                 Edit();
                             }
                             else if (e.Key == Key.Delete)
                             {
                                 e.Handled = true;
                                 Delete();
                             }
                         };

        // right-click a row for Edit / Delete (they act on the selection, which the right-click makes); the blank area offers
        // New -- see ContextCommands
        ContextCommands.SetItemCommands(_list, new CommandList(() =>
                                                               [
                                                                   ProvidedCommand.From("enum.edit", "_Edit...", Edit,
                                                                                        placement: CommandPlacement.Menu) with
                                                                   {
                                                                       IsDefault = true
                                                                   },
                                                                   ProvidedCommand.From("enum.delete", "_Delete", Delete,
                                                                                        group: ProvidedCommand.LastGroup,
                                                                                        placement: CommandPlacement.Menu) with
                                                                   {
                                                                       IsDestructive = true
                                                                   }
                                                               ]));
        ContextCommands.SetCommands(_list, new CommandList(() =>
                                                           [
                                                               ProvidedCommand.From("enum.new", "_New...", New,
                                                                                    placement: CommandPlacement.Menu)
                                                           ]));
        ContextCommands.Attach(_list);
        root.Children.Add(_list);

        Content = root;
        Rebuild();

        Loaded += (_, _) =>
                  {
                      DefinitionEvents.Changed += OnChanged;
                      if (App.Instance is { } app) app.ActiveSiloChanged += OnChanged;
                      Rebuild();
                  };
        Unloaded += (_, _) =>
                    {
                        DefinitionEvents.Changed -= OnChanged;
                        if (App.Instance is { } app) app.ActiveSiloChanged -= OnChanged;
                    };
    }
}