using System.Globalization;

namespace Silo.Labs.Images;

// The raster engine of the Image Lab: pixels, masks, layers, drawing, filters, transforms and history, with nothing from WPF in it
// (the Lab's views turn rasters into bitmaps and back), so all of it can be tested without a window.

/// <summary>A color: straight (not premultiplied) 8-bit channels.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Rgba Transparent = new(0, 0, 0, 0);
    public static readonly Rgba Black       = new(0, 0, 0);
    public static readonly Rgba White       = new(255, 255, 255);

    /// <summary>Reads <c>#RGB</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (the # is optional) and the word "transparent".</summary>
    public static bool TryParse(string? text, out Rgba color)
    {
        color = Black;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string t = text.Trim().TrimStart('#');
        if (t.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            color = Transparent;
            return true;
        }

        if (t.Length == 3) t = string.Concat(t.Select(c => new string(c, 2)));
        if (t.Length is not (6 or 8) || !t.All(Uri.IsHexDigit)) return false;

        byte Part(int i) => byte.Parse(t.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        color = new Rgba(Part(0), Part(1), Part(2), t.Length == 8 ? Part(3) : (byte)255);
        return true;
    }

    public static Rgba ParseOr(string? text, Rgba fallback) => TryParse(text, out var c) ? c : fallback;

    /// <summary>#RRGGBB, or #RRGGBBAA when not opaque.</summary>
    public string ToHex() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    public Rgba WithAlpha(byte a) => this with { A = a };

    public static Rgba Lerp(Rgba a, Rgba b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte L(byte x, byte y) => (byte)Math.Clamp((int)Math.Round(x + (y - x) * t), 0, 255);
        return new Rgba(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B), L(a.A, b.A));
    }
}

/// <summary>A point in image coordinates (the top left corner of the top left pixel is 0,0).</summary>
public readonly record struct Pt(double X, double Y)
{
    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);
    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
    public static Pt operator *(Pt a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double DistanceTo(Pt o) => (this - o).Length;
}

/// <summary>A rectangle of whole pixels.</summary>
public readonly record struct IntRect(int X, int Y, int Width, int Height)
{
    public static readonly IntRect Empty = new(0, 0, 0, 0);

    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public IntRect Intersect(IntRect o)
    {
        int x1 = Math.Max(X, o.X), y1 = Math.Max(Y, o.Y), x2 = Math.Min(Right, o.Right), y2 = Math.Min(Bottom, o.Bottom);
        return x2 <= x1 || y2 <= y1 ? Empty : new IntRect(x1, y1, x2 - x1, y2 - y1);
    }

    public IntRect Union(IntRect o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;

        int x1 = Math.Min(X, o.X), y1 = Math.Min(Y, o.Y), x2 = Math.Max(Right, o.Right), y2 = Math.Max(Bottom, o.Bottom);
        return new IntRect(x1, y1, x2 - x1, y2 - y1);
    }

    public IntRect Inflate(int n) => new(X - n, Y - n, Width + 2 * n, Height + 2 * n);

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public static IntRect FromCorners(double x1, double y1, double x2, double y2)
    {
        int ax = (int)Math.Floor(Math.Min(x1, x2)), ay = (int)Math.Floor(Math.Min(y1, y2));
        int bx = (int)Math.Ceiling(Math.Max(x1, x2)), by = (int)Math.Ceiling(Math.Max(y1, y2));
        return new IntRect(ax, ay, bx - ax, by - ay);
    }
}

/// <summary>A picture: rows of pixels, four bytes each in the order blue, green, red, alpha (straight alpha). The order WPF's Bgra32 wants.</summary>
public sealed class Raster
{
    public const int MaxSide   = 8192;
    public const long MaxPixels = 24_000_000;

