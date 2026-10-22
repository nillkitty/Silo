using System.Windows.Media;
using System.Windows.Media.Imaging;
using Silo.Providers.Reflection;

namespace Silo.Providers.Document;

/// <summary>
///     A picture format that Windows can decode (and, for most, encode): it reads files into <see cref="ImageSource" />
///     frames and writes frames
///     back out, so a picture opened in one format can be converted to another.
/// </summary>
public interface IImageFormat : IFileFormat, IDocumentProvider<ImageSource>, IDocumentWriter<ImageSource>
{
    /// <summary>Whether pictures can be written in this format.</summary>
    bool CanWrite => CreateEncoder() is not null;

    /// <summary>A new WPF encoder for the format; null for a format Windows reads but has no encoder for (icons).</summary>
    BitmapEncoder? CreateEncoder();
}