namespace Silo.Labs.Images;

/// <summary>A 2D affine transform: x' = A x + C y + E, y' = B x + D y + F.</summary>
public readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public static Affine Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    public static Affine Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    public static Affine Rotate(double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new Affine(c, s, -s, c, 0, 0);
    }

    /// <summary>A shear: x' = x + kx y, y' = ky x + y (kx and ky are tangents of the skew angles).</summary>
    public static Affine Skew(double kx, double ky) => new(1, ky, kx, 1, 0, 0);

    /// <summary>The transform that does this first and then <paramref name="next" />.</summary>
    public Affine Then(Affine n) => new(n.A * A + n.C * B, n.B * A + n.D * B, n.A * C + n.C * D, n.B * C + n.D * D, n.A * E + n.C * F + n.E, n.B * E + n.D * F + n.F);

    public double Determinant => A * D - B * C;

    public bool IsInvertible => Math.Abs(Determinant) > 1e-12;

    public Affine Invert()
    {
        double det = Determinant;
        if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("The transform flattens the picture to a line.");

        return new Affine(D / det, -B / det, -C / det, A / det, (C * F - D * E) / det, (B * E - A * F) / det);
    }

    public Pt Apply(Pt p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);

    /// <summary>Only the linear part (no move).</summary>
    public Pt ApplyVector(Pt p) => new(A * p.X + C * p.Y, B * p.X + D * p.Y);

    /// <summary>Does <paramref name="linear" /> (a scale, rotation, skew...) around <paramref name="pivot" />.</summary>
    public static Affine About(Pt pivot, Affine linear) => Translate(-pivot.X, -pivot.Y).Then(linear).Then(Translate(pivot.X, pivot.Y));

    public bool IsIdentity => Math.Abs(A - 1) < 1e-9 && Math.Abs(B) < 1e-9 && Math.Abs(C) < 1e-9 && Math.Abs(D - 1) < 1e-9 && Math.Abs(E) < 1e-9 && Math.Abs(F) < 1e-9;
}

/// <summary>Moving, scaling, turning and skewing pictures and selections.</summary>
public static class ImageTransform
{
    /// <summary>
    ///     Draws the picture moved by <paramref name="m" /> onto a transparent picture of the given size (smoothly, by looking each result pixel up
    ///     in the original).
    /// </summary>
    public static Raster Warp(Raster source, Affine m, int width, int height)
    {
        var result = new Raster(width, height);
        if (!m.IsInvertible) return result;

        var inv = m.Invert();
        // only the part of the result the picture can reach
        var corners = new[] { m.Apply(new Pt(0, 0)), m.Apply(new Pt(source.Width, 0)), m.Apply(new Pt(0, source.Height)), m.Apply(new Pt(source.Width, source.Height)) };
        var reach   = IntRect.FromCorners(corners.Min(p => p.X) - 1, corners.Min(p => p.Y) - 1, corners.Max(p => p.X) + 1, corners.Max(p => p.Y) + 1).Intersect(result.Bounds);
        for (int y = reach.Y; y < reach.Bottom; y++)
        for (int x = reach.X; x < reach.Right; x++)
        {
            var p = inv.Apply(new Pt(x + 0.5, y + 0.5));
            var c = source.SamplePremultiplied(p.X - 0.5, p.Y - 0.5);
            if (c.a > 0.5) result.SetFromPremultiplied(x, y, c.r, c.g, c.b, c.a);
        }

        return result;
    }

    public static Mask WarpMask(Mask mask, Affine m)
    {
        var r = new Raster(mask.Width, mask.Height);
        for (int i = 0; i < mask.Values.Length; i++) r.Pixels[i * 4 + 3] = mask.Values[i];
        return Mask.FromAlpha(Warp(r, m, mask.Width, mask.Height));
    }

