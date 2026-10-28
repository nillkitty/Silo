using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Serilog;

using Silo.Assets;
using Silo.Contracts.Core;
using Silo.Contracts.Resources;
using Silo.Providers.Document;
using Silo.Providers.Format;

using Telefrag.DI;

namespace Silo.Tools;

/// <summary>
///     Browses every icon in the app -- the <c>StreamGeometry</c> icons in the application's
///     resource dictionary and the PNG/SVG files compiled or embedded in its own assemblies --
///     and copies the selected one's resource key or path. What exists, and where, comes from the
///     startup-time <see cref="ResourceIndex" />; this class only knows how to draw an icon.
/// </summary>
[Transient(typeof(ITool))]
public class IconViewer : ToolBase
{
    public IconViewer() : base("iconviewer")
    {
        DisplayName = "_Icon Viewer";
    }

    protected override UIElement OnBuildContent()
    {
        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // the size and the light/dark choice are shared by every tile: the tiles bind to / draw with these
        var view = new ViewSettings();
        var items = DiscoverIcons(ResourceIndex.Instance, view.Icon);

        var size = new Slider { Minimum = 16, Maximum = 160, Value = view.TileSize, Width = 200, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 4 };
        size.ValueChanged += (_, e) => view.TileSize = e.NewValue;
        var dark = new CheckBox { Content = "Dark", Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Shows the icons on a dark background" };
        dark.Click += (_, _) => view.IsDark = dark.IsChecked == true;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 0) };
        bar.Children.Add(new TextBlock { Text = "Size: ", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(size);
        bar.Children.Add(dark);
        Grid.SetRow(bar, 0);
        rootGrid.Children.Add(bar);

        var listBox = new ListBox
                      {
                          ItemsSource                = items,
                          Margin                     = new Thickness(8),
                          HorizontalContentAlignment = HorizontalAlignment.Center,
                          VerticalContentAlignment   = VerticalAlignment.Center
                      };

        // Attached property must be set via static setter
        ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);

        var panelFactory = new FrameworkElementFactory(typeof(WrapPanel));
        listBox.ItemsPanel = new ItemsPanelTemplate(panelFactory);

        var dt           = new DataTemplate(typeof(IconItem));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BorderThicknessProperty,  new Thickness(1));
        // bound, not set: a template freezes a brush it is given, and these change with the light/dark switch
        borderFactory.SetBinding(Border.BorderBrushProperty, new Binding(nameof(ViewSettings.Edge)) { Source = view });
        borderFactory.SetBinding(Border.BackgroundProperty,  new Binding(nameof(ViewSettings.Tile)) { Source = view });
        borderFactory.SetValue(Border.MarginProperty,           new Thickness(2));
        borderFactory.SetValue(Border.PaddingProperty,          new Thickness(4));
        borderFactory.SetBinding(FrameworkElement.WidthProperty,  new Binding(nameof(ViewSettings.TileSize)) { Source = view });
        borderFactory.SetBinding(FrameworkElement.HeightProperty, new Binding(nameof(ViewSettings.TileSize)) { Source = view });
        borderFactory.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Name"));

        var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenter.SetBinding(ContentPresenter.ContentProperty, new Binding("VisualElement"));
        borderFactory.AppendChild(contentPresenter);
        dt.VisualTree        = borderFactory;
        listBox.ItemTemplate = dt;

        Grid.SetRow(listBox, 1);
        rootGrid.Children.Add(listBox);

        var bottomPanel = new Grid { Margin = new Thickness(8, 0, 8, 8) };
        bottomPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
                    {
                        Text              = "Resource Key / Path: ",
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin            = new Thickness(0, 0, 6, 0)
                    };
        Grid.SetColumn(label, 0);
        bottomPanel.Children.Add(label);

        var copyBox = new TextBox
                      {
                          VerticalAlignment = VerticalAlignment.Center,
                          Margin            = new Thickness(0, 0, 8, 0),
                          IsReadOnly        = true
                      };
        Grid.SetColumn(copyBox, 1);
        bottomPanel.Children.Add(copyBox);

        var copyButton = new Button
                         {
                             Content   = "Copy",
                             Width     = 75,
                             Height    = 26,
                             IsEnabled = false
                         };
        Grid.SetColumn(copyButton, 2);
        bottomPanel.Children.Add(copyButton);

        listBox.SelectionChanged += (_, _) =>
                                    {
                                        if (listBox.SelectedItem is IconItem selected)
                                        {
                                            copyBox.Text         = selected.CopyValue;
                                            copyButton.IsEnabled = true;
                                        }
                                        else
                                        {
                                            copyBox.Text         = string.Empty;
                                            copyButton.IsEnabled = false;
                                        }
                                    };

