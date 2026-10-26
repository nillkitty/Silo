namespace Silo.Labs.Images;

public enum BrushPattern { Round, Soft, Square, Diamond, Spray, Chalk, Hatch, Dots }

/// <summary>How a brush (or the eraser) puts down paint.</summary>
/// <param name="Size">Across, in pixels (1 to 500).</param>
/// <param name="Pattern">The shape and texture of the tip.</param>
/// <param name="Opacity">How much of the color each dab lays down, 0 to 1.</param>
/// <param name="Spacing">The distance between dabs as a fraction of the size (0.05 to 2).</param>
public sealed record BrushSettings(int Size = 12, BrushPattern Pattern = BrushPattern.Round, double Opacity = 1, double Spacing = 0.2)
{
    public static readonly IReadOnlyList<string> PatternNames = Enum.GetNames<BrushPattern>();

    public BrushSettings Normalized() => this with { Size = Math.Clamp(Size, 1, 500), Opacity = Math.Clamp(Opacity, 0.01, 1), Spacing = Math.Clamp(Spacing, 0.05, 2) };
}

/// <summary>
///     One stroke with a brush: dabs of the tip put along the path the pointer takes, close enough together to look continuous. The stroke
///     paints into <see cref="Target" />, only where the selection allows, and remembers which part it touched.
/// </summary>
public sealed class BrushStroke
{
    private readonly BrushSettings _brush;
    private readonly Mask?         _selection;
    private readonly Rgba          _color;
    private readonly bool          _erase;
    private          int           _dab;
    private          Pt            _last;
    private          double        _carry;
    private          bool          _started;

    public BrushStroke(Raster target, Mask? selection, BrushSettings brush, Rgba color, bool erase)
    {
        Target     = target;
        _selection = selection;
        _brush     = brush.Normalized();
        _color     = color;
        _erase     = erase;
    }

    public Raster Target { get; }

    /// <summary>The part of the picture the stroke touched so far.</summary>
    public IntRect Dirty { get; private set; } = IntRect.Empty;

    public void Begin(Pt at)
    {
        _started = true;
        _last    = at;
        _carry   = 0;
        Dab(at);
    }

    /// <summary>Carries the stroke on to a point: dabs along the way, spaced evenly whatever the pointer's speed.</summary>
    public void LineTo(Pt to)
    {
        if (!_started)
        {
            Begin(to);
            return;
        }

        double spacing = Math.Max(1, _brush.Size * _brush.Spacing);
        double length  = _last.DistanceTo(to);
        if (length <= 0) return;

        double d = spacing - _carry;
        while (d <= length)
        {
            double t = d / length;
            Dab(new Pt(_last.X + (to.X - _last.X) * t, _last.Y + (to.Y - _last.Y) * t));
            d += spacing;
        }

        _carry = length - (d - spacing);
        _last  = to;
    }

    /// <summary>The coverage of the tip (0 to 1) at a pixel whose centre is (dx, dy) from the middle of the dab.</summary>
    public static double Coverage(BrushPattern pattern, double radius, double dx, double dy, int px, int py, int dab)
    {
        double dist = Math.Sqrt(dx * dx + dy * dy);
        switch (pattern)
        {
            case BrushPattern.Round:
                return Math.Clamp(radius - dist + 0.5, 0, 1);
            case BrushPattern.Soft:
            {
                double t = dist / Math.Max(0.5, radius);
                if (t >= 1) return 0;

                double s = 1 - t;
                return s * s * (3 - 2 * s);
            }
            case BrushPattern.Square:
                return Math.Clamp(radius - Math.Abs(dx) + 0.5, 0, 1) * Math.Clamp(radius - Math.Abs(dy) + 0.5, 0, 1);
            case BrushPattern.Diamond:
                return Math.Clamp(radius - (Math.Abs(dx) + Math.Abs(dy)) + 0.5, 0, 1);
            case BrushPattern.Spray:
            {
                if (dist > radius) return 0;

                double density = 0.45 * (1 - dist / Math.Max(0.5, radius));
                return Hash(px, py, dab) < density ? 1 : 0;
            }
            case BrushPattern.Chalk:
            {
                double round = Math.Clamp(radius - dist + 0.5, 0, 1);
                if (round <= 0) return 0;

                return round * (Hash(px, py, 0) > 0.4 ? 1 : 0.15);
            }
            case BrushPattern.Hatch:
            {
                double round = Math.Clamp(radius - dist + 0.5, 0, 1);
                return round > 0 && ((px + py) & 3) < 2 ? round : 0;
            }
            case BrushPattern.Dots:
            {
                double round = Math.Clamp(radius - dist + 0.5, 0, 1);
                return round > 0 && (((px >> 1) + (py >> 1)) & 1) == 0 ? round : 0;
            }
            default:
                return 0;
        }
    }