    /// <summary>Lays <paramref name="top" /> over <paramref name="bottom" /> (same size) and gives the result.</summary>
    public static Raster Over(Raster bottom, Raster top)
    {
        var r = bottom.Clone();
        for (int p = 0; p < r.Width * r.Height; p++)
        {
            int i = p * 4;
            if (top.Pixels[i + 3] == 0) continue;

            r.Over(p % r.Width, p / r.Width, new Rgba(top.Pixels[i + 2], top.Pixels[i + 1], top.Pixels[i], top.Pixels[i + 3]), 1);
        }

        return r;
    }

    /// <summary>
    ///     Splits a layer into the part under the selection (floating, as it looks) and what is left behind (with the selected part taken
    ///     out, softly where the selection is soft). No selection: all of it floats.
    /// </summary>
    public static (Raster Floating, Raster Remainder) Lift(Raster layer, Mask? selection)
    {
        if (selection is null) return (layer.Clone(), new Raster(layer.Width, layer.Height));

        var floating  = layer.Clone();
        var remainder = layer.Clone();
        for (int p = 0; p < layer.Width * layer.Height; p++)
        {
            int m = selection.Values[p], i = p * 4 + 3;
            floating.Pixels[i]  = (byte)(layer.Pixels[i] * m / 255);
            remainder.Pixels[i] = (byte)(layer.Pixels[i] * (255 - m) / 255);
        }

        return (floating, remainder);
    }
}

/// <summary>
///     A move, scale, turn or skew of what is selected on the active layer (or of the whole layer) that is being worked on: the layer shows the
///     result while the transform changes, and nothing is final -- nor in the history -- until <see cref="Commit" />.
/// </summary>
public sealed class LayerTransform
{
    private readonly ImageDocument _doc;
    private readonly ImageLayer    _layer;
    private readonly Raster        _floating;
    private readonly Raster        _remainder;
    private readonly Mask?         _selection;
    private          IntRect       _shown;

    private LayerTransform(ImageDocument doc, ImageLayer layer, Raster floating, Raster remainder, Mask? selection, IntRect box)
    {
        _doc       = doc;
        _layer     = layer;
        _floating  = floating;
        _remainder = remainder;
        _selection = selection;
        Box        = box;
        _shown     = box;
        Matrix     = Affine.Identity;
    }

    /// <summary>The rectangle that is being transformed (the selection's, or what the layer has on it, or the whole picture).</summary>
    public IntRect Box { get; }

    public Pt Center => new(Box.X + Box.Width / 2.0, Box.Y + Box.Height / 2.0);

    public Affine Matrix { get; private set; }

    public bool IsChanged => !Matrix.IsIdentity;

    public ImageLayer Layer => _layer;

    /// <summary>Starts on the active layer and the selection; null when there is nothing to transform.</summary>
    public static LayerTransform? Begin(ImageDocument doc)
    {
        var layer = doc.ActiveLayer;
        if (layer is null) return null;

        var selection = doc.Selection;
        IntRect box;
        if (selection is not null)
        {
            box = selection.Bounds();
            if (box.IsEmpty) return null;
        }
        else
        {
            box = layer.Pixels.ContentBounds();
            if (box.IsEmpty) box = layer.Pixels.Bounds;
        }

        var (floating, remainder) = ImageTransform.Lift(layer.Pixels, selection);
        return new LayerTransform(doc, layer, floating, remainder, selection, box);
    }

    /// <summary>Shows the layer transformed by <paramref name="m" />.</summary>
    public void SetMatrix(Affine m)
    {
        Matrix = m;
        if (m.IsIdentity)
        {
            _layer.Preview       = null;
            _doc.PreviewSelection = null;
        }
        else
        {
            _layer.Preview       = Result();
            _doc.PreviewSelection = _selection is null ? null : ImageTransform.WarpMask(_selection, m);
        }

        var now = m.IsIdentity ? Box : Reach(m);
        _doc.RaiseChanged(_shown.Union(now).Inflate(2).Intersect(_layer.Pixels.Bounds));
        _shown = now;
    }

