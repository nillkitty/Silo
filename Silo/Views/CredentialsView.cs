using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Serilog;
using Silo.Commanding;
using Silo.Credentials;
using Silo.Model;
using Silo.Ui.Windows;

namespace Silo.Views;

/// <summary>One line of the credentials list.</summary>
public sealed class CredentialRow(CredentialSummary summary)
{
    public string Account => Summary.IsReadable ? Summary.Account ?? string.Empty : "(cannot be opened here)";
    public int Id => Summary.Id;
    public string Modified => Summary.Modified == DateTime.MinValue ? string.Empty : Summary.Modified.ToLocalTime().ToString("g");
    public string Name => Summary.Name;
    public CredentialSummary Summary { get; } = summary;
}

/// <summary>
///     The "Credentials" navigation view: the credentials saved in the active Silo, with New, Edit and Delete.
///     Double-click or press Enter to
///     edit one. Passwords are never shown in the list; the edit dialog is where one can be revealed or copied.
/// </summary>
/// <remarks>
///     Built in code like the other nav views. It refreshes when a credential is saved or deleted and when the active
///     Silo changes, while on screen.
/// </remarks>
public class CredentialsView : UserControl
{
    private readonly Button                              _delete;
    private readonly Button                              _edit;
    private readonly TextBlock                           _empty;
    private readonly TextBlock                           _footer;
    private readonly ListView                            _list;
    private readonly Button                              _new;
    private readonly ObservableCollection<CredentialRow> _rows = [];

    private static OpenSilo? ActiveSilo => App.Instance?.ActiveSilo;

    private void OnChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(Rebuild), DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        int? keep = (_list.SelectedItem as CredentialRow)?.Id;

        _rows.Clear();
        var service = ActiveSilo?.Credentials;
        if (service is not null)
            try
            {
                foreach (var summary in service.List()) _rows.Add(new CredentialRow(summary));
                _footer.Text = $"Encrypted in this Silo. Protected by: {CredentialWindow.ProtectorName(service)}.";
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "The credentials could not be listed");
                _footer.Text = "The credentials could not be read: " + ex.Message;
            }
        else
            _footer.Text = ActiveSilo is null ? "No Silo is open." : "This Silo's storage cannot keep credentials.";

        bool any = _rows.Count > 0;
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        _list.Visibility  = any ? Visibility.Visible : Visibility.Collapsed;
        if (keep is { } id && _rows.FirstOrDefault(r => r.Id == id) is { } row) _list.SelectedItem = row;
        else if (any) _list.SelectedIndex                                                          = 0;

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool silo = ActiveSilo?.Credentials is not null;
        _new.IsEnabled    = silo;
        _edit.IsEnabled   = silo && _list.SelectedItem is CredentialRow;
        _delete.IsEnabled = silo && _list.SelectedItem is CredentialRow;
    }

    private void New()
    {
        if (ActiveSilo is { } silo) CredentialWindow.Prompt(silo);
    }

    private void Edit()
    {
        if (ActiveSilo is { } silo && _list.SelectedItem is CredentialRow row) CredentialWindow.Prompt(silo, row.Id);
    }

    private void Delete()
    {
        if (ActiveSilo?.Credentials is not { } service || _list.SelectedItem is not CredentialRow row) return;

        if (MessageBox.Show($"Delete the credential \"{row.Name}\" from this Silo? Connections that use it will have to be given another.",
                            "Delete credential", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        service.Delete(row.Id);
    }

    private static Button MakeButton(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    public CredentialsView()
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
        titles.Children.Add(new TextBlock { Text = "Credentials", FontSize = 20, FontWeight = FontWeights.SemiBold });
        var hint = new TextBlock
                   {
                       Text =
                           "User names and passwords saved in this Silo, encrypted. Connections that need to sign in can use them.",
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
                     Text                = "No credentials yet. Use Silo -> New Credential, or the New button above.",
                     TextWrapping        = TextWrapping.Wrap,
                     MaxWidth            = 480,
                     HorizontalAlignment = HorizontalAlignment.Left,
                     Margin              = new Thickness(0, 12, 0, 0)
                 };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        DockPanel.SetDock(_empty, Dock.Top);
        root.Children.Add(_empty);

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn
                         { Header = "Name", Width = 220, DisplayMemberBinding = new Binding(nameof(CredentialRow.Name)) });
        grid.Columns.Add(new GridViewColumn
                         { Header = "Account", Width = 220, DisplayMemberBinding = new Binding(nameof(CredentialRow.Account)) });
        grid.Columns.Add(new GridViewColumn
                         { Header = "Modified", Width = 160, DisplayMemberBinding = new Binding(nameof(CredentialRow.Modified)) });

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
                                                                   ProvidedCommand.From("credential.edit", "_Edit...", Edit,
                                                                                        placement: CommandPlacement.Menu) with
                                                                   {
                                                                       IsDefault = true
                                                                   },
                                                                   ProvidedCommand.From("credential.delete", "_Delete", Delete,
                                                                                        group: ProvidedCommand.LastGroup,
                                                                                        placement: CommandPlacement.Menu) with
                                                                   {
                                                                       IsDestructive = true
                                                                   }
                                                               ]));
        ContextCommands.SetCommands(_list, new CommandList(() =>
                                                           [
                                                               ProvidedCommand.From("credential.new", "_New...", New, null,
                                                                                    () => ActiveSilo?.Credentials is not null,
                                                                                    placement: CommandPlacement.Menu)
                                                           ]));
        ContextCommands.Attach(_list);
        root.Children.Add(_list);

        Content = root;
        Rebuild();

        Loaded += (_, _) =>
                  {
                      CredentialService.Changed += OnChanged;
                      if (App.Instance is { } app) app.ActiveSiloChanged += OnChanged;
                      Rebuild();
                  };
        Unloaded += (_, _) =>
                    {
                        CredentialService.Changed -= OnChanged;
                        if (App.Instance is { } app) app.ActiveSiloChanged -= OnChanged;
                    };
    }
}