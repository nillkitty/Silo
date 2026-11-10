using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Silo.DataEditing.Sprites;

/// <summary>
///     Draws one frame of sprite pieces with the art and palette it uses, the way the VDP would (<see cref="SpriteComposer" />), with the object's
///     origin marked and the pieces outlined. Click a piece to select it; drag it to move it. The view does not own the pieces: whoever does calls
///     <see cref="Refresh" /> after changing them.
/// </summary>
public sealed class SpriteFrameView : FrameworkElement
{
    private readonly Pen _origin = new(new SolidColorBrush(Color.FromArgb(160, 255, 80, 80)), 1);
    private readonly Pen _outline = new(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1) { DashStyle = DashStyles.Dot };
    private readonly Pen _selected = new(new SolidColorBrush(Color.FromRgb(61, 139, 219)), 2);

    private WriteableBitmap? _bitmap;
    private RenderedFrame    _frame = RenderedFrame.Empty;
    private int              _dragPiece = -1;
    private Point            _dragFrom;
    private int              _dragStartX, _dragStartY;
    private bool             _dragged;

    public SpriteFrameView()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        SnapsToDevicePixels = true;
    }

    /// <summary>The pieces of the frame being shown (the first is on top); the list is the owner's.</summary>
    public IReadOnlyList<SpritePiece> Pieces { get; set; } = [];

    public TileSheet Art { get; set; } = new([]);

    public uint[] Palette { get; set; } = MdPalette.Greys();

    /// <summary>Turns a tile number of a piece into a tile of the art (the DPLC); null for as it is.</summary>
    public Func<int, int>? ArtTile { get; set; }

    /// <summary>Pixels of the screen for one pixel of the sprite.</summary>
    public int Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, 1, 16);
            InvalidateVisual();
        }
    }

    private int _zoom = 4;

    public bool ShowOutlines { get; set; } = true;

    public bool ShowGrid { get; set; } = true;

    /// <summary>Which piece is selected; -1 for none.</summary>
    public int SelectedIndex { get; set; } = -1;

    /// <summary>The user selected a piece (or none, -1) by clicking.</summary>
    public event Action<int>? PieceClicked;

    /// <summary>The user is about to drag a piece: the owner takes its undo snapshot.</summary>
    public event Action? DragStarting;

    /// <summary>The user dragged a piece to (x, y).</summary>
    public event Action<int, int, int>? PieceDragged;

    /// <summary>What was last drawn (for tests and for saving a picture).</summary>
    public RenderedFrame Frame => _frame;

    /// <summary>The pieces or what they use changed: draw again.</summary>
    public void Refresh()
    {
        _frame = SpriteComposer.Render(Pieces, Art, Palette, ArtTile);
        _bitmap = null;
        if (_frame.Width > 0 && _frame.Height > 0)
        {
            _bitmap = new WriteableBitmap(_frame.Width, _frame.Height, 96, 96, PixelFormats.Bgra32, null);
            var pixels = new int[_frame.Pixels.Length];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (int)_frame.Pixels[i];
            _bitmap.WritePixels(new Int32Rect(0, 0, _frame.Width, _frame.Height), pixels, _frame.Width * 4, 0);
        }

        InvalidateVisual();
    }

    private Point Origin => new(Math.Round(ActualWidth / 2), Math.Round(ActualHeight / 2));

    private Rect ScreenRect(SpritePiece p) => new(Origin.X + p.X * _zoom, Origin.Y + p.Y * _zoom, p.PixelWidth * _zoom, p.PixelHeight * _zoom);

    protected override void OnRender(DrawingContext dc)
    {
        // a checkerboard shows what is transparent
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(46, 46, 52)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        var checker = new SolidColorBrush(Color.FromRgb(54, 54, 60));
        int cell = Math.Max(8, 8 * _zoom / 2);
        for (int y = 0; y * cell < ActualHeight; y++)
        for (int x = 0; x * cell < ActualWidth; x++)
            if ((x + y) % 2 == 0)
                dc.DrawRectangle(checker, null, new Rect(x * cell, y * cell, cell, cell));

        var origin = Origin;
        if (ShowGrid)
        {
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
            int step = 8 * _zoom;
            for (double x = origin.X % step; x < ActualWidth; x += step) dc.DrawLine(grid, new Point(x, 0), new Point(x, ActualHeight));
            for (double y = origin.Y % step; y < ActualHeight; y += step) dc.DrawLine(grid, new Point(0, y), new Point(ActualWidth, y));
        }

        if (_bitmap is not null)
            dc.DrawImage(_bitmap, new Rect(origin.X + _frame.Left * _zoom, origin.Y + _frame.Top * _zoom, _frame.Width * _zoom, _frame.Height * _zoom));

        if (ShowOutlines)
            for (int i = 0; i < Pieces.Count; i++)
                if (i != SelectedIndex) dc.DrawRectangle(null, _outline, ScreenRect(Pieces[i]));

        if (SelectedIndex >= 0 && SelectedIndex < Pieces.Count) dc.DrawRectangle(null, _selected, ScreenRect(Pieces[SelectedIndex]));

        dc.DrawLine(_origin, new Point(origin.X - 8, origin.Y), new Point(origin.X + 8, origin.Y));
        dc.DrawLine(_origin, new Point(origin.X, origin.Y - 8), new Point(origin.X, origin.Y + 8));
    }

    private int PieceAt(Point at)
    {
        // the first piece is the one on top
        for (int i = 0; i < Pieces.Count; i++)
            if (ScreenRect(Pieces[i]).Contains(at))
                return i;
        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var at  = e.GetPosition(this);
        int hit = PieceAt(at);

        // a click inside the selected piece keeps it (and starts moving it), so overlapping pieces can still be dragged
        if (SelectedIndex >= 0 && SelectedIndex < Pieces.Count && ScreenRect(Pieces[SelectedIndex]).Contains(at)) hit = SelectedIndex;

        SelectedIndex = hit;
        PieceClicked?.Invoke(hit);
        if (hit >= 0)
        {
            _dragPiece  = hit;
            _dragFrom   = at;
            _dragStartX = Pieces[hit].X;
            _dragStartY = Pieces[hit].Y;
            _dragged    = false;
            CaptureMouse();
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragPiece < 0 || e.LeftButton != MouseButtonState.Pressed) return;

        var at = e.GetPosition(this);
        int dx = (int)Math.Round((at.X - _dragFrom.X) / _zoom);
        int dy = (int)Math.Round((at.Y - _dragFrom.Y) / _zoom);
        if (!_dragged && dx == 0 && dy == 0) return;

        if (!_dragged)
        {
            _dragged = true;
            DragStarting?.Invoke();
        }

        PieceDragged?.Invoke(_dragPiece, _dragStartX + dx, _dragStartY + dy);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragPiece = -1;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Zoom += e.Delta > 0 ? 1 : -1;
        e.Handled = true;
    }
}

