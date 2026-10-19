using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Telefrag.DI;

namespace Silo.Providers.Document;

[Transient(typeof(IDocument<ImageSource>))]
public class ImageDocument(IDocumentProvider<ImageSource> provider, Uri uri) : DocumentBase<ImageSource>(provider, uri);

/// <summary>
///     The base of the image formats WPF decodes by itself (through the Windows Imaging Component): one subclass per
///     format, each
///     giving its names. Reading is the same for all of them.
/// </summary>
[Transient(typeof(IDocumentProvider<ImageSource>))]
public abstract class ImageFormat : IFileFormat, IDocumentProvider<ImageSource>
{
    public abstract string ContentType { get; }

    public abstract string DisplayName { get; }

    public abstract string FileExtension { get; }

    public IEnumerable<DataVerb> GetSupportedVerbs()
    {
        throw new NotImplementedException();
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

public class JpegFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/jpeg";
    public override string DisplayName   { get; } = "JPEG Image";
    public override string FileExtension { get; } = "jpg,jpeg";
}

public class BmpFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/bmp";
    public override string DisplayName   { get; } = "Bitmap Image";
    public override string FileExtension { get; } = "bmp";
}

public class PngFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/png";
    public override string DisplayName   { get; } = "PNG Image";
    public override string FileExtension { get; } = "png";
}

public class GifFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/gif";
    public override string DisplayName   { get; } = "GIF Image";
    public override string FileExtension { get; } = "gif";
}

public class IconFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/x-icon";
    public override string DisplayName   { get; } = "Icon";
    public override string FileExtension { get; } = "ico";
}

public class TiffFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/tiff";
    public override string DisplayName   { get; } = "TIFF Image";
    public override string FileExtension { get; } = "tif,tiff";
}

/// <summary>Windows Media Photo, also called HD Photo and JPEG XR.</summary>
public class WmpFormat : ImageFormat
{
    public override string ContentType   { get; } = "image/vnd.ms-photo";
    public override string DisplayName   { get; } = "Windows Media Photo";
    public override string FileExtension { get; } = "wdp,hdp,jxr";
}