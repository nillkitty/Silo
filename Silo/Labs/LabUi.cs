using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Silo.Ui.Windows;

namespace Silo.Labs;

/// <summary>Small helpers for the code-built views of the Labs (the Regex, Syntax, Stream, QR Code, Menu and Schema Labs share them).</summary>
internal static partial class LabUi
{
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    public static readonly Brush Green = Frozen(0x3F, 0xA9, 0x5B);
    public static readonly Brush Red   = Frozen(0xE5, 0x48, 0x4D);
    public static readonly Brush Gray  = Frozen(0x8A, 0x8F, 0x98);
    public static readonly Brush Blue  = Frozen(0x3B, 0x82, 0xD6);
    public static readonly Brush Amber = Frozen(0xD9, 0xA4, 0x1E);

    /// <summary>Soft colors for marking things in text (matches, groups), light enough to read the text through.</summary>
    public static readonly Brush[] Marks =
    [
        Frozen(0x3B, 0x82, 0xD6, 0x55), Frozen(0x3F, 0xA9, 0x5B, 0x55), Frozen(0xD9, 0xA4, 0x1E, 0x66), Frozen(0xB0, 0x5D, 0xD6, 0x55),
        Frozen(0xE5, 0x48, 0x4D, 0x44), Frozen(0x2D, 0xB8, 0xC4, 0x55)
    ];

