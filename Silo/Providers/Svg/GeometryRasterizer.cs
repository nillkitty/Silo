using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Silo.Providers;

public static class GeometryRasterizer
{
    /// <summary>
    ///     Renders a Geometry to an image file. If the path ends in .ico, produces a multi-size icon.
    ///     Otherwise, saves a single PNG at the specified dimensions.
    /// </summary>
    public static void ExportToFile(Geometry geometry,      string outputPath,     Brush stroke, double strokeThickness, Brush fill = null,
                                    int      pngWidth = 32, int    pngHeight = 32, IEnumerable<int> icoSizes = null)
    {
        string ext = Path.GetExtension(outputPath).ToLowerInvariant();

        if (ext == ".ico")
        {
            var sizes      = (icoSizes ?? new[] { 16, 24, 32, 48, 64, 128, 256 }).Distinct().OrderBy(s => s).ToList();
            var pngBuffers = new List<(int Size, byte[] Data)>();

            foreach (int size in sizes)
            {
                byte[] rawPng = RenderGeometryToPngBytes(geometry, size, size, stroke, strokeThickness, fill);
                pngBuffers.Add((size, rawPng));
            }

            WriteIcoFile(outputPath, pngBuffers);
        }
        else
        {
            byte[] rawPng = RenderGeometryToPngBytes(geometry, pngWidth, pngHeight, stroke, strokeThickness, fill);
            File.WriteAllBytes(outputPath, rawPng);
        }
    }

    public static byte[] RenderGeometryToPngBytes(Geometry geometry, int width, int height, Brush stroke, double strokeThickness,
                                                  Brush    fill = null)
    {
        Rect bounds = geometry.GetRenderBounds(new Pen(stroke, strokeThickness));
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            bounds = new Rect(0, 0, width, height);

        // Uniform scaling with margin preservation
        double availableW = Math.Max(1, width  - strokeThickness * 2);
        double availableH = Math.Max(1, height - strokeThickness * 2);
        double scale      = Math.Min(availableW / bounds.Width, availableH / bounds.Height);

        double offsetX = (width  - bounds.Width  * scale) / 2.0 - bounds.X * scale;
        double offsetY = (height - bounds.Height * scale) / 2.0 - bounds.Y * scale;

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.PushTransform(new TransformGroup
                             {
                                 Children = new TransformCollection
                                            {
                                                new ScaleTransform(scale, scale),
                                                new TranslateTransform(offsetX, offsetY)
                                            }
                             });

            var pen = new Pen(stroke, strokeThickness)
                      {
                          StartLineCap = PenLineCap.Round,
                          EndLineCap   = PenLineCap.Round,
                          LineJoin     = PenLineJoin.Round
                      };

            dc.DrawGeometry(fill, pen, geometry);
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void WriteIcoFile(string outputPath, List<(int Size, byte[] Data)> images)
    {
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // ICONDIR Header
        bw.Write((ushort)0); // Reserved (must be 0)
        bw.Write((ushort)1); // Image type: 1 = ICO
        bw.Write((ushort)images.Count);

        // Image data offset starts immediately after header and all directory entries (16 bytes each)
        int offset = 6 + 16 * images.Count;

        // ICONDIRENTRY list
        foreach (var img in images)
        {
            bw.Write((byte)(img.Size >= 256 ? 0 : img.Size)); // Width (0 for 256)
            bw.Write((byte)(img.Size >= 256 ? 0 : img.Size)); // Height (0 for 256)
            bw.Write((byte)0);                                // Color count (0 = >8bpp)
            bw.Write((byte)0);                                // Reserved
            bw.Write((ushort)1);                              // Color planes
            bw.Write((ushort)32);                             // Bits per pixel
            bw.Write((uint)img.Data.Length);                  // Size of image data
            bw.Write((uint)offset);                           // Offset of image data

            offset += img.Data.Length;
        }

        // Write raw PNG image blocks
        foreach (var img in images) bw.Write(img.Data);
    }
}