    public Raster(int width, int height)
    {
        CheckSize(width, height);
        Width  = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    public Raster(int width, int height, byte[] pixels)
    {
        CheckSize(width, height);
        if (pixels.Length != width * height * 4) throw new ArgumentException("The pixels do not fit the size.", nameof(pixels));

        Width  = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public IntRect Bounds => new(0, 0, Width, Height);
    public int Stride => Width * 4;

    public static void CheckSize(int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentException("An image is at least 1 x 1.");
        if (width > MaxSide || height > MaxSide || (long)width * height > MaxPixels)
            throw new ArgumentException($"An image can be at most {MaxSide} x {MaxSide} and {MaxPixels / 1_000_000} million pixels.");
    }

    public Raster Clone() => new(Width, Height, (byte[])Pixels.Clone());

    public void CopyFrom(Raster o)
    {
        if (o.Width != Width || o.Height != Height) throw new ArgumentException("The sizes differ.");

        Buffer.BlockCopy(o.Pixels, 0, Pixels, 0, Pixels.Length);
    }

    public void Fill(Rgba c)
    {
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i]     = c.B;
            Pixels[i + 1] = c.G;
            Pixels[i + 2] = c.R;
            Pixels[i + 3] = c.A;
        }
    }

    public Rgba Get(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return Rgba.Transparent;

        int i = (y * Width + x) * 4;
        return new Rgba(Pixels[i + 2], Pixels[i + 1], Pixels[i], Pixels[i + 3]);
    }

    public void Set(int x, int y, Rgba c)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;

