using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>A stretch of the results that lies in one block: what the map beside the scroll bar is drawn from.</summary>
/// <param name="Start">Index of its first result.</param>
/// <param name="Count">How many results it has.</param>
/// <param name="Name">The block's name ("Arrows"); empty for code points in none.</param>
/// <param name="Plane">The plane it is in (0 is the Basic Multilingual Plane).</param>
internal sealed record MapSegment(int Start, int Count, string Name, int Plane);

/// <summary>
///     The characters of the results as a grid (or a list), drawn the way a virtualizing list is: only the rows in view have elements, and they
///     are reused as the view scrolls, so a list of every character (about 150 000) scrolls as smoothly as one of ten. It has a scroll bar and,
///     beside it, a map of the whole list by block that shows where in it the view is and takes a click or a drag to go somewhere else.
/// </summary>
/// <remarks>
///     It knows nothing of what the characters are: the owner says how many there are, how to get one (<see cref="Resolve" />), whether it is
///     selected (<see cref="IsSelected" />) and which color it is marked with (<see cref="ColorOf" />), and is told about clicks.
/// </remarks>
internal sealed class ResultsView : Grid
{
    public const double GridCell = 46;
    public const double ListRow  = 28;

    private const double CellGap = 2; // a pixel around every cell

    private readonly Canvas                       _canvas = new() { ClipToBounds = true, Background = Brushes.Transparent, Focusable = false };
    private readonly ScrollBar                    _bar    = new() { Orientation = Orientation.Vertical };
    private readonly List<Cell>                   _pool   = [];
    private readonly Dictionary<Color, Brush>     _brushes = [];
    private readonly Dictionary<Color, Brush>     _tints   = [];
    private Silo.Unicode.ColorMode                _colorMode = Silo.Unicode.ColorMode.Accent;
    private bool                                  _listMode;
    private bool                                  _settingBar;
    private double                                _offset;
    private int                                   _cols = 1;
    private double                                _rowHeight = GridCell + CellGap;
    private int                                   _shownFirst = -2, _shownLast = -2;

    public ResultsView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Map = new ResultsMap(this);

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(_canvas, 0);
        SetColumn(_bar, 1);
        SetColumn(Map, 2);
        Children.Add(_canvas);
        Children.Add(_bar);
        Children.Add(Map);

