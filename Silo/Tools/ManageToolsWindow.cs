using System.Windows.Controls;

namespace Silo.Tools;

/// <summary>
///     The Manage Tools dialog: switch tools on or off and put them in order. The order is that of the Tools menu and of
///     the toolbar's tool buttons and Tools drop-down. OK saves and applies (<see cref="ToolSettings" />).
/// </summary>
public sealed class ManageToolsWindow : Window
{
    private readonly Button  _down = new() { Content = "Move _Down", MinWidth = 110, Margin = new Thickness(0, 0, 0, 6) };
    private readonly ListBox _list = new();
    private readonly Button  _up   = new() { Content = "Move _Up", MinWidth = 110, Margin = new Thickness(0, 0, 0, 6) };

    private readonly List<ToolEntry> _work;

    private ToolEntry? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as ToolEntry;

    /// <summary>Shows the dialog; if OK is pressed the arrangement is saved and the menu and toolbar follow.</summary>
    public static void ShowFor(Window? owner)
    {
        var tools = ToolSettings.Ordered(App.Resolve<IEnumerable<ITool>>() ?? []);
        var work  = tools.DistinctBy(t => t.Name).Select(t => new ToolEntry { Name = t.Name, Enabled = ToolSettings.IsEnabled(t.Name) }).ToList();

        var dialog = new ManageToolsWindow(work, tools.ToDictionary(t => t.Name, t => t.Header, StringComparer.Ordinal));
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation              = WindowStartupLocation.CenterScreen;

        if (dialog.ShowDialog() == true) ToolSettings.Apply(dialog._work);
    }

    private void Fill(ToolEntry? select, IReadOnlyDictionary<string, string> headers)
    {
        _list.Items.Clear();
        foreach (var entry in _work)
        {
            var check = new CheckBox { Content = headers.GetValueOrDefault(entry.Name, entry.Name), IsChecked = entry.Enabled };
            var e     = entry;
            check.Checked   += (_, _) => e.Enabled = true;
            check.Unchecked += (_, _) => e.Enabled = false;

            var item = new ListBoxItem { Content = check, Tag = entry };
            _list.Items.Add(item);
            if (ReferenceEquals(entry, select)) item.IsSelected = true;
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        int i = Selected is { } s ? _work.IndexOf(s) : -1;
        _up.IsEnabled   = i > 0;
        _down.IsEnabled = i >= 0 && i < _work.Count - 1;
    }

    private void Move(int by, IReadOnlyDictionary<string, string> headers)
    {
        if (Selected is not { } entry) return;

        int i = _work.IndexOf(entry), j = i + by;
        if (j < 0 || j >= _work.Count) return;

        _work.RemoveAt(i);
        _work.Insert(j, entry);
        Fill(entry, headers);
    }

    private ManageToolsWindow(List<ToolEntry> work, IReadOnlyDictionary<string, string> headers)
    {
        _work = work;

        Title                 = "Manage Tools";
        Width                 = 460;
        Height                = 480;
        MinWidth              = 380;
        MinHeight             = 320;
        ShowInTaskbar         = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Theme.Background");
        SetResourceReference(ForegroundProperty, "Theme.Text");

        var root = new DockPanel { Margin = new Thickness(14) };

        var intro = new TextBlock
                    {
                        Text         = "Tick the tools to have in the Tools menu and on the toolbar, and put them in the order they should come in.",
                        TextWrapping = TextWrapping.Wrap,
                        Margin       = new Thickness(0, 0, 0, 10)
                    };
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        var bottom  = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var ok      = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel  = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);

        var reset = new Button { Content = "_Reset", Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) =>
                       {
                           var sorted = _work.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
                           _work.Clear();
                           foreach (var e in sorted)
                           {
                               e.Enabled = true;
                               _work.Add(e);
                           }

                           Fill(null, headers);
                       };
        bottom.Children.Add(reset);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var side = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        side.Children.Add(_up);
        side.Children.Add(_down);
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);

        root.Children.Add(_list);
        Content = root;

        _list.SelectionChanged += (_, _) => UpdateButtons();
        _up.Click              += (_, _) => Move(-1, headers);
        _down.Click            += (_, _) => Move(+1, headers);

        Fill(null, headers);
    }
}
