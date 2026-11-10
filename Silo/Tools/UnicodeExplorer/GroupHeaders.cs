using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     The header row of a group of characters in a tile grid (the collection, a palette page): a full-width line with a fold arrow, the name and the
///     count. A click folds or unfolds the group; its right-click menu is the caller's. It is a child of the same wrap panel as the tiles, marked with
///     <see cref="GlyphDrag.IsHeaderProperty" /> so drops know it.
/// </summary>
internal static class GroupHeaders
{
    /// <param name="scroll">The scroll viewer the tiles are in: the header is as wide as its view, so the tiles after it start on a new line.</param>
    /// <param name="title">The group's name.</param>
    /// <param name="count">How many characters it has.</param>
    /// <param name="collapsed">Whether its characters are hidden.</param>
    /// <param name="startIndex">The index of the group's first character (where a drop on the header goes).</param>
    /// <param name="toggle">Folds or unfolds it.</param>
    /// <param name="menu">The entries of its right-click menu.</param>
    public static Border Create(ScrollViewer scroll, string title, int count, bool collapsed, int startIndex, Action toggle, Func<IEnumerable<Control>> menu)
    {
        var text = new TextBlock { Text = $"{(collapsed ? "▸" : "▾")}  {title}  ({count:N0})", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");

        var header = new Border
                     {
                         Child           = text,
                         Height          = 24,
                         Margin          = new Thickness(1, 4, 1, 2),
                         Padding         = new Thickness(6, 0, 6, 0),
                         BorderThickness = new Thickness(0, 0, 0, 1),
                         Cursor          = System.Windows.Input.Cursors.Hand,
                         ToolTip         = collapsed ? "Click to show its characters" : "Click to fold the group",
                         Focusable       = false
                     };
        header.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        header.SetResourceReference(Border.BackgroundProperty, "Theme.BackgroundAlt");
        header.SetBinding(FrameworkElement.WidthProperty, new Binding(nameof(ScrollViewer.ViewportWidth)) { Source = scroll, Converter = Narrower.Instance, ConverterParameter = 14.0 });
        GlyphDrag.SetIsHeader(header, true);
        GlyphDrag.SetIndex(header, startIndex);

        header.MouseLeftButtonUp += (_, e) =>
                                    {
                                        toggle();
                                        e.Handled = true;
                                    };
        header.ContextMenu = new ContextMenu();
        header.ContextMenuOpening += (_, _) =>
                                     {
                                         header.ContextMenu!.Items.Clear();
                                         foreach (var item in menu()) header.ContextMenu.Items.Add(item);
                                     };
        return header;
    }

    /// <summary>A width less a fixed amount (never below one pixel), for a header that has to fit inside the scroll viewer's view.</summary>
    private sealed class Narrower : IValueConverter
    {
        public static Narrower Instance { get; } = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double width = value is double d && double.IsFinite(d) ? d : 0;
            return Math.Max(1, width - (parameter is double less ? less : 0));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
