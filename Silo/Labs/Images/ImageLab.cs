using System.IO;
using Telefrag.DI;

namespace Silo.Labs.Images;

public static class ImageLabParameters
{
    public const string Width = "width";
    public const string Height = "height";
    public const string Background = "background";
    public const string JpegQuality = "jpegQuality";
    public const string FlattenOn = "flattenOn";
}

/// <summary>
///     Paints and edits pictures in layers: draw with brushes, select with marquees and lassos, move, scale, turn and skew, set text, fill with
///     gradients, draw bezier paths, filter, and keep the result in the Silo or write it to PNG, JPEG, BMP, GIF and ICO files.
/// </summary>
[Singleton(typeof(ILab))]
public class ImageLab : LabBase<ImageData>
{
    public override string Description => "Paint and edit pictures in layers: brushes, an eraser, gradients, bezier paths and text, selections by marquee and lasso, moving, scaling, turning and skewing, " +
                                          "filters, and undo. Pictures are kept in the Silo (with their layers) and can be brought in from, and written to, PNG, JPEG, BMP, GIF and ICO files.";

    public override string IconKey => "Design";

    public override string Id => "image";

    public override string Name => "Image Lab";

    public override int Order => 80;

    protected override IEnumerable<LabParameter> DefineParameters()
    {
        yield return new LabParameter(ImageLabParameters.Width, "Width of a new image", LabParameterKind.Text, "800", "In pixels (1 to 8192).", "New images", hint: "800");
        yield return new LabParameter(ImageLabParameters.Height, "Height of a new image", LabParameterKind.Text, "600", "In pixels (1 to 8192).", "New images", hint: "600");
        yield return new LabParameter(ImageLabParameters.Background, "Background of a new image", LabParameterKind.Choice, "White", "What the first layer is filled with.", "New images", ["White", "Black", "Gray", "Transparent"]);
        yield return new LabParameter(ImageLabParameters.JpegQuality, "JPEG quality", LabParameterKind.Text, "90", "1 (small and rough) to 100 (large and fine).", "Writing files", hint: "90");
        yield return new LabParameter(ImageLabParameters.FlattenOn, "Behind transparency (JPEG, BMP)", LabParameterKind.Choice, "White", "Formats with no transparency get the picture laid over this color.", "Writing files", ["White", "Black", "Gray"]);
    }

    public override LabSession<ImageData> CreateSession() => new ImageLabSession(this);

    internal static Rgba ColorOf(string? name) => name switch
                                                 {
                                                     "Black"       => Rgba.Black,
                                                     "Gray"        => new Rgba(128, 128, 128),
                                                     "Transparent" => Rgba.Transparent,
                                                     _             => Rgba.White
                                                 };

    private static int Size(string text, int fallback) => int.TryParse(text.Trim(), out int n) ? Math.Clamp(n, 1, Raster.MaxSide) : fallback;

    /// <summary>A new image with the Lab's own defaults.</summary>
    public ImageData Blank()
    {
        int w = Size(Parameters[ImageLabParameters.Width].Text, 800), h = Size(Parameters[ImageLabParameters.Height].Text, 600);
        return ImageDocument.Blank(w, h, ColorOf(Parameters[ImageLabParameters.Background].Text)).ToData();
    }

    internal Rgba FlattenColor => ColorOf(Parameters[ImageLabParameters.FlattenOn].Text);

    internal int JpegQuality => int.TryParse(Parameters[ImageLabParameters.JpegQuality].Text.Trim(), out int q) ? Math.Clamp(q, 1, 100) : 90;

    protected override bool TryConvert(object? candidate, out ImageData? value)
    {
        switch (candidate)
        {
            case ImageData data:
                value = data;
                return true;
            // asked of every node when a menu is built: only files that are plainly pictures are taken (the extension is looked at, not the disk)
            case string path when ImageFormat.IsPicture(path):
                value = new ImageData { ImportPath = path };
                return true;
            case Uri { IsFile: true } uri when ImageFormat.IsPicture(uri.LocalPath):
                value = new ImageData { ImportPath = uri.LocalPath };
                return true;
            case FileInfo file when ImageFormat.IsPicture(file.Name):
                value = new ImageData { ImportPath = file.FullName };
                return true;
            default:
                value = null;
                return false;
        }
    }

    /// <summary>Text typed in the value box: the path of a picture, or a size (<c>640x480</c>) for a new blank image.</summary>
    public override bool TryParse(string text, out ImageData? value)
    {
        value = null;
        text  = text.Trim().Trim('"');
        if (text.Length == 0) return false;

        var parts = text.Split(new[] { 'x', 'X', '*', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && w is >= 1 and <= Raster.MaxSide && h is >= 1 and <= Raster.MaxSide && (long)w * h <= Raster.MaxPixels)
        {
            value = ImageDocument.Blank(w, h, ColorOf(Parameters[ImageLabParameters.Background].Text)).ToData();
            return true;
        }

        if (ImageFormat.IsPicture(text))
        {
            value = new ImageData { ImportPath = text };
            return true;
        }

        return false;
    }
}
