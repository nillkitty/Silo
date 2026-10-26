using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DockSide = System.Windows.Controls.Dock;

namespace Silo.Labs.Images;

/// <summary>Small helpers the panes of the Image Lab share.</summary>
internal static class ImagePaneUi
{
    public static Color ToColor(Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public static Brush BrushOf(Rgba c)
    {
        var brush = new SolidColorBrush(ToColor(c));
        brush.Freeze();
        return brush;
    }

    /// <summary>The checkerboard that shows through what is transparent.</summary>
    public static Brush Checker()
    {
        var light = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        var dark  = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 2, 2))));
        group.Children.Add(new GeometryDrawing(dark, null, new GeometryGroup { Children = [new RectangleGeometry(new Rect(0, 0, 1, 1)), new RectangleGeometry(new Rect(1, 1, 1, 1))] }));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 8, 8), ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 2, 2) };
        brush.Freeze();
        return brush;
    }

    public static Slider Slider(double min, double max, Action<int> changed)
    {
        var slider = new Slider { Minimum = min, Maximum = max, IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 10), IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => changed((int)Math.Round(e.NewValue));
        return slider;
    }

    /// <summary>A label, a slider and the number it stands for, in a row.</summary>
    public static Grid SliderRow(string title, Slider slider, TextBlock value, string? tip = null)
    {
        var grid = new Grid { Margin = new Thickness(8, 2, 8, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        var label = LabUi.Label(title, muted: true);
        label.VerticalAlignment = VerticalAlignment.Center;
        value.VerticalAlignment = VerticalAlignment.Center;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        grid.Children.Add(label);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        if (tip is not null)
        {
            label.ToolTip  = tip;
            slider.ToolTip = tip;
        }

        return grid;
    }

    public static Border Swatch(Rgba color, double size, string tip, Action<bool> pick)
    {
        var inner = new Border { Background = BrushOf(color) };
        var swatch = new Border
                     {
                         Width           = size,
                         Height          = size,
                         Margin          = new Thickness(1),
                         BorderThickness = new Thickness(1),
                         Background      = Checker(),
                         Child           = inner,
                         ToolTip         = tip,
                         Cursor          = Cursors.Hand
                     };
        swatch.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        swatch.MouseLeftButtonUp  += (_, e) =>
                                     {
                                         pick(true);
                                         e.Handled = true;
                                     };
        swatch.MouseRightButtonUp += (_, e) =>
                                     {
                                         pick(false);
                                         e.Handled = true;
                                     };
        return swatch;
    }

    /// <summary>A small picture of a part of a raster, scaled to fit.</summary>
    public static Raster Thumbnail(Raster source, int width, int height)
    {
        var thumb = new Raster(width, height);
        double sx = (double)source.Width / width, sy = (double)source.Height / height;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double r = 0, g = 0, b = 0, a = 0;
            const int n = 3;
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int px = Math.Min(source.Width - 1, (int)((x + (i + 0.5) / n) * sx)), py = Math.Min(source.Height - 1, (int)((y + (j + 0.5) / n) * sy));
                var c = source.Get(px, py);
                double alpha = c.A / 255.0;
                r += c.R * alpha;
                g += c.G * alpha;
                b += c.B * alpha;
                a += alpha;
            }

            if (a <= 0) continue;

            thumb.Set(x, y, new Rgba((byte)Math.Round(r / a), (byte)Math.Round(g / a), (byte)Math.Round(b / a), (byte)Math.Round(a / (n * n) * 255)));
        }

        return thumb;
    }
}

/// <summary>The foreground and background colors: swatches, numbers, a palette and the colors used lately.</summary>
internal sealed class ImageColorsInspector(ImageLabSession session) : LiveInspector(session)
{
    private static readonly string[] Palette =
    [
        "#000000", "#404040", "#808080", "#C0C0C0", "#FFFFFF", "#7F0000", "#FF0000", "#FF7F00", "#FFD800", "#FFFF00", "#7FFF00", "#00FF00", "#007F00", "#00FF7F", "#00FFFF", "#007F7F",
        "#0000FF", "#00007F", "#7F00FF", "#FF00FF", "#7F007F", "#FF007F", "#8B5A2B", "#D2B48C", "#FFC0CB", "#FFA07A", "#F5DEB3", "#98FB98", "#87CEEB", "#B0A0FF", "#DDA0DD", "#556B2F"
    ];

