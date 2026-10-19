using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>Small helpers for building the explorer's code-built UI with the app's theme (<c>Theme.*</c> brushes are looked up live, so a theme change applies at once).</summary>
internal static class UiKit
{
    /// <summary>The fonts glyphs are drawn with: text, symbols, emoji, math, and a few for scripts Segoe UI lacks.</summary>
    public static readonly FontFamily GlyphFont =
        new("Segoe UI, Segoe UI Symbol, Segoe UI Emoji, Cambria Math, Segoe UI Historic, Nirmala UI, Malgun Gothic, Microsoft YaHei, Yu Gothic, MS Gothic");

    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    /// <summary>Makes the element take a theme brush for the property, and follow it when the theme changes.</summary>
    public static T Themed<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    public static TextBlock Text(string text, bool muted = false, double size = 0, FontWeight? weight = null, bool wrap = false)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (size > 0) t.FontSize = size;
        if (weight is { } w) t.FontWeight = w;
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        if (muted) t.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        return t;
    }

    public static Button Btn(string text, Action click, string? tip = null, double minWidth = 0)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        if (minWidth > 0) b.MinWidth = minWidth;
        if (tip is not null) b.ToolTip = tip;
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A button that opens a menu below it.</summary>
    public static Button MenuBtn(string text, Func<IEnumerable<Control>> items, string? tip = null)
    {
        var b = new Button { Content = text + " ▾", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        if (tip is not null) b.ToolTip = tip;
        b.Click += (_, _) =>
                   {
                       var menu = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                       foreach (var item in items()) menu.Items.Add(item);
                       menu.IsOpen = true;
                   };
        return b;
    }

    public static MenuItem Item(string header, Action click, bool enabled = true, string? tip = null, string? gesture = null)
    {
        var m = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture };
        if (tip is not null) m.ToolTip = tip;
        m.Click += (_, _) => click();
        return m;
    }

    public static Separator Rule() => new();

    /// <summary>A bordered cell that shows a glyph (and optionally a small caption); the caller sets its size, tooltip and handlers.</summary>
    public static Border Cell(string glyph, double glyphSize, double width, double height, string? caption = null)
    {
        var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        inner.Children.Add(new TextBlock
                           {
                               Text = glyph, FontFamily = GlyphFont, FontSize = glyphSize, HorizontalAlignment = HorizontalAlignment.Center,
                               TextAlignment = TextAlignment.Center
                           }.Themed(TextBlock.ForegroundProperty, "Theme.Text"));
        if (!string.IsNullOrEmpty(caption))
            inner.Children.Add(new TextBlock { Text = caption, FontSize = 10, MaxWidth = width - 4, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center }
                                   .Themed(TextBlock.ForegroundProperty, "Theme.TextMuted"));

        var border = new Border
                     {
                         Width = width, Height = height, Margin = new Thickness(1), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                         Child = inner, Cursor = Cursors.Hand, Focusable = false
                     };
        border.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        border.SetResourceReference(Border.BackgroundProperty, "Theme.Surface");
        return border;
    }

    /// <summary>Shows a cell as selected or not (and hovered).</summary>
    public static void Paint(Border cell, bool selected, bool hot = false)
    {
        cell.SetResourceReference(Border.BackgroundProperty, selected ? "Theme.Selection" : hot ? "Theme.SurfaceHover" : "Theme.Surface");
        cell.SetResourceReference(Border.BorderBrushProperty, selected ? "Theme.Accent" : "Theme.Border");
    }

    public static ComboBox Combo(double width, string tip)
    {
        return new ComboBox { Width = width, Margin = new Thickness(0, 0, 6, 0), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>Puts text on the clipboard; false (not an exception) when the clipboard is busy.</summary>
    public static bool TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }
}
