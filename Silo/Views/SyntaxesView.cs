using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Silo.Commanding;
using Silo.Definitions;
using Silo.Model;
using Silo.Providers.Definitions;

namespace Silo.Views;

/// <summary>One line of the syntaxes list.</summary>
public sealed class SyntaxRow(SyntaxRecord record)
{
    public SyntaxRecord Record      { get; } = record;
    public int          Id          => Record.Id;
    public string       Name        => DefinitionText.NameWithAlias(Record);
    public string       Source      => DefinitionText.Source(Record);
    public string       Example     => (Record.Example ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
    public string       Origin      => Record.IsSeeded ? "Predefined" : string.Empty;
    public string       Description => Record.Description ?? string.Empty;
}

/// <summary>
///     The "Syntaxes" view: the syntaxes defined in the active Silo (rules for checking a string and turning it into an object), with New, Edit, Delete
///     and Restore Defaults, which brings back any predefined syntax that was deleted. Double-click or press Enter to edit one. Other parts of the Silo
///     refer to a syntax as <c>syntax:Name</c> (or by its alias).
/// </summary>
/// <remarks>Built in code like the other views. It refreshes when a syntax is saved or deleted and when the active Silo changes, while on screen.</remarks>
public class SyntaxesView : UserControl
{
    private readonly ObservableCollection<SyntaxRow> _rows = [];
    private readonly ListView                      _list;
    private readonly TextBlock                     _empty;
    private readonly TextBlock                     _footer;
    private readonly Button                        _edit;
    private readonly Button                        _delete;
    private readonly Button                        _new;
    private readonly Button                        _restore;

    public SyntaxesView()
    {
        var root = new DockPanel { Margin = new Thickness(24) };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(header, Dock.Top);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Right);
        _new     = MakeButton("New...",  New);
        _edit    = MakeButton("Edit...", Edit);
        _delete  = MakeButton("Delete",  Delete);
        _restore = MakeButton("Restore defaults", () => DefinitionActions.RestoreDefaults(ActiveSilo));
        buttons.Children.Add(_new);
        buttons.Children.Add(_edit);
        buttons.Children.Add(_delete);
        buttons.Children.Add(_restore);
        header.Children.Add(buttons);

        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "Syntaxes", FontSize = 20, FontWeight = FontWeights.SemiBold });
        var hint = new TextBlock { Text = "Rules for checking a string and turning it into an object (a timestamp, a guid, JSON ...). Refer to one as syntax:Name.", Margin = new Thickness(0, 2, 0, 0) };
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
                     Text                = "No syntaxes. Use the New button above, or Restore Defaults to bring back the predefined ones.",
                     TextWrapping        = TextWrapping.Wrap,
                     MaxWidth            = 480,
                     HorizontalAlignment = HorizontalAlignment.Left,
                     Margin              = new Thickness(0, 12, 0, 0)
                 };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        DockPanel.SetDock(_empty, Dock.Top);
        root.Children.Add(_empty);

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "Name",        Width = 170, DisplayMemberBinding = new Binding(nameof(SyntaxRow.Name)) });
        grid.Columns.Add(new GridViewColumn { Header = "Built on",    Width = 170, DisplayMemberBinding = new Binding(nameof(SyntaxRow.Source)) });
        grid.Columns.Add(new GridViewColumn { Header = "Example",     Width = 200, DisplayMemberBinding = new Binding(nameof(SyntaxRow.Example)) });
        grid.Columns.Add(new GridViewColumn { Header = "",            Width = 80,  DisplayMemberBinding = new Binding(nameof(SyntaxRow.Origin)) });
        grid.Columns.Add(new GridViewColumn { Header = "Description", Width = 360, DisplayMemberBinding = new Binding(nameof(SyntaxRow.Description)) });

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
            ProvidedCommand.From("syntax.edit", "_Edit...", Edit, placement: CommandPlacement.Menu) with { IsDefault = true },
            ProvidedCommand.From("syntax.delete", "_Delete", Delete, group: ProvidedCommand.LastGroup, placement: CommandPlacement.Menu)
                with { IsDestructive = true }
        ]));
        ContextCommands.SetCommands(_list, new CommandList(() =>
        [
            ProvidedCommand.From("syntax.new", "_New...", New, placement: CommandPlacement.Menu),
            ProvidedCommand.From("syntax.restore", "_Restore defaults", () => DefinitionActions.RestoreDefaults(ActiveSilo),
                                 group: ProvidedCommand.LastGroup, placement: CommandPlacement.Menu)
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

    private static OpenSilo? ActiveSilo => App.Instance?.ActiveSilo;

    private void OnChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(Rebuild), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        int? keep = (_list.SelectedItem as SyntaxRow)?.Id;

        _rows.Clear();
        if (ActiveSilo?.Data is ISyntaxStore store)
        {
            try
            {
                foreach (var e in store.GetSyntaxes()) _rows.Add(new SyntaxRow(e));
                _footer.Text = "Stored in this Silo, in the metaverse database.";
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "The syntaxes could not be listed");
                _footer.Text = "The syntaxes could not be read: " + ex.Message;
            }
        }
        else
        {
            _footer.Text = ActiveSilo is null ? "No Silo is open." : "This Silo's storage cannot keep syntaxes.";
        }

        bool any = _rows.Count > 0;
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        _list.Visibility  = any ? Visibility.Visible : Visibility.Collapsed;
        if (keep is { } id && _rows.FirstOrDefault(r => r.Id == id) is { } row) _list.SelectedItem = row;
        else if (any) _list.SelectedIndex = 0;

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool silo = ActiveSilo?.Data is ISyntaxStore;
        _new.IsEnabled    = silo;
        _edit.IsEnabled   = silo && _list.SelectedItem is SyntaxRow;
        _delete.IsEnabled = silo && _list.SelectedItem is SyntaxRow;
        _restore.IsEnabled = silo;
    }

    private void New() => DefinitionActions.NewSyntax(ActiveSilo);

    private void Edit()
    {
        if (_list.SelectedItem is SyntaxRow row) DefinitionActions.EditSyntax(ActiveSilo, row.Id);
    }

    private void Delete()
    {
        if (_list.SelectedItem is SyntaxRow row) DefinitionActions.DeleteSyntax(ActiveSilo, row.Id);
    }

    private static Button MakeButton(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }
}
