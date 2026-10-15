using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Silo.Tabular;

/// <summary>
///     Shows a rich text cell: set <see cref="MarkupProperty" /> on a <see cref="TextBlock" /> and its inlines are built
///     from the cell's inline markdown (<see cref="InlineMarkup" />).
/// </summary>
public static class RichTextBlock
{
    public static readonly DependencyProperty MarkupProperty =
        DependencyProperty.RegisterAttached("Markup", typeof(string), typeof(RichTextBlock), new PropertyMetadata(null, OnMarkupChanged));

    private static readonly Brush CodeBackground = MakeBrush(Color.FromArgb(48, 128, 128, 128));

    public static string? GetMarkup(DependencyObject d) => (string?)d.GetValue(MarkupProperty);

    public static void SetMarkup(DependencyObject d, string? value) => d.SetValue(MarkupProperty, value);

    private static Brush MakeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static void OnMarkupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock tb) Build(tb, e.NewValue as string);
    }

    public static void Build(TextBlock tb, string? markup)
    {
        tb.Inlines.Clear();
        foreach (var span in InlineMarkup.Parse(markup))
        {
            var run = new Run(span.Text);
            if (span.Bold) run.FontWeight = FontWeights.Bold;
            if (span.Italic) run.FontStyle = FontStyles.Italic;

            var decorations = new TextDecorationCollection();
            if (span.Strike) decorations.Add(TextDecorations.Strikethrough[0]);
            if (span.Link is not null)
            {
                decorations.Add(TextDecorations.Underline[0]);
                run.SetResourceReference(TextElement.ForegroundProperty, "Theme.Accent");
                run.ToolTip = span.Link;
            }

            if (decorations.Count > 0) run.TextDecorations = decorations;

            if (span.Code)
            {
                run.FontFamily = new FontFamily("Consolas");
                run.Background = CodeBackground;
            }

            tb.Inlines.Add(run);
        }
    }
}

/// <summary>Converts a boolean cell (held as text) to and from a check box.</summary>
public sealed class BoolTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return SheetExport.TryBool(value as string, out bool b) && b;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "true" : "false";
    }
}

/// <summary>
///     A grid column for rich text: shows the cell formatted, edits it as its markdown source in an ordinary text box
///     (so typing, selecting and the usual text-box shortcuts all work), and adds Ctrl+B / Ctrl+I / Ctrl+K / Ctrl+` and
///     Shift+Enter (a line break inside the cell).
/// </summary>
public sealed class RichTextColumn : DataGridTextColumn
{
    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var element = base.GenerateElement(cell, dataItem);
        if (element is TextBlock tb && base.Binding is System.Windows.Data.Binding b)
        {
            // show formatting instead of the raw markers
            BindingOperations.ClearBinding(tb, TextBlock.TextProperty);
            tb.SetBinding(RichTextBlock.MarkupProperty, new Binding(b.Path.Path) { Mode = BindingMode.OneWay });
            tb.TextWrapping = TextWrapping.Wrap;
        }

        return element;
    }

    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem)
    {
        var element = base.GenerateEditingElement(cell, dataItem);
        if (element is TextBox tb)
        {
            tb.TextWrapping  = TextWrapping.Wrap;
            tb.AcceptsReturn = false;
            tb.PreviewKeyDown += OnEditKeyDown;
        }

        return element;
    }

    private static void OnEditKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;

        bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (e.Key == Key.Enter && shift)
        {
            int at = tb.SelectionStart;
            tb.SelectedText = "\n";
            tb.SelectionStart  = at + 1;
            tb.SelectionLength = 0;
            e.Handled = true;
            return;
        }

        if (!ctrl) return;

        string? marker = e.Key switch
                         {
                             Key.B                      => "**",
                             Key.I                      => "*",
                             Key.OemTilde               => "`",
                             Key.S when shift           => "~~",
                             _                          => null
                         };
        if (marker is not null)
        {
            Wrap(tb, marker);
            e.Handled = true;
        }
        else if (e.Key == Key.K)
        {
            Link(tb);
            e.Handled = true;
        }
    }

    /// <summary>Wraps (or unwraps) the selection of a cell's text box in a formatting marker.</summary>
    public static void Wrap(TextBox tb, string marker)
    {
        var r = InlineMarkup.ToggleWrap(tb.Text, tb.SelectionStart, tb.SelectionLength, marker);
        tb.Text            = r.Text;
        tb.SelectionStart  = r.Start;
        tb.SelectionLength = r.Length;
    }

    /// <summary>Turns the selection into <c>[selection](https://)</c> and selects the address ready to be typed over.</summary>
    public static void Link(TextBox tb)
    {
        int    start = tb.SelectionStart;
        string label = tb.SelectedText.Length > 0 ? tb.SelectedText : "link";
        string url   = "https://";
        tb.SelectedText = $"[{label}]({url})";
        tb.SelectionStart  = start + label.Length + 3;
        tb.SelectionLength = url.Length;
    }
}