        int i = (y * Width + x) * 4;
        Pixels[i]     = c.B;
        Pixels[i + 1] = c.G;
        Pixels[i + 2] = c.R;
        Pixels[i + 3] = c.A;
    }

    /// <summary>Lays <paramref name="c" /> over the pixel with the given coverage (0 to 1): the usual "over" blend of straight alpha.</summary>
    public void Over(int x, int y, Rgba c, double coverage)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;

        double sa = c.A / 255.0 * coverage;
        if (sa <= 0) return;

        int    i  = (y * Width + x) * 4;
        double da = Pixels[i + 3] / 255.0;
        double oa = sa + da * (1 - sa);
        if (oa <= 0) return;

        double k = da * (1 - sa);
        Pixels[i]     = (byte)Math.Clamp((int)Math.Round((c.B * sa + Pixels[i] * k) / oa), 0, 255);
        Pixels[i + 1] = (byte)Math.Clamp((int)Math.Round((c.G * sa + Pixels[i + 1] * k) / oa), 0, 255);
        Pixels[i + 2] = (byte)Math.Clamp((int)Math.Round((c.R * sa + Pixels[i + 2] * k) / oa), 0, 255);
        Pixels[i + 3] = (byte)Math.Clamp((int)Math.Round(oa * 255), 0, 255);
    }

    /// <summary>Takes away <paramref name="coverage" /> (0 to 1) of the pixel's opacity.</summary>
    public void Erase(int x, int y, double coverage)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || coverage <= 0) return;

        int i = (y * Width + x) * 4 + 3;
        Pixels[i] = (byte)Math.Clamp((int)Math.Round(Pixels[i] * (1 - Math.Min(1, coverage))), 0, 255);
    }

    public Raster Crop(IntRect r)
    {
        var inside = r.Intersect(Bounds);
        if (inside.IsEmpty) throw new ArgumentException("Nothing of the image is inside that rectangle.");

        var result = new Raster(inside.Width, inside.Height);
        for (int y = 0; y < inside.Height; y++)
            Buffer.BlockCopy(Pixels, ((inside.Y + y) * Width + inside.X) * 4, result.Pixels, y * result.Width * 4, inside.Width * 4);

        return result;
    }

    /// <summary>Draws <paramref name="o" /> into this picture with its top left corner at (dx, dy), replacing what is there.</summary>
    public void Paste(Raster o, int dx, int dy)
    {
        for (int y = 0; y < o.Height; y++)
        {
            int ty = y + dy;
            if (ty < 0 || ty >= Height) continue;

            int x0 = Math.Max(0, -dx), x1 = Math.Min(o.Width, Width - dx);
            if (x1 <= x0) continue;

            Buffer.BlockCopy(o.Pixels, (y * o.Width + x0) * 4, Pixels, (ty * Width + x0 + dx) * 4, (x1 - x0) * 4);
        }
    }

    /// <summary>The smallest rectangle holding every pixel that is not fully transparent; empty when there is none.</summary>
    public IntRect ContentBounds()
    {
        int minX = Width, minY = Height, maxX = -1, maxY = -1;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if (Pixels[(y * Width + x) * 4 + 3] == 0) continue;

            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        return maxX < 0 ? IntRect.Empty : new IntRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>The rectangle where this picture differs from <paramref name="o" /> (same size); empty when they are the same.</summary>
    public IntRect DifferenceBounds(Raster o)
    {
        int minX = Width, minY = Height, maxX = -1, maxY = -1;
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width * 4;
            if (Pixels.AsSpan(row, Width * 4).SequenceEqual(o.Pixels.AsSpan(row, Width * 4))) continue;

            for (int x = 0; x < Width; x++)
            {
                int i = row + x * 4;
                if (Pixels[i] == o.Pixels[i] && Pixels[i + 1] == o.Pixels[i + 1] && Pixels[i + 2] == o.Pixels[i + 2] && Pixels[i + 3] == o.Pixels[i + 3]) continue;

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        return maxX < 0 ? IntRect.Empty : new IntRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>A copy at another size, smoothly (bilinear on premultiplied color, so transparent edges do not darken).</summary>
    public Raster Resize(int width, int height)
    {
        var result = new Raster(width, height);
        double sx = (double)Width / width, sy = (double)Height / height;
        // shrinking a lot: average the pixels in a box first so thin lines do not vanish
        var source = this;
        if (sx > 2 || sy > 2)
        {
            int bx = Math.Max(1, (int)Math.Floor(sx)), by = Math.Max(1, (int)Math.Floor(sy));
            source = ImageFilters.BoxShrink(this, bx, by);
            sx     = (double)source.Width / width;
            sy     = (double)source.Height / height;
        }

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var c = source.SamplePremultiplied((x + 0.5) * sx - 0.5, (y + 0.5) * sy - 0.5);
            result.SetFromPremultiplied(x, y, c.r, c.g, c.b, c.a);
        }

        return result;
    }

    /// <summary>The bilinear sample at a point, as premultiplied channels 0 to 255 (outside the picture is transparent).</summary>
    public (double r, double g, double b, double a) SamplePremultiplied(double x, double y)
    {
        int    x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double r = 0, g = 0, b = 0, a = 0;
        for (int j = 0; j < 2; j++)
        for (int i = 0; i < 2; i++)
        {
            int px = x0 + i, py = y0 + j;
            if ((uint)px >= (uint)Width || (uint)py >= (uint)Height) continue;

            double w = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
            if (w <= 0) continue;

            int    k  = (py * Width + px) * 4;
            double pa = Pixels[k + 3] / 255.0;
            r += Pixels[k + 2] * pa * w;
            g += Pixels[k + 1] * pa * w;
            b += Pixels[k] * pa * w;
            a += Pixels[k + 3] * w;
        }

        return (r, g, b, a);
    }

    public void SetFromPremultiplied(int x, int y, double r, double g, double b, double a)
    {
        int i = (y * Width + x) * 4;
        if (a <= 0.5)
        {
            Pixels[i] = Pixels[i + 1] = Pixels[i + 2] = Pixels[i + 3] = 0;
            return;
        }

        double pa = a / 255.0;
        Pixels[i]     = (byte)Math.Clamp((int)Math.Round(b / pa), 0, 255);
        Pixels[i + 1] = (byte)Math.Clamp((int)Math.Round(g / pa), 0, 255);
        Pixels[i + 2] = (byte)Math.Clamp((int)Math.Round(r / pa), 0, 255);
        Pixels[i + 3] = (byte)Math.Clamp((int)Math.Round(a), 0, 255);
    }

    public Raster FlipHorizontal()
    {
        var r = new Raster(Width, Height);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
            Buffer.BlockCopy(Pixels, (y * Width + x) * 4, r.Pixels, (y * Width + (Width - 1 - x)) * 4, 4);

        return r;
    }

    public Raster FlipVertical()
    {
        var r = new Raster(Width, Height);
        for (int y = 0; y < Height; y++)
            Buffer.BlockCopy(Pixels, y * Width * 4, r.Pixels, (Height - 1 - y) * Width * 4, Width * 4);

        return r;
    }

    /// <summary>Turned a quarter turn: clockwise when <paramref name="clockwise" />. The result is Height wide and Width high.</summary>
    public Raster Rotate90(bool clockwise)
    {
        var r = new Raster(Height, Width);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int nx = clockwise ? Height - 1 - y : y;
            int ny = clockwise ? x : Width - 1 - x;
            Buffer.BlockCopy(Pixels, (y * Width + x) * 4, r.Pixels, (ny * r.Width + nx) * 4, 4);
        }

        return r;
    }
}