        _canvas.SizeChanged += (_, _) => Relayout(keepFirst: true);
        _bar.ValueChanged   += (_, e) =>
                               {
                                   if (_settingBar) return;
                                   _offset = e.NewValue;
                                   Render();
                               };
        PreviewMouseWheel += OnWheel;
        MouseDown         += (_, _) => Focus();
    }

    // ---- what the owner gives ------------------------------------------------------------------------------------

    /// <summary>The character at a position of the results.</summary>
    public Func<int, UnicodeCharacter>? Resolve { get; set; }

    /// <summary>Whether the result at a position is selected.</summary>
    public Func<int, bool> IsSelected { get; set; } = _ => false;

    /// <summary>The color the result at a position is marked with; null for none.</summary>
    public Func<int, Color?> ColorOf { get; set; } = _ => null;

    /// <summary>How a mark color is shown: on the glyph, behind it, or as a bar (see <see cref="Silo.Unicode.ColorMode" />).</summary>
    public Silo.Unicode.ColorMode ColorMode
    {
        get => _colorMode;
        set
        {
            if (_colorMode == value) return;

            _colorMode = value;
            Render();
        }
    }

    /// <summary>How many results there are.</summary>
    public int Count { get; private set; }

    /// <summary>The map beside the scroll bar.</summary>
    public ResultsMap Map { get; }

    /// <summary>A cell was clicked: its position, the keys held, and the click count.</summary>
    public event Action<int, ModifierKeys, int>? ItemClicked;

    /// <summary>The context menu of a cell is about to open: its position and the (empty) menu to fill.</summary>
    public event Action<int, ContextMenu>? MenuRequested;

    /// <summary>The left button went down on an item and the mouse moved far enough to drag it: the index and the element to drag from.</summary>
    public event Action<int, UIElement>? ItemDragStarted;

    /// <summary>The left button came up on the item it went down on, without a drag.</summary>
    public event Action<int>? ItemReleased;

    private Point? _pressAt;      // where the left button went down on an item, for a drag
    private int    _pressIndex = -1;

    /// <summary>What is in view changed (scrolled, resized, new results).</summary>
    public event Action? ViewChanged;

    /// <summary>Whether the results are a list (a row each, with the name) rather than a grid.</summary>
    public bool ListMode
    {
        get => _listMode;
        set
        {
            if (_listMode == value) return;

            int keep = FirstVisible;
            _listMode = value;
            _pool.Clear();
            _canvas.Children.Clear();
            Relayout(keepFirst: false, keepIndex: keep);
        }
    }

    // ---- what the owner asks -------------------------------------------------------------------------------------

    /// <summary>How many cells are in a row (1 in the list).</summary>
    public int Columns => _cols;

    /// <summary>How many rows are in view in full.</summary>
    public int VisibleRows => Math.Max(1, (int)(_canvas.ActualHeight / _rowHeight));

    /// <summary>The position of the first result in view; -1 when there are none.</summary>
    public int FirstVisible => Count == 0 ? -1 : Math.Min(Count - 1, (int)(_offset / _rowHeight) * _cols);

    /// <summary>The position of the last result in view (the last of its row); -1 when there are none.</summary>
    public int LastVisible
    {
        get
        {
            if (Count == 0) return -1;

            int lastRow = (int)Math.Max(0, (_offset + _canvas.ActualHeight - 1) / _rowHeight);
            return Math.Min(Count - 1, lastRow * _cols + _cols - 1);
        }
    }

    /// <summary>Sets how many results there are; the view goes to the top unless it is told to stay where it is.</summary>
    public void SetCount(int count, bool keepPosition = false)
    {
        int keep = keepPosition ? FirstVisible : 0;
        Count = count;
        Relayout(keepFirst: false, keepIndex: Math.Max(0, Math.Min(keep, Math.Max(0, count - 1))));
    }

    /// <summary>Draws the elements in view again (the selection or the colors changed).</summary>
    public void Refresh() => Render();

    /// <summary>Scrolls just far enough to bring a result into view.</summary>
    public void EnsureVisible(int index)
    {
        if (index < 0 || index >= Count) return;

        double top    = index / _cols * _rowHeight;
        double height = _canvas.ActualHeight;
        if (top < _offset) SetOffset(top);
        else if (top + _rowHeight > _offset + height) SetOffset(top + _rowHeight - height);
    }

    /// <summary>Scrolls to put a result in the middle of the view.</summary>
    public void CenterOn(int index)
    {
        if (index < 0 || index >= Count) return;
        SetOffset(index / _cols * _rowHeight - (_canvas.ActualHeight - _rowHeight) / 2);
    }

    // ---- layout --------------------------------------------------------------------------------------------------

    private void Relayout(bool keepFirst, int keepIndex = 0)
    {
        double width  = _canvas.ActualWidth;
        double height = _canvas.ActualHeight;
        int    keep   = keepFirst ? FirstVisible : keepIndex;

        _cols      = _listMode ? 1 : Math.Max(1, (int)(width / (GridCell + CellGap)));
        _rowHeight = _listMode ? ListRow + 1 : GridCell + CellGap;

        double content = Math.Ceiling(Count / (double)_cols) * _rowHeight + 2;
        double max     = Math.Max(0, content - height);

        _settingBar         = true;
        _bar.Maximum        = max;
        _bar.ViewportSize   = Math.Max(0, height);
        _bar.SmallChange    = _rowHeight;
        _bar.LargeChange    = Math.Max(_rowHeight, height - _rowHeight);
        _bar.IsEnabled      = max > 0;
        _offset             = Math.Clamp(Math.Max(0, keep) / _cols * _rowHeight, 0, max);
        _bar.Value          = _offset;
        _settingBar         = false;

        _shownFirst = _shownLast = -2;
        Render();
        Map.InvalidateVisual();
    }

    private void SetOffset(double value)
    {
        double clamped = Math.Clamp(value, 0, _bar.Maximum);
        if (Math.Abs(clamped - _offset) < 0.01) return;

        _offset = clamped;
        _settingBar = true;
        _bar.Value  = clamped;
        _settingBar = false;
        Render();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        // three rows to a notch; with Ctrl, a screenful
        double step = Keyboard.Modifiers == ModifierKeys.Control ? Math.Max(_rowHeight, _canvas.ActualHeight - _rowHeight) : _rowHeight * 3;
        SetOffset(_offset - Math.Sign(e.Delta) * step * Math.Max(1, Math.Abs(e.Delta) / 120.0));
        e.Handled = true;
    }

    // ---- drawing -------------------------------------------------------------------------------------------------

    /// <summary>Puts the characters that are in view on the elements, making more elements when the view has grown.</summary>
    private void Render()
    {
        double height = _canvas.ActualHeight;
        double width  = _canvas.ActualWidth;
        if (Resolve is null || height <= 0 || width <= 0)
        {
            foreach (var cell in _pool) cell.Root.Visibility = Visibility.Collapsed;
            return;
        }

        int firstRow = (int)(_offset / _rowHeight);
        int rows     = (int)Math.Ceiling(height / _rowHeight) + 1;
        int needed   = rows * _cols;

        while (_pool.Count < needed)
        {
            var cell = _listMode ? MakeRow() : MakeCell();
            _pool.Add(cell);
            _canvas.Children.Add(cell.Root);
        }

        for (int k = 0; k < _pool.Count; k++)
        {
            var cell  = _pool[k];
            int index = firstRow * _cols + k;
            if (k >= needed || index >= Count)
            {
                cell.Index           = -1;
                cell.Root.Visibility = Visibility.Collapsed;
                continue;
            }

            int row = k / _cols, col = k % _cols;
            Canvas.SetLeft(cell.Root, _listMode ? 1 : col * (GridCell + CellGap) + 1);
            Canvas.SetTop(cell.Root, (firstRow + row) * _rowHeight - _offset + 1);
            if (_listMode) cell.Root.Width = Math.Max(40, width - 2);
            cell.Root.Visibility = Visibility.Visible;
            Fill(cell, index);
        }

        int first = FirstVisible, last = LastVisible;
        if (first != _shownFirst || last != _shownLast)
        {
            _shownFirst = first;
            _shownLast  = last;
            Map.InvalidateVisual();
            ViewChanged?.Invoke();
        }
    }

    private void Fill(Cell cell, int index)
    {
        var c = Resolve!(index);
        cell.Index      = index;
        cell.Glyph.Text = c.Glyph;
        if (_listMode)
        {
            cell.Hex!.Text  = c.CodePoint.Hex;
            cell.Name!.Text = c.Name;
            cell.Cat!.Text  = c.Category;
        }

        cell.Root.ToolTip = $"{c.CodePoint.Hex}  {c.Name}";

        var marked = ColorOf(index);
        Brush? solid = null, tint = null;
        if (marked is { } color)
        {
            if (!_brushes.TryGetValue(color, out solid))
            {
                solid = new SolidColorBrush(color);
                solid.Freeze();
                _brushes[color] = solid;
            }

            if (_colorMode == Silo.Unicode.ColorMode.Background && !_tints.TryGetValue(color, out tint))
            {
                tint = UiKit.TintBrush(color);
                _tints[color] = tint;
            }
        }

        // accent: the bar
        if (solid is not null && _colorMode == Silo.Unicode.ColorMode.Accent)
        {
            cell.Strip.Fill       = solid;
            cell.Strip.Visibility = Visibility.Visible;
        }
        else cell.Strip.Visibility = Visibility.Collapsed;

        // glyph: the character itself
        if (solid is not null && _colorMode == Silo.Unicode.ColorMode.Glyph)
        {
            cell.Glyph.Foreground = solid;
            cell.GlyphTinted      = true;
        }
        else if (cell.GlyphTinted)
        {
            cell.Glyph.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
            cell.GlyphTinted = false;
        }

        // background: behind it (a selected or hovered cell keeps the selection's look)
        cell.Root.SetValue(UiKit.TintProperty, tint);
        UiKit.Paint(cell.Root, IsSelected(index));
    }

    // ---- the elements --------------------------------------------------------------------------------------------

    private sealed class Cell
    {
        public required Border    Root;
        public required TextBlock Glyph;
        public required Rectangle Strip;
        public TextBlock?         Hex, Name, Cat;
        public int                Index = -1;
        public bool               GlyphTinted;
    }

    private Cell MakeCell()
    {
        var glyph = new TextBlock
                    {
                        FontFamily = UiKit.GlyphFont, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center
                    }.Themed(TextBlock.ForegroundProperty, "Theme.Text");
        var strip = new Rectangle { Height = 4, RadiusX = 1.5, RadiusY = 1.5, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(5, 0, 5, 2), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        var inner = new Grid();
        inner.Children.Add(glyph);
        inner.Children.Add(strip);
        return Wire(new Cell { Root = Frame(inner, GridCell, GridCell), Glyph = glyph, Strip = strip });
    }

    private Cell MakeRow()
    {
        var inner = new Grid { Height = ListRow };
        foreach (double w in new[] { 6, 34, 74 }) inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        var strip = new Rectangle { Width = 4, Margin = new Thickness(1, 3, 1, 3), RadiusX = 1.5, RadiusY = 1.5, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        var glyph = new TextBlock { FontFamily = UiKit.GlyphFont, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            .Themed(TextBlock.ForegroundProperty, "Theme.Text");
        var hex  = UiKit.Text(string.Empty, muted: true);
        hex.FontFamily = UiKit.Mono;
        var name = UiKit.Text(string.Empty);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var cat = UiKit.Text(string.Empty, muted: true);

        for (int i = 0; i < 5; i++)
        {
            UIElement e = i switch { 0 => strip, 1 => glyph, 2 => hex, 3 => name, _ => cat };
            SetColumn(e, i);
            inner.Children.Add(e);
        }

        return Wire(new Cell { Root = Frame(inner, double.NaN, ListRow), Glyph = glyph, Strip = strip, Hex = hex, Name = name, Cat = cat });
    }

    private static Border Frame(UIElement child, double width, double height)
    {
        var border = new Border { Child = child, Height = height, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand, Focusable = false };
        if (!double.IsNaN(width)) border.Width = width;
        UiKit.Paint(border, false);
        return border;
    }

    private Cell Wire(Cell cell)
    {
        var root = cell.Root;
        root.MouseEnter += (_, _) =>
                           {
                               if (cell.Index >= 0) UiKit.Paint(root, IsSelected(cell.Index), hot: true);
                           };
        root.MouseLeave += (_, _) =>
                           {
                               if (cell.Index >= 0) UiKit.Paint(root, IsSelected(cell.Index));
                           };
        root.MouseLeftButtonDown += (_, e) =>
                                    {
                                        Focus();
                                        _pressAt    = cell.Index >= 0 ? e.GetPosition(this) : null;
                                        _pressIndex = cell.Index;
                                        if (cell.Index >= 0) ItemClicked?.Invoke(cell.Index, Keyboard.Modifiers, e.ClickCount);
                                        e.Handled = true;
                                    };
        root.MouseMove += (_, e) =>
                          {
                              if (_pressAt is not { } start || e.LeftButton != MouseButtonState.Pressed || _pressIndex != cell.Index) return;
                              if (!GlyphDrag.FarEnough(start, e.GetPosition(this))) return;

                              _pressAt = null;
                              ItemDragStarted?.Invoke(_pressIndex, root);
                          };
        root.MouseLeftButtonUp += (_, _) =>
                                  {
                                      if (_pressAt is not null && _pressIndex == cell.Index && cell.Index >= 0) ItemReleased?.Invoke(cell.Index);
                                      _pressAt = null;
                                  };
        root.ContextMenu = new ContextMenu();
        root.ContextMenuOpening += (_, e) =>
                                   {
                                       if (cell.Index < 0)
                                       {
                                           e.Handled = true;
                                           return;
                                       }

                                       root.ContextMenu.Items.Clear();
                                       MenuRequested?.Invoke(cell.Index, root.ContextMenu);
                                       if (root.ContextMenu.Items.Count == 0) e.Handled = true;
                                   };
        return cell;
    }
}

/// <summary>
///     The strip beside a <see cref="ResultsView" />'s scroll bar: the whole list from top to bottom, divided by block (and a line where a plane
///     begins), with a frame around the part in view. Click or drag to go there; while the mouse is over it <see cref="Hover" /> says which result
///     it is over, so the owner can name the block.
/// </summary>
internal sealed class ResultsMap : FrameworkElement
{
    private readonly ResultsView          _view;
    private          IReadOnlyList<MapSegment> _segments = [];

    public ResultsMap(ResultsView view)
    {
        _view  = view;
        Width  = 16;
        Cursor = Cursors.Hand;
        Margin = new Thickness(2, 0, 0, 0);
        ToolTip = "Where you are in the whole list, by block. Click or drag to go somewhere else.";
    }

    /// <summary>The mouse is over the result at this position (-1: it left).</summary>
    public event Action<int>? Hover;

    /// <summary>The blocks of the results, in order.</summary>
    public IReadOnlyList<MapSegment> Segments => _segments;

    public void SetSegments(IReadOnlyList<MapSegment> segments)
    {
        _segments = segments;
        InvalidateVisual();
    }

    /// <summary>The segment the result at a position is in; null when there are none.</summary>
    public MapSegment? SegmentAt(int index)
    {
        int lo = 0, hi = _segments.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            var s   = _segments[mid];
            if (index < s.Start) hi = mid - 1;
            else if (index >= s.Start + s.Count) lo = mid + 1;
            else return s;
        }

        return null;
    }

    private Brush Find(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        int    n = _view.Count;
        dc.DrawRectangle(Find("Theme.BackgroundAlt"), new Pen(Find("Theme.Border"), 1), new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1)));
        if (n == 0 || h <= 2) return;

        var a    = Find("Theme.Surface");
        var b    = Find("Theme.SurfaceHover");
        var line = Find("Theme.TextMuted");
        int k    = 0, lastPlane = -1;
        foreach (var s in _segments)
        {
            double y0 = (double)s.Start / n * h;
            double y1 = (double)(s.Start + s.Count) / n * h;
            dc.DrawRectangle(k++ % 2 == 0 ? a : b, null, new Rect(1.5, y0, Math.Max(0, w - 3), Math.Max(1, y1 - y0)));
            if (lastPlane >= 0 && s.Plane != lastPlane) dc.DrawRectangle(line, null, new Rect(1, y0 - 0.5, Math.Max(0, w - 2), 1));
            lastPlane = s.Plane;
        }

        double top    = (double)Math.Max(0, _view.FirstVisible) / n * h;
        double bottom = (double)(_view.LastVisible + 1) / n * h;
        var    accent = Find("Theme.Accent");
        var    color  = (accent as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B)), new Pen(accent, 1.5), new Rect(0.75, top, Math.Max(0, w - 1.5), Math.Max(5, bottom - top)));
    }

    private int IndexAt(MouseEventArgs e)
    {
        if (_view.Count == 0 || ActualHeight <= 0) return -1;

        double f = Math.Clamp(e.GetPosition(this).Y / ActualHeight, 0, 0.999999);
        return (int)(f * _view.Count);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        if (IndexAt(e) is >= 0 and var i) _view.CenterOn(i);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e);
        Hover?.Invoke(i);
        if (IsMouseCaptured && i >= 0) _view.CenterOn(i);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsMouseCaptured) Hover?.Invoke(-1);
    }
}