    /// <summary>Where the box ends up on the picture under a transform.</summary>
    private IntRect Reach(Affine m)
    {
        var corners = new[] { m.Apply(new Pt(Box.X, Box.Y)), m.Apply(new Pt(Box.Right, Box.Y)), m.Apply(new Pt(Box.X, Box.Bottom)), m.Apply(new Pt(Box.Right, Box.Bottom)) };
        return IntRect.FromCorners(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
    }

    private Raster Result() => ImageTransform.Over(_remainder, ImageTransform.Warp(_floating, Matrix, _layer.Pixels.Width, _layer.Pixels.Height));

    /// <summary>Makes it final (one step in the history). A transform that changes nothing is dropped.</summary>
    public void Commit(string name)
    {
        var m = Matrix;
        Cancel();
        if (m.IsIdentity) return;

        var result = ImageTransform.Over(_remainder, ImageTransform.Warp(_floating, m, _layer.Pixels.Width, _layer.Pixels.Height));
        var mask   = _selection is null ? null : ImageTransform.WarpMask(_selection, m);
        _doc.Do(name, () =>
                      {
                          _layer.Pixels = result;
                          if (mask is not null) _doc.Selection = mask;
                      });
    }

    /// <summary>Puts the layer back as it was.</summary>
    public void Cancel()
    {
        _layer.Preview        = null;
        _doc.PreviewSelection = null;
        _doc.RaiseChanged(_shown.Union(Box).Inflate(2).Intersect(_layer.Pixels.Bounds));
        _shown = Box;
    }
}

/// <summary>
///     The numbers a transform is made of -- turn, scale, skew and move -- and what dragging a handle of the box or turning it does to them.
///     The matrix is: scale, then skew, then turn, all about the middle of the box, then move.
/// </summary>
/// <param name="Angle">Degrees, clockwise.</param>
/// <param name="ScaleX">1 is as it is, negative mirrors.</param>
/// <param name="SkewX">The tangent of the angle: x moves by SkewX for each step down.</param>
/// <param name="SkewY">The tangent of the angle: y moves by SkewY for each step across.</param>
public sealed record TransformState(double Angle = 0, double ScaleX = 1, double ScaleY = 1, double SkewX = 0, double SkewY = 0, double MoveX = 0, double MoveY = 0)
{
    public const double MaxSkewDegrees = 80;

    public static readonly TransformState None = new();

    public bool IsNone => this == None;

    /// <summary>A scale a person may not make zero (a picture squeezed to a line cannot be brought back).</summary>
    public static double Away(double scale) => Math.Abs(scale) < 0.01 ? (scale < 0 ? -0.01 : 0.01) : scale;

    private static Affine Linear(double sx, double sy, double kx, double ky, double angle) =>
        Affine.Scale(sx, sy).Then(Affine.Skew(kx, ky)).Then(Affine.Rotate(angle * Math.PI / 180));

    public Affine Linear() => Linear(ScaleX, ScaleY, SkewX, SkewY, Angle);

    /// <summary>The transform of the box whose middle is <paramref name="center" />.</summary>
    public Affine Matrix(Pt center) => Affine.About(center, Linear()).Then(Affine.Translate(MoveX, MoveY));

    /// <summary>The handles of a box: 0 to 3 the corners (top-left, top-right, bottom-right, bottom-left), 4 to 7 the middles of the top, right, bottom and left edges.</summary>
    public static Pt HandlePoint(IntRect box, int handle)
    {
        double cx = box.X + box.Width / 2.0, cy = box.Y + box.Height / 2.0;
        return handle switch
               {
                   0 => new Pt(box.X, box.Y),
                   1 => new Pt(box.Right, box.Y),
                   2 => new Pt(box.Right, box.Bottom),
                   3 => new Pt(box.X, box.Bottom),
                   4 => new Pt(cx, box.Y),
                   5 => new Pt(box.Right, cy),
                   6 => new Pt(cx, box.Bottom),
                   _ => new Pt(box.X, cy)
               };
    }

    public static int Opposite(int handle) => handle < 4 ? (handle + 2) % 4 : 4 + (handle - 4 + 2) % 4;

    /// <summary>Mirrors on the screen about the middle of the box.</summary>
    public TransformState Mirrored(bool horizontal) => this with { Angle = -Angle, SkewX = -SkewX, SkewY = -SkewY, ScaleX = horizontal ? -ScaleX : ScaleX, ScaleY = horizontal ? ScaleY : -ScaleY };

