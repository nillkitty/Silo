using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Silo.Labs.Images;

/// <summary>A kind of picture file the Lab reads and writes.</summary>
/// <param name="Name">What it is called in menus.</param>
/// <param name="Extension">The extension, with the dot.</param>
/// <param name="HasAlpha">Whether the format keeps transparency.</param>
internal sealed record ImageFormat(string Name, string Extension, bool HasAlpha)
{
    public static readonly ImageFormat Png = new("PNG", ".png", true);
    public static readonly ImageFormat Jpeg = new("JPEG", ".jpg", false);
    public static readonly ImageFormat Bmp = new("BMP", ".bmp", false);
    public static readonly ImageFormat Gif = new("GIF", ".gif", true);
    public static readonly ImageFormat Ico = new("ICO", ".ico", true);

    public static readonly IReadOnlyList<ImageFormat> All = [Png, Jpeg, Bmp, Gif, Ico];

    public string Filter => $"{Name} picture (*{Extension})|*{Extension}" + (this == Jpeg ? ";*.jpeg" : string.Empty);

    /// <summary>Whether a file name is one the Lab can open.</summary>
    public static bool IsPicture(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 400) return false;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".ico" or ".tif" or ".tiff" or ".webp";
    }

    public const string OpenFilter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.tif;*.tiff|All files|*.*";
}

/// <summary>Turns rasters into the bitmaps WPF shows and files hold, and back.</summary>
internal static class ImageCodec
{
    public static BitmapSource ToBitmap(Raster r)
    {
        var bitmap = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgra32, null, r.Pixels, r.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static Raster FromBitmap(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var raster    = new Raster(converted.PixelWidth, converted.PixelHeight);
        converted.CopyPixels(raster.Pixels, raster.Stride, 0);
        return raster;
    }

    /// <summary>Reads a picture file (the first frame of an animation).</summary>
    public static Raster Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            // an icon holds several sizes: take the biggest
            var icon = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return FromBitmap(icon.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First());
        }

        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return FromBitmap(decoder.Frames[0]);
    }

    /// <summary>The picture as a file of the given format. Formats without transparency get it laid over <paramref name="background" />.</summary>
    public static byte[] Encode(Raster picture, ImageFormat format, Rgba background, int jpegQuality = 90)
    {
        if (format == ImageFormat.Png) return ImageEncoders.Png(picture);
        if (format == ImageFormat.Ico) return ImageEncoders.Ico(picture, [16, 24, 32, 48, 64, 128, 256]);

        var source = ToBitmap(format.HasAlpha ? picture : ImageEncoders.Flatten(picture, background));
        BitmapEncoder encoder = format == ImageFormat.Jpeg ? new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) }
                                : format == ImageFormat.Bmp ? new BmpBitmapEncoder()
                                : new GifBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>Puts a picture on the clipboard in two ways: as a bitmap for programs that know only those, and as PNG (which keeps transparency).</summary>
    public static void ToClipboard(Raster picture)
    {
        var data = new DataObject();
        data.SetImage(ToBitmap(picture));
        data.SetData("PNG", new MemoryStream(ImageEncoders.Png(picture)));
        Clipboard.SetDataObject(data, true);
    }

    /// <summary>The picture on the clipboard, or null.</summary>
    public static Raster? FromClipboard()
    {
        try
        {
            if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream png)
            {
                png.Position = 0;
                var decoder = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return FromBitmap(decoder.Frames[0]);
            }

            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image) return FromBitmap(image);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // the clipboard is in use by another program
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException)
        {
            // something that is not a picture, or not one that can be read
        }

        return null;
    }
}

/// <summary>Draws text into a raster with WPF's text engine.</summary>
internal static class ImageText
{
    public static readonly IReadOnlyList<string> Fonts =
        ["Segoe UI", "Arial", "Calibri", "Cambria", "Candara", "Consolas", "Cascadia Mono", "Comic Sans MS", "Courier New", "Georgia", "Impact", "Palatino Linotype", "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana"];

    /// <summary>
    ///     The text as a picture just big enough for it (transparent around the letters). Returns null for text with nothing to draw.
    /// </summary>
    public static Raster? Render(string text, string font, double size, bool bold, bool italic, bool underline, TextAlignment align, Rgba color)
    {
        if (string.IsNullOrEmpty(text)) return null;

        size = Math.Clamp(size, 2, 1000);
        var typeface = new Typeface(new FontFamily(string.IsNullOrWhiteSpace(font) ? "Segoe UI" : font), italic ? FontStyles.Italic : FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var brush    = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, 1.0) { TextAlignment = align };
        if (underline) formatted.SetTextDecorations(TextDecorations.Underline);

        int width  = (int)Math.Ceiling(Math.Max(formatted.WidthIncludingTrailingWhitespace, formatted.Width)) + 4;
        int height = (int)Math.Ceiling(formatted.Height) + 4;
        if (width < 1 || height < 1 || (long)width * height > 16_000_000) return null;

        if (align != TextAlignment.Left) formatted.MaxTextWidth = width - 4;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawText(formatted, new Point(2, 2));

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var raw = new byte[width * height * 4];
        target.CopyPixels(raw, width * 4, 0);

        // Pbgra32 is premultiplied; the alpha of the glyphs is the coverage, the color is the text color scaled by the opacity of the layer it is drawn with
        var result = new Raster(width, height);
        for (int i = 0; i < raw.Length; i += 4)
        {
            byte a = raw[i + 3];
            if (a == 0) continue;

            result.Pixels[i]     = color.B;
            result.Pixels[i + 1] = color.G;
            result.Pixels[i + 2] = color.R;
            result.Pixels[i + 3] = (byte)(a * color.A / 255);
        }

        return result;
    }
}
