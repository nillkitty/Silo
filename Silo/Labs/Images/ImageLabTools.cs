using System.Globalization;

namespace Silo.Labs.Images;

/// <summary>Which of Shift, Ctrl and Alt are down.</summary>
internal readonly record struct Mods(bool Shift, bool Ctrl, bool Alt);

/// <summary>What the pointer should look like over the picture.</summary>
internal enum PointerKind { Arrow, Cross, Move, Hand, Text, Brush, Rotate, SizeNwse, SizeNesw, SizeWe, SizeNs }

/// <summary>What the tools show on top of the picture (in picture coordinates): the view draws it, scaled.</summary>
internal sealed class ImageOverlay
{
    /// <summary>A lasso being drawn, and where the next piece would end.</summary>
    public IReadOnlyList<Pt>? Lasso { get; init; }

    public Pt? LassoTo { get; init; }

    /// <summary>The rectangle or ellipse being dragged out.</summary>
    public (Pt A, Pt B)? Rubber { get; init; }

    public bool RubberIsEllipse { get; init; }

    /// <summary>The gradient's line.</summary>
    public (Pt A, Pt B)? Line { get; init; }

    /// <summary>The bezier path as drawn so far (a fine polyline), its points with their handles, and whether it is shut.</summary>
    public IReadOnlyList<Pt>? Curve { get; init; }

    public IReadOnlyList<PathPoint>? Anchors { get; init; }

    public bool CurveClosed { get; init; }

    /// <summary>The corners (top-left, top-right, bottom-right, bottom-left) of the box being transformed, and the eight handles (the corners, then the middles of the top, right, bottom and left edges).</summary>
    public IReadOnlyList<Pt>? Box { get; init; }

    public IReadOnlyList<Pt>? Handles { get; init; }

    public Pt? Pivot { get; init; }
}

// The tools: what the pointer does with each one. The view turns the mouse into picture coordinates and calls PointerDown / PointerMove / PointerUp / HandleKey.
public sealed partial class ImageLabSession
{
    private enum Drag { None, Stroke, Gradient, Marquee, LassoFree, MoveLayer, MoveContent, Bezier, Handle, Turn }

    private enum MoveMode { None, Content, Layer, Click }

    private Drag                        _drag;
    private Pt                          _down;
    private Pt                          _cursor;
    private Pt                          _lastPoint;
    private bool                        _hasLast;
    private bool                        _dragged;
    private ImageDocument.PixelSnapshot? _snap;
    private BrushStroke?                _stroke;
    private Pt                          _gradientEnd;
    private Pt                          _gradientShown;
    private Rgba                        _strokeColor;
    private List<Pt>?                   _lasso;
    private readonly List<PathPoint>    _bezier = [];
    private MoveMode                    _moveMode;
    private bool                        _deselectFirst;

    // the transform being worked on (by the Scale/Skew and Rotate tools, the numbers of the aspect, or the Selection Tool's move)
    private LayerTransform?   _tf;
    private (ImageLayer? Layer, Mask? Selection, int Revision) _tfStamp;
    private TransformState   _t     = TransformState.None;
    private TransformState   _start = TransformState.None;
    private bool              _tfWriting;
    private int               _handle = -1;
    private Pt                _turnFrom;

    private string? ToolId => ActiveTool?.Id;

    private static bool IsTransformTool(string? id) => id is "scale" or "rotate";

    private double Pixel => 1 / Math.Max(ViewScale, 0.01);

    private SelectionMode Mode => Enum.TryParse<SelectionMode>(_selectionSet["mode"].Text, out var mode) ? mode : SelectionMode.Replace;

    // ---- switching tools ---------------------------------------------------------------------------------------------

    private void ToolSwitched(string id, bool on)
    {
        if (on)
        {
            _hasLast = false;
            if (IsTransformTool(id)) EnsureTransform();
        }
        else
        {
            FinishTool(id);
        }

        RaiseOverlay();
    }

    /// <summary>The tool is put down: what it has not finished is finished (a polygon is closed, a path is drawn, a transform is made final).</summary>
    private void FinishTool(string id)
    {
        switch (id)
        {
            case "lasso":
                if (_lasso is { Count: >= 3 }) ApplyLasso();
                else _lasso = null;
                break;
            case "bezier":
                if (_bezier.Count >= 2) FinishBezier(false);
                else _bezier.Clear();
                break;
        }

        if (_tf is not null && (IsTransformTool(id) || _tf.IsChanged))
        {
            CommitTransform();
            _tf = null;
            ResetTransformFields();
        }

        _drag = Drag.None;
    }