    private static double Hash(int x, int y, int salt)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + salt * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (double)0x1000000;
        }
    }

    private void Dab(Pt at)
    {
        _dab++;
        double radius = _brush.Size / 2.0;
        int    x0 = (int)Math.Floor(at.X - radius - 1), x1 = (int)Math.Ceiling(at.X + radius + 1);
        int    y0 = (int)Math.Floor(at.Y - radius - 1), y1 = (int)Math.Ceiling(at.Y + radius + 1);
        var    area = new IntRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1).Intersect(Target.Bounds);
        if (area.IsEmpty) return;

        for (int y = area.Y; y < area.Bottom; y++)
        for (int x = area.X; x < area.Right; x++)
        {
            double cover = Coverage(_brush.Pattern, _brush.Size == 1 ? 0.5 : radius, x + 0.5 - at.X, y + 0.5 - at.Y, x, y, _dab);
            if (cover <= 0) continue;

            cover *= _brush.Opacity;
            if (_selection is not null) cover *= _selection.Values[y * Target.Width + x] / 255.0;
            if (cover <= 0) continue;

            if (_erase) Target.Erase(x, y, cover);
            else Target.Over(x, y, _color, cover);
        }

        Dirty = Dirty.Union(area);
    }
}

public enum GradientKind { Linear, Radial, Reflected, Angular, Diamond }

public static class Gradients
{
    /// <summary>Paints a gradient over the picture (where the selection allows), blending with what is there by the colors' alpha.</summary>
    public static void Fill(Raster target, Mask? selection, Pt from, Pt to, Rgba start, Rgba end, GradientKind kind)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.5) return;

        double angle0 = Math.Atan2(dy, dx);
        for (int y = 0; y < target.Height; y++)
        for (int x = 0; x < target.Width; x++)
        {
            double sel = selection is null ? 1 : selection.Values[y * target.Width + x] / 255.0;
            if (sel <= 0) continue;

            double px = x + 0.5 - from.X, py = y + 0.5 - from.Y;
            double t;
            switch (kind)
            {
                case GradientKind.Linear:
                    t = (px * dx + py * dy) / (len * len);
                    break;
                case GradientKind.Radial:
                    t = Math.Sqrt(px * px + py * py) / len;
                    break;
                case GradientKind.Reflected:
                    t = Math.Abs((px * dx + py * dy) / (len * len));
                    break;
                case GradientKind.Angular:
                    t = Math.IEEERemainder(Math.Atan2(py, px) - angle0, 2 * Math.PI) / (2 * Math.PI);
                    if (t < 0) t += 1;
                    break;
                case GradientKind.Diamond:
                    t = (Math.Abs(px) + Math.Abs(py)) / len;
                    break;
                default:
                    t = 0;
                    break;
            }

            var c = Rgba.Lerp(start, end, t);
            target.Over(x, y, c, sel);
        }
    }
}

/// <summary>A point of a bezier path with the handles that bend the curve on either side of it (absolute positions; the same as the point for none).</summary>
public readonly record struct PathPoint(Pt At, Pt In, Pt Out)
{
    public static PathPoint Corner(Pt at) => new(at, at, at);

    /// <summary>A smooth point: the handle going out is <paramref name="handle" />, the one coming in is its mirror.</summary>
    public static PathPoint Smooth(Pt at, Pt handle) => new(at, new Pt(2 * at.X - handle.X, 2 * at.Y - handle.Y), handle);

    public bool HasHandles => In != At || Out != At;
}

public static class BezierPaths
{
    /// <summary>The path as a polyline fine enough that the straight pieces cannot be told from the curve.</summary>
    public static List<Pt> Flatten(IReadOnlyList<PathPoint> points, bool closed, double tolerance = 0.25)
    {
        var result = new List<Pt>();
        if (points.Count == 0) return result;

        result.Add(points[0].At);
        int segments = closed ? points.Count : points.Count - 1;
        for (int i = 0; i < segments; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            if (!a.HasHandles && !b.HasHandles)
            {
                result.Add(b.At);
                continue;
            }

            double approx = a.At.DistanceTo(a.Out) + a.Out.DistanceTo(b.In) + b.In.DistanceTo(b.At);
            int    steps  = (int)Math.Clamp(Math.Ceiling(Math.Sqrt(approx / Math.Max(0.05, tolerance)) * 1.5), 4, 400);
            for (int s = 1; s <= steps; s++)
            {
                double t = (double)s / steps, u = 1 - t;
                double x = u * u * u * a.At.X + 3 * u * u * t * a.Out.X + 3 * u * t * t * b.In.X + t * t * t * b.At.X;
                double y = u * u * u * a.At.Y + 3 * u * u * t * a.Out.Y + 3 * u * t * t * b.In.Y + t * t * t * b.At.Y;
                result.Add(new Pt(x, y));
            }
        }

        return result;
    }

    /// <summary>Strokes the path with a brush.</summary>
    public static IntRect Stroke(Raster target, Mask? selection, IReadOnlyList<PathPoint> points, bool closed, BrushSettings brush, Rgba color)
    {
        var line = Flatten(points, closed);
        if (line.Count == 0) return IntRect.Empty;

        var stroke = new BrushStroke(target, selection, brush, color, false);
        stroke.Begin(line[0]);
        for (int i = 1; i < line.Count; i++) stroke.LineTo(line[i]);
        return stroke.Dirty;
    }

    /// <summary>Fills the inside of the (closed) path.</summary>
    public static void Fill(Raster target, Mask? selection, IReadOnlyList<PathPoint> points, Rgba color)
    {
        var outline = Flatten(points, true);
        if (outline.Count < 3) return;

        Scan.Fill(target.Width, target.Height, [outline], (x, y, cover) =>
                                                          {
                                                              if (selection is not null) cover *= selection.Values[y * target.Width + x] / 255.0;
                                                              target.Over(x, y, color, cover);
                                                          });
    }
}
