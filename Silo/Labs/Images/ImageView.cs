using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace Silo.Labs.Images;

/// <summary>
///     The picture on a checkerboard that can be zoomed and moved, with the selection's marching ants and the marks of the tool in hand drawn over it.
///     The mouse is turned into picture coordinates and given to the session, which does what the tool does.
/// </summary>
internal sealed class ImageView : Grid
{
    private const double MinZoom = 0.02;
    private const double MaxZoom = 64;

    private readonly ImageLabSession _session;
    private readonly Grid            _viewport = new() { ClipToBounds = true, Background = Brushes.Transparent, Focusable = false };
    private readonly Canvas          _stage    = new() { IsHitTestVisible = false, Background = null };
    private readonly Rectangle       _checker  = new();
    private readonly DrawingBrush    _checkerBrush;
    private readonly Image           _image = new() { Stretch = Stretch.None, SnapsToDevicePixels = false };
    private readonly Canvas          _marks = new() { IsHitTestVisible = false };
    private readonly Ellipse         _ring  = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly TextBlock       _info  = LabUi.Label(string.Empty, muted: true, mono: true);

    private WriteableBitmap? _bitmap;
    private Raster?          _buffer;
    private double           _zoom = 1;
    private Point            _origin;
    private bool             _fitted = true;
    private bool             _panning;
    private bool             _space;
    private Point            _panFrom;
    private Point            _panOrigin;
    private Point            _mouse;
    private bool             _inside;
    private bool             _marksPending;
    private Mask?            _antsMask;
    private bool             _antsPending;
    private Path?            _antsWhite, _antsBlack;
    private Pt               _textAt;
    private TextBox?         _textBox;

    public ImageView(ImageLabSession session)
    {
        _session = session;
        Focusable = true;
        FocusVisualStyle = null;
        SetResourceReference(BackgroundProperty, "Theme.Background");
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _checkerBrush = MakeChecker();
        _checker.Fill = _checkerBrush;
        RenderOptions.SetEdgeMode(_checker, EdgeMode.Aliased);
        _ring.Stroke = Brushes.White;
        _ring.Effect = null;

        _stage.Children.Add(_checker);
        _stage.Children.Add(_image);
        _stage.Children.Add(_marks);
        _stage.Children.Add(_ring);
        _stage.HorizontalAlignment = HorizontalAlignment.Left;
        _stage.VerticalAlignment   = VerticalAlignment.Top;
        _viewport.Children.Add(_stage);
        SetRow(_viewport, 0);
        Children.Add(_viewport);

        var bar = new Border { Padding = new Thickness(8, 3, 8, 3), Child = _info };
        bar.SetResourceReference(Border.BackgroundProperty, "Theme.Surface");
        SetRow(bar, 1);
        Children.Add(bar);

        _viewport.SizeChanged += (_, _) =>
                                 {
                                     if (_fitted) Fit();
                                 };
        _viewport.MouseDown      += OnMouseDown;
        _viewport.MouseMove      += OnMouseMove;
        _viewport.MouseUp        += OnMouseUp;
        _viewport.MouseWheel     += OnWheel;
        _viewport.MouseLeave     += (_, _) =>
                                    {
                                        _inside = false;
                                        _ring.Visibility = Visibility.Collapsed;
                                        UpdateInfo();
                                    };
        _viewport.LostMouseCapture += (_, _) => _panning = false;
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp   += OnKeyUp;

        Loaded   += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        if (IsLoaded) Attach();
    }

    private bool _attached;

    private void Attach()
    {
        if (_attached) return;

        _attached = true;
        _session.Loaded           += OnSessionLoaded;
        _session.Changed          += OnSessionChanged;
        _session.Painted          += OnPainted;
        _session.OverlayChanged   += OnOverlayChanged;
        _session.TextRequested    += OnTextRequested;
        _session.ZoomRequested    += OnZoomRequested;
        Rebuild(true);
    }