    public static Brush Frozen(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static T Themed<T>(this T control, string? background = "Theme.Background", string? foreground = "Theme.Text") where T : Control
    {
        if (background is not null) control.SetResourceReference(Control.BackgroundProperty, background);
        if (foreground is not null) control.SetResourceReference(Control.ForegroundProperty, foreground);
        return control;
    }

    /// <summary>
    ///     Makes a box look like something to type in: a white page with dark text and a visible edge, whatever the theme (a box in the theme's
    ///     background reads as a read-only panel).
    /// </summary>
    public static T Paper<T>(this T box) where T : TextBox
    {
        box.Background      = Brushes.White;
        box.Foreground      = Brushes.Black;
        box.CaretBrush      = Brushes.Black;
        box.BorderBrush     = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
        box.BorderThickness = new Thickness(1);
        return box;
    }

    public static TextBlock Label(string text, bool muted = false, bool bold = false, bool mono = false, bool wrap = false)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Theme.TextMuted" : "Theme.Text");
        if (bold) block.FontWeight = FontWeights.SemiBold;
        if (mono) block.FontFamily = Mono;
        if (wrap) block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    /// <summary>A heading line over a group of controls.</summary>
    public static TextBlock Heading(string text)
    {
        var block = Label(text, bold: true);
        block.Margin = new Thickness(6, 8, 6, 2);
        return block;
    }

    public static Border Box(UIElement content, bool selected = false, string background = "Theme.Surface", Thickness? padding = null)
    {
        var border = new Border
                     {
                         Child           = content,
                         BorderThickness = new Thickness(selected ? 2 : 1),
                         CornerRadius    = new CornerRadius(4),
                         Padding         = padding ?? new Thickness(6, 4, 6, 4),
                         Margin          = new Thickness(2)
                     };
        border.SetResourceReference(Border.BorderBrushProperty, selected ? "Theme.Accent" : "Theme.Border");
        border.SetResourceReference(Border.BackgroundProperty, background);
        return border;
    }

    /// <summary>A button with an icon and its text (the icon that fits the text unless one is given): in the Labs no button is only an icon or only text.</summary>
    public static Button Button(string text, Action click, string? tip = null, bool enabled = true, string? icon = null)
    {
        return Tool(text, icon, click, tip, enabled);
    }

    public static CheckBox Check(string text, bool on, Action<bool> changed, string? tip = null)
    {
        var box = new CheckBox { Content = text, IsChecked = on, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        box.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        box.Checked   += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    public static ComboBox Combo(IEnumerable<string> items, string? selected, Action<string> changed, double width = 140)
    {
        var combo = new ComboBox { MinWidth = width, Margin = new Thickness(2, 0, 2, 0) };
        foreach (string item in items) combo.Items.Add(item);
        combo.SelectedItem = selected;
        combo.SelectionChanged += (_, _) =>
                                  {
                                      if (combo.SelectedItem is string s) changed(s);
                                  };
        return combo;
    }

    /// <summary>A one-line box that reports its text as it is typed.</summary>
    public static TextBox Line(string? text, Action<string> changed, double width = 160, string? tip = null, bool mono = false)
    {
        var box = new TextBox { Text = text ?? string.Empty, MinWidth = width, Margin = new Thickness(2, 0, 2, 0), ToolTip = tip, VerticalContentAlignment = VerticalAlignment.Center };
        if (mono) box.FontFamily = Mono;
        box.TextChanged += (_, _) => changed(box.Text);
        return box;
    }

    /// <summary>A one-line box that reports its text when the person is done with it (Enter, or the focus leaves).</summary>
    public static TextBox Field(string? text, Action<string> commit, double width = 140, string? tip = null, bool mono = false)
    {
        var    box    = new TextBox { Text = text ?? string.Empty, MinWidth = width, Margin = new Thickness(2, 0, 2, 0), ToolTip = tip, VerticalContentAlignment = VerticalAlignment.Center };
        string before = box.Text;
        if (mono) box.FontFamily = Mono;

        void Commit()
        {
            if (box.Text == before) return;

            before = box.Text;
            commit(box.Text);
        }

        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
                       {
                           if (e.Key == Key.Enter) Commit();
                       };
        return box;
    }

    /// <summary>A several-line box for text that is typed or pasted, in a fixed-width font.</summary>
    public static TextBox Memo(string? text, Action<string> changed, bool readOnly = false, bool wrap = false)
    {
        var box = new TextBox
                  {
                      Text                          = text ?? string.Empty,
                      AcceptsReturn                 = true,
                      AcceptsTab                    = true,
                      IsReadOnly                    = readOnly,
                      FontFamily                    = Mono,
                      TextWrapping                  = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                      VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                      HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                      Padding                       = new Thickness(6),
                      BorderThickness               = new Thickness(0, 1, 0, 1)
                  };
        box.Themed("Theme.Background");
        box.TextChanged += (_, _) => changed(box.Text);
        return box;
    }

    public static WrapPanel Bar(params UIElement[] items)
    {
        var bar = new WrapPanel { Margin = new Thickness(6, 4, 6, 4) };
        foreach (var item in items) bar.Children.Add(item);
        return bar;
    }

    public static DockPanel Layout(UIElement? top, UIElement fill, UIElement? bottom = null)
    {
        var panel = new DockPanel { LastChildFill = true };
        if (top is not null)
        {
            DockPanel.SetDock(top, System.Windows.Controls.Dock.Top);
            panel.Children.Add(top);
        }

        if (bottom is not null)
        {
            DockPanel.SetDock(bottom, System.Windows.Controls.Dock.Bottom);
            panel.Children.Add(bottom);
        }

        panel.Children.Add(fill);
        return panel;
    }

    public static ListBox List()
    {
        var list = new ListBox { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Grid.SetIsSharedSizeScope(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        return list.Themed();
    }

    public static TextBox Output(bool wrap = false)
    {
        var box = new TextBox
                  {
                      IsReadOnly                    = true,
                      FontFamily                    = Mono,
                      TextWrapping                  = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                      VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                      HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                      BorderThickness               = new Thickness(0),
                      Padding                       = new Thickness(6)
                  };
        box.Themed();
        return box;
    }

    /// <summary>A grid of two columns (a title and a control) for a form.</summary>
    public static Grid Form(IEnumerable<(string Title, UIElement Control)> rows, double titleWidth = 110)
    {
        var grid = new Grid { Margin = new Thickness(6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(titleWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        foreach (var (title, control) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Label(title, muted: true);
            label.Margin = new Thickness(0, 3, 6, 3);
            Grid.SetRow(label, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 2, 0, 2);
            grid.Children.Add(label);
            grid.Children.Add(control);
            row++;
        }

        return grid;
    }

    public static string? Ask(string title, string prompt, string initial = "")
    {
        return PromptDialog.Ask(title, prompt, "OK", initial);
    }

    public static bool Confirm(string text, string title)
    {
        return PromptDialog.Show(title, text, ["Yes", "No"]).Button == 0;
    }

    public static void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // the clipboard is in use by another program: nothing was copied
        }
    }

    public static string Shorten(string text, int max = 40)
    {
        string one = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return one.Length <= max ? one : one[..max] + "…";
    }
}

/// <summary>Runs an action once the person stops typing for a moment (on the UI thread).</summary>
internal sealed class Debouncer(TimeSpan delay, Action action)
{
    private DispatcherTimer? _timer;

    public void Poke()
    {
        _timer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = delay }.Also(t => t.Tick += (_, _) =>
                                                                                                         {
                                                                                                             t.Stop();
                                                                                                             action();
                                                                                                         });
        _timer.Stop();
        _timer.Start();
    }

    public void Cancel() => _timer?.Stop();
}

internal static class LabExtensions
{
    public static T Also<T>(this T value, Action<T> act)
    {
        act(value);
        return value;
    }
}
