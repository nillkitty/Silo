using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Silo.Labs.Soundboard;

internal static class BoardColors
{
    public static readonly string[] Palette =
    [
        "#E5484D", "#D9731E", "#D9A41E", "#3FA95B", "#2DB8C4", "#3B82D6", "#7A5CD6", "#B05DD6", "#D65D9E", "#555B66", "#20242E", "#FFFFFF"
    ];

    public static Color Parse(string? text, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && ColorConverter.ConvertFromString(text.Trim()) is Color c) return c;
        }
        catch (FormatException)
        {
            // not a color: the fallback is used
        }

        return fallback;
    }

    public static bool IsColor(string text)
    {
        try
        {
            return ColorConverter.ConvertFromString(text.Trim()) is Color;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static Color Mix(Color a, Color b, double t)
    {
        return Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    public static Color Lighten(Color c, double t) => Mix(c, Colors.White, t);

    public static Color Darken(Color c, double t) => Mix(c, Colors.Black, t);

    public static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}

/// <summary>One button of the board, drawn by what the button says: its shape, style, colors and label, and whether it is playing, pressed or selected.</summary>
internal sealed class ButtonVisual : FrameworkElement
{
    private const double Grip = 14;

    private Geometry? _outline;

    public ButtonVisual(SoundButton model)
    {
        Model = model;
    }

    public SoundButton Model { get; set; }

    public bool IsActive { get; set; }

    public bool IsPressed { get; set; }

    public bool IsSelected { get; set; }

    public bool EditMode { get; set; }

    /// <summary>Whether a point (in the button's own space) is on the grip that resizes it.</summary>
    public bool OnGrip(Point p) => p.X >= ActualWidth - Grip && p.Y >= ActualHeight - Grip;

    /// <summary>Takes up the model's size, position, tilt and glow.</summary>
    public void Refresh()
    {
        var m = Model;
        Width   = m.Width;
        Height  = m.Height;
        Opacity = m.Opacity;
        Canvas.SetLeft(this, m.X);
        Canvas.SetTop(this, m.Y);
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform       = Math.Abs(m.Tilt) < 0.01 ? Transform.Identity : new RotateTransform(m.Tilt);

        var fill = BoardColors.Parse(m.Fill, Colors.SteelBlue);
        Effect = IsActive || m.Style == ButtonStyle.Neon
                     ? new DropShadowEffect { Color = IsActive ? BoardColors.Lighten(fill, 0.3) : fill, BlurRadius = IsActive ? 26 : 16, ShadowDepth = 0, Opacity = IsActive ? 1 : 0.8 }
                     : null;
        ToolTip = $"{m.Label}\n{m.Describe()}" + (m.Hotkey.Length > 0 ? $"\nKey: {m.Hotkey}" : string.Empty);
        InvalidateVisual();
    }

    private Geometry Outline(Rect r)
    {
        double radius = Math.Min(Model.CornerRadius, Math.Min(r.Width, r.Height) / 2);
        switch (Model.Shape)
        {
            case ButtonShape.Rectangle:
                return new RectangleGeometry(r);
            case ButtonShape.Pill:
                return new RectangleGeometry(r, r.Height / 2, r.Height / 2);
            case ButtonShape.Ellipse:
                return new EllipseGeometry(r);
            case ButtonShape.Diamond:
                return Polygon(new Point(r.Left + r.Width / 2, r.Top), new Point(r.Right, r.Top + r.Height / 2), new Point(r.Left + r.Width / 2, r.Bottom), new Point(r.Left, r.Top + r.Height / 2));
            case ButtonShape.Hexagon:
                return Polygon(new Point(r.Left + r.Width * 0.25, r.Top), new Point(r.Left + r.Width * 0.75, r.Top), new Point(r.Right, r.Top + r.Height / 2), new Point(r.Left + r.Width * 0.75, r.Bottom),
                               new Point(r.Left + r.Width * 0.25, r.Bottom), new Point(r.Left, r.Top + r.Height / 2));
            default:
                return new RectangleGeometry(r, radius, radius);
        }
    }

    private static Geometry Polygon(params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], true, true);
            ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        bool inside = _outline is { } g ? g.FillContains(hitTestParameters.HitPoint) || (EditMode && IsSelected && bounds.Contains(hitTestParameters.HitPoint) && OnGrip(hitTestParameters.HitPoint)) : bounds.Contains(hitTestParameters.HitPoint);
        return inside ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var m      = Model;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var baseColor = BoardColors.Parse(m.Fill, Colors.SteelBlue);
        var textColor = BoardColors.Parse(m.TextColor, Colors.White);
        if (IsActive) baseColor = BoardColors.Lighten(baseColor, 0.28);

        double inset = Math.Max(1, m.BorderWidth / 2) + (IsPressed ? 3 : 0);
        var    rect  = new Rect(inset, inset, Math.Max(1, ActualWidth - inset * 2), Math.Max(1, ActualHeight - inset * 2));
        _outline = Outline(rect);

        Brush fill;
        Pen?  pen = null;
        switch (m.Style)
        {
            case ButtonStyle.Flat:
                fill = BoardColors.Frozen(baseColor);
                break;
            case ButtonStyle.Outline:
                fill = BoardColors.Frozen(Color.FromArgb(IsActive ? (byte)0x90 : (byte)0x22, baseColor.R, baseColor.G, baseColor.B));
                pen  = new Pen(BoardColors.Frozen(baseColor), Math.Max(2, m.BorderWidth));
                break;
            case ButtonStyle.Neon:
                fill = BoardColors.Frozen(BoardColors.Mix(BoardColors.Darken(baseColor, 0.85), baseColor, IsActive ? 0.45 : 0));
                pen  = new Pen(BoardColors.Frozen(baseColor), Math.Max(2.5, m.BorderWidth));
                break;
            default:
                fill = new LinearGradientBrush(BoardColors.Lighten(baseColor, 0.22), BoardColors.Darken(baseColor, 0.22), 90);
                break;
        }

        if (pen is null && m.BorderWidth > 0 && m.Style != ButtonStyle.Outline && m.Style != ButtonStyle.Neon) pen = new Pen(BoardColors.Frozen(BoardColors.Parse(m.BorderColor, Colors.White)), m.BorderWidth);

        dc.DrawGeometry(fill, pen, _outline);

        if (m.Style == ButtonStyle.Glossy)
        {
            dc.PushClip(_outline);
            var shine = new LinearGradientBrush(Color.FromArgb(0x80, 255, 255, 255), Color.FromArgb(0x08, 255, 255, 255), 90);
            dc.DrawRectangle(shine, null, new Rect(rect.Left, rect.Top, rect.Width, rect.Height * 0.48));
            dc.Pop();
        }

        DrawText(dc, rect, textColor, dip);

        if (IsActive) dc.DrawEllipse(BoardColors.Frozen(Color.FromRgb(0x9B, 0xF0, 0x6A)), new Pen(Brushes.Black, 1), new Point(rect.Left + 11, rect.Top + 11), 4.5, 4.5);

        if (EditMode && IsSelected)
        {
            var accent = BoardColors.Frozen(Color.FromRgb(0xFF, 0xD0, 0x4A));
            dc.DrawRectangle(null, new Pen(accent, 1.5) { DashStyle = DashStyles.Dash }, new Rect(0.75, 0.75, Math.Max(1, ActualWidth - 1.5), Math.Max(1, ActualHeight - 1.5)));
            var tri = Polygon(new Point(ActualWidth - Grip, ActualHeight), new Point(ActualWidth, ActualHeight - Grip), new Point(ActualWidth, ActualHeight));
            dc.DrawGeometry(accent, null, tri);
        }
    }

    private void DrawText(DrawingContext dc, Rect rect, Color textColor, double dip)
    {
        var m     = Model;
        var brush = BoardColors.Frozen(textColor);
        double maxWidth = Math.Max(10, rect.Width - 12);
        FormattedText? icon = m.Icon.Length > 0
                                  ? new FormattedText(m.Icon, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Emoji, Segoe UI Symbol"), Math.Max(10, m.FontSize * 1.8), brush, dip)
                                    { MaxTextWidth = maxWidth, MaxLineCount = 1, TextAlignment = TextAlignment.Center }
                                  : null;
        FormattedText? label = m.Label.Length > 0
                                   ? new FormattedText(m.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                                                       Math.Max(6, m.FontSize), brush, dip)
                                     { MaxTextWidth = maxWidth, MaxTextHeight = Math.Max(10, rect.Height - (icon?.Height ?? 0) - 6), TextAlignment = TextAlignment.Center, Trimming = TextTrimming.CharacterEllipsis }
                                   : null;

        double total = (icon?.Height ?? 0) + (label?.Height ?? 0);
        double y     = rect.Top + Math.Max(2, (rect.Height - total) / 2);
        double x     = rect.Left + 6;
        if (icon is not null)
        {
            dc.DrawText(icon, new Point(x, y));
            y += icon.Height;
        }

        if (label is not null) dc.DrawText(label, new Point(x, y));

        if (m.Hotkey.Length > 0)
        {
            var key = new FormattedText(m.Hotkey, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Cascadia Mono, Consolas"), 9, brush, dip);
            dc.PushOpacity(0.7);
            dc.DrawText(key, new Point(rect.Right - key.Width - 6, rect.Bottom - key.Height - 3));
            dc.Pop();
        }
    }
}

/// <summary>The dots of the grid, shown while editing.</summary>
internal sealed class GridOverlay : FrameworkElement
{
    public int Step { get; set; }

    public bool Shown { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Shown || Step < 8) return;

        var brush = BoardColors.Frozen(Color.FromArgb(0x55, 255, 255, 255));
        if ((ActualWidth / Step) * (ActualHeight / Step) > 30_000) return;

        for (double y = Step; y < ActualHeight; y += Step)
        for (double x = Step; x < ActualWidth; x += Step)
            dc.DrawRectangle(brush, null, new Rect(x - 0.75, y - 0.75, 1.5, 1.5));
    }
}

/// <summary>
///     The main view of the Soundboard Lab: a toolbar (edit or perform, master volume, mute, stop), and the board itself on a canvas, with each button where the person put it. Editing drags and resizes buttons;
///     performing presses them, with the mouse or their keys.
/// </summary>
internal sealed class SoundboardView : Grid
{
    private readonly SoundboardLabSession               _session;
    private readonly Canvas                             _canvas = new() { ClipToBounds = true };
    private readonly Image                              _picture = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
    private readonly GridOverlay                        _dots = new() { IsHitTestVisible = false };
    private readonly Dictionary<string, ButtonVisual>   _visuals = new();
    private readonly Dictionary<Key, SoundButton>       _keysDown = new();
    private readonly ScaleTransform                     _zoom = new(1, 1);
    private readonly Button                             _mode;
    private readonly Button                             _mute;
    private readonly Slider                             _volume;
    private readonly TextBlock                          _volumeText;
    private readonly CheckBox                           _snap;
    private          string?                            _pictureFile;
    private          bool                               _loading;

    // dragging
    private ButtonVisual? _dragging;
    private Point         _dragStart;
    private Rect          _dragRect;
    private bool          _resizing;
    private bool          _moved;
    private ButtonVisual? _pressed;

    public SoundboardView(SoundboardLabSession session)
    {
        _session  = session;
        Focusable = true;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- toolbar --------------------------------------------------------------------------------
        _mode = LabUi.Button("Perform", () => _session.SetEditMode(!_session.EditMode), "Switch between editing the board and performing it (F4)", icon: "Play");
        _mute = LabUi.Button("Mute All", _session.ToggleMute, "Silence everything until this is pressed again", icon: "Mute");
        var stop = LabUi.Button("Stop All", () => _session.Run(BoardCommand.StopAll), "Stop every sound", icon: "Stop");
        var add  = LabUi.Button("Add Button", () => _session.AddButton(), "Put a new button on the board (Ctrl+B), or double-click an empty place", icon: "TbNew");
        _volume = new Slider { Minimum = 0, Maximum = 100, Width = 110, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Master volume", IsMoveToPointEnabled = true };
        _volume.ValueChanged += (_, e) =>
                                {
                                    if (!_loading) _session.SetMaster((int)Math.Round(e.NewValue));
                                };
        _volumeText = LabUi.Label("100%", muted: true);
        _volumeText.Width = 38;
        _snap = LabUi.Check("Snap", true, on =>
                                          {
                                              if (!_loading) _session.EditBoard(b => b.Snap = on);
                                          }, "Buttons snap to the grid as they are moved");
        var zoom = new Slider { Minimum = 25, Maximum = 200, Value = 100, Width = 90, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Zoom (Ctrl+wheel)" };
        zoom.ValueChanged += (_, e) => SetZoom(e.NewValue / 100);
        var bar = LabUi.Bar(_mode, add, stop, _mute, LabUi.Label("Volume", muted: true).Also(l => l.Margin = new Thickness(10, 0, 4, 0)), _volume, _volumeText, _snap, LabUi.Label("Zoom", muted: true).Also(l => l.Margin = new Thickness(10, 0, 4, 0)), zoom);
        SetRow(bar, 0);
        Children.Add(bar);

        // ---- the board ------------------------------------------------------------------------------
        _canvas.Children.Add(_picture);
        _canvas.Children.Add(_dots);
        _canvas.MouseLeftButtonDown += OnBoardDown;
        var stage = new Grid { LayoutTransform = _zoom, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12) };
        stage.Children.Add(new Border { Child = _canvas, BorderThickness = new Thickness(1), BorderBrush = BoardColors.Frozen(Color.FromRgb(0x55, 0x5B, 0x66)) });
        var scroll = new ScrollViewer { Content = stage, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        scroll.PreviewMouseWheel += (_, e) =>
                                    {
                                        if (Keyboard.Modifiers != ModifierKeys.Control) return;

                                        zoom.Value = Math.Clamp(zoom.Value + (e.Delta > 0 ? 10 : -10), zoom.Minimum, zoom.Maximum);
                                        e.Handled  = true;
                                    };
        SetRow(scroll, 1);
        Children.Add(scroll);
        this.SetResourceReference(BackgroundProperty, "Theme.Background");

        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp   += OnKeyUp;
        LostKeyboardFocus += (_, _) => ReleaseKeys();
        _session.Changed += (_, _) => Sync();
        _session.Loaded  += (_, _) => Sync();
        Sync();
    }

    private void SetZoom(double scale)
    {
        _zoom.ScaleX = scale;
        _zoom.ScaleY = scale;
    }

    // ---- drawing the board -----------------------------------------------------------------------

    private void Sync()
    {
        var board = _session.Board;
        _loading = true;
        try
        {
            _canvas.Width      = board.Width;
            _canvas.Height     = board.Height;
            _canvas.Background = new LinearGradientBrush(BoardColors.Parse(board.Background, Color.FromRgb(0x1B, 0x1F, 0x2A)), BoardColors.Parse(board.Background2, Color.FromRgb(0x0E, 0x11, 0x18)), 90);
            _picture.Width     = board.Width;
            _picture.Height    = board.Height;
            _picture.Opacity   = board.ImageOpacity / 100.0;
            LoadPicture(board.BackgroundImage);
            _dots.Width  = board.Width;
            _dots.Height = board.Height;
            _dots.Step   = board.GridSize;
            _dots.Shown  = _session.EditMode;
            _dots.InvalidateVisual();

            SetText(_mode, _session.EditMode ? "Perform" : "Edit");
            SetText(_mute, _session.Engine.Muted ? "Unmute" : "Mute All");
            _volume.Value = board.MasterVolume;
            _volumeText.Text = board.MasterVolume + "%";
            _snap.IsChecked = board.Snap;
            _snap.IsEnabled = _session.EditMode;
        }
        finally
        {
            _loading = false;
        }

        var ids    = board.Buttons.Select(b => b.Id).ToHashSet();
        var active = _session.Engine.Active.ToHashSet();
        foreach (string gone in _visuals.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _canvas.Children.Remove(_visuals[gone]);
            _visuals.Remove(gone);
        }

        for (int i = 0; i < board.Buttons.Count; i++)
        {
            var b = board.Buttons[i];
            if (!_visuals.TryGetValue(b.Id, out var v))
            {
                v = new ButtonVisual(b);
                Wire(v);
                _visuals[b.Id] = v;
                _canvas.Children.Add(v);
            }

            v.Model      = b;
            v.EditMode   = _session.EditMode;
            v.IsSelected = _session.Selected?.Id == b.Id;
            v.IsActive   = active.Contains(b.Id);
            v.Cursor     = _session.EditMode ? Cursors.SizeAll : Cursors.Hand;
            if (v != _dragging) v.Refresh();
            Panel.SetZIndex(v, i + 2);
        }
    }

    /// <summary>Changes the words on a Lab button (the text after its icon).</summary>
    private static void SetText(Button button, string text)
    {
        if (button.Content is StackPanel { Children: [.., TextBlock label] }) label.Text = text;
    }

    private void LoadPicture(string path)
    {
        if (path == _pictureFile) return;

        _pictureFile = path;
        _picture.Source = null;
        if (path.Length == 0 || !File.Exists(path)) return;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource   = new Uri(path);
            image.EndInit();
            image.Freeze();
            _picture.Source = image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UriFormatException)
        {
            _session.Say("The background picture could not be read: " + ex.Message);
        }
    }

    // ---- mouse -----------------------------------------------------------------------------------

    private void Wire(ButtonVisual v)
    {
        v.MouseLeftButtonDown += OnButtonDown;
        v.MouseMove           += OnButtonMove;
        v.MouseLeftButtonUp   += OnButtonUp;
        v.MouseRightButtonDown += OnButtonRight;
    }

    private void OnBoardDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.Focus(this);
        if (e.OriginalSource is ButtonVisual) return;

        if (!_session.EditMode) return;

        _session.Select(null);
        if (e.ClickCount == 2)
        {
            var p = e.GetPosition(_canvas);
            var b = new SoundButton { X = _session.Snap(p.X - 60), Y = _session.Snap(p.Y - 45) };
            b.X = Math.Max(b.X, 1);
            b.Y = Math.Max(b.Y, 1);
            _session.AddButton(b);
        }
    }

    private void OnButtonDown(object sender, MouseButtonEventArgs e)
    {
        var v = (ButtonVisual)sender;
        Keyboard.Focus(this);
        if (!_session.EditMode)
        {
            _pressed       = v;
            v.IsPressed    = true;
            v.InvalidateVisual();
            v.CaptureMouse();
            _session.Press(v.Model);
            e.Handled = true;
            return;
        }

        _session.Select(v.Model.Id);
        _dragging  = v;
        _dragStart = e.GetPosition(_canvas);
        _dragRect  = new Rect(v.Model.X, v.Model.Y, v.Model.Width, v.Model.Height);
        _resizing  = v.OnGrip(e.GetPosition(v));
        _moved     = false;
        v.CaptureMouse();
        e.Handled = true;
    }

    private void OnButtonMove(object sender, MouseEventArgs e)
    {
        var v = (ButtonVisual)sender;
        if (_session.EditMode && _dragging != v)
        {
            v.Cursor = v.IsSelected && v.OnGrip(e.GetPosition(v)) ? Cursors.SizeNWSE : Cursors.SizeAll;
            return;
        }

        if (_dragging != v || e.LeftButton != MouseButtonState.Pressed) return;

        var now = e.GetPosition(_canvas);
        double dx = now.X - _dragStart.X, dy = now.Y - _dragStart.Y;
        if (!_moved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return;

        _moved = true;
        var board = _session.Board;
        if (_resizing)
        {
            double w = Math.Clamp(_session.Snap(_dragRect.Width + dx), 24, board.Width - _dragRect.X);
            double h = Math.Clamp(_session.Snap(_dragRect.Height + dy), 24, board.Height - _dragRect.Y);
            v.Width  = w;
            v.Height = h;
        }
        else
        {
            Canvas.SetLeft(v, Math.Clamp(_session.Snap(_dragRect.X + dx), 0, Math.Max(0, board.Width - v.Width)));
            Canvas.SetTop(v, Math.Clamp(_session.Snap(_dragRect.Y + dy), 0, Math.Max(0, board.Height - v.Height)));
        }

        v.InvalidateVisual();
    }

    private void OnButtonUp(object sender, MouseButtonEventArgs e)
    {
        var v = (ButtonVisual)sender;
        v.ReleaseMouseCapture();
        if (_pressed == v)
        {
            _pressed    = null;
            v.IsPressed = false;
            v.InvalidateVisual();
            _session.Release(v.Model);
            e.Handled = true;
            return;
        }

        if (_dragging != v) return;

        _dragging = null;
        if (_moved)
        {
            double x = Canvas.GetLeft(v), y = Canvas.GetTop(v);
            _session.Place(v.Model.Id, x, y, _resizing ? v.Width : null, _resizing ? v.Height : null);
        }

        e.Handled = true;
    }

    private void OnButtonRight(object sender, MouseButtonEventArgs e)
    {
        var v = (ButtonVisual)sender;
        if (!_session.EditMode) return;

        _session.Select(v.Model.Id);
        var menu = new ContextMenu();

        void Item(string text, Action run)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }

        Item("Test", () => _session.Test(v.Model));
        Item("Duplicate", _session.DuplicateSelected);
        Item("Bring to front", _session.BringToFront);
        Item("Send to back", _session.SendToBack);
        menu.Items.Add(new Separator());
        Item("Delete", _session.DeleteSelected);
        menu.IsOpen = true;
        e.Handled   = true;
    }

    // ---- keyboard --------------------------------------------------------------------------------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_session.EditMode)
        {
            EditKey(key, e);
            return;
        }

        if (e.IsRepeat || Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;

        if (_session.ByHotkey(key.ToString()) is not { } button) return;

        _keysDown[key] = button;
        if (_visuals.TryGetValue(button.Id, out var v))
        {
            v.IsPressed = true;
            v.InvalidateVisual();
        }

        _session.Press(button);
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!_keysDown.Remove(key, out var button)) return;

        if (_visuals.TryGetValue(button.Id, out var v))
        {
            v.IsPressed = false;
            v.InvalidateVisual();
        }

        _session.Release(button);
        e.Handled = true;
    }

    private void ReleaseKeys()
    {
        foreach (var button in _keysDown.Values.ToList())
        {
            if (_visuals.TryGetValue(button.Id, out var v))
            {
                v.IsPressed = false;
                v.InvalidateVisual();
            }

            _session.Release(button);
        }

        _keysDown.Clear();
    }

    private void EditKey(Key key, KeyEventArgs e)
    {
        if (_session.Selected is not { } b) return;

        double step = _session.Board.GridSize > 0 && _session.Board.Snap ? _session.Board.GridSize : 1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) step *= 5;
        (double dx, double dy) = key switch { Key.Left => (-step, 0.0), Key.Right => (step, 0.0), Key.Up => (0.0, -step), Key.Down => (0.0, step), _ => (0.0, 0.0) };
        if (key == Key.Delete)
        {
            _session.DeleteSelected();
            e.Handled = true;
        }
        else if (dx != 0 || dy != 0)
        {
            _session.Place(b.Id, b.X + dx, b.Y + dy);
            e.Handled = true;
        }
    }
}