    private void Detach()
    {
        if (!_attached) return;

        _attached = false;
        _session.Loaded         -= OnSessionLoaded;
        _session.Changed        -= OnSessionChanged;
        _session.Painted        -= OnPainted;
        _session.OverlayChanged -= OnOverlayChanged;
        _session.TextRequested  -= OnTextRequested;
        _session.ZoomRequested  -= OnZoomRequested;
        CommitText();
    }

    private static DrawingBrush MakeChecker()
    {
        var light = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        var dark  = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        light.Freeze();
        dark.Freeze();
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 2, 2))));
        group.Children.Add(new GeometryDrawing(dark, null, new GeometryGroup { Children = [new RectangleGeometry(new Rect(0, 0, 1, 1)), new RectangleGeometry(new Rect(1, 1, 1, 1))] }));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 16, 16), ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 2, 2) };
        return brush;
    }

    private ImageDocument Doc => _session.Doc;

    // ---- the picture ----------------------------------------------------------------------------------------------------

    private void OnSessionLoaded(object? sender, EventArgs e) => Rebuild(true);

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        UpdateInfo();
        QueueMarks();
    }

    private void OnPainted(IntRect rect)
    {
        if (_bitmap is null || _buffer is null || _buffer.Width != Doc.Width || _buffer.Height != Doc.Height)
        {
            Rebuild(_fitted);
            return;
        }

        Draw(rect);
        QueueAnts();
    }

    private void OnOverlayChanged(object? sender, EventArgs e) => QueueMarks();

    /// <summary>A bitmap the size of the picture, drawn in full.</summary>
    private void Rebuild(bool fit)
    {
        var doc = Doc;
        _buffer = new Raster(doc.Width, doc.Height);
        _bitmap = new WriteableBitmap(doc.Width, doc.Height, 96, 96, PixelFormats.Bgra32, null);
        _image.Source = _bitmap;
        _image.Width  = doc.Width;
        _image.Height = doc.Height;
        _checker.Width  = doc.Width;
        _checker.Height = doc.Height;
        _marks.Width    = doc.Width;
        _marks.Height   = doc.Height;
        _stage.Width    = doc.Width;
        _stage.Height   = doc.Height;
        Draw(IntRect.Empty);
        if (fit)
        {
            _fitted = true;
            Fit();
        }
        else
        {
            ApplyView();
        }

        UpdateInfo();
        QueueAnts();
    }

    private void Draw(IntRect rect)
    {
        if (_bitmap is null || _buffer is null) return;

        var all = new IntRect(0, 0, _buffer.Width, _buffer.Height);
        rect = rect.IsEmpty ? all : rect.Intersect(all);
        if (rect.IsEmpty) return;

        Doc.CompositeInto(_buffer, rect);
        _bitmap.WritePixels(new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height), _buffer.Pixels, _buffer.Stride, rect.Y * _buffer.Stride + rect.X * 4);
    }

    // ---- zoom and position -------------------------------------------------------------------------------------------------------

    private Pt ToPicture(Point screen) => new((screen.X - _origin.X) / _zoom, (screen.Y - _origin.Y) / _zoom);

    private Point ToScreen(Pt p) => new(p.X * _zoom + _origin.X, p.Y * _zoom + _origin.Y);

    private void ApplyView()
    {
        _stage.RenderTransform = new MatrixTransform(_zoom, 0, 0, _zoom, _origin.X, _origin.Y);
        _checkerBrush.Viewport = new Rect(0, 0, 16 / _zoom, 16 / _zoom);
        RenderOptions.SetBitmapScalingMode(_image, _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        _session.ViewScale = _zoom;
        QueueMarks();
        UpdateInfo();
    }

    private void Fit()
    {
        double w = _viewport.ActualWidth, h = _viewport.ActualHeight;
        if (w < 1 || h < 1) return;

        _zoom = Math.Clamp(Math.Min((w - 24) / Doc.Width, (h - 24) / Doc.Height), MinZoom, 32);
        if (_zoom > 1) _zoom = Math.Floor(_zoom);

        _origin = new Point((w - Doc.Width * _zoom) / 2, (h - Doc.Height * _zoom) / 2);
        ApplyView();
    }

    private void ZoomAbout(Point screen, double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var p = ToPicture(screen);
        _zoom   = zoom;
        _origin = new Point(screen.X - p.X * zoom, screen.Y - p.Y * zoom);
        _fitted = false;
        ApplyView();
    }

    private void OnZoomRequested(string how)
    {
        var middle = new Point(_viewport.ActualWidth / 2, _viewport.ActualHeight / 2);
        switch (how)
        {
            case "in": ZoomAbout(middle, _zoom * 1.25); break;
            case "out": ZoomAbout(middle, _zoom / 1.25); break;
            case "100": ZoomAbout(middle, 1); break;
            default:
                _fitted = true;
                Fit();
                break;
        }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var at = e.GetPosition(_viewport);
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            _origin = new Point(_origin.X + e.Delta, _origin.Y);
            _fitted = false;
            ApplyView();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            _origin = new Point(_origin.X, _origin.Y + e.Delta);
            _fitted = false;
            ApplyView();
        }
        else
        {
            ZoomAbout(at, _zoom * Math.Pow(1.15, e.Delta / 120.0));
        }

        e.Handled = true;
    }

    // ---- mouse and keys ------------------------------------------------------------------------------------------------------------

    private static Mods ModsNow() => new((Keyboard.Modifiers & ModifierKeys.Shift) != 0, (Keyboard.Modifiers & ModifierKeys.Control) != 0, (Keyboard.Modifiers & ModifierKeys.Alt) != 0);

    private bool HandActive => _space || _session.ActiveTool?.Id == "hand";

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        var screen = e.GetPosition(_viewport);
        _mouse = screen;
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && HandActive))
        {
            _panning   = true;
            _panFrom   = screen;
            _panOrigin = _origin;
            _viewport.CaptureMouse();
            _viewport.Cursor = Cursors.ScrollAll;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;

        _viewport.CaptureMouse();
        _session.PointerDown(ToPicture(screen), e.ChangedButton == MouseButton.Left ? 0 : 1, ModsNow(), e.ClickCount);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var screen = e.GetPosition(_viewport);
        _mouse  = screen;
        _inside = true;
        if (_panning)
        {
            _origin = new Point(_panOrigin.X + screen.X - _panFrom.X, _panOrigin.Y + screen.Y - _panFrom.Y);
            _fitted = false;
            ApplyView();
            return;
        }

        var p = ToPicture(screen);
        _session.PointerMove(p, ModsNow());

        UpdateCursor(p);
        UpdateInfo();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            _viewport.ReleaseMouseCapture();
            UpdateCursor(ToPicture(e.GetPosition(_viewport)));
            return;
        }

        if (!_viewport.IsMouseCaptured) return;

        _viewport.ReleaseMouseCapture();
        _session.PointerUp(ToPicture(e.GetPosition(_viewport)), ModsNow());
        e.Handled = true;
    }

    private void UpdateCursor(Pt p)
    {
        if (HandActive)
        {
            _viewport.Cursor = Cursors.Hand;
            _ring.Visibility = Visibility.Collapsed;
            return;
        }

        int ring = _session.RingSize;
        var kind = _session.CursorAt(p, ModsNow());
        double radius = ring * _zoom / 2;
        if (ring > 0 && radius >= 3 && kind == PointerKind.Brush)
        {
            _viewport.Cursor = Cursors.None;
            _ring.Visibility = Visibility.Visible;
            double d = ring;
            _ring.Width  = d;
            _ring.Height = d;
            _ring.StrokeThickness = 1 / _zoom;
            _ring.Stroke = Brushes.White;
            Canvas.SetLeft(_ring, p.X - d / 2);
            Canvas.SetTop(_ring, p.Y - d / 2);
            return;
        }

        _ring.Visibility = Visibility.Collapsed;
        _viewport.Cursor = kind switch
                           {
                               PointerKind.Cross    => Cursors.Cross,
                               PointerKind.Brush    => Cursors.Cross,
                               PointerKind.Move     => Cursors.SizeAll,
                               PointerKind.Hand     => Cursors.Hand,
                               PointerKind.Text     => Cursors.IBeam,
                               PointerKind.Rotate   => Cursors.Cross,
                               PointerKind.SizeNwse => Cursors.SizeNWSE,
                               PointerKind.SizeNesw => Cursors.SizeNESW,
                               PointerKind.SizeWe   => Cursors.SizeWE,
                               PointerKind.SizeNs   => Cursors.SizeNS,
                               _                    => Cursors.Arrow
                           };
    }

    private static string? KeyName(Key key) => key switch
                                              {
                                                  Key.Enter                                                => "Enter",
                                                  Key.Escape                                               => "Escape",
                                                  Key.Back                                                 => "Back",
                                                  Key.Delete                                               => "Delete",
                                                  Key.OemOpenBrackets                                      => "[",
                                                  Key.OemCloseBrackets                                     => "]",
                                                  >= Key.A and <= Key.Z                                    => ((char)('A' + (key - Key.A))).ToString(),
                                                  _                                                        => null
                                              };

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_textBox is not null || e.OriginalSource is TextBox) return;

        if (e.Key == Key.Space)
        {
            if (!_space)
            {
                _space = true;
                UpdateCursor(ToPicture(_mouse));
            }

            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;

        if (KeyName(e.Key) is { } name && _session.HandleKey(name)) e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _space)
        {
            _space = false;
            UpdateCursor(ToPicture(_mouse));
            e.Handled = true;
        }
        else if (e.Key is Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl)
        {
            UpdateCursor(ToPicture(_mouse));
        }
    }

    // ---- information ---------------------------------------------------------------------------------------------------------------------

    private void UpdateInfo()
    {
        var doc = Doc;
        var sb  = new System.Text.StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{_zoom * 100:0.#}%   {doc.Width} x {doc.Height}");
        if (_inside)
        {
            var p = ToPicture(_mouse);
            sb.Append(CultureInfo.InvariantCulture, $"   x {Math.Floor(p.X)}, y {Math.Floor(p.Y)}");
        }

        if (doc.Selection is { } selection && selection.Bounds() is { IsEmpty: false } bounds)
            sb.Append(CultureInfo.InvariantCulture, $"   selection {bounds.Width} x {bounds.Height} at {bounds.X}, {bounds.Y}");

        sb.Append(CultureInfo.InvariantCulture, $"   layer {doc.ActiveLayer?.Name}");
        if (_session.ActiveTool is { } tool) sb.Append("   ").Append(tool.Name.Replace("_", string.Empty));

        _info.Text = sb.ToString();
    }

    // ---- marching ants and the marks of the tool -----------------------------------------------------------------------------------------

    private void QueueMarks()
    {
        if (_marksPending) return;

        _marksPending = true;
        Dispatcher.InvokeAsync(() =>
                               {
                                   _marksPending = false;
                                   DrawMarks();
                               }, DispatcherPriority.Render);
    }

    private void QueueAnts()
    {
        if (_antsPending) return;

        _antsPending = true;
        Dispatcher.InvokeAsync(() =>
                               {
                                   _antsPending = false;
                                   if (!ReferenceEquals(_antsMask, Doc.SelectionShown)) DrawMarks();
                               }, DispatcherPriority.Background);
    }

    private static readonly Brush ToolBlue = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xD6)));

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private double Thin => 1 / _zoom;

    /// <summary>A line that shows on any background: white with black dashes over it.</summary>
    private void Pair(Func<Shape> make, bool dashed = true)
    {
        var white = make();
        white.Stroke          = Brushes.White;
        white.StrokeThickness = Thin;
        _marks.Children.Add(white);
        var black = make();
        black.Stroke          = Brushes.Black;
        black.StrokeThickness = Thin;
        if (dashed) black.StrokeDashArray = [4, 4];

        _marks.Children.Add(black);
    }

    private void DrawMarks()
    {
        _marks.Children.Clear();
        _antsWhite = null;
        _antsBlack = null;
        var mask = Doc.SelectionShown;
        _antsMask = mask;
        if (mask is not null) DrawAnts(mask);

        var o = _session.BuildOverlay(ModsNow());
        if (o.Rubber is { } rubber)
        {
            double x = Math.Min(rubber.A.X, rubber.B.X), y = Math.Min(rubber.A.Y, rubber.B.Y), w = Math.Abs(rubber.B.X - rubber.A.X), h = Math.Abs(rubber.B.Y - rubber.A.Y);
            Pair(() => o.RubberIsEllipse ? (Shape)new Ellipse { Width = w, Height = h }.Placed(x, y) : new Rectangle { Width = w, Height = h }.Placed(x, y));
        }

        if (o.Lasso is { Count: > 0 } lasso)
        {
            var points = lasso.Select(p => new Point(p.X, p.Y)).ToList();
            if (o.LassoTo is { } to) points.Add(new Point(to.X, to.Y));

            Pair(() => new Polyline { Points = new PointCollection(points) });
            _marks.Children.Add(Square(lasso[0], 6, Brushes.White, Brushes.Black));
        }

        if (o.Line is { } line)
        {
            Pair(() => new Line { X1 = line.A.X, Y1 = line.A.Y, X2 = line.B.X, Y2 = line.B.Y }, false);
            _marks.Children.Add(Square(line.A, 7, Brushes.White, Brushes.Black));
            _marks.Children.Add(Square(line.B, 7, Brushes.Black, Brushes.White));
        }

        if (o.Curve is { Count: > 0 } curve)
            _marks.Children.Add(new Polyline { Points = new PointCollection(curve.Select(p => new Point(p.X, p.Y))), Stroke = ToolBlue, StrokeThickness = Thin * 1.5 });

        if (o.Anchors is { } anchors)
            for (int i = 0; i < anchors.Count; i++)
            {
                var a = anchors[i];
                if (a.HasHandles)
                {
                    foreach (var handle in new[] { a.In, a.Out })
                    {
                        _marks.Children.Add(new Line { X1 = a.At.X, Y1 = a.At.Y, X2 = handle.X, Y2 = handle.Y, Stroke = ToolBlue, StrokeThickness = Thin });
                        _marks.Children.Add(new Ellipse { Width = 6 * Thin, Height = 6 * Thin, Fill = ToolBlue }.Placed(handle.X - 3 * Thin, handle.Y - 3 * Thin));
                    }
                }

                _marks.Children.Add(Square(a.At, 7, i == 0 ? Brushes.White : ToolBlue, Brushes.Black));
            }

        if (o.Box is { Count: 4 } box)
        {
            var points = box.Select(p => new Point(p.X, p.Y)).ToList();
            points.Add(points[0]);
            Pair(() => new Polyline { Points = new PointCollection(points) });
            if (o.Handles is { } handles)
                foreach (var h in handles)
                    _marks.Children.Add(Square(h, 8, Brushes.White, Brushes.Black));

            if (o.Pivot is { } pivot)
            {
                _marks.Children.Add(new Line { X1 = pivot.X - 5 * Thin, Y1 = pivot.Y, X2 = pivot.X + 5 * Thin, Y2 = pivot.Y, Stroke = Brushes.Black, StrokeThickness = Thin });
                _marks.Children.Add(new Line { X1 = pivot.X, Y1 = pivot.Y - 5 * Thin, X2 = pivot.X, Y2 = pivot.Y + 5 * Thin, Stroke = Brushes.Black, StrokeThickness = Thin });
            }
        }
    }

    /// <summary>A small square, always the same size on the screen.</summary>
    private Rectangle Square(Pt at, double size, Brush fill, Brush stroke)
    {
        double side = size * Thin;
        return new Rectangle { Width = side, Height = side, Fill = fill, Stroke = stroke, StrokeThickness = Thin }.Placed(at.X - side / 2, at.Y - side / 2);
    }

    private Mask?     _antsFor;
    private Geometry? _antsGeometry;

    private void DrawAnts(Mask mask)
    {
        if (!ReferenceEquals(mask, _antsFor))
        {
            var segments = mask.Outline();
            var built    = new StreamGeometry();
            using (var context = built.Open())
            {
                foreach (var (x1, y1, x2, y2) in segments)
                {
                    context.BeginFigure(new Point(x1, y1), false, false);
                    context.LineTo(new Point(x2, y2), true, false);
                }
            }

            built.Freeze();
            _antsFor      = mask;
            _antsGeometry = segments.Count == 0 ? null : built;
        }

        if (_antsGeometry is not { } geometry) return;

        _antsWhite = new Path { Data = geometry, Stroke = Brushes.White, StrokeThickness = Thin, SnapsToDevicePixels = false };
        _antsBlack = new Path { Data = geometry, Stroke = Brushes.Black, StrokeThickness = Thin, StrokeDashArray = [4, 4] };
        _marks.Children.Add(_antsWhite);
        _marks.Children.Add(_antsBlack);
        _antsBlack.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, 8, TimeSpan.FromMilliseconds(700)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    // ---- text -------------------------------------------------------------------------------------------------------------------------------

    private void OnTextRequested(Pt at)
    {
        CommitText();
        _textAt = at;
        var look = _session.TextLook;
        var color = _session.Foreground;
        var brush = new SolidColorBrush(Color.FromArgb(255, color.R, color.G, color.B));
        var box = new TextBox
                  {
                      AcceptsReturn   = true,
                      AcceptsTab      = false,
                      FontFamily      = new FontFamily(look.Font),
                      FontSize        = Math.Max(6, look.Size * _zoom),
                      FontWeight      = look.Bold ? FontWeights.Bold : FontWeights.Normal,
                      FontStyle       = look.Italic ? FontStyles.Italic : FontStyles.Normal,
                      Foreground      = brush,
                      CaretBrush      = brush,
                      Background      = Brushes.Transparent,
                      BorderBrush     = ToolBlue,
                      BorderThickness = new Thickness(1),
                      Padding         = new Thickness(0),
                      MinWidth        = 60,
                      TextWrapping    = TextWrapping.NoWrap,
                      HorizontalAlignment = HorizontalAlignment.Left,
                      VerticalAlignment   = VerticalAlignment.Top,
                      ToolTip         = "Ctrl+Enter places the text; Esc gives up",
                      TextAlignment   = look.Align
                  };
        var screen = ToScreen(at);
        box.Margin = new Thickness(screen.X - 1, screen.Y - 1, 0, 0);
        box.PreviewKeyDown += (_, e) =>
                              {
                                  if (e.Key == Key.Escape)
                                  {
                                      CancelText();
                                      e.Handled = true;
                                  }
                                  else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                                  {
                                      CommitText();
                                      e.Handled = true;
                                  }
                              };
        box.LostKeyboardFocus += (_, _) => Dispatcher.InvokeAsync(() =>
                                                                  {
                                                                      if (ReferenceEquals(_textBox, box)) CommitText();
                                                                  }, DispatcherPriority.Background);
        _textBox = box;
        _viewport.Children.Add(box);
        box.Focus();
    }

    private void CancelText()
    {
        if (_textBox is null) return;

        _viewport.Children.Remove(_textBox);
        _textBox = null;
        Focus();
    }

    private void CommitText()
    {
        if (_textBox is not { } box) return;

        _textBox = null;
        string text = box.Text;
        _viewport.Children.Remove(box);
        if (text.Trim().Length > 0) _session.PlaceText(_textAt, text.TrimEnd('\r', '\n'));
    }
}

internal static class ShapeExtensions
{
    /// <summary>Puts a shape on a canvas at a place.</summary>
    public static T Placed<T>(this T shape, double x, double y) where T : System.Windows.Shapes.Shape
    {
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        return shape;
    }
}
