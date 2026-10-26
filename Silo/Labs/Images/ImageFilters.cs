namespace Silo.Labs.Images;

/// <summary>The filters of the Image Lab. Each takes a picture and gives a new one; <see cref="Masked" /> limits one to the selection.</summary>
public static class ImageFilters
{
    /// <summary>The names the Lab lists them under, with what each is.</summary>
    public static readonly IReadOnlyList<string> Names = ["Grayscale", "Invert", "Sepia", "Brightness/Contrast", "Blur", "Sharpen", "Pixelate", "Threshold"];

    /// <summary>Runs a filter on a picture and lets only the selected part of the result through (a soft edge blends).</summary>
    public static Raster Masked(Raster source, Mask? mask, Func<Raster, Raster> filter)
    {
        var filtered = filter(source);
        if (mask is null) return filtered;

        var result = source.Clone();
        int n      = source.Width * source.Height;
        for (int p = 0; p < n; p++)
        {
            int m = mask.Values[p];
            if (m == 0) continue;

            int i = p * 4;
            if (m == 255)
            {
                result.Pixels[i]     = filtered.Pixels[i];
                result.Pixels[i + 1] = filtered.Pixels[i + 1];
                result.Pixels[i + 2] = filtered.Pixels[i + 2];
                result.Pixels[i + 3] = filtered.Pixels[i + 3];
                continue;
            }

            // blend on premultiplied color so a half-transparent pixel does not shift its hue
            double aa = source.Pixels[i + 3] / 255.0, ba = filtered.Pixels[i + 3] / 255.0, t = m / 255.0;
            double oa = aa * (1 - t) + ba * t;
            if (oa <= 0)
            {
                result.Pixels[i] = result.Pixels[i + 1] = result.Pixels[i + 2] = result.Pixels[i + 3] = 0;
                continue;
            }

            for (int c = 0; c < 3; c++)
            {
                double v = (source.Pixels[i + c] * aa * (1 - t) + filtered.Pixels[i + c] * ba * t) / oa;
                result.Pixels[i + c] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
            }

            result.Pixels[i + 3] = (byte)Math.Clamp((int)Math.Round(oa * 255), 0, 255);
        }

        return result;
    }