/// <summary>
///     Shows the tiles of the art in rows of sixteen with a palette line, so that the tile of a piece can be picked by eye. The tiles a piece
///     covers are outlined. With a DPLC the tiles shown are the slots it fills (what the piece's tile numbers refer to).
/// </summary>
public sealed class TilePickerView : FrameworkElement
{
    private const int PerRow = 16;

    private WriteableBitmap? _bitmap;
    private int              _tileCount;

    public TilePickerView()
    {
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public TileSheet Art { get; set; } = new([]);

    public uint[] Palette { get; set; } = MdPalette.Greys();

    public int PaletteLine { get; set; }

    /// <summary>Slot n shows this tile of the art (a DPLC's expansion); null: slot n is tile n.</summary>
    public int[]? Slots { get; set; }

    /// <summary>The tiles to outline (first tile, count); count 0 for none.</summary>
    public (int First, int Count) Highlight { get; set; }

    public int Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, 1, 8);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private int _zoom = 3;

    /// <summary>The user clicked a tile (a slot, with a DPLC).</summary>
    public event Action<int>? TilePicked;

    public void Refresh()
    {
        _tileCount = Slots?.Length ?? Art.Count;
        int rows = Math.Max(1, (_tileCount + PerRow - 1) / PerRow);
        _bitmap = new WriteableBitmap(PerRow * 8, rows * 8, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new int[PerRow * 8 * rows * 8];
        for (int t = 0; t < _tileCount; t++)
        {
            int art = Slots is null ? t : Slots[t];
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int index = Art.Pixel(art, x, y);
                int at    = ((t / PerRow) * 8 + y) * PerRow * 8 + (t % PerRow) * 8 + x;
                pixels[at] = index == 0 ? unchecked((int)0xFF2A2A30) : (int)Palette[(PaletteLine & 3) * MdPalette.ColorsPerLine + index];
            }
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, PerRow * 8, rows * 8), pixels, PerRow * 8 * 4, 0);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int rows = Math.Max(1, (_tileCount + PerRow - 1) / PerRow);
        return new Size(PerRow * 8 * _zoom, rows * 8 * _zoom);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(42, 42, 48)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_bitmap is null) return;

        dc.DrawImage(_bitmap, new Rect(0, 0, PerRow * 8 * _zoom, _bitmap.PixelHeight * _zoom));
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(61, 139, 219)), 1.5);
        for (int t = Highlight.First; t < Highlight.First + Highlight.Count; t++)
            if (t >= 0 && t < _tileCount)
                dc.DrawRectangle(null, pen, new Rect((t % PerRow) * 8 * _zoom, (t / PerRow) * 8 * _zoom, 8 * _zoom, 8 * _zoom));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var at   = e.GetPosition(this);
        int tile = (int)(at.Y / (8 * _zoom)) * PerRow + (int)(at.X / (8 * _zoom));
        if (tile >= 0 && tile < _tileCount) TilePicked?.Invoke(tile);
        e.Handled = true;
    }
}