/// <summary>
///     A selection: how much of each pixel is selected, 0 to 255. Masks are never changed once made (an edit makes a new one), so history can
///     keep the old ones.
/// </summary>
public sealed class Mask
{
    private Mask(int width, int height, byte[] values)
    {
        Width  = width;
        Height = height;
        Values = values;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Values { get; }

    public static Mask Empty(int width, int height) => new(width, height, new byte[width * height]);

    public static Mask Full(int width, int height)
    {
        var v = new byte[width * height];
        Array.Fill(v, (byte)255);
        return new Mask(width, height, v);
    }

    public static Mask FromValues(int width, int height, byte[] values) => new(width, height, values);

    public byte this[int x, int y] => (uint)x < (uint)Width && (uint)y < (uint)Height ? Values[y * Width + x] : (byte)0;

    public bool IsEmpty => Values.All(v => v == 0);

    /// <summary>The rectangle that holds everything selected at least a little; empty when nothing is.</summary>
    public IntRect Bounds()
    {
        int minX = Width, minY = Height, maxX = -1, maxY = -1;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if (Values[y * Width + x] == 0) continue;

            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        return maxX < 0 ? IntRect.Empty : new IntRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    public static Mask Rectangle(int width, int height, double x1, double y1, double x2, double y2)
    {
        var r = IntRect.FromCorners(Math.Round(x1), Math.Round(y1), Math.Round(x2), Math.Round(y2)).Intersect(new IntRect(0, 0, width, height));
        var v = new byte[width * height];
        for (int y = r.Y; y < r.Bottom; y++)
            Array.Fill(v, (byte)255, y * width + r.X, r.Width);

        return new Mask(width, height, v);
    }

    public static Mask Ellipse(int width, int height, double x1, double y1, double x2, double y2)
    {
        double cx = (x1 + x2) / 2, cy = (y1 + y2) / 2, rx = Math.Abs(x2 - x1) / 2, ry = Math.Abs(y2 - y1) / 2;
        int    n  = (int)Math.Clamp(Math.Max(rx, ry) * 1.5, 24, 720);
        var    pts = new List<Pt>(n);
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            pts.Add(new Pt(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a)));
        }

        return Polygon(width, height, pts);
    }

    /// <summary>The inside of a polygon (even-odd), with smooth edges.</summary>
    public static Mask Polygon(int width, int height, IReadOnlyList<Pt> points)
    {
        var v = new byte[width * height];
        if (points.Count >= 3) Scan.Fill(width, height, [points], (x, y, cover) => v[y * width + x] = (byte)Math.Clamp((int)Math.Round(cover * 255), 0, 255));
        return new Mask(width, height, v);
    }