    private static Raster Map(Raster s, Func<byte, byte, byte, (byte b, byte g, byte r)> f)
    {
        var r = new Raster(s.Width, s.Height);
        for (int i = 0; i < s.Pixels.Length; i += 4)
        {
            var (b, g, red) = f(s.Pixels[i], s.Pixels[i + 1], s.Pixels[i + 2]);
            r.Pixels[i]     = b;
            r.Pixels[i + 1] = g;
            r.Pixels[i + 2] = red;
            r.Pixels[i + 3] = s.Pixels[i + 3];
        }

        return r;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private static double Luma(byte b, byte g, byte r) => 0.299 * r + 0.587 * g + 0.114 * b;

    public static Raster Grayscale(Raster s) => Map(s, (b, g, r) =>
                                                       {
                                                           byte y = Clamp(Luma(b, g, r));
                                                           return (y, y, y);
                                                       });

    public static Raster Invert(Raster s) => Map(s, (b, g, r) => ((byte)(255 - b), (byte)(255 - g), (byte)(255 - r)));

    public static Raster Sepia(Raster s) => Map(s, (b, g, r) => (Clamp(0.272 * r + 0.534 * g + 0.131 * b), Clamp(0.349 * r + 0.686 * g + 0.168 * b), Clamp(0.393 * r + 0.769 * g + 0.189 * b)));

    /// <summary>Brightness and contrast, each from -100 to 100 (0 changes nothing).</summary>
    public static Raster BrightnessContrast(Raster s, double brightness, double contrast)
    {
        brightness = Math.Clamp(brightness, -100, 100);
        contrast   = Math.Clamp(contrast, -100, 100);
        double factor = contrast >= 0 ? 1 / (1 - contrast / 100 * 0.99) : 1 + contrast / 100;
        double shift  = brightness / 100 * 255;
        var    lut    = new byte[256];
        for (int v = 0; v < 256; v++) lut[v] = Clamp((v - 127.5) * factor + 127.5 + shift);

        return Map(s, (b, g, r) => (lut[b], lut[g], lut[r]));
    }

    /// <summary>Everything lighter than <paramref name="level" /> (0 to 255) becomes white, the rest black.</summary>
    public static Raster Threshold(Raster s, int level)
    {
        return Map(s, (b, g, r) =>
                      {
                          byte v = Luma(b, g, r) >= level ? (byte)255 : (byte)0;
                          return (v, v, v);
                      });
    }

    /// <summary>Squares of <paramref name="block" /> pixels, each the average of the pixels under it.</summary>
    public static Raster Pixelate(Raster s, int block)
    {
        block = Math.Clamp(block, 1, 512);
        if (block == 1) return s.Clone();

        var r = new Raster(s.Width, s.Height);
        for (int by = 0; by < s.Height; by += block)
        for (int bx = 0; bx < s.Width; bx += block)
        {
            int    x2 = Math.Min(s.Width, bx + block), y2 = Math.Min(s.Height, by + block);
            double sr = 0, sg = 0, sb = 0, sa = 0;
            int    n  = (x2 - bx) * (y2 - by);
            for (int y = by; y < y2; y++)
            for (int x = bx; x < x2; x++)
            {
                int    i = (y * s.Width + x) * 4;
                double a = s.Pixels[i + 3];
                sb += s.Pixels[i] * a;
                sg += s.Pixels[i + 1] * a;
                sr += s.Pixels[i + 2] * a;
                sa += a;
            }

            byte oa = Clamp(sa / n);
            byte ob = sa > 0 ? Clamp(sb / sa) : (byte)0, og = sa > 0 ? Clamp(sg / sa) : (byte)0, orr = sa > 0 ? Clamp(sr / sa) : (byte)0;
            for (int y = by; y < y2; y++)
            for (int x = bx; x < x2; x++)
            {
                int i = (y * s.Width + x) * 4;
                r.Pixels[i]     = ob;
                r.Pixels[i + 1] = og;
                r.Pixels[i + 2] = orr;
                r.Pixels[i + 3] = oa;
            }
        }

        return r;
    }

    /// <summary>A Gaussian-like blur (three passes of a box blur) of radius <paramref name="radius" /> pixels, on premultiplied color so edges stay clean.</summary>
    public static Raster Blur(Raster s, double radius)
    {
        if (radius < 0.5) return s.Clone();

        int n = s.Width * s.Height;
        var ch = new float[4][];
        for (int c = 0; c < 4; c++) ch[c] = new float[n];

        for (int p = 0; p < n; p++)
        {
            float a = s.Pixels[p * 4 + 3] / 255f;
            ch[0][p] = s.Pixels[p * 4] * a;
            ch[1][p] = s.Pixels[p * 4 + 1] * a;
            ch[2][p] = s.Pixels[p * 4 + 2] * a;
            ch[3][p] = s.Pixels[p * 4 + 3];
        }

        foreach (int size in BoxSizes(radius, 3))
        {
            int r = (size - 1) / 2;
            for (int c = 0; c < 4; c++)
            {
                BoxPass(ch[c], s.Width, s.Height, r, true);
                BoxPass(ch[c], s.Width, s.Height, r, false);
            }
        }

        var result = new Raster(s.Width, s.Height);
        for (int p = 0; p < n; p++) result.SetFromPremultiplied(p % s.Width, p / s.Width, ch[2][p], ch[1][p], ch[0][p], ch[3][p]);
        return result;
    }

    private static int[] BoxSizes(double sigma, int boxes)
    {
        double ideal = Math.Sqrt(12 * sigma * sigma / boxes + 1);
        int    wl    = (int)Math.Floor(ideal);
        if (wl % 2 == 0) wl--;

        int    wu = wl + 2;
        double m  = (12 * sigma * sigma - boxes * wl * wl - 4 * boxes * wl - 3 * boxes) / (-4 * wl - 4);
        int    mi = (int)Math.Round(m);
        var    sizes = new int[boxes];
        for (int i = 0; i < boxes; i++) sizes[i] = Math.Max(1, i < mi ? wl : wu);
        return sizes;
    }

    private static void BoxPass(float[] data, int w, int h, int r, bool horizontal)
    {
        if (r <= 0) return;

        int lines = horizontal ? h : w, len = horizontal ? w : h;
        var line  = new float[len];
        float inv = 1f / (2 * r + 1);
        for (int l = 0; l < lines; l++)
        {
            for (int i = 0; i < len; i++) line[i] = horizontal ? data[l * w + i] : data[i * w + l];

            float sum = 0;
            for (int i = -r; i <= r; i++) sum += line[Math.Clamp(i, 0, len - 1)];

            for (int i = 0; i < len; i++)
            {
                float v = sum * inv;
                if (horizontal) data[l * w + i] = v;
                else data[i * w + l] = v;

                sum += line[Math.Min(len - 1, i + r + 1)] - line[Math.Max(0, i - r)];
            }
        }
    }

    /// <summary>Unsharp mask: the difference between the picture and a blur of it, added back <paramref name="amount" /> times (0 to 5).</summary>
    public static Raster Sharpen(Raster s, double radius, double amount)
    {
        var blurred = Blur(s, Math.Max(0.5, radius));
        var r       = s.Clone();
        for (int i = 0; i < s.Pixels.Length; i += 4)
        {
            if (s.Pixels[i + 3] == 0) continue;

            for (int c = 0; c < 3; c++) r.Pixels[i + c] = Clamp(s.Pixels[i + c] + amount * (s.Pixels[i + c] - blurred.Pixels[i + c]));
        }

        return r;
    }

    /// <summary>The average of each <paramref name="bx" /> x <paramref name="by" /> block of pixels (the picture made smaller by those factors).</summary>
    internal static Raster BoxShrink(Raster s, int bx, int by)
    {
        int w = Math.Max(1, s.Width / bx), h = Math.Max(1, s.Height / by);
        var r = new Raster(w, h);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double sr = 0, sg = 0, sb = 0, sa = 0;
            int    x0 = x * s.Width / w, x1 = Math.Max(x0 + 1, (x + 1) * s.Width / w), y0 = y * s.Height / h, y1 = Math.Max(y0 + 1, (y + 1) * s.Height / h);
            int    n  = 0;
            for (int yy = y0; yy < y1 && yy < s.Height; yy++)
            for (int xx = x0; xx < x1 && xx < s.Width; xx++)
            {
                int i = (yy * s.Width + xx) * 4;
                double a = s.Pixels[i + 3];
                sb += s.Pixels[i] * a;
                sg += s.Pixels[i + 1] * a;
                sr += s.Pixels[i + 2] * a;
                sa += a;
                n++;
            }

            if (n == 0) continue;

            r.SetFromPremultiplied(x, y, sr / 255.0 / n, sg / 255.0 / n, sb / 255.0 / n, sa / n);
        }

        return r;
    }
}
