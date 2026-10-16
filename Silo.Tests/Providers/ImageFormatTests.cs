using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Silo.Providers.Document;

namespace Silo.Tests.Providers;

public class ImageFormatTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "silo-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A small picture; <paramref name="shade" /> tells frames apart.</summary>
    private static BitmapSource Picture(int width, int height, byte shade)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i]     = shade;
            pixels[i + 1] = (byte)(255 - shade);
            pixels[i + 2] = 128;
            pixels[i + 3] = 255;
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }

    private static byte[] Encode(BitmapEncoder encoder, params BitmapSource[] frames)
    {
        foreach (var frame in frames)
            encoder.Frames.Add(BitmapFrame.Create(frame));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private List<ImageSource> Read(ImageFormat format, byte[] content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, content);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return format.Read(fs).ToList();
    }

    [Test]
    public void A_PNG_is_one_frame_of_its_size()
    {
        var frames = Read(new PngFormat(), Encode(new PngBitmapEncoder(), Picture(4, 3, 10)));

        Assert.That(frames, Has.Count.EqualTo(1));
        Assert.That(((BitmapSource)frames[0]).PixelWidth, Is.EqualTo(4));
        Assert.That(((BitmapSource)frames[0]).PixelHeight, Is.EqualTo(3));
    }

    [Test]
    public void A_BMP_and_a_JPEG_are_read()
    {
        Assert.That(Read(new BmpFormat(), Encode(new BmpBitmapEncoder(), Picture(5, 2, 10))), Has.Count.EqualTo(1));
        Assert.That(Read(new JpegFormat(), Encode(new JpegBitmapEncoder(), Picture(16, 16, 10))), Has.Count.EqualTo(1));
    }

    [Test]
    public void A_GIF_gives_a_frame_per_animation_frame()
    {
        var frames = Read(new GifFormat(), Encode(new GifBitmapEncoder(), Picture(4, 4, 10), Picture(4, 4, 200)));

        Assert.That(frames, Has.Count.EqualTo(2));
    }

    [Test]
    public void A_TIFF_gives_a_frame_per_page()
    {
        var frames = Read(new TiffFormat(), Encode(new TiffBitmapEncoder(), Picture(4, 4, 10), Picture(6, 6, 200), Picture(2, 2, 90)));

        Assert.That(frames.Select(f => ((BitmapSource)f).PixelWidth), Is.EqualTo(new[] { 4, 6, 2 }));
    }

    [Test]
    public void A_Windows_Media_Photo_is_read()
    {
        var frames = Read(new WmpFormat(), Encode(new WmpBitmapEncoder(), Picture(8, 8, 10)));

        Assert.That(frames, Has.Count.EqualTo(1));
        Assert.That(((BitmapSource)frames[0]).PixelWidth, Is.EqualTo(8));
    }

    [Test]
    public void An_icon_gives_a_frame_per_size()
    {
        // an ICO is a directory of pictures; PNG data is allowed inside one
        var png = Encode(new PngBitmapEncoder(), Picture(16, 16, 10));
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, true))
        {
            w.Write((ushort)0);          // reserved
            w.Write((ushort)1);          // type: icon
            w.Write((ushort)1);          // one image
            w.Write((byte)16);           // width
            w.Write((byte)16);           // height
            w.Write((byte)0);            // colours in palette
            w.Write((byte)0);            // reserved
            w.Write((ushort)1);          // planes
            w.Write((ushort)32);         // bits per pixel
            w.Write((uint)png.Length);   // size of the image data
            w.Write((uint)22);           // where it starts: 6 + 16
            w.Write(png);
        }

        var frames = Read(new IconFormat(), ms.ToArray());

        Assert.That(frames, Has.Count.EqualTo(1));
        Assert.That(((BitmapSource)frames[0]).PixelWidth, Is.EqualTo(16));
    }

    [Test]
    public void Frames_are_frozen_so_any_thread_can_use_them()
    {
        var frames = Read(new PngFormat(), Encode(new PngBitmapEncoder(), Picture(2, 2, 10)));

        Assert.That(frames[0].IsFrozen);
    }

    [Test]
    public void The_stream_can_be_closed_once_Read_has_returned()
    {
        var path = Path.Combine(_dir, "p.png");
        File.WriteAllBytes(path, Encode(new PngBitmapEncoder(), Picture(3, 3, 10)));

        List<ImageSource> frames;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            frames = new PngFormat().Read(fs).ToList();

        Assert.That(((BitmapSource)frames[0]).PixelWidth, Is.EqualTo(3));
    }

    [Test]
    public void Content_that_is_not_an_image_is_refused()
    {
        Assert.That(() => Read(new PngFormat(), "not an image"u8.ToArray()), Throws.InstanceOf<Exception>());
    }

    [Test]
    public void Each_format_names_itself()
    {
        ImageFormat[] formats = [new JpegFormat(), new BmpFormat(), new PngFormat(), new GifFormat(), new IconFormat(), new TiffFormat(), new WmpFormat()];

        Assert.That(formats.Select(f => f.ContentType).Distinct().Count(), Is.EqualTo(formats.Length));
        Assert.That(formats.Select(f => f.FileExtension), Is.EqualTo(new[] { "jpg,jpeg", "bmp", "png", "gif", "ico", "tif,tiff", "wdp,hdp,jxr" }));
        Assert.That(formats.All(f => f.DisplayName.Length > 0));
    }
}
