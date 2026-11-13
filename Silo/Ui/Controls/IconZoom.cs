using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Silo.Converters;
using Silo.Design;

namespace Silo.Ui.Controls;

/// <summary>
///     The zoom of an icon grid (the Icon Editor's palette, Change Icon): a slider over <see cref="AppDesignSettings.PickerIconSize" />,
///     and Ctrl+mouse wheel over the grid. Tiles made with <see cref="Tile" /> follow it live and are always big enough for their icon:
///     their size is worked out from the icon's, with no padding from the button style to squeeze it.
/// </summary>
public static class IconZoom
{
    /// <summary>The room around the icon inside a tile, each side.</summary>
    private const double Margin = 7;

    private static AppDesignSettings Settings => AppDesignSettings.Instance;

    /// <summary>The icon size now.</summary>
    public static double Size => Settings.PickerIconSize;

    /// <summary>
    ///     A tile for an icon: <paramref name="content" /> is an <see cref="AppIcon" /> or an <see cref="Image" />, whose size the tile sets
    ///     (and keeps setting as the zoom changes).
    /// </summary>
    public static ToggleButton Tile(FrameworkElement content)
    {
        var tile = new ToggleButton
                   {
                       Content                    = content,
                       Padding                    = new Thickness(0),
                       Margin                     = new Thickness(2),
                       HorizontalContentAlignment = HorizontalAlignment.Center,
                       VerticalContentAlignment   = VerticalAlignment.Center
                   };
        Resize(tile);

        PropertyChangedEventHandler handler = (_, e) =>
                                              {
                                                  if (e.PropertyName == nameof(AppDesignSettings.PickerIconSize)) Resize(tile);
                                              };
        tile.Loaded   += (_, _) =>
                         {
                             Settings.PropertyChanged -= handler;
                             Settings.PropertyChanged += handler;
                             Resize(tile);
                         };
        tile.Unloaded += (_, _) => Settings.PropertyChanged -= handler;
        return tile;
    }

    private static void Resize(ToggleButton tile)
    {
        double size = Size;
        tile.Width  = size + 2 * Margin;
        tile.Height = size + 2 * Margin;
        switch (tile.Content)
        {
            case AppIcon icon:
                icon.Size = size;
                break;
            case FrameworkElement element:
                element.Width  = size;
                element.Height = size;
                break;
        }
    }

    /// <summary>A "Zoom" slider (with the size in pixels) for the grids; Ctrl+mouse wheel over <paramref name="grid" /> zooms too.</summary>
    public static FrameworkElement Bar(UIElement grid)
    {
        var slider = new Slider
                     {
                         Minimum = 12, Maximum = 96, Value = Size, Width = 160, SmallChange = 2, LargeChange = 8,
                         TickFrequency = 2, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center,
                         ToolTip = "Icon size (Ctrl+mouse wheel over the icons)"
                     };
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), MinWidth = 40 };
        value.Text = $"{Size:0} px";

        slider.ValueChanged += (_, e) =>
                               {
                                   Settings.PickerIconSize = e.NewValue;
                                   value.Text              = $"{Size:0} px";
                               };

        grid.PreviewMouseWheel += (_, e) =>
                                  {
                                      if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

                                      slider.Value = Math.Clamp(slider.Value + (e.Delta > 0 ? 4 : -4), slider.Minimum, slider.Maximum);
                                      e.Handled    = true;
                                  };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(new TextBlock { Text = "Zoom:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        bar.Children.Add(slider);
        bar.Children.Add(value);
        return bar;
    }
}