    public static Mask FromAlpha(Raster r)
    {
        var v = new byte[r.Width * r.Height];
        for (int i = 0; i < v.Length; i++) v[i] = r.Pixels[i * 4 + 3];
        return new Mask(r.Width, r.Height, v);
    }

    public Mask Invert()
    {
        var v = new byte[Values.Length];
        for (int i = 0; i < v.Length; i++) v[i] = (byte)(255 - Values[i]);
        return new Mask(Width, Height, v);
    }

    /// <summary>Softens the edge: each value becomes the average around it.</summary>
    public Mask Feather(int radius)
    {
        if (radius <= 0) return this;

        var gray = new Raster(Width, Height);
        for (int i = 0; i < Values.Length; i++)
        {
            gray.Pixels[i * 4 + 3] = Values[i];
        }

        var blurred = ImageFilters.Blur(gray, radius);
        var v       = new byte[Values.Length];
        for (int i = 0; i < v.Length; i++) v[i] = blurred.Pixels[i * 4 + 3];
        return new Mask(Width, Height, v);
    }

    /// <summary>Makes the selection bigger (positive) or smaller (negative) by that many pixels.</summary>
    public Mask Grow(int pixels)
    {
        if (pixels == 0) return this;

        bool grow = pixels > 0;
        var  cur  = Values;
        for (int step = 0; step < Math.Abs(pixels); step++)
        {
            var next = new byte[cur.Length];
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                byte best = cur[y * Width + x];
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    byte o = (uint)nx < (uint)Width && (uint)ny < (uint)Height ? cur[ny * Width + nx] : (byte)0;
                    best = grow ? Math.Max(best, o) : Math.Min(best, o);
                }

                next[y * Width + x] = best;
            }

            cur = next;
        }

        return new Mask(Width, Height, cur);
    }

    public Mask Combine(Mask other, SelectionMode mode)
    {
        if (other.Width != Width || other.Height != Height) throw new ArgumentException("The sizes differ.");

        var v = new byte[Values.Length];
        for (int i = 0; i < v.Length; i++)
        {
            int a = Values[i], b = other.Values[i];
            v[i] = mode switch
                   {
                       SelectionMode.Replace   => (byte)b,
                       SelectionMode.Add       => (byte)Math.Max(a, b),
                       SelectionMode.Subtract  => (byte)(a * (255 - b) / 255),
                       SelectionMode.Intersect => (byte)(a * b / 255),
                       _                       => (byte)b
                   };
        }

        return new Mask(Width, Height, v);
    }

    /// <summary>A copy at another size (nearest neighbour; used when the image is scaled).</summary>
    public Mask Resize(int width, int height)
    {
        var v = new byte[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            v[y * width + x] = Values[Math.Min(Height - 1, y * Height / height) * Width + Math.Min(Width - 1, x * Width / width)];

        return new Mask(width, height, v);
    }

    public Mask Crop(IntRect r)
    {
        var inside = r.Intersect(new IntRect(0, 0, Width, Height));
        var v      = new byte[inside.Width * inside.Height];
        for (int y = 0; y < inside.Height; y++)
            Buffer.BlockCopy(Values, (inside.Y + y) * Width + inside.X, v, y * inside.Width, inside.Width);

        return new Mask(inside.Width, inside.Height, v);
    }

    /// <summary>The mask on a canvas of another size, with its top left corner at (dx, dy).</summary>
    public Mask Place(int width, int height, int dx, int dy)
    {
        var v = new byte[width * height];
        for (int y = 0; y < Height; y++)
        {
            int ty = y + dy;
            if (ty < 0 || ty >= height) continue;

            for (int x = 0; x < Width; x++)
            {
                int tx = x + dx;
                if (tx >= 0 && tx < width) v[ty * width + tx] = Values[y * Width + x];
            }
        }

        return new Mask(width, height, v);
    }

    public Mask FlipHorizontal()
    {
        var v = new byte[Values.Length];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
            v[y * Width + Width - 1 - x] = Values[y * Width + x];

        return new Mask(Width, Height, v);
    }

    public Mask FlipVertical()
    {
        var v = new byte[Values.Length];
        for (int y = 0; y < Height; y++)
            Buffer.BlockCopy(Values, y * Width, v, (Height - 1 - y) * Width, Width);

        return new Mask(Width, Height, v);
    }

    public Mask Rotate90(bool clockwise)
    {
        var v = new byte[Values.Length];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int nx = clockwise ? Height - 1 - y : y;
            int ny = clockwise ? x : Width - 1 - x;
            v[ny * Height + nx] = Values[y * Width + x];
        }

        return new Mask(Height, Width, v);
    }

    /// <summary>
    ///     The edge of the selection as line pieces between pixels (x1, y1, x2, y2): wherever one side is selected more than half and the
    ///     other is not. For drawing the marching ants.
    /// </summary>
    public List<(int X1, int Y1, int X2, int Y2)> Outline()
    {
        var lines = new List<(int, int, int, int)>();
        bool In(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height && Values[y * Width + x] >= 128;
        for (int y = 0; y <= Height; y++)
        for (int x = 0; x <= Width; x++)
        {
            bool here = In(x, y);
            if (here != In(x, y - 1) && x < Width) lines.Add((x, y, x + 1, y));
            if (here != In(x - 1, y) && y < Height) lines.Add((x, y, x, y + 1));
        }

        return lines;
    }
}

