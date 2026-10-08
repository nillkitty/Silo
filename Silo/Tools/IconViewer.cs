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
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var items = DiscoverIcons(ResourceIndex.Instance);

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

        var dt            = new DataTemplate(typeof(IconItem));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BorderThicknessProperty,  new Thickness(1));
        borderFactory.SetValue(Border.BorderBrushProperty,      Brushes.LightGray);
        borderFactory.SetValue(Border.BackgroundProperty,       Brushes.White);
        borderFactory.SetValue(Border.MarginProperty,           new Thickness(2));
        borderFactory.SetValue(Border.PaddingProperty,          new Thickness(4));
        borderFactory.SetValue(FrameworkElement.WidthProperty,  48.0);
        borderFactory.SetValue(FrameworkElement.HeightProperty, 48.0);
        borderFactory.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Name"));

        var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenter.SetBinding(ContentPresenter.ContentProperty, new Binding("VisualElement"));
        borderFactory.AppendChild(contentPresenter);
        dt.VisualTree        = borderFactory;
        listBox.ItemTemplate = dt;

        Grid.SetRow(listBox, 0);
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

        Grid.SetRow(bottomPanel, 1);
        rootGrid.Children.Add(bottomPanel);

        return rootGrid;
    }

    /// <summary>
    ///     Turns the index's icon-like resources into things to draw: dictionary
    ///     <c>StreamGeometry</c> entries first, then every PNG and SVG.
    /// </summary>
    private static List<IconItem> DiscoverIcons(IResourceIndex index)
    {
        var list = new List<IconItem>();

        foreach (var entry in index.OfKind(ResourceKind.ResourceDictionaryEntry))
            if (entry.GetValue() is StreamGeometry geometry)
                list.Add(new IconItem
                         {
                             Name          = entry.Name,
                             SourceType    = "StreamGeometry",
                             VisualElement = CreatePathViewbox(geometry),
                             CopyValue     = entry.Reference
                         });

        foreach (var entry in index.WithExtension(".png", ".svg"))
        {
            bool compiled = entry.Kind == ResourceKind.CompiledResource;

            if (entry.Extension == ".png")
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
                                                 Stretch   = Stretch.Uniform,
                                                 MaxWidth  = 40,
                                                 MaxHeight = 40
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
                             VisualElement = CreatePathViewbox(geometry),
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

    private static Viewbox CreatePathViewbox(Geometry geometry)
    {
        var path = new System.Windows.Shapes.Path
                   {
                       Data    = geometry,
                       Fill    = Brushes.Black,
                       Stretch = Stretch.Uniform
                   };

        return new Viewbox
               {
                   Width  = 24,
                   Height = 24,
                   Child  = path
               };
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
