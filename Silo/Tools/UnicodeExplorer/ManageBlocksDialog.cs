using System.Windows.Controls;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     Blocks &gt; Manage Blocks…: every Unicode block with a check box, so whole blocks can be switched on and off in bulk. The buttons act on the blocks
///     the search box leaves showing: type "CJK" and uncheck what is shown to switch off all of them.
/// </summary>
internal sealed class ManageBlocksDialog : Window
{
    private readonly record struct Row(UnicodeBlock Block, CheckBox Box, UIElement Element);

    private readonly List<Row>  _rows    = [];
    private readonly TextBox    _filter  = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock  _summary = UiKit.Text(string.Empty, muted: true);

    private ManageBlocksDialog(Window? owner, IReadOnlyList<UnicodeBlock> blocks, BlockOverrides current)
    {
        Title                 = "Manage Blocks";
        Width                 = 560;
        Height                = 640;
        MinWidth              = 420;
        MinHeight             = 360;
        ShowInTaskbar         = false;
        Owner                 = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        this.SetResourceReference(BackgroundProperty, "Theme.Background");
        this.SetResourceReference(ForegroundProperty, "Theme.Text");

        var list = new StackPanel();
        foreach (var block in blocks.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            var box = new CheckBox { IsChecked = BlockPolicy.IsEnabled(block.Name, current), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 2, 2, 2) };
            var text = new StackPanel { Orientation = Orientation.Horizontal };
            text.Children.Add(new TextBlock { Text = block.Name });
            var detail = UiKit.Text($"   U+{block.Start:X4}–U+{block.End:X4} · {block.Count:N0}", muted: true);
            text.Children.Add(detail);
            box.Content = text;
            box.Checked   += (_, _) => UpdateSummary();
            box.Unchecked += (_, _) => UpdateSummary();
            list.Children.Add(box);
            _rows.Add(new Row(block, box, box));
        }

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
        var frame  = new Border { BorderThickness = new Thickness(1), Child = scroll };
        frame.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        frame.SetResourceReference(Border.BackgroundProperty, "Theme.BackgroundAlt");

        var tools = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        tools.Children.Add(UiKit.Btn("Check shown", () => SetShown(true), "Switch on the blocks the search box shows"));
        tools.Children.Add(UiKit.Btn("Uncheck shown", () => SetShown(false), "Switch off the blocks the search box shows"));
        tools.Children.Add(UiKit.Btn("Reset to default", Reset, "Back to the shipped choice: the ideograph, Hangul syllable, Yi, Tangut, Nushu and Khitan blocks off, everything else on"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "OK", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true });

        var root = new Grid { Margin = new Thickness(14) };
        for (int i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var intro = UiKit.Text("Blocks that are switched off are left out of searches and lists. Choosing one from the Blocks menu, choosing its script, or looking up one of its characters still shows it.", muted: true, wrap: true);
        intro.Margin = new Thickness(0, 0, 0, 8);
        Place(root, intro, 0);
        _filter.ToolTip = "Show only the blocks whose name contains this";
        var filterRow = new StackPanel();
        filterRow.Children.Add(_filter);
        Place(root, filterRow, 1);
        Place(root, frame, 2);
        var below = new StackPanel();
        below.Children.Add(tools);
        _summary.Margin = new Thickness(0, 4, 0, 0);
        below.Children.Add(_summary);
        Place(root, below, 3);
        Place(root, buttons, 4);
        Content = root;

        _filter.TextChanged += (_, _) => ApplyFilter();
        Loaded += (_, _) => _filter.Focus();
        UpdateSummary();
    }

    private static void Place(Grid grid, UIElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private IEnumerable<Row> Shown => _rows.Where(r => r.Element.Visibility == Visibility.Visible);

    private void ApplyFilter()
    {
        string text = _filter.Text.Trim();
        foreach (var r in _rows)
            r.Element.Visibility = text.Length == 0 || r.Block.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;

        UpdateSummary();
    }

    private void SetShown(bool on)
    {
        foreach (var r in Shown) r.Box.IsChecked = on;
    }

    private void Reset()
    {
        foreach (var r in _rows) r.Box.IsChecked = !BlockPolicy.IsOffByDefault(r.Block.Name);
    }

    private void UpdateSummary()
    {
        int on = _rows.Count(r => r.Box.IsChecked == true);
        long characters = _rows.Where(r => r.Box.IsChecked != true).Sum(r => (long)r.Block.Count);
        int shown = Shown.Count();
        _summary.Text = $"{on:N0} of {_rows.Count:N0} blocks on" + (shown == _rows.Count ? string.Empty : $" · {shown:N0} shown") + $" · {characters:N0} code points switched off";
    }

    /// <summary>The blocks the person left checked, as changes from the default.</summary>
    private BlockOverrides Result() => BlockPolicy.FromEnabled(_rows.Select(r => r.Block), name => _rows.First(r => r.Block.Name == name).Box.IsChecked == true);

    /// <summary>Shows the dialog; null when it was cancelled.</summary>
    public static BlockOverrides? Ask(Window? owner, IReadOnlyList<UnicodeBlock> blocks, BlockOverrides current)
    {
        var dialog = new ManageBlocksDialog(owner, blocks, current);
        return dialog.ShowDialog() == true ? dialog.Result() : null;
    }
}