    /// <summary>Makes whatever is under way final (Enter).</summary>
    internal void CommitPending()
    {
        string? id = ToolId;
        if (id == "lasso" && _lasso is { Count: >= 3 }) ApplyLasso();
        else if (id == "bezier" && _bezier.Count >= 2) FinishBezier(false);

        if (_tf is { IsChanged: true })
        {
            CommitTransform();
            if (IsTransformTool(id)) EnsureTransform();
        }

        RaiseOverlay();
    }

    /// <summary>Gives up whatever is under way (Esc, or before undoing).</summary>
    internal void CancelPending()
    {
        if (_snap is { } snap)
        {
            snap.Layer.Pixels.CopyFrom(snap.Before);
            _snap   = null;
            _stroke = null;
            RaisePainted(IntRect.Empty);
        }

        _drag = Drag.None;
        _lasso = null;
        _bezier.Clear();
        if (_tf is not null)
        {
            _tf.Cancel();
            ResetTransformFields();
            WriteTransformParameters();
            _tf = null;
            if (IsTransformTool(ToolId)) EnsureTransform();
        }

        RaiseOverlay();
    }

    /// <summary>The image was replaced or reset: nothing of the old one is under way.</summary>
    private void ResetTransformState()
    {
        _snap   = null;
        _stroke = null;
        _drag   = Drag.None;
        _lasso  = null;
        _hasLast = false;
        _bezier.Clear();
        _tf = null;
        ResetTransformFields();
        WriteTransformParameters();
        if (IsTransformTool(ToolId)) EnsureTransform();
    }

    /// <summary>The layers, the selection or the history changed: a transform that has not begun starts again from the new state.</summary>
    private void AfterDocChanged()
    {
        if (IsTransformTool(ToolId) && _drag == Drag.None) EnsureTransform();
    }

    private IEnumerable<ProvidedCommand> PendingActions()
    {
        if (_bezier.Count >= 2 || _lasso is { Count: >= 3 } || _tf is { IsChanged: true })
        {
            yield return ProvidedCommand.From("pending-finish", "_Finish What Is Under Way", () => CommitPending(), "Check", null, "Make the transform, path or polygon final (Enter)");
            yield return ProvidedCommand.From("pending-cancel", "_Cancel What Is Under Way", () => CancelPending(), null, null, "Give it up (Esc)");
        }
    }

    // ---- pointer ------------------------------------------------------------------------------------------------------------

    private static (int X, int Y) Cell(Pt p) => ((int)Math.Floor(p.X), (int)Math.Floor(p.Y));

    internal PointerKind CursorAt(Pt p, Mods mods)
    {
        switch (ToolId)
        {
            case "hand": return PointerKind.Hand;
            case "text": return PointerKind.Text;
            case "brush" or "eraser": return mods.Alt && ToolId == "brush" ? PointerKind.Cross : PointerKind.Brush;
            case "marquee" or "lasso" or "gradient" or "bezier": return PointerKind.Cross;
            case "rotate": return _tf is null ? PointerKind.Arrow : PointerKind.Rotate;
            case "scale":
                if (_tf is null) return PointerKind.Arrow;

                int h = HitHandle(p);
                if (h >= 0) return h switch { 0 or 2 => PointerKind.SizeNwse, 1 or 3 => PointerKind.SizeNesw, 4 or 6 => PointerKind.SizeNs, _ => PointerKind.SizeWe };

                return InsideBox(p) ? PointerKind.Move : PointerKind.Arrow;
            case "selection":
                var (x, y) = Cell(p);
                if (_doc.Selection is { } mask && mask[x, y] > 0) return PointerKind.Move;

                return _moveSet["pick"].Bool && _doc.LayerAt(x, y) >= 0 ? PointerKind.Move : PointerKind.Arrow;
            default: return PointerKind.Arrow;
        }
    }

    internal void PointerDown(Pt p, int button, Mods mods, int clicks)
    {
        _cursor  = p;
        _down    = p;
        _dragged = false;
        string? tool = ToolId;
        if (tool is null) return;

        // numbers typed into the transform aspect belong to the Scale/Skew and Rotate tools: any other tool makes them final first
        if (!IsTransformTool(tool) && _tf is not null)
        {
            if (_tf.IsChanged)
            {
                CommitTransform();
            }
            else
            {
                _tf.Cancel();
                _tf = null;
            }
        }

        switch (tool)
        {
            case "brush": BrushDown(p, button, mods, false); break;
            case "eraser": BrushDown(p, button, mods, true); break;
            case "gradient": GradientDown(p); break;
            case "marquee": _drag = Drag.Marquee; break;
            case "lasso": LassoDown(p, clicks); break;
            case "selection": MoveDown(p, button); break;
            case "scale": ScaleDown(p); break;
            case "rotate": RotateDown(p); break;
            case "text":
                if (button == 0) RequestText(p);
                break;
            case "bezier": BezierDown(p, button, clicks); break;
        }
    }