    /// <summary>Turned so that what was at angle <paramref name="from" /> (seen from the box's middle) is at <paramref name="to" />.</summary>
    public TransformState Turned(Pt center, Pt from, Pt to, bool snap)
    {
        var middle = new Pt(center.X + MoveX, center.Y + MoveY);
        double turned = (Math.Atan2(to.Y - middle.Y, to.X - middle.X) - Math.Atan2(from.Y - middle.Y, from.X - middle.X)) * 180 / Math.PI;
        double angle = Angle + turned;
        if (snap) angle = Math.Round(angle / 15) * 15;

        return this with { Angle = Math.IEEERemainder(angle, 360) };
    }

    /// <summary>Moved by a distance (whole pixels, so the picture is not blurred by being resampled).</summary>
    public TransformState Moved(double dx, double dy) => this with { MoveX = MoveX + Math.Round(dx), MoveY = MoveY + Math.Round(dy) };

    /// <summary>
    ///     This state with a handle dragged to <paramref name="pointer" />. A corner scales both ways about the opposite corner (<paramref name="uniform" />
    ///     keeps the shape; <paramref name="aboutCenter" /> scales about the middle); an edge scales one way; an edge with <paramref name="skew" /> slants
    ///     instead. The anchor stays where it is on the screen whatever the turn and skew.
    /// </summary>
    public TransformState DragHandle(IntRect box, int handle, Pt pointer, bool aboutCenter = false, bool uniform = false, bool skew = false)
    {
        var center = new Pt(box.X + box.Width / 2.0, box.Y + box.Height / 2.0);
        bool corner = handle < 4;
        var anchor = aboutCenter ? center : HandlePoint(box, Opposite(handle));
        var dragged = HandlePoint(box, handle);
        var anchorWorld = Matrix(center).Apply(anchor);
        var delta = new Pt(dragged.X - anchor.X, dragged.Y - anchor.Y);
        double sx = ScaleX, sy = ScaleY, kx = SkewX, ky = SkewY;
        var rotation = Affine.Rotate(Angle * Math.PI / 180);

        if (skew && !corner)
        {
            // slants: the edge slides along itself while the opposite edge stays
            var local = rotation.Invert().ApplyVector(pointer - anchorWorld);
            double limit = Math.Tan(MaxSkewDegrees * Math.PI / 180);
            if (handle is 4 or 6)
            {
                if (Math.Abs(delta.Y * sy) > 1e-9) kx = Math.Clamp(local.X / (sy * delta.Y), -limit, limit);
            }
            else if (Math.Abs(delta.X * sx) > 1e-9)
            {
                ky = Math.Clamp(local.Y / (sx * delta.X), -limit, limit);
            }
        }
        else
        {
            // scales: the dragged handle follows the pointer in the turned and skewed frame
            var frame = Affine.Skew(kx, ky).Then(rotation);
            if (!frame.IsInvertible) return this;

            var w = frame.Invert().ApplyVector(pointer - anchorWorld);
            double nx = sx, ny = sy;
            if (handle is not (4 or 6) && Math.Abs(delta.X) > 1e-9) nx = Away(w.X / delta.X);
            if (handle is not (5 or 7) && Math.Abs(delta.Y) > 1e-9) ny = Away(w.Y / delta.Y);

            if (corner && uniform)
            {
                double fx = nx / sx, fy = ny / sy;
                double factor = Math.Abs(fx - 1) >= Math.Abs(fy - 1) ? fx : fy;
                nx = Away(sx * factor);
                ny = Away(sy * factor);
            }

            sx = nx;
            sy = ny;
        }

        // the move that keeps the anchor where it was
        var linear = Linear(sx, sy, kx, ky, Angle);
        var shift = anchorWorld - center - linear.ApplyVector(anchor - center);
        return this with { ScaleX = sx, ScaleY = sy, SkewX = kx, SkewY = ky, MoveX = shift.X, MoveY = shift.Y };
    }
}