    private Grid?      _swatches;
    private TextBox?   _fgHex, _bgHex;
    private Slider?    _red, _green, _blue, _alpha;
    private TextBlock? _redText, _greenText, _blueText, _alphaText, _editing;
    private WrapPanel? _recent;
    private bool       _background;
    private bool       _loading;
    private string     _recentKey = string.Empty;

    public override string Id => "colors";

    public override string Title => "Colors";

    public override string IconKey => "Symbol";

    protected override UIElement Create()
    {
        _swatches = new Grid { Width = 78, Height = 62, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 8, 8, 4) };
        _fgHex = LabUi.Field("#000000", v => Apply(true, v), 84, "The foreground color: #RGB, #RRGGBB or #RRGGBBAA", true);
        _bgHex = LabUi.Field("#FFFFFF", v => Apply(false, v), 84, "The background color: #RGB, #RRGGBB or #RRGGBBAA", true);
        var swap  = LabUi.Button("Swap", session.SwapColors, "Foreground becomes background and the other way round (X)");
        var reset = LabUi.Button("Black / White", session.ResetColors, "Back to black on white (D)");
        _editing = LabUi.Label("Editing: foreground", muted: true);
        _editing.Margin = new Thickness(8, 4, 8, 0);

        var hexes = new StackPanel { Margin = new Thickness(4, 8, 8, 4) };
        hexes.Children.Add(LabUi.Form([("Foreground", _fgHex), ("Background", _bgHex)], 74));
        hexes.Children.Add(LabUi.Bar(swap, reset));
        var top = new DockPanel();
        DockPanel.SetDock(_swatches, DockSide.Left);
        top.Children.Add(_swatches);
        top.Children.Add(hexes);

        _redText   = LabUi.Label("0", mono: true);
        _greenText = LabUi.Label("0", mono: true);
        _blueText  = LabUi.Label("0", mono: true);
        _alphaText = LabUi.Label("255", mono: true);
        _red   = ImagePaneUi.Slider(0, 255, _ => FromSliders());
        _green = ImagePaneUi.Slider(0, 255, _ => FromSliders());
        _blue  = ImagePaneUi.Slider(0, 255, _ => FromSliders());
        _alpha = ImagePaneUi.Slider(0, 255, _ => FromSliders());

        var palette = new WrapPanel { Margin = new Thickness(7, 2, 7, 2) };
        foreach (string hex in Palette)
        {
            var color = Rgba.ParseOr(hex, Rgba.Black);
            palette.Children.Add(ImagePaneUi.Swatch(color, 18, hex + "  (left click: foreground, right click: background)", fg => Apply(fg, hex)));
        }

        _recent = new WrapPanel { Margin = new Thickness(7, 2, 7, 6) };

        var panel = new StackPanel();
        panel.Children.Add(top);
        panel.Children.Add(_editing);
        panel.Children.Add(ImagePaneUi.SliderRow("Red", _red, _redText));
        panel.Children.Add(ImagePaneUi.SliderRow("Green", _green, _greenText));
        panel.Children.Add(ImagePaneUi.SliderRow("Blue", _blue, _blueText));
        panel.Children.Add(ImagePaneUi.SliderRow("Opacity", _alpha, _alphaText, "255 is solid, 0 is clear"));
        panel.Children.Add(LabUi.Heading("Palette"));
        panel.Children.Add(palette);
        panel.Children.Add(LabUi.Heading("Used lately"));
        panel.Children.Add(_recent);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void Apply(bool foreground, string text)
    {
        if (!Rgba.TryParse(text, out var color)) return;

        if (foreground) session.Foreground = color;
        else session.Background = color;
    }

    private Rgba Editing => _background ? session.Background : session.Foreground;

    private void FromSliders()
    {
        if (_loading || _red is null || _green is null || _blue is null || _alpha is null) return;

        var color = new Rgba((byte)_red.Value, (byte)_green.Value, (byte)_blue.Value, (byte)_alpha.Value);
        if (_background) session.Background = color;
        else session.Foreground = color;
    }