    internal void PointerMove(Pt p, Mods mods)
    {
        _cursor = p;
        if (!_dragged && _drag != Drag.None && p.DistanceTo(_down) > 2 * Pixel) _dragged = true;

        switch (_drag)
        {
            case Drag.Stroke: StrokeMove(p); break;
            case Drag.Gradient: GradientMove(p); break;
            case Drag.Marquee: RaiseOverlay(); break;
            case Drag.LassoFree:
                if (_lasso is { Count: > 0 } && p.DistanceTo(_lasso[^1]) >= Pixel)
                {
                    _lasso.Add(p);
                    RaiseOverlay();
                }

                break;
            case Drag.MoveContent or Drag.MoveLayer: MoveMove(p, mods); break;
            case Drag.Handle: HandleMove(p, mods); break;
            case Drag.Turn: TurnMove(p, mods); break;
            case Drag.Bezier:
                if (_bezier.Count > 0 && p.DistanceTo(_down) > 3 * Pixel)
                {
                    _bezier[^1] = PathPoint.Smooth(_down, p);
                    RaiseOverlay();
                }

                break;
            default:
                if (_lasso is not null || _bezier.Count > 0) RaiseOverlay();
                break;
        }
    }

    internal void PointerUp(Pt p, Mods mods)
    {
        _cursor = p;
        if (!_dragged && _drag != Drag.None && p.DistanceTo(_down) > 2 * Pixel) _dragged = true;

        var drag = _drag;
        _drag = Drag.None;
        switch (drag)
        {
            case Drag.Stroke: StrokeEnd(p); break;
            case Drag.Gradient: GradientEnd(p); break;
            case Drag.Marquee: MarqueeEnd(p, mods); break;
            case Drag.LassoFree:
                if (_dragged && _lasso is { Count: >= 3 }) ApplyLasso();
                else
                {
                    _lasso = null;
                    if (_doc.Selection is not null) _doc.SelectNone();
                }

                RaiseOverlay();
                break;
            case Drag.MoveContent or Drag.MoveLayer: MoveEnd(); break;
            case Drag.Handle or Drag.Turn: RaiseOverlay(); break;
        }
    }

    /// <summary>A key the view did not use for itself. Returns whether it was used.</summary>
    internal bool HandleKey(string key)
    {
        switch (key)
        {
            case "Enter":
                if (!HasPending) return false;

                CommitPending();
                return true;
            case "Escape":
                if (!HasPending && _drag == Drag.None) return false;

                CancelPending();
                return true;
            case "Back":
                if (_bezier.Count == 0) return false;

                RemoveLastBezierPoint();
                return true;
            case "Delete":
                if (!HasLayer) return false;

                FillWith(Rgba.Transparent, "Clear");
                return true;
            case "[":
                SetBrush(size: Math.Min(Brush.Size - 1, (int)Math.Round(Brush.Size * 0.8)));
                return true;
            case "]":
                SetBrush(size: Math.Max(Brush.Size + 1, (int)Math.Round(Brush.Size * 1.25)));
                return true;
            case "X":
                SwapColors();
                return true;
            case "D":
                ResetColors();
                return true;
        }

        string? tool = key switch
                       {
                           "V" => "selection", "H" => "hand", "M" => "marquee", "L" => "lasso", "S" => "scale", "R" => "rotate", "T" => "text", "B" => "brush", "E" => "eraser", "G" => "gradient", "P" => "bezier",
                           _   => null
                       };
        if (tool is null || Tools.FirstOrDefault(t => t.Id == tool) is not { } found) return false;

        ActivateTool(found);
        return true;
    }

    private bool HasPending => _bezier.Count > 0 || _lasso is not null || _tf is { IsChanged: true };

    // ---- brush and eraser -------------------------------------------------------------------------------------------------

    private bool CanPaint()
    {
        if (_doc.ActiveLayer is not { } layer) return false;

        if (layer.Visible) return true;

        Say("The active layer is hidden: show it to paint on it.");
        return false;
    }

