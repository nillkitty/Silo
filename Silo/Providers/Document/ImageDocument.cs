using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Silo.Providers.Data.Source;
using Telefrag.DI;

namespace Silo.Providers.Document;

[Transient(typeof(IDocument<ImageSource>))]
public class ImageDocument(IDocumentProvider<ImageSource> provider, Uri uri) : DocumentBase<ImageSource>(provider, uri);

/// <summary>
///     The base of the image formats WPF decodes by itself (through the Windows Imaging Component): one subclass per
///     format, each
///     giving its names. Reading is the same for all of them.
/// </summary>
public abstract class ImageFormat : IImageFormat
{
    public abstract string DisplayName { get; }

    public abstract string FileExtension { get; }
    public abstract string ContentType   { get; }

    public IEnumerable<DataVerb> GetSupportedVerbs()
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public virtual BitmapEncoder? CreateEncoder()
    {
        return null;
    }

    /// <summary>
    ///     Writes the frames as one picture of this format (a still format takes the first frame only, as the encoder does: an
    ///     animated GIF and
    ///     a multi-page TIFF take all of them). Throws <see cref="NotSupportedException" /> for a format with no encoder.
    /// </summary>
    public void Write(Stream stream, IEnumerable<ImageSource> documents)
    {
        stream.Required();
        var encoder = CreateEncoder() ?? throw new NotSupportedException($"{DisplayName} can be read but not written.");
        foreach (var image in documents)
            if (image is BitmapSource bitmap)
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

        if (encoder.Frames.Count == 0) throw new InvalidOperationException("There is no picture to write.");
        encoder.Save(stream);
    }

    /// <summary>
    ///     Decodes every frame of the image in <paramref name="stream" />, from its current position: a still picture has one,
    ///     an animated
    ///     GIF one per animation frame, a multi-page TIFF one per page, an icon one per size. The frames are fully loaded and
    ///     frozen,
    ///     so they can be used on any thread and the stream can be closed afterwards. Which decoder reads it is decided from
    ///     the
    ///     content, not from this format's name or extension. Throws <see cref="NotSupportedException" /> if the content is
    ///     not an
    ///     image Windows has a codec for, and <see cref="FileFormatException" /> if it is damaged.
    /// </summary>
    public IEnumerable<ImageSource> Read(FileStream stream)
    {
        stream.Required();

        // PreservePixelFormat keeps 16-bit, gray and CMYK pictures as they are instead of converting them to Bgra32;
        // OnLoad reads everything now, so the frames do not hold on to the stream.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        var frames = new List<ImageSource>(decoder.Frames.Count);
        foreach (var frame in decoder.Frames)
        {
            if (!frame.IsFrozen && frame.CanFreeze)
                frame.Freeze();
            frames.Add(frame);
        }

        return frames;
    }
}

[Singleton(typeof(IImageFormat))]
public class JpegFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/jpeg";
    public override string DisplayName   { get; } = "JPEG Image";
    public override string FileExtension { get; } = "jpg,jpeg";

    public override BitmapEncoder? CreateEncoder()
    {
        return new JpegBitmapEncoder();
    }
}

[Singleton(typeof(IImageFormat))]
public class BmpFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/bmp";
    public override string DisplayName   { get; } = "Bitmap Image";
    public override string FileExtension { get; } = "bmp";

    public override BitmapEncoder? CreateEncoder()
    {
        return new BmpBitmapEncoder();
    }
}

[Singleton(typeof(IImageFormat))]
public class PngFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/png";
    public override string DisplayName   { get; } = "PNG Image";
    public override string FileExtension { get; } = "png";

    public override BitmapEncoder? CreateEncoder()
    {
        return new PngBitmapEncoder();
    }
}

[Singleton(typeof(IImageFormat))]
public class GifFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/gif";
    public override string DisplayName   { get; } = "GIF Image";
    public override string FileExtension { get; } = "gif";

    public override BitmapEncoder? CreateEncoder()
    {
        return new GifBitmapEncoder();
    }
}

[Singleton(typeof(IImageFormat))]
public class IconFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/x-icon";
    public override string DisplayName   { get; } = "Icon";
    public override string FileExtension { get; } = "ico";
}

[Singleton(typeof(IImageFormat))]
public class TiffFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/tiff";
    public override string DisplayName   { get; } = "TIFF Image";
    public override string FileExtension { get; } = "tif,tiff";

    public override BitmapEncoder? CreateEncoder()
    {
        return new TiffBitmapEncoder();
    }
}

/// <summary>Windows Media Photo, also called HD Photo and JPEG XR.</summary>
[Singleton(typeof(IImageFormat))]
public class WmpFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/vnd.ms-photo";
    public override string DisplayName   { get; } = "Windows Media Photo";
    public override string FileExtension { get; } = "wdp,hdp,jxr";

    public override BitmapEncoder? CreateEncoder()
    {
        return new WmpBitmapEncoder();
    }
}

/// <summary>WebP: read through the Windows WebP codec (installed with Windows 10 and later); Windows has no encoder for it.</summary>
[Singleton(typeof(IImageFormat))]
public class WebpFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/webp";
    public override string DisplayName   { get; } = "WebP Image";
    public override string FileExtension { get; } = "webp";
}
