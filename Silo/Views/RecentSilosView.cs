using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Silo.Commanding;
using Silo.Providers.Action;
using Silo.Settings;

namespace Silo.Views;

/// <summary>
///     The "Recent" navigation view, shown while no Silo is open: the Silos opened lately, newest first, so the last one
///     can be
///     reopened with a single double-click (or by pressing Enter -- the newest row is already selected and focused).
///     Right-click a row to open it, show it in its folder, copy its path or take it off the list.
/// </summary>
/// <remarks>
///     Built in code like the other nav views. Every colour is a <c>Theme.*</c> resource reference, so the list follows
///     Light/Dark live. It listens to <see cref="RecentSilos.Items" /> only while it is on screen, so a closed view is not
///     kept alive by the singleton list.
/// </remarks>
public class RecentSilosView : UserControl
{
    private readonly Button    _clear;
    private readonly TextBlock _empty;
    private readonly ListBox   _list;

    private static Button MakeButton(string text, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0) };
        button.Click += (_, _) => click();
        return button;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Items is cleared and refilled as one change; rebuild once things settle rather than per Add
        Dispatcher.BeginInvoke(new Action(Rebuild), DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        string? keep = (_list.SelectedItem as ListBoxItem)?.Tag is RecentSilo selected ? selected.Target : null;

        _list.Items.Clear();
        foreach (var entry in RecentSilos.Instance.Items)
        {
            var item = MakeItem(entry);
            _list.Items.Add(item);
            if (keep is not null && string.Equals(entry.Target, keep, StringComparison.OrdinalIgnoreCase))
                item.IsSelected = true;
        }

        bool any = _list.Items.Count > 0;
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        _list.Visibility  = any ? Visibility.Visible : Visibility.Collapsed;
        _clear.IsEnabled  = any;
        if (any && _list.SelectedItem is null)
            ((ListBoxItem)_list.Items[0]!).IsSelected = true;
    }

    /// <summary>Selects the newest row and gives the list the keyboard focus, so Enter opens it.</summary>
    private void FocusNewest()
    {
        if (_list.Items.Count == 0)
            return;

        _list.SelectedIndex = 0;
        _list.Focus();
    }

    private ListBoxItem MakeItem(RecentSilo entry)
    {
        bool exists = entry.Exists;

        var grid = new Grid { Margin                            = new Thickness(4, 6, 4, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new AppIcon
                   {
                       Margin  = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                       Opacity = exists ? 1 : 0.45
                   };
        icon.SetResourceReference(AppIcon.IconProperty, entry.IsRemote ? "Network" : "Database6");
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
                          {
                              Text         = entry.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, Opacity = exists ? 1 : 0.6,
                              TextTrimming = TextTrimming.CharacterEllipsis
                          });
        var path = new TextBlock
                   {
                       Text         = exists ? entry.Target : entry.Target + "  (not found)",
                       TextTrimming = TextTrimming.CharacterEllipsis,
                       FontSize     = 12,
                       Margin       = new Thickness(0, 1, 0, 0)
                   };
        path.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        text.Children.Add(path);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var when = new TextBlock
                   {
                       Text              = Ago(entry.LastOpenedUtc), Margin   = new Thickness(16, 0, 4, 0),
                       VerticalAlignment = VerticalAlignment.Center, FontSize = 12
                   };
        when.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        Grid.SetColumn(when, 2);
        grid.Children.Add(when);

        // the row stands for the entry: its right-click menu comes from RecentSiloActions (see ContextCommands)
        var item = new ListBoxItem { Content = grid, Tag = entry, DataContext = entry, ToolTip = entry.Target };
        return item;
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // only a double-click on a row, not on the scroll bar or the empty space below the rows
        if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListBoxItem { Tag: RecentSilo entry })
        {
            e.Handled = true;
            _         = OpenAsync(entry);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not RecentSilo entry)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _         = OpenAsync(entry);
                break;
            case Key.Delete:
                e.Handled = true;
                RecentSilos.Instance.Remove(entry);
                break;
        }
    }

    /// <summary>Opens the Silo like File -> Open would (problems are reported to the person by the command).</summary>
    internal static async Task OpenAsync(RecentSilo entry)
    {
        await OpenSiloCommand.OpenPathAsync(entry.Target);
    }

    internal static void CopyPath(RecentSilo entry)
    {
        try
        {
            Clipboard.SetText(entry.Target);
        }
        catch (Exception ex) // the clipboard can be locked by another program for a moment
        {
            App.Status($"Could not copy to the clipboard: {ex.Message}");
        }
    }

    internal static void ShowInFolder(RecentSilo entry)
    {
        try
        {
            if (File.Exists(entry.Target))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.Target}\"") { UseShellExecute = true });
            else if (Path.GetDirectoryName(entry.Target) is { } dir && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            else
                App.Status("That folder no longer exists");
        }
        catch (Exception ex)
        {
            App.Status($"Could not open the folder: {ex.Message}");
        }
    }

    private static string Ago(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalMinutes < 1 ? "just now" :
               age.TotalHours   < 1 ? $"{(int)age.TotalMinutes} min ago" :
               age.TotalDays    < 1 ? $"{(int)age.TotalHours} h ago" :
               age.TotalDays    < 7 ? $"{(int)age.TotalDays} d ago" : utc.ToLocalTime().ToString("d");
    }

    public RecentSilosView()
    {
        var root = new DockPanel { Margin = new Thickness(24) };

        // header: title, hint, and the buttons for opening something that is not in the list yet
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(header, Dock.Top);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Right);
        buttons.Children.Add(MakeButton("New Silo...",  () => App.NewSiloCommand.Execute(null)));
        buttons.Children.Add(MakeButton("Open Silo...", () => App.OpenSiloCommand.Execute(null)));
        _clear = MakeButton("Clear list", () => RecentSilos.Instance.Clear());
        buttons.Children.Add(_clear);
        header.Children.Add(buttons);

        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "Recent Silos", FontSize                  = 20, FontWeight = FontWeights.SemiBold });
        var hint = new TextBlock { Text          = "Double-click a Silo to open it.", Margin = new Thickness(0, 2, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        titles.Children.Add(hint);
        header.Children.Add(titles);
        root.Children.Add(header);

        _empty = new TextBlock
                 {
                     Text =
                         "Nothing opened yet. Use New Silo... or Open Silo... (or drop a .silo file on the window) and it will be listed here.",
                     TextWrapping        = TextWrapping.Wrap,
                     MaxWidth            = 480,
                     HorizontalAlignment = HorizontalAlignment.Left,
                     Margin              = new Thickness(0, 12, 0, 0)
                 };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        DockPanel.SetDock(_empty, Dock.Top);
        root.Children.Add(_empty);

        var settings = SiloFileSettings.Instance;
        var reopen = new CheckBox
                     {
                         Content = "Reopen the last Silo when Silo starts", IsChecked = settings.ReopenLastSilo,
                         Margin  = new Thickness(0, 12, 0, 0)
                     };
        reopen.Click += (_, _) => settings.ReopenLastSilo = reopen.IsChecked == true;
        DockPanel.SetDock(reopen, Dock.Bottom);
        root.Children.Add(reopen);

        _list = new ListBox { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _list.SetResourceReference(BackgroundProperty, "Theme.Background");
        _list.MouseDoubleClick += OnDoubleClick;
        _list.KeyDown          += OnKeyDown;

        // right-click a Silo for RecentSiloActions' commands; the blank area offers the buttons above
        ContextCommands.SetCommands(_list, new CommandList(() =>
                                                           [
                                                               ProvidedCommand.From("recent.new", "_New Silo...",
                                                                                    () => App.NewSiloCommand.Execute(null),
                                                                                    placement: CommandPlacement.Menu),
                                                               ProvidedCommand.From("recent.open", "_Open Silo...",
                                                                                    () => App.OpenSiloCommand.Execute(null),
                                                                                    placement: CommandPlacement.Menu),
                                                               ProvidedCommand.From("recent.clear", "_Clear list",
                                                                                    () => RecentSilos.Instance.Clear(), null,
                                                                                    () => RecentSilos.Instance.Items.Count > 0,
                                                                                    group: ProvidedCommand.LastGroup,
                                                                                    placement: CommandPlacement.Menu)
                                                           ]));
        ContextCommands.Attach(_list);
        root.Children.Add(_list);

        Content = root;
        Rebuild();

        // subscribe while on screen only
        Loaded += (_, _) =>
                  {
                      RecentSilos.Instance.Items.CollectionChanged += OnItemsChanged;
                      Rebuild();
                      FocusNewest();
                  };
        Unloaded += (_, _) => RecentSilos.Instance.Items.CollectionChanged -= OnItemsChanged;
    }
}

/// <summary>The commands of a recently opened Silo: open it, show it in its folder, copy its path, forget it.</summary>
public sealed class RecentSiloActions : IActionProvider<RecentSilo>
{
    public IEnumerable<ProvidedCommand> GetCommands(RecentSilo entry)
    {
        yield return ProvidedCommand.From("recentSilo.open", "_Open", () => _ = RecentSilosView.OpenAsync(entry),
                                          placement: CommandPlacement.Menu) with
                     {
                         IsDefault = true
                     };
        if (!entry.IsRemote)
            yield return ProvidedCommand.From("recentSilo.folder", "Show in _folder", () => RecentSilosView.ShowInFolder(entry),
                                              placement: CommandPlacement.Menu);
        yield return ProvidedCommand.From("recentSilo.copy", "_Copy path", () => RecentSilosView.CopyPath(entry),
                                          placement: CommandPlacement.Menu);
        yield return ProvidedCommand.From("recentSilo.remove", "_Remove from list", () => RecentSilos.Instance.Remove(entry),
                                          group: ProvidedCommand.LastGroup, placement: CommandPlacement.Menu);
    }
}