    protected override void Refresh()
    {
        if (_swatches is null) return;

        _loading = true;
        try
        {
            Rgba fg = session.Foreground, bg = session.Background;
            _swatches.Children.Clear();
            var back = ImagePaneUi.Swatch(bg, 38, "Background color: click to edit it with the sliders", _ => Choose(true));
            back.HorizontalAlignment = HorizontalAlignment.Right;
            back.VerticalAlignment   = VerticalAlignment.Bottom;
            var front = ImagePaneUi.Swatch(fg, 38, "Foreground color: click to edit it with the sliders", _ => Choose(false));
            front.HorizontalAlignment = HorizontalAlignment.Left;
            front.VerticalAlignment   = VerticalAlignment.Top;
            if (_background) front.BorderThickness = new Thickness(1);
            else back.BorderThickness = new Thickness(1);

            var marked = _background ? back : front;
            marked.BorderThickness = new Thickness(2);
            _swatches.Children.Add(back);
            _swatches.Children.Add(front);
            Put(_fgHex, fg.ToHex());
            Put(_bgHex, bg.ToHex());
            var c = Editing;
            _editing!.Text = "Editing: " + (_background ? "background" : "foreground");
            Set(_red, _redText, c.R);
            Set(_green, _greenText, c.G);
            Set(_blue, _blueText, c.B);
            Set(_alpha, _alphaText, c.A);
            string key = string.Join(',', session.RecentColors.Select(r => r.ToHex()));
            if (key != _recentKey)
            {
                _recentKey = key;
                _recent!.Children.Clear();
                foreach (var color in session.RecentColors)
                {
                    var used = color;
                    _recent.Children.Add(ImagePaneUi.Swatch(used, 18, used.ToHex() + "  (left click: foreground, right click: background)", foreground => Apply(foreground, used.ToHex())));
                }
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void Choose(bool background)
    {
        _background = background;
        Refresh();
    }

    private static void Set(Slider? slider, TextBlock? text, int value)
    {
        if (slider is not null && Math.Abs(slider.Value - value) > 0.5) slider.Value = value;

        if (text is not null) text.Text = value.ToString(CultureInfo.InvariantCulture);
    }

    private static void Put(TextBox? box, string text)
    {
        if (box is not null && !box.IsKeyboardFocusWithin && box.Text != text) box.Text = text;
    }
}

/// <summary>The brush: how big, what tip, how solid and how close the dabs are, with a stroke to see it by.</summary>
internal sealed class ImageBrushInspector(ImageLabSession session) : LiveInspector(session)
{
    private Slider?    _size, _opacity, _spacing;
    private TextBlock? _sizeText, _opacityText, _spacingText;
    private Image?     _preview;
    private WrapPanel? _patterns;
    private bool       _loading;
    private string     _shown = string.Empty;

    public override string Id => "brush";

    public override string Title => "Brush";

    public override string IconKey => "Record";

    protected override UIElement Create()
    {
        _sizeText    = LabUi.Label("12", mono: true);
        _opacityText = LabUi.Label("100", mono: true);
        _spacingText = LabUi.Label("20", mono: true);
        _size    = ImagePaneUi.Slider(1, 500, v => Change(size: v));
        _opacity = ImagePaneUi.Slider(1, 100, v => Change(opacity: v));
        _spacing = ImagePaneUi.Slider(5, 200, v => Change(spacing: v));
        _preview = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
        var previewBox = new Border { Margin = new Thickness(8, 8, 8, 4), Background = ImagePaneUi.Checker(), Child = _preview, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "A stroke with the brush as it is set, in the foreground color" };
        _patterns = new WrapPanel { Margin = new Thickness(7, 2, 7, 6) };

        var panel = new StackPanel();
        panel.Children.Add(previewBox);
        panel.Children.Add(ImagePaneUi.SliderRow("Size", _size, _sizeText, "Across the tip, in pixels. [ and ] change it."));
        panel.Children.Add(ImagePaneUi.SliderRow("Opacity", _opacity, _opacityText, "How much color each dab lays down (percent)"));
        panel.Children.Add(ImagePaneUi.SliderRow("Spacing", _spacing, _spacingText, "The distance between dabs as a percent of the size: small is smooth"));
        panel.Children.Add(LabUi.Heading("Tip"));
        panel.Children.Add(_patterns);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void Change(int? size = null, int? opacity = null, int? spacing = null)
    {
        if (_loading) return;

        session.SetBrush(size, null, opacity, spacing);
    }

    /// <summary>A wavy stroke made with the brush engine itself, so the preview is what painting does.</summary>
    private static BitmapSource Stroke(BrushSettings brush, Rgba color, int width, int height)
    {
        var raster = new Raster(width, height);
        var stroke = new BrushStroke(raster, null, brush, color, false);
        double margin = Math.Min(brush.Size / 2.0 + 4, width / 3.0);
        stroke.Begin(new Pt(margin, height / 2.0));
        double amplitude = Math.Max(0, height / 2.0 - brush.Size / 2.0 - 4) * 0.8;
        for (double x = margin; x <= width - margin; x += 2) stroke.LineTo(new Pt(x, height / 2.0 + Math.Sin((x - margin) / Math.Max(1, width - 2 * margin) * 2 * Math.PI) * amplitude));

        return ImageCodec.ToBitmap(raster);
    }

    protected override void Refresh()
    {
        if (_size is null) return;

        var brush = session.Brush;
        _loading = true;
        try
        {
            Set(_size, _sizeText, brush.Size, " px");
            Set(_opacity, _opacityText, (int)Math.Round(brush.Opacity * 100), "%");
            Set(_spacing, _spacingText, (int)Math.Round(brush.Spacing * 100), "%");
        }
        finally
        {
            _loading = false;
        }

        // the preview is drawn again only when what it shows changed
        string key = $"{brush}|{session.Foreground.ToHex()}";
        if (key == _shown) return;

        _shown = key;
        int height = (int)Math.Clamp(brush.Size + 16, 40, 90);
        _preview!.Source = Stroke(brush with { Size = Math.Min(brush.Size, 80) }, session.Foreground, 240, height);

        _patterns!.Children.Clear();
        foreach (var pattern in Enum.GetValues<BrushPattern>())
        {
            var tip = new BrushSettings(26, pattern, 1, 0.2);
            var image = new Image { Source = Stroke(tip, session.Foreground.A == 0 ? Rgba.Black : session.Foreground.WithAlpha(255), 74, 34), Stretch = Stretch.None };
            var cell = new Border
                       {
                           Width           = 78,
                           Height          = 38,
                           Margin          = new Thickness(2),
                           Background      = ImagePaneUi.Checker(),
                           BorderThickness = new Thickness(pattern == brush.Pattern ? 2 : 1),
                           BorderBrush     = pattern == brush.Pattern ? LabUi.Blue : Brushes.Gray,
                           Child           = image,
                           ToolTip         = pattern.ToString(),
                           Cursor          = Cursors.Hand
                       };
            var chosen = pattern;
            cell.MouseLeftButtonUp += (_, _) => session.SetBrush(pattern: chosen);
            _patterns.Children.Add(cell);
        }
    }

    private static void Set(Slider? slider, TextBlock? text, int value, string unit)
    {
        if (slider is not null && Math.Abs(slider.Value - value) > 0.5) slider.Value = value;

        if (text is not null) text.Text = value.ToString(CultureInfo.InvariantCulture) + unit;
    }
}

/// <summary>The layers, top first: show or hide, pick, rename, and how solid and how blended the active one is.</summary>
internal sealed class ImageLayersInspector(ImageLabSession session) : LiveInspector(session)
{
    private ListBox?   _list;
    private Slider?    _opacity;
    private TextBlock? _opacityText;
    private ComboBox?  _blend;
    private bool       _loading;
    private int        _revision = -1;
    private int        _active   = -1;
    private int        _count    = -1;
    private ImageDocument? _doc;

    public override string Id => "layers";

    public override string Title => "Layers";

    public override string IconKey => "Layers";

    protected override UIElement Create()
    {
        _list = LabUi.List();
        _list.SelectionChanged += (_, _) =>
                                  {
                                      if (_loading || _list.SelectedItem is not ListBoxItem { Tag: ImageLayer layer }) return;

                                      int index = session.Doc.IndexOf(layer.Id);
                                      if (index >= 0) session.Doc.SetActive(index);
                                  };
        _list.MouseDoubleClick += (_, e) =>
                                  {
                                      if (e.OriginalSource is DependencyObject source && FindParent<CheckBox>(source) is not null) return;

                                      Rename();
                                  };

        var doc = session;
        var bar = LabUi.Bar(LabUi.Button("Add", () => doc.Doc.AddLayer(doc.Doc.UniqueLayerName("Layer")), "Add an empty layer above the active one"),
                            LabUi.Button("Copy", () => doc.Doc.DuplicateLayer(doc.Doc.ActiveIndex), "Copy the active layer"),
                            LabUi.Button("Delete", () => { if (doc.Doc.Layers.Count > 1) doc.Doc.DeleteLayer(doc.Doc.ActiveIndex); }, "Remove the active layer"),
                            LabUi.Button("Up", () => doc.Doc.MoveLayer(doc.Doc.ActiveIndex, 1), "Raise the layer one step"),
                            LabUi.Button("Down", () => doc.Doc.MoveLayer(doc.Doc.ActiveIndex, -1), "Lower the layer one step"),
                            LabUi.Button("Merge", () => { if (!doc.Doc.MergeDown(doc.Doc.ActiveIndex)) doc.Say("There is no layer below."); }, "Join the active layer to the one below it"));
        bar.Margin = new Thickness(4, 4, 4, 2);

        _opacityText = LabUi.Label("100%", mono: true);
        _opacity = ImagePaneUi.Slider(0, 100, v =>
                                              {
                                                  if (!_loading) session.Doc.SetOpacity(session.Doc.ActiveIndex, v / 100.0);
                                              });
        _blend = LabUi.Combo(Enum.GetNames<BlendMode>(), nameof(BlendMode.Normal), name =>
                                                                                    {
                                                                                        if (!_loading && Enum.TryParse<BlendMode>(name, out var mode)) session.Doc.SetBlend(session.Doc.ActiveIndex, mode);
                                                                                    }, 120);
        var settings = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        settings.Children.Add(ImagePaneUi.SliderRow("Opacity", _opacity, _opacityText, "How much of the layer shows through"));
        var blendRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 8, 2) };
        blendRow.Children.Add(LabUi.Label("Blend", muted: true).Also(l =>
                                                                      {
                                                                          l.Width = 72;
                                                                          l.VerticalAlignment = VerticalAlignment.Center;
                                                                      }));
        blendRow.Children.Add(_blend);
        settings.Children.Add(blendRow);

        var dock = new DockPanel();
        DockPanel.SetDock(bar, DockSide.Top);
        DockPanel.SetDock(settings, DockSide.Bottom);
        dock.Children.Add(bar);
        dock.Children.Add(settings);
        dock.Children.Add(_list);
        return dock;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T found)
                return found;

        return null;
    }

    private void Rename()
    {
        var doc = session.Doc;
        if (doc.ActiveLayer is not { } layer) return;

        string? name = LabUi.Ask("Rename layer", "New name:", layer.Name);
        if (!string.IsNullOrWhiteSpace(name)) doc.RenameLayer(doc.ActiveIndex, name.Trim());
    }

    private ListBoxItem Row(ImageLayer layer)
    {
        var doc = session.Doc;
        var visible = new CheckBox { IsChecked = layer.Visible, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Show or hide the layer", Margin = new Thickness(0, 0, 6, 0) };
        visible.Click += (_, _) =>
                         {
                             int index = doc.IndexOf(layer.Id);
                             if (index >= 0) doc.SetVisible(index, visible.IsChecked == true);
                         };
        var thumb = new Image { Width = 32, Height = 32, Stretch = Stretch.Uniform, Source = ImageCodec.ToBitmap(ImagePaneUi.Thumbnail(layer.Pixels, 32, 32)) };
        var frame = new Border { Background = ImagePaneUi.Checker(), Child = thumb, Margin = new Thickness(0, 0, 8, 0), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        var name = LabUi.Label(layer.Name);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Opacity = layer.Visible ? 1 : 0.5;
        var detail = LabUi.Label(layer.Opacity < 0.999 || layer.Blend != BlendMode.Normal ? $"{layer.Opacity:P0}{(layer.Blend == BlendMode.Normal ? string.Empty : " " + layer.Blend)}" : string.Empty, muted: true);
        detail.VerticalAlignment = VerticalAlignment.Center;
        detail.Margin = new Thickness(8, 0, 0, 0);
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(2) };
        DockPanel.SetDock(visible, DockSide.Left);
        DockPanel.SetDock(frame, DockSide.Left);
        DockPanel.SetDock(detail, DockSide.Right);
        row.Children.Add(visible);
        row.Children.Add(frame);
        row.Children.Add(detail);
        row.Children.Add(name);
        return new ListBoxItem { Content = row, Tag = layer, HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = layer.Name };
    }

    protected override void Refresh()
    {
        if (_list is null) return;

        var doc = session.Doc;
        _loading = true;
        try
        {
            bool same = ReferenceEquals(doc, _doc) && doc.Revision == _revision && doc.Layers.Count == _count;
            if (!same)
            {
                _doc      = doc;
                _revision = doc.Revision;
                _count    = doc.Layers.Count;
                _list.Items.Clear();
                for (int i = doc.Layers.Count - 1; i >= 0; i--) _list.Items.Add(Row(doc.Layers[i]));
            }

            // the active layer is selected (the list is top first)
            int shown = doc.Layers.Count - 1 - doc.ActiveIndex;
            if (_list.SelectedIndex != shown && shown >= 0 && shown < _list.Items.Count) _list.SelectedIndex = shown;

            _active = doc.ActiveIndex;
            if (doc.ActiveLayer is { } layer)
            {
                int percent = (int)Math.Round(layer.Opacity * 100);
                if (Math.Abs(_opacity!.Value - percent) > 0.5) _opacity.Value = percent;

                _opacityText!.Text = percent.ToString(CultureInfo.InvariantCulture) + "%";
                _blend!.SelectedItem = layer.Blend.ToString();
            }
        }
        finally
        {
            _loading = false;
        }
    }
}

/// <summary>The filters: what each one needs, and a button to run it on the selection (or the whole layer).</summary>
internal sealed class ImageFiltersInspector(ImageLabSession session) : LiveInspector(session)
{
    private readonly Dictionary<string, TextBox> _fields = [];
    private TextBlock? _target;

    public override string Id => "filters";

    public override string Title => "Filters";

    public override string IconKey => "DataCleaning";

    private TextBox Field(string name, string tip)
    {
        var box = LabUi.Field(session.FiltersSet[name].Text, v => session.FiltersSet[name].Value = v.Trim(), 60, tip, true);
        _fields[name] = box;
        return box;
    }

    protected override UIElement Create()
    {
        var panel = new StackPanel();
        _target = LabUi.Label(string.Empty, muted: true, wrap: true);
        _target.Margin = new Thickness(8, 8, 8, 4);
        panel.Children.Add(_target);

        Button Run(string filter, string text, string tip) => LabUi.Button(text, () => session.RunFilter(filter), tip);

        var simple = LabUi.Bar(Run("Grayscale", "Grayscale", "Take the color out"), Run("Invert", "Invert", "Turn every color into its opposite"), Run("Sepia", "Sepia", "Tint like an old photograph"));
        simple.Margin = new Thickness(8, 2, 8, 6);
        panel.Children.Add(simple);

        void Add(string title, string run, string tip, params (string Title, string Name, string Tip)[] fields)
        {
            panel.Children.Add(LabUi.Heading(title));
            var rows = fields.Select(f => (f.Title, (UIElement)Field(f.Name, f.Tip))).ToList();
            var form = LabUi.Form(rows, 120);
            form.Margin = new Thickness(8, 2, 8, 2);
            panel.Children.Add(form);
            var bar = LabUi.Bar(Run(run, run, tip));
            bar.Margin = new Thickness(8, 2, 8, 8);
            panel.Children.Add(bar);
        }

        Add("Brightness and contrast", "Brightness/Contrast", "Change the brightness and the contrast by these amounts",
            ("Brightness", "brightness", "-100 (darker) to 100 (lighter)"), ("Contrast", "contrast", "-100 (flatter) to 100 (harder)"));
        Add("Blur", "Blur", "Soften the picture", ("Radius (pixels)", "blur", "How far the softening reaches"));
        Add("Sharpen", "Sharpen", "Make edges crisper", ("Radius (pixels)", "sharpRadius", "How wide an edge is"), ("Amount", "sharpAmount", "0 to 5"));
        Add("Pixelate", "Pixelate", "Turn the picture into big square blocks", ("Block (pixels)", "pixel", "The side of a block"));
        Add("Threshold", "Threshold", "Make every pixel black or white", ("Level (0 to 255)", "threshold", "Lighter than this becomes white"));
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    protected override void Refresh()
    {
        if (_target is null) return;

        var doc = session.Doc;
        _target.Text = "Filters change " + (doc.Selection is { } selection && selection.Bounds() is { IsEmpty: false } b ? $"the selection ({b.Width} x {b.Height})" : "the whole layer") + $" of {doc.ActiveLayer?.Name ?? "the image"}.";
        foreach (var (name, box) in _fields)
        {
            string text = session.FiltersSet[name].Text;
            if (!box.IsKeyboardFocusWithin && box.Text != text) box.Text = text;
        }
    }
}