    private void PickColor(Pt p, bool foreground)
    {
        var (x, y) = Cell(p);
        if (x < 0 || y < 0 || x >= _doc.Width || y >= _doc.Height) return;

        var color = _doc.Composite().Get(x, y).WithAlpha(255);
        if (foreground) Foreground = color;
        else Background = color;
    }

    private IntRect Around(Pt a, Pt b, int radius)
    {
        var box = IntRect.FromCorners(Math.Min(a.X, b.X) - radius - 2, Math.Min(a.Y, b.Y) - radius - 2, Math.Max(a.X, b.X) + radius + 2, Math.Max(a.Y, b.Y) + radius + 2);
        return box.Intersect(new IntRect(0, 0, _doc.Width, _doc.Height));
    }

    private void BrushDown(Pt p, int button, Mods mods, bool erase)
    {
        if (mods.Alt && !erase)
        {
            PickColor(p, button == 0);
            return;
        }

        if (!CanPaint()) return;

        var layer = _doc.ActiveLayer!;
        _snap = _doc.BeginPaint();
        var brush = Brush;
        _strokeColor = button == 0 ? Foreground : Background;
        _stroke = new BrushStroke(layer.Pixels, _doc.Selection, brush, _strokeColor, erase);
        var from = mods.Shift && _hasLast ? _lastPoint : p;
        _stroke.Begin(from);
        if (from != p) _stroke.LineTo(p);

        RaisePainted(Around(from, p, brush.Size / 2 + 1));
        _lastPoint = p;
        _hasLast   = true;
        _drag      = Drag.Stroke;
    }

    private void StrokeMove(Pt p)
    {
        if (_stroke is null) return;

        var before = _lastPoint;
        _stroke.LineTo(p);
        RaisePainted(Around(before, p, Brush.Size / 2 + 1));
        _lastPoint = p;
    }

    private void StrokeEnd(Pt p)
    {
        if (_stroke is null || _snap is null) return;

        StrokeMove(p);
        bool erase = ToolId == "eraser";
        _doc.EndPaint(_snap, erase ? "Erase" : "Brush Stroke");
        if (!erase) Remember(_strokeColor);

        _stroke = null;
        _snap   = null;
    }

    // ---- gradient ---------------------------------------------------------------------------------------------------------------

    private (Rgba Start, Rgba End) GradientColors()
    {
        Rgba fg = Foreground, bg = Background;
        var (start, end) = _gradientSet["colors"].Text switch
                           {
                               "Background to Foreground"  => (bg, fg),
                               "Foreground to Transparent" => (fg, fg.WithAlpha(0)),
                               "Transparent to Foreground" => (fg.WithAlpha(0), fg),
                               _                           => (fg, bg)
                           };
        double opacity = IntOf(_gradientSet["opacity"], 100, 1, 100) / 100.0;
        return (start.WithAlpha((byte)Math.Round(start.A * opacity)), end.WithAlpha((byte)Math.Round(end.A * opacity)));
    }

    private void GradientDown(Pt p)
    {
        if (!CanPaint()) return;

        _snap          = _doc.BeginPaint();
        _gradientEnd   = p;
        _gradientShown = p;
        _drag          = Drag.Gradient;
    }

    private void ShowGradient()
    {
        if (_snap is null) return;

        var layer = _snap.Layer;
        layer.Pixels.CopyFrom(_snap.Before);
        var (start, end) = GradientColors();
        Gradients.Fill(layer.Pixels, _doc.Selection, _down, _gradientEnd, start, end, Enum.TryParse<GradientKind>(_gradientSet["kind"].Text, out var kind) ? kind : GradientKind.Linear);
        RaisePainted(_doc.Selection?.Bounds() ?? IntRect.Empty);
    }

    private void GradientMove(Pt p)
    {
        _gradientEnd = p;
        if (p.DistanceTo(_gradientShown) >= Pixel)
        {
            _gradientShown = p;
            ShowGradient();
        }

        RaiseOverlay();
    }

    private void GradientEnd(Pt p)
    {
        if (_snap is null) return;

        _gradientEnd = p;
        ShowGradient();
        var (start, _) = GradientColors();
        _doc.EndPaint(_snap, "Gradient");
        Remember(start.A == 0 ? Foreground : start);
        _snap = null;
        RaiseOverlay();
    }

    // ---- marquee and lasso -----------------------------------------------------------------------------------------------------

    /// <summary>The rectangle being dragged out, made square when Shift is down and from the middle when Alt is.</summary>
    private (Pt A, Pt B) RubberCorners(Pt to, Mods mods)
    {
        var a = _down;
        var b = to;
        if (mods.Shift)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            b = new Pt(a.X + (dx < 0 ? -side : side), a.Y + (dy < 0 ? -side : side));
        }