        copyButton.Click += (_, _) =>
                            {
                                if (!string.IsNullOrEmpty(copyBox.Text)) Clipboard.SetText(copyBox.Text);
                            };

        Grid.SetRow(bottomPanel, 2);
        rootGrid.Children.Add(bottomPanel);

        return rootGrid;
    }

    /// <summary>
    ///     Turns the index's icon-like resources into things to draw: dictionary
    ///     <c>StreamGeometry</c> entries first, then every PNG and SVG.
    /// </summary>
    private static List<IconItem> DiscoverIcons(IResourceIndex index, Brush fill)
    {
        var list = new List<IconItem>();

        foreach (var entry in index.OfKind(ResourceKind.ResourceDictionaryEntry))
            if (entry.GetValue() is StreamGeometry geometry)
                list.Add(new IconItem
                         {
                             Name          = entry.Name,
                             SourceType    = "StreamGeometry",
                             VisualElement = CreatePathViewbox(geometry, fill),
                             CopyValue     = entry.Reference
                         });

        var png = Formats.Of<PngFormat>();
        var svg = Formats.Of<SvgFormat>();
        foreach (var entry in index.WithExtension([..png.DottedExtensions(), ..svg.DottedExtensions()]))
        {
            bool compiled = entry.Kind == ResourceKind.CompiledResource;

            if (png.Claims(entry.Extension))
            {
                var bitmap = LoadBitmap(entry);
                if (bitmap is null) continue;

                list.Add(new IconItem
                         {
                             Name       = entry.Name,
                             SourceType = compiled ? "PNG (Pack URI)" : "Embedded PNG",
                             VisualElement = new Image
                                             {
                                                 Source    = bitmap,
                                                 Stretch   = Stretch.Uniform
                                             },
                             CopyValue = entry.Reference
                         });
            }
            else
            {
                var geometry = LoadSvgGeometry(entry);
                if (geometry is null) continue;

                list.Add(new IconItem
                         {
                             Name          = entry.Name,
                             SourceType    = compiled ? "SVG Stream" : "Embedded SVG",
                             VisualElement = CreatePathViewbox(geometry, fill),
                             CopyValue     = entry.Reference
                         });
            }
        }

        return list;
    }

    private static BitmapImage? LoadBitmap(ResourceEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            if (stream is null) return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption  = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not load image resource '{resource}'", entry);
            return null;
        }
    }

    private static Geometry? LoadSvgGeometry(ResourceEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            if (stream is null) return null;

            using var reader = new StreamReader(stream);
            return ExtractGeometryFromSvg(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read SVG resource '{resource}'", entry);
            return null;
        }
    }

    private static Viewbox CreatePathViewbox(Geometry geometry, Brush fill)
    {
        var path = new System.Windows.Shapes.Path
                   {
                       Data    = geometry,
                       Fill    = fill, // shared: the light/dark switch changes its colour for every icon at once
                       Stretch = Stretch.Uniform
                   };

        return new Viewbox { Child = path }; // fills its tile, so it follows the tile's size
    }

    /// <summary>What the tiles share: how big they are and whether they are drawn light-on-dark.</summary>
    private sealed class ViewSettings : System.ComponentModel.INotifyPropertyChanged
    {
        private bool   _dark;
        private double _tileSize = 48;

        public Brush Edge { get; private set; } = Brushes.LightGray;

        /// <summary>The colour the icons are drawn in.</summary>
        public SolidColorBrush Icon { get; } = new(Colors.Black);

        public bool IsDark
        {
            get => _dark;
            set
            {
                _dark      = value;
                Icon.Color = value ? Colors.White : Colors.Black;
                Tile       = value ? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)) : Brushes.White;
                Edge       = value ? new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)) : Brushes.LightGray;
                Changed(nameof(Tile));
                Changed(nameof(Edge));
            }
        }

        public Brush Tile { get; private set; } = Brushes.White;

        private void Changed(string name)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        public double TileSize
        {
            get => _tileSize;
            set
            {
                _tileSize = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TileSize)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private static Geometry? ExtractGeometryFromSvg(string svgContent)
    {
        var matches = Regex.Matches(svgContent, "(?i)<path[^>]*?\\sd=[\"']([^\"']+)[\"']");
        if (matches.Count == 0) return null;

        string combined = string.Join(" ", matches.Select(m => m.Groups[1].Value.Trim()));
        try
        {
            return StreamGeometry.Parse(combined);
        }
        catch
        {
            return null;
        }
    }

    private sealed class IconItem
    {
        public string  CopyValue     { get; set; } = string.Empty;
        public string  Name          { get; set; } = string.Empty;
        public string  SourceType    { get; set; } = string.Empty;
        public object? VisualElement { get; set; }
    }
}
