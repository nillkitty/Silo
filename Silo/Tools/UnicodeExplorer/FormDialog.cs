using System.Globalization;
using System.Windows.Controls;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     A small modal form built from rows (a check box, a number, a choice, a line of text), for the explorer's Page Setup and Preferences: the caller adds
///     the rows, shows it, and reads the values back from the row objects.
/// </summary>
internal sealed class FormDialog : Window
{
    private readonly StackPanel _rows = new();

    /// <summary>A row's value, read after the form was accepted.</summary>
    internal interface IRow
    {
    }

    internal sealed class CheckRow(CheckBox box) : IRow
    {
        public bool Value => box.IsChecked == true;
    }

    internal sealed class NumberRow(TextBox box, double min, double max, double fallback) : IRow
    {
        /// <summary>The number typed, kept within its range; the starting one when what was typed is not a number.</summary>
        public double Value => double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double v) || double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                                   ? Math.Clamp(v, min, max)
                                   : fallback;
    }

    internal sealed class ChoiceRow(ComboBox box) : IRow
    {
        public int Index => Math.Max(0, box.SelectedIndex);
    }

    internal FormDialog(Window? owner, string title, string? intro = null, double width = 460)
    {
        Title                 = title;
        Width                 = width;
        SizeToContent         = SizeToContent.Height;
        ResizeMode            = ResizeMode.NoResize;
        ShowInTaskbar         = false;
        Owner                 = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        this.SetResourceReference(BackgroundProperty, "Theme.Background");
        this.SetResourceReference(ForegroundProperty, "Theme.Text");

        var root = new StackPanel { Margin = new Thickness(16) };
        if (intro is not null)
        {
            var text = UiKit.Text(intro, muted: true, wrap: true);
            text.Margin = new Thickness(0, 0, 0, 10);
            root.Children.Add(text);
        }

        root.Children.Add(_rows);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "OK", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true });
        root.Children.Add(buttons);
        Content = root;
    }

    private Grid Row(string label, UIElement editor, string? tip)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = UiKit.Text(label);
        grid.Children.Add(text);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        if (tip is not null)
        {
            text.ToolTip = tip;
            if (editor is FrameworkElement fe) fe.ToolTip = tip;
        }

        _rows.Children.Add(grid);
        return grid;
    }

    public CheckRow AddCheck(string label, bool value, string? tip = null)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 3, 0, 3) };
        if (tip is not null) box.ToolTip = tip;
        _rows.Children.Add(box);
        return new CheckRow(box);
    }

    public NumberRow AddNumber(string label, double value, double min, double max, string? tip = null)
    {
        var box = new TextBox { Text = value.ToString("0.##", CultureInfo.CurrentCulture), Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        Row(label, box, tip);
        return new NumberRow(box, min, max, value);
    }

    public ChoiceRow AddChoice(string label, IEnumerable<string> options, int selected, string? tip = null)
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220 };
        foreach (string o in options) box.Items.Add(o);
        box.SelectedIndex = selected;
        Row(label, box, tip);
        return new ChoiceRow(box);
    }

    /// <summary>Shows the form; true when it was accepted.</summary>
    public bool Ask() => ShowDialog() == true;
}