        if (mods.Alt) a = new Pt(2 * _down.X - b.X, 2 * _down.Y - b.Y);

        return (new Pt(Math.Round(a.X), Math.Round(a.Y)), new Pt(Math.Round(b.X), Math.Round(b.Y)));
    }

    private int Feather => IntOf(_selectionSet["feather"], 0, 0, 250);

    private void SelectShape(Mask shape)
    {
        if (Feather > 0) shape = shape.Feather(Feather);

        _doc.Select(shape, Mode);
    }

    private void MarqueeEnd(Pt p, Mods mods)
    {
        if (!_dragged)
        {
            if (_doc.Selection is not null) _doc.SelectNone();

            RaiseOverlay();
            return;
        }

        var (a, b) = RubberCorners(p, mods);
        double x1 = Math.Min(a.X, b.X), y1 = Math.Min(a.Y, b.Y), x2 = Math.Max(a.X, b.X), y2 = Math.Max(a.Y, b.Y);
        if (x2 - x1 >= 1 && y2 - y1 >= 1)
            SelectShape(_selectionSet["shape"].Text == "Ellipse" ? Mask.Ellipse(_doc.Width, _doc.Height, x1, y1, x2, y2) : Mask.Rectangle(_doc.Width, _doc.Height, x1, y1, x2, y2));

        RaiseOverlay();
    }

    /// <summary>How text is set now (for the box the view shows while it is typed).</summary>
    internal (string Font, int Size, bool Bold, bool Italic, bool Underline, TextAlignment Align) TextLook =>
        (_textSet["font"].Text, IntOf(_textSet["size"], 32, 4, 1000), _textSet["bold"].Bool, _textSet["italic"].Bool, _textSet["underline"].Bool, Enum.TryParse<TextAlignment>(_textSet["align"].Text, out var a) ? a : TextAlignment.Left);

    /// <summary>The brush size in picture pixels while the Brush or Eraser is in hand (else 0): the view draws a ring of it around the pointer.</summary>
    internal int RingSize => ToolId is "brush" or "eraser" ? Brush.Size : 0;

    private bool PolygonStyle => _selectionSet["lasso"].Text == "Polygon";

    private void LassoDown(Pt p, int clicks)
    {
        if (!PolygonStyle)
        {
            _lasso = [p];
            _drag  = Drag.LassoFree;
            return;
        }

        _lasso ??= [];
        if (clicks >= 2 || (_lasso.Count >= 3 && p.DistanceTo(_lasso[0]) <= 8 * Pixel))
        {
            if (_lasso.Count >= 3) ApplyLasso();
            else _lasso = null;

            RaiseOverlay();
            return;
        }

        _lasso.Add(p);
        RaiseOverlay();
    }

    private void ApplyLasso()
    {
        var points = _lasso;
        _lasso = null;
        if (points is not { Count: >= 3 }) return;

        SelectShape(Mask.Polygon(_doc.Width, _doc.Height, points));
    }

    // ---- selection tool: moving ------------------------------------------------------------------------------------------------------

    private void MoveDown(Pt p, int button)
    {
        if (button != 0) return;

        var (x, y) = Cell(p);
        _moveMode      = MoveMode.None;
        _deselectFirst = false;
        if (_doc.Selection is { } mask && mask[x, y] > 0)
        {
            _moveMode = MoveMode.Content;
        }
        else
        {
            if (_moveSet["pick"].Bool)
            {
                int index = _doc.LayerAt(x, y);
                if (index >= 0)
                {
                    _doc.SetActive(index);
                    _moveMode = MoveMode.Layer;
                }
                else
                {
                    _moveMode = MoveMode.Click;
                }
            }
            else
            {
                _moveMode = MoveMode.Layer;
            }

            _deselectFirst = _doc.Selection is not null;
        }

        _drag = _moveMode == MoveMode.Content ? Drag.MoveContent : Drag.MoveLayer;
    }

    private void MoveMove(Pt p, Mods mods)
    {
        if (_moveMode is MoveMode.None or MoveMode.Click || !_dragged) return;

        if (_tf is null)
        {
            if (_moveMode == MoveMode.Layer && _deselectFirst) _doc.SelectNone();

            if (_doc.ActiveLayer is { Visible: false })
            {
                Say("The active layer is hidden: show it to move it.");
                _moveMode = MoveMode.None;
                return;
            }

            _tf = LayerTransform.Begin(_doc);
            if (_tf is null)
            {
                _moveMode = MoveMode.None;
                return;
            }

            _tfStamp = (_doc.ActiveLayer, _doc.Selection, _doc.Revision);
        }

        double dx = Math.Round(p.X - _down.X), dy = Math.Round(p.Y - _down.Y);
        if (mods.Shift)
        {
            if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
            else dx = 0;
        }

        _tf.SetMatrix(Affine.Translate(dx, dy));
        RaiseOverlay();
    }

    private void MoveEnd()
    {
        if (_dragged && _tf is not null)
        {
            _tf.Commit(_moveMode == MoveMode.Content ? "Move Selection" : "Move Layer");
            _tf = null;
            ResetTransformFields();
        }
        else if (!_dragged && _moveMode is MoveMode.Click or MoveMode.Layer && _deselectFirst && _doc.Selection is not null)
        {
            _doc.SelectNone();
        }

        _moveMode = MoveMode.None;
        RaiseOverlay();
    }

    // ---- transform: scale, skew, rotate, move ---------------------------------------------------------------------------------------------

    internal static IEnumerable<LabParameter> TransformParameters()
    {
        yield return new LabParameter("angle", "Rotate (degrees)", LabParameterKind.Text, "0", "Clockwise; a negative number turns the other way.", "Rotate", hint: "0");
        yield return new LabParameter("scaleX", "Scale across (%)", LabParameterKind.Text, "100", "100 is as it is; a negative number mirrors.", "Scale", hint: "100");
        yield return new LabParameter("scaleY", "Scale down (%)", LabParameterKind.Text, "100", "100 is as it is; a negative number mirrors.", "Scale", hint: "100");
        yield return new LabParameter("skewX", "Skew across (degrees)", LabParameterKind.Text, "0", "Slants the top and bottom sideways (-80 to 80).", "Skew", hint: "0");
        yield return new LabParameter("skewY", "Skew down (degrees)", LabParameterKind.Text, "0", "Slants the sides up and down (-80 to 80).", "Skew", hint: "0");
        yield return new LabParameter("moveX", "Move across (pixels)", LabParameterKind.Text, "0", "Right is positive.", "Move", hint: "0");
        yield return new LabParameter("moveY", "Move down (pixels)", LabParameterKind.Text, "0", "Down is positive.", "Move", hint: "0");
    }

    private static string Num(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private void ResetTransformFields() => _t = TransformState.None;

    private void WriteTransformParameters()
    {
        _tfWriting = true;
        try
        {
            var s = _transformSet;
            s["angle"].Value  = Num(_t.Angle);
            s["scaleX"].Value = Num(_t.ScaleX * 100);
            s["scaleY"].Value = Num(_t.ScaleY * 100);
            s["skewX"].Value  = Num(Math.Atan(_t.SkewX) * 180 / Math.PI);
            s["skewY"].Value  = Num(Math.Atan(_t.SkewY) * 180 / Math.PI);
            s["moveX"].Value  = Num(_t.MoveX);
            s["moveY"].Value  = Num(_t.MoveY);
        }
        finally
        {
            _tfWriting = false;
        }
    }

    private void ReadTransformParameters()
    {
        var s = _transformSet;
        const double skewLimit = TransformState.MaxSkewDegrees;
        _t = new TransformState(DoubleOf(s["angle"], 0, -3600, 3600),
                                TransformState.Away(DoubleOf(s["scaleX"], 100, -10000, 10000) / 100),
                                TransformState.Away(DoubleOf(s["scaleY"], 100, -10000, 10000) / 100),
                                Math.Tan(DoubleOf(s["skewX"], 0, -skewLimit, skewLimit) * Math.PI / 180),
                                Math.Tan(DoubleOf(s["skewY"], 0, -skewLimit, skewLimit) * Math.PI / 180),
                                DoubleOf(s["moveX"], 0, -50000, 50000),
                                DoubleOf(s["moveY"], 0, -50000, 50000));
    }

    private void OnTransformParameterChanged(LabParameter? changed)
    {
        if (_tfWriting) return;

        ReadTransformParameters();
        if (!EnsureTransform()) return;

        ShowTransform(false);
    }

    private void ResetTransformNumbers()
    {
        ResetTransformFields();
        WriteTransformParameters();
        if (EnsureTransform()) ShowTransform(false);
    }

    /// <summary>Mirrors on the screen about the middle of the box.</summary>
    private void FlipTransform(bool horizontal)
    {
        if (!EnsureTransform()) return;

        _t = _t.Mirrored(horizontal);
        ShowTransform(true);
    }

    /// <summary>The transform that has begun, or begins now (when there is something to transform).</summary>
    private bool EnsureTransform()
    {
        if (_tf is not null)
        {
            if (_tf.IsChanged || _tfStamp == (_doc.ActiveLayer, _doc.Selection, _doc.Revision)) return true;

            _tf.Cancel();
        }

        _tf = LayerTransform.Begin(_doc);
        if (_tf is null)
        {
            Say("There is nothing on the active layer (or in the selection) to transform.");
            return false;
        }

        _tfStamp = (_doc.ActiveLayer, _doc.Selection, _doc.Revision);
        RaiseOverlay();
        return true;
    }

    private Affine Compose() => _tf is null ? Affine.Identity : _t.Matrix(_tf.Center);

    private void ShowTransform(bool writeNumbers)
    {
        if (_tf is null) return;

        _tf.SetMatrix(Compose());
        if (writeNumbers) WriteTransformParameters();

        RaiseOverlay();
    }

    private string TransformName()
    {
        bool turned = Math.Abs(_t.Angle) > 1e-9, scaled = Math.Abs(_t.ScaleX - 1) > 1e-9 || Math.Abs(_t.ScaleY - 1) > 1e-9, skewed = Math.Abs(_t.SkewX) > 1e-9 || Math.Abs(_t.SkewY) > 1e-9, moved = Math.Abs(_t.MoveX) > 1e-9 || Math.Abs(_t.MoveY) > 1e-9;
        return (turned, scaled, skewed, moved) switch
               {
                   (true, false, false, false) => "Rotate",
                   (false, true, false, false) => "Scale",
                   (false, false, true, false) => "Skew",
                   (false, false, false, true) => "Move",
                   _                           => "Transform"
               };
    }

    private void CommitTransform()
    {
        if (_tf is null) return;

        string name = TransformName();
        _tf.Commit(name);
        _tf = null;
        ResetTransformFields();
        WriteTransformParameters();
    }

    private int HitHandle(Pt p)
    {
        if (_tf is null) return -1;

        var m = Compose();
        double tolerance = 9 * Pixel;
        for (int i = 0; i < 8; i++)
            if (m.Apply(TransformState.HandlePoint(_tf.Box, i)).DistanceTo(p) <= tolerance)
                return i;

        return -1;
    }

    private bool InsideBox(Pt p)
    {
        if (_tf is null) return false;

        var m = Compose();
        if (!m.IsInvertible) return false;

        var q = m.Invert().Apply(p);
        var b = _tf.Box;
        return q.X >= b.X && q.X <= b.Right && q.Y >= b.Y && q.Y <= b.Bottom;
    }

    private void ScaleDown(Pt p)
    {
        if (!EnsureTransform()) return;

        _start  = _t;
        _handle = HitHandle(p);
        if (_handle >= 0 || InsideBox(p)) _drag = Drag.Handle;
    }

    private void RotateDown(Pt p)
    {
        if (!EnsureTransform()) return;

        _start    = _t;
        _turnFrom = p;
        _drag     = Drag.Turn;
    }

    private void TurnMove(Pt p, Mods mods)
    {
        if (_tf is null) return;

        _t = _start.Turned(_tf.Center, _turnFrom, p, mods.Shift);
        ShowTransform(true);
    }

    private void HandleMove(Pt p, Mods mods)
    {
        if (_tf is null) return;

        if (_handle < 0)
        {
            double dx = p.X - _down.X, dy = p.Y - _down.Y;
            if (mods.Shift)
            {
                if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
                else dx = 0;
            }

            _t = _start.Moved(dx, dy);
        }
        else
        {
            _t = _start.DragHandle(_tf.Box, _handle, p, mods.Ctrl, mods.Shift, mods.Alt);
        }

        ShowTransform(true);
    }

    // ---- bezier -------------------------------------------------------------------------------------------------------------------------------

    private void BezierDown(Pt p, int button, int clicks)
    {
        if (button != 0) return;

        if (_bezier.Count >= 3 && p.DistanceTo(_bezier[0].At) <= 8 * Pixel)
        {
            FinishBezier(true);
            return;
        }

        if (clicks >= 2 && _bezier.Count >= 2)
        {
            FinishBezier(false);
            return;
        }

        _bezier.Add(PathPoint.Corner(p));
        _drag = Drag.Bezier;
        RaiseOverlay();
    }

    private void RemoveLastBezierPoint()
    {
        if (_bezier.Count > 0) _bezier.RemoveAt(_bezier.Count - 1);

        RaiseOverlay();
    }

    private void FinishBezier(bool closed)
    {
        var points = _bezier.ToList();
        _bezier.Clear();
        _drag = Drag.None;
        RaiseOverlay();
        if (points.Count < 2) return;

        bool stroke = _bezierSet["stroke"].Bool, fill = _bezierSet["fill"].Bool;
        if (!stroke && !fill)
        {
            Say("Turn on Stroke or Fill in the Bezier Tool's settings to draw the path.");
            return;
        }

        if (!CanPaint()) return;

        bool shut = closed || fill || _bezierSet["close"].Bool;
        var layer = _doc.ActiveLayer!;
        var snap  = _doc.BeginPaint();
        if (fill) BezierPaths.Fill(layer.Pixels, _doc.Selection, points, Background);

        if (stroke) BezierPaths.Stroke(layer.Pixels, _doc.Selection, points, shut, Brush, Foreground);

        _doc.EndPaint(snap!, "Bezier Path");
        Remember(stroke ? Foreground : Background);
    }

    // ---- text ---------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Sets the text in the font and size of the Text Settings, with its top-left corner at a point.</summary>
    internal void PlaceText(Pt at, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _doc.ActiveLayer is null) return;

        var s = _textSet;
        var align = Enum.TryParse<TextAlignment>(s["align"].Text, out var a) ? a : TextAlignment.Left;
        var picture = ImageText.Render(text, s["font"].Text, IntOf(s["size"], 32, 4, 1000), s["bold"].Bool, s["italic"].Bool, s["underline"].Bool, align, Foreground);
        if (picture is null)
        {
            Say("There was nothing to draw.");
            return;
        }

        int ox = (int)Math.Round(at.X) - 2, oy = (int)Math.Round(at.Y) - 2;
        bool ownLayer = s["ownLayer"].Bool;
        var target = ownLayer ? new Raster(_doc.Width, _doc.Height) : _doc.ActiveLayer.Pixels;
        ImageDocument.PixelSnapshot? snap = null;
        if (!ownLayer)
        {
            if (!CanPaint()) return;

            snap = _doc.BeginPaint();
        }

        var selection = _doc.Selection;
        for (int ty = 0; ty < picture.Height; ty++)
        for (int tx = 0; tx < picture.Width; tx++)
        {
            int x = ox + tx, y = oy + ty;
            if (x < 0 || y < 0 || x >= _doc.Width || y >= _doc.Height) continue;

            var c = picture.Get(tx, ty);
            if (c.A == 0) continue;

            double cover = selection is null ? 1 : selection.Values[y * _doc.Width + x] / 255.0;
            if (cover > 0) target.Over(x, y, c, cover);
        }

        string first = text.Split('\n')[0].Trim();
        if (ownLayer) _doc.AddLayer(_doc.UniqueLayerName(first.Length == 0 ? "Text" : LabUi.Shorten(first, 24)), pixels: target);
        else _doc.EndPaint(snap!, "Text");

        Remember(Foreground);
    }

    // ---- what the view draws on top ------------------------------------------------------------------------------------------------------------------

    /// <summary>The marks of the tool in hand, in picture coordinates.</summary>
    internal ImageOverlay BuildOverlay(Mods mods)
    {
        switch (ToolId)
        {
            case "marquee" when _drag == Drag.Marquee && _dragged:
                return new ImageOverlay { Rubber = RubberCorners(_cursor, mods), RubberIsEllipse = _selectionSet["shape"].Text == "Ellipse" };
            case "lasso" when _lasso is { Count: > 0 }:
                return new ImageOverlay { Lasso = _lasso.ToList(), LassoTo = PolygonStyle && _drag == Drag.None ? _cursor : null };
            case "gradient" when _drag == Drag.Gradient:
                return new ImageOverlay { Line = (_down, _gradientEnd) };
            case "bezier" when _bezier.Count > 0:
                var anchors = _bezier.ToList();
                var curve   = BezierPaths.Flatten(anchors, false);
                if (_drag != Drag.Bezier) curve.Add(_cursor);

                return new ImageOverlay { Curve = curve, Anchors = anchors };
            case "scale" or "rotate" when _tf is not null:
                var m = Compose();
                var corners = Enumerable.Range(0, 4).Select(i => m.Apply(TransformState.HandlePoint(_tf.Box, i))).ToList();
                var handles = Enumerable.Range(0, 8).Select(i => m.Apply(TransformState.HandlePoint(_tf.Box, i))).ToList();
                return new ImageOverlay { Box = corners, Handles = ToolId == "scale" ? handles : null, Pivot = m.Apply(_tf.Center) };
            default:
                return new ImageOverlay();
        }
    }
}