public enum SelectionMode { Replace, Add, Subtract, Intersect }

/// <summary>Scan conversion of polygons with smooth edges: calls back with the coverage (0 to 1) of every pixel the polygon touches.</summary>
internal static class Scan
{
    private const int Sub = 4;

    /// <summary>Fills the even-odd inside of the closed outlines. <paramref name="emit" /> is called once for each pixel with some coverage.</summary>
    public static void Fill(int width, int height, IEnumerable<IReadOnlyList<Pt>> outlines, Action<int, int, double> emit)
    {
        var edges = new List<(double X1, double Y1, double X2, double Y2)>();
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var outline in outlines)
        {
            int n = outline.Count;
            for (int i = 0; i < n; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % n];
                if (a.Y == b.Y) continue;

                edges.Add(a.Y < b.Y ? (a.X, a.Y, b.X, b.Y) : (b.X, b.Y, a.X, a.Y));
                minY = Math.Min(minY, Math.Min(a.Y, b.Y));
                maxY = Math.Max(maxY, Math.Max(a.Y, b.Y));
            }
        }

        if (edges.Count == 0) return;

        int y0 = Math.Max(0, (int)Math.Floor(minY)), y1 = Math.Min(height - 1, (int)Math.Ceiling(maxY));
        var row = new double[width + 2];
        var xs  = new List<double>();
        for (int y = y0; y <= y1; y++)
        {
            Array.Clear(row);
            bool any = false;
            for (int s = 0; s < Sub; s++)
            {
                double sy = y + (s + 0.5) / Sub;
                xs.Clear();
                foreach (var e in edges)
                    if (sy >= e.Y1 && sy < e.Y2)
                        xs.Add(e.X1 + (sy - e.Y1) / (e.Y2 - e.Y1) * (e.X2 - e.X1));

                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    double a = Math.Clamp(xs[k], 0, width), b = Math.Clamp(xs[k + 1], 0, width);
                    if (b <= a) continue;

                    any = true;
                    int ia = (int)Math.Floor(a), ib = (int)Math.Floor(b);
                    if (ia == ib)
                        row[ia] += (b - a) / Sub;
                    else
                    {
                        row[ia] += (ia + 1 - a) / Sub;
                        for (int x = ia + 1; x < ib; x++) row[x] += 1.0 / Sub;
                        if (ib < width) row[ib] += (b - ib) / Sub;
                    }
                }
            }

            if (!any) continue;

            for (int x = 0; x < width; x++)
                if (row[x] > 0.001)
                    emit(x, y, Math.Min(1, row[x]));
        }
    }
}
