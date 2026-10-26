using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using Silo.Labs.Images;

namespace Silo.Tests.Labs;

/// <summary>The raster engine of the Image Lab: colors, masks, painting, filters, transforms, layers, history, and the PNG and icon writers.</summary>
public class ImageLabEngineTests
{
    private static readonly Rgba Red   = new(255, 0, 0);
    private static readonly Rgba Blue  = new(0, 0, 255);
    private static readonly Rgba Green = new(0, 255, 0);

    private static Raster Solid(int w, int h, Rgba c)
    {
        var r = new Raster(w, h);
        r.Fill(c);
        return r;
    }

    private static int CountAlpha(Raster r) => Enumerable.Range(0, r.Width * r.Height).Count(i => r.Pixels[i * 4 + 3] > 0);

    // ---- colors and pixels ------------------------------------------------------------------------------------

    [Test]
    public void ColorsAreReadAndWritten()
    {
        Assert.That(Rgba.TryParse("#F00", out var c), Is.True);
        Assert.That(c, Is.EqualTo(Red));
        Assert.That(Rgba.TryParse("00ff0080", out var d), Is.True);
        Assert.That(d, Is.EqualTo(new Rgba(0, 255, 0, 128)));
        Assert.That(Rgba.TryParse("transparent", out var t), Is.True);
        Assert.That(t.A, Is.EqualTo(0));
        Assert.That(Rgba.TryParse("#12", out _), Is.False);
        Assert.That(Rgba.TryParse("#GGGGGG", out _), Is.False);
        Assert.That(Red.ToHex(), Is.EqualTo("#FF0000"));
        Assert.That(new Rgba(1, 2, 3, 4).ToHex(), Is.EqualTo("#01020304"));
        Assert.That(Rgba.Lerp(Rgba.Black, Rgba.White, 0.5).R, Is.EqualTo(128));
    }

    [Test]
    public void OverBlendsByAlpha()
    {
        var r = Solid(2, 2, Blue);
        r.Over(0, 0, Red, 0.5);
        var p = r.Get(0, 0);
        Assert.That(p.A, Is.EqualTo(255));
        Assert.That(Math.Abs(p.R - 128) <= 1 && Math.Abs(p.B - 127) <= 1, Is.True);

        var empty = new Raster(1, 1);
        empty.Over(0, 0, Green, 1);
        Assert.That(empty.Get(0, 0), Is.EqualTo(Green));

        r.Over(5, 5, Red, 1); // outside: nothing happens
        r.Erase(1, 1, 1);
        Assert.That(r.Get(1, 1).A, Is.EqualTo(0));
    }

    [Test]
    public void SizesAreChecked()
    {
        Assert.Throws<ArgumentException>(() => new Raster(0, 5));
        Assert.Throws<ArgumentException>(() => new Raster(9000, 10));
        Assert.Throws<ArgumentException>(() => new Raster(8000, 8000));
    }

    [Test]
    public void CropPasteAndBounds()
    {
        var r = new Raster(10, 10);
        r.Set(3, 4, Red);
        r.Set(6, 7, Red);
        Assert.That(r.ContentBounds(), Is.EqualTo(new IntRect(3, 4, 4, 4)));
        var piece = r.Crop(new IntRect(3, 4, 4, 4));
        Assert.That(piece.Get(0, 0), Is.EqualTo(Red));
        Assert.That(piece.Get(3, 3), Is.EqualTo(Red));
        var other = new Raster(10, 10);
        other.Paste(piece, 1, 1);
        Assert.That(other.Get(1, 1), Is.EqualTo(Red));
        Assert.That(other.DifferenceBounds(r), Is.Not.EqualTo(IntRect.Empty));
        Assert.That(r.DifferenceBounds(r.Clone()), Is.EqualTo(IntRect.Empty));
    }

    [Test]
    public void ResizeKeepsAverageColorAndTransparentEdges()
    {
        var r = Solid(40, 40, Red);
        var small = r.Resize(10, 10);
        Assert.That(small.Get(5, 5), Is.EqualTo(Red));
        var big = r.Resize(80, 80);
        Assert.That(big.Get(40, 40), Is.EqualTo(Red));

        var half = new Raster(4, 4);
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 2; x++)
            half.Set(x, y, Red);

        var scaled = half.Resize(8, 8);
        Assert.That(scaled.Get(0, 4).R, Is.EqualTo(255)); // no dark fringe from the transparent side
    }

    [Test]
    public void FlipAndTurn()
    {
        var r = new Raster(3, 2);
        r.Set(0, 0, Red);
        Assert.That(r.FlipHorizontal().Get(2, 0), Is.EqualTo(Red));
        Assert.That(r.FlipVertical().Get(0, 1), Is.EqualTo(Red));
        var cw = r.Rotate90(true);
        Assert.That((cw.Width, cw.Height), Is.EqualTo((2, 3)));
        Assert.That(cw.Get(1, 0), Is.EqualTo(Red));
        Assert.That(r.Rotate90(false).Get(0, 2), Is.EqualTo(Red));
        Assert.That(cw.Rotate90(false).Get(0, 0), Is.EqualTo(Red));
    }

    // ---- masks -----------------------------------------------------------------------------------------------

    [Test]
    public void MaskShapes()
    {
        var rect = Mask.Rectangle(20, 20, 2, 3, 12, 9);
        Assert.That(rect.Bounds(), Is.EqualTo(new IntRect(2, 3, 10, 6)));
        Assert.That(rect[5, 5], Is.EqualTo(255));
        Assert.That(rect[0, 0], Is.EqualTo(0));

        var ellipse = Mask.Ellipse(40, 40, 5, 5, 35, 35);
        Assert.That(ellipse[20, 20], Is.EqualTo(255));
        Assert.That(ellipse[6, 6], Is.EqualTo(0));
        double area = ellipse.Values.Sum(v => v / 255.0);
        Assert.That(Math.Abs(area - Math.PI * 15 * 15) < 12, Is.True);

        var tri = Mask.Polygon(30, 30, [new Pt(0, 0), new Pt(20, 0), new Pt(0, 20)]);
        double triArea = tri.Values.Sum(v => v / 255.0);
        Assert.That(Math.Abs(triArea - 200) < 6, Is.True);
        Assert.That(Mask.Empty(5, 5).IsEmpty, Is.True);
    }

    [Test]
    public void MaskCombineInvertGrowFeather()
    {
        var a = Mask.Rectangle(20, 20, 0, 0, 10, 10);
        var b = Mask.Rectangle(20, 20, 5, 5, 15, 15);
        Assert.That(a.Combine(b, SelectionMode.Add).Bounds(), Is.EqualTo(new IntRect(0, 0, 15, 15)));
        Assert.That(a.Combine(b, SelectionMode.Intersect).Bounds(), Is.EqualTo(new IntRect(5, 5, 5, 5)));
        Assert.That(a.Combine(b, SelectionMode.Subtract)[7, 7], Is.EqualTo(0));
        Assert.That(a.Combine(b, SelectionMode.Subtract)[2, 2], Is.EqualTo(255));
        Assert.That(a.Combine(b, SelectionMode.Replace)[2, 2], Is.EqualTo(0));
        Assert.That(a.Invert()[2, 2], Is.EqualTo(0));
        Assert.That(a.Invert()[15, 15], Is.EqualTo(255));
        Assert.That(a.Grow(2).Bounds(), Is.EqualTo(new IntRect(0, 0, 12, 12)));
        Assert.That(a.Grow(-2).Bounds(), Is.EqualTo(new IntRect(2, 2, 6, 6)));
        var soft = Mask.Rectangle(40, 40, 10, 10, 30, 30).Feather(3);
        Assert.That(soft[20, 20], Is.EqualTo(255));
        Assert.That(soft[10, 20] > 0 && soft[10, 20] < 255, Is.True);
        Assert.That(soft[2, 2], Is.EqualTo(0));
    }

    [Test]
    public void MaskGeometry()
    {
        var m = Mask.Rectangle(10, 6, 0, 0, 3, 2);
        Assert.That(m.FlipHorizontal().Bounds(), Is.EqualTo(new IntRect(7, 0, 3, 2)));
        Assert.That(m.FlipVertical().Bounds(), Is.EqualTo(new IntRect(0, 4, 3, 2)));
        var cw = m.Rotate90(true);
        Assert.That((cw.Width, cw.Height), Is.EqualTo((6, 10)));
        Assert.That(cw.Bounds(), Is.EqualTo(new IntRect(4, 0, 2, 3)));
        Assert.That(m.Place(20, 20, 5, 5).Bounds(), Is.EqualTo(new IntRect(5, 5, 3, 2)));
        Assert.That(m.Crop(new IntRect(0, 0, 3, 2)).Bounds(), Is.EqualTo(new IntRect(0, 0, 3, 2)));
        Assert.That(m.Resize(20, 12).Bounds(), Is.EqualTo(new IntRect(0, 0, 6, 4)));
    }

    [Test]
    public void OutlineOfASquareIsItsPerimeter()
    {
        var m = Mask.Rectangle(10, 10, 2, 2, 6, 6);
        Assert.That(m.Outline().Count, Is.EqualTo(16));
    }

    // ---- brushes ----------------------------------------------------------------------------------------------

    [Test]
    public void RoundBrushPaintsADisc()
    {
        var r = new Raster(40, 40);
        var stroke = new BrushStroke(r, null, new BrushSettings(11), Red, false);
        stroke.Begin(new Pt(20, 20));
        Assert.That(r.Get(20, 20), Is.EqualTo(Red));
        Assert.That(r.Get(20, 24).A, Is.EqualTo(255));
        Assert.That(r.Get(20, 27).A, Is.EqualTo(0));
        double n = Enumerable.Range(0, 40 * 40).Sum(i => r.Pixels[i * 4 + 3]) / 255.0;
        Assert.That(Math.Abs(n - Math.PI * 5.5 * 5.5) < 5, Is.True);
        Assert.That(stroke.Dirty.IsEmpty, Is.False);
    }

    [Test]
    public void StrokeIsContinuousWhateverTheSpeed()
    {
        var r = new Raster(100, 20);
        var stroke = new BrushStroke(r, null, new BrushSettings(4), Blue, false);
        stroke.Begin(new Pt(5, 10));
        stroke.LineTo(new Pt(95, 10)); // one big jump
        for (int x = 5; x <= 95; x++) Assert.That(r.Get(x, 10).A, Is.EqualTo(255));
    }

    [Test]
    public void BrushRespectsTheSelection()
    {
        var r    = new Raster(40, 40);
        var mask = Mask.Rectangle(40, 40, 0, 0, 20, 40);
        var s    = new BrushStroke(r, mask, new BrushSettings(30), Red, false);
        s.Begin(new Pt(20, 20));
        Assert.That(r.Get(10, 20).A, Is.EqualTo(255));
        Assert.That(r.Get(30, 20).A, Is.EqualTo(0));
    }

    [Test]
    public void EraserTakesAwayOpacity()
    {
        var r = Solid(30, 30, Red);
        var s = new BrushStroke(r, null, new BrushSettings(10), Rgba.Black, true);
        s.Begin(new Pt(15, 15));
        Assert.That(r.Get(15, 15).A, Is.EqualTo(0));
        Assert.That(r.Get(1, 1).A, Is.EqualTo(255));
        var half = Solid(10, 10, Red);
        new BrushStroke(half, null, new BrushSettings(10, BrushPattern.Round, 0.5), Rgba.Black, true).Begin(new Pt(5, 5));
        Assert.That(Math.Abs(half.Get(5, 5).A - 128) <= 1, Is.True);
    }

    [Test]
    public void EveryPatternPaintsSomethingDifferent()
    {
        var seen = new HashSet<string>();
        foreach (var pattern in Enum.GetValues<BrushPattern>())
        {
            var r = new Raster(40, 40);
            new BrushStroke(r, null, new BrushSettings(30, pattern), Rgba.Black, false).Begin(new Pt(20, 20));
            Assert.That(CountAlpha(r) > 20, Is.True, pattern.ToString());
            seen.Add(Convert.ToBase64String(r.Pixels));
        }

        Assert.That(seen.Count, Is.EqualTo(Enum.GetValues<BrushPattern>().Length));

        // texture is repeatable
        var a = new Raster(40, 40);
        var b = new Raster(40, 40);
        new BrushStroke(a, null, new BrushSettings(30, BrushPattern.Spray), Red, false).Begin(new Pt(20, 20));
        new BrushStroke(b, null, new BrushSettings(30, BrushPattern.Spray), Red, false).Begin(new Pt(20, 20));
        Assert.That(a.Pixels.SequenceEqual(b.Pixels), Is.True);
    }

    [Test]
    public void SoftBrushFadesToTheEdge()
    {
        var r = new Raster(40, 40);
        new BrushStroke(r, null, new BrushSettings(30, BrushPattern.Soft), Red, false).Begin(new Pt(20, 20));
        Assert.That(r.Get(20, 20).A > r.Get(20, 28).A, Is.True);
        Assert.That(r.Get(20, 28).A > r.Get(20, 34).A, Is.True);
    }

    // ---- gradients and paths ------------------------------------------------------------------------------------

    [Test]
    public void LinearGradientRunsFromOneColorToTheOther()
    {
        var r = new Raster(101, 3);
        Gradients.Fill(r, null, new Pt(0, 0), new Pt(101, 0), Rgba.Black, Rgba.White, GradientKind.Linear);
        Assert.That(r.Get(0, 1).R < 4, Is.True);
        Assert.That(r.Get(100, 1).R > 251, Is.True);
        Assert.That(Math.Abs(r.Get(50, 1).R - 128) < 4, Is.True);
    }

    [Test]
    public void GradientKindsDiffer()
    {
        var seen = new HashSet<string>();
        foreach (var kind in Enum.GetValues<GradientKind>())
        {
            var r = new Raster(40, 40);
            Gradients.Fill(r, null, new Pt(20, 20), new Pt(35, 20), Rgba.Black, Rgba.White, kind);
            seen.Add(Convert.ToBase64String(r.Pixels));
        }

        Assert.That(seen.Count, Is.EqualTo(5));
    }

    [Test]
    public void GradientStaysInsideTheSelectionAndCanFadeOut()
    {
        var r    = new Raster(20, 20);
        var mask = Mask.Rectangle(20, 20, 0, 0, 10, 20);
        Gradients.Fill(r, mask, new Pt(0, 0), new Pt(20, 0), Red, Red.WithAlpha(0), GradientKind.Linear);
        Assert.That(r.Get(15, 5).A, Is.EqualTo(0));
        Assert.That(r.Get(1, 5).A > 200, Is.True);
        Assert.That(r.Get(9, 5).A < r.Get(1, 5).A, Is.True);
    }

    [Test]
    public void BezierFlattenEndsWhereThePathEnds()
    {
        var pts = new List<PathPoint> { PathPoint.Smooth(new Pt(0, 0), new Pt(10, -10)), PathPoint.Smooth(new Pt(40, 0), new Pt(50, 10)) };
        var line = BezierPaths.Flatten(pts, false);
        Assert.That(line[0], Is.EqualTo(new Pt(0, 0)));
        Assert.That(line[^1], Is.EqualTo(new Pt(40, 0)));
        Assert.That(line.Count > 6, Is.True);
        Assert.That(line.Any(p => p.Y < -2), Is.True); // it bends
        Assert.That(BezierPaths.Flatten([PathPoint.Corner(new Pt(0, 0)), PathPoint.Corner(new Pt(5, 5))], false).Count, Is.EqualTo(2));
        Assert.That(BezierPaths.Flatten([], false).Count, Is.EqualTo(0));
    }

    [Test]
    public void ClosedPathFillsItsInside()
    {
        var r = new Raster(30, 30);
        var square = new[] { PathPoint.Corner(new Pt(5, 5)), PathPoint.Corner(new Pt(25, 5)), PathPoint.Corner(new Pt(25, 25)), PathPoint.Corner(new Pt(5, 25)) };
        BezierPaths.Fill(r, null, square, Red);
        Assert.That(r.Get(15, 15), Is.EqualTo(Red));
        Assert.That(r.Get(2, 2).A, Is.EqualTo(0));
        Assert.That(CountAlpha(r), Is.EqualTo(400));
        var stroked = new Raster(30, 30);
        BezierPaths.Stroke(stroked, null, square, true, new BrushSettings(3), Blue);
        Assert.That(stroked.Get(5, 15).A, Is.EqualTo(255));
        Assert.That(stroked.Get(15, 15).A, Is.EqualTo(0));
    }

    // ---- filters -------------------------------------------------------------------------------------------------

    [Test]
    public void SimpleFilters()
    {
        var c = Solid(4, 4, new Rgba(200, 100, 50, 180));
        var g = ImageFilters.Grayscale(c).Get(1, 1);
        Assert.That(g.R == g.G && g.G == g.B && g.A == 180, Is.True);
        Assert.That(ImageFilters.Invert(c).Get(0, 0), Is.EqualTo(new Rgba(55, 155, 205, 180)));
        Assert.That(ImageFilters.Invert(ImageFilters.Invert(c)).Pixels.SequenceEqual(c.Pixels), Is.True);
        var sepia = ImageFilters.Sepia(Solid(2, 2, Rgba.White)).Get(0, 0);
        Assert.That(sepia.R == 255 && sepia.B < 250, Is.True);
        var mid = ImageFilters.Sepia(Solid(2, 2, new Rgba(100, 100, 100))).Get(0, 0);
        Assert.That(mid.B < mid.G && mid.G < mid.R, Is.True);
        Assert.That(ImageFilters.BrightnessContrast(c, 0, 0).Pixels.SequenceEqual(c.Pixels), Is.True);
        Assert.That(ImageFilters.BrightnessContrast(c, 50, 0).Get(0, 0).R, Is.EqualTo(255));
        Assert.That(ImageFilters.BrightnessContrast(Solid(2, 2, new Rgba(100, 100, 100)), -50, 0).Get(0, 0).R < 100, Is.True);
        var hi = ImageFilters.BrightnessContrast(Solid(1, 1, new Rgba(200, 200, 200)), 0, 50).Get(0, 0).R;
        var lo = ImageFilters.BrightnessContrast(Solid(1, 1, new Rgba(50, 50, 50)), 0, 50).Get(0, 0).R;
        Assert.That(hi > 200 && lo < 50, Is.True);
        Assert.That(ImageFilters.Threshold(Solid(2, 2, new Rgba(130, 130, 130)), 128).Get(0, 0), Is.EqualTo(Rgba.White));
        Assert.That(ImageFilters.Threshold(Solid(2, 2, new Rgba(120, 120, 120)), 128).Get(0, 0), Is.EqualTo(Rgba.Black));
    }

    [Test]
    public void BlurSpreadsWithoutChangingFlatAreasOrTotalOpacity()
    {
        var flat = Solid(20, 20, Red);
        Assert.That(ImageFilters.Blur(flat, 3).Get(10, 10), Is.EqualTo(Red));
        Assert.That(ImageFilters.Blur(flat, 3).Get(0, 0), Is.EqualTo(Red)); // edges are clamped, not darkened

        var dot = new Raster(31, 31);
        dot.Set(15, 15, Red);
        var blurred = ImageFilters.Blur(dot, 3);
        Assert.That(blurred.Get(15, 15).A < 255 && blurred.Get(15, 15).A > 0, Is.True);
        Assert.That(blurred.Get(17, 15).A > 0, Is.True);
        double total = Enumerable.Range(0, 31 * 31).Sum(i => blurred.Pixels[i * 4 + 3]);
        Assert.That(Math.Abs(total - 255) < 25, Is.True); // some is lost to rounding the faint edges to whole bytes
        Assert.That(blurred.Get(15, 15).R, Is.EqualTo(255)); // the color stays red, only the opacity spreads
        Assert.That(ImageFilters.Blur(dot, 0).Pixels.SequenceEqual(dot.Pixels), Is.True);
    }

    [Test]
    public void SharpenMakesEdgesHarder()
    {
        var edge = new Raster(20, 10);
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 20; x++)
            edge.Set(x, y, x < 10 ? new Rgba(100, 100, 100) : new Rgba(150, 150, 150));

        var sharp = ImageFilters.Sharpen(edge, 2, 1.5);
        Assert.That(sharp.Get(9, 5).R < 100, Is.True);
        Assert.That(sharp.Get(10, 5).R > 150, Is.True);
        Assert.That(sharp.Get(2, 5).R, Is.EqualTo(100));
    }

    [Test]
    public void PixelateMakesBlocks()
    {
        var r = new Raster(8, 8);
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            r.Set(x, y, (x + y) % 2 == 0 ? Rgba.Black : Rgba.White);

        var p = ImageFilters.Pixelate(r, 4);
        Assert.That(p.Get(0, 0), Is.EqualTo(p.Get(3, 3)));
        Assert.That(p.Get(0, 0), Is.Not.EqualTo(Rgba.Black));
        Assert.That(Math.Abs(p.Get(1, 1).R - 128) <= 1, Is.True);
        Assert.That(ImageFilters.Pixelate(r, 1).Pixels.SequenceEqual(r.Pixels), Is.True);
    }

    [Test]
    public void MaskedFilterTouchesOnlyTheSelection()
    {
        var r    = Solid(20, 20, Red);
        var mask = Mask.Rectangle(20, 20, 0, 0, 10, 20);
        var f    = ImageFilters.Masked(r, mask, ImageFilters.Invert);
        Assert.That(f.Get(2, 2), Is.EqualTo(new Rgba(0, 255, 255)));
        Assert.That(f.Get(15, 2), Is.EqualTo(Red));
        var soft = ImageFilters.Masked(r, Mask.Rectangle(20, 20, 5, 5, 15, 15).Feather(3), ImageFilters.Invert);
        var edge = soft.Get(5, 10);
        Assert.That(edge.R > 0 && edge.R < 255, Is.True);
        Assert.That(ImageFilters.Masked(r, null, ImageFilters.Invert).Get(15, 2), Is.EqualTo(new Rgba(0, 255, 255)));
    }

    // ---- transforms ---------------------------------------------------------------------------------------------

    [Test]
    public void AffineAlgebra()
    {
        var m = Affine.Scale(2, 3).Then(Affine.Rotate(0.4)).Then(Affine.Translate(5, -7));
        var p = new Pt(3, 4);
        var back = m.Invert().Apply(m.Apply(p));
        Assert.That(Math.Abs(back.X - 3) < 1e-9 && Math.Abs(back.Y - 4) < 1e-9, Is.True);
        Assert.That(Affine.Identity.IsIdentity, Is.True);
        Assert.That(Affine.Scale(0, 1).IsInvertible, Is.False);
        var about = Affine.About(new Pt(10, 10), Affine.Rotate(Math.PI));
        var q = about.Apply(new Pt(12, 10));
        Assert.That(Math.Abs(q.X - 8) < 1e-9 && Math.Abs(q.Y - 10) < 1e-9, Is.True);
        var skew = Affine.Skew(1, 0).Apply(new Pt(0, 5));
        Assert.That(skew, Is.EqualTo(new Pt(5, 5)));
    }

    [Test]
    public void WarpMovesPixels()
    {
        var r = new Raster(20, 20);
        r.Set(5, 5, Red);
        var moved = ImageTransform.Warp(r, Affine.Translate(3, 4), 20, 20);
        Assert.That(moved.Get(8, 9), Is.EqualTo(Red));
        Assert.That(moved.Get(5, 5).A, Is.EqualTo(0));

        var big = new Raster(10, 10);
        big.Fill(Red);
        var scaled = ImageTransform.Warp(big, Affine.Scale(2, 2), 30, 30);
        Assert.That(scaled.Get(15, 15), Is.EqualTo(Red));
        Assert.That(scaled.Get(25, 25).A, Is.EqualTo(0));
        Assert.That(CountAlpha(scaled) is >= 400 and <= 441, Is.True);

        var half = ImageTransform.Warp(big, Affine.About(new Pt(5, 5), Affine.Rotate(Math.PI / 4)), 10, 10);
        Assert.That(half.Get(5, 5), Is.EqualTo(Red));
        Assert.That(half.Get(0, 0).A, Is.EqualTo(0)); // a corner has turned out of the picture
    }

    [Test]
    public void TurningAQuarterTurnMatchesRotate90()
    {
        var r = new Raster(9, 9);
        r.Set(2, 1, Red);
        r.Set(7, 3, Blue);
        var turned = ImageTransform.Warp(r, Affine.About(new Pt(4.5, 4.5), Affine.Rotate(Math.PI / 2)), 9, 9);
        var exact  = r.Rotate90(true);
        Assert.That(turned.Pixels.SequenceEqual(exact.Pixels), Is.True);
    }

    [Test]
    public void LiftSplitsTheLayerBySelection()
    {
        var r = Solid(10, 10, Red);
        var (floating, rest) = ImageTransform.Lift(r, Mask.Rectangle(10, 10, 0, 0, 5, 10));
        Assert.That(floating.Get(2, 2).A, Is.EqualTo(255));
        Assert.That(floating.Get(7, 2).A, Is.EqualTo(0));
        Assert.That(rest.Get(2, 2).A, Is.EqualTo(0));
        Assert.That(rest.Get(7, 2).A, Is.EqualTo(255));
        var (all, none) = ImageTransform.Lift(r, null);
        Assert.That(CountAlpha(all), Is.EqualTo(100));
        Assert.That(CountAlpha(none), Is.EqualTo(0));
    }

    [Test]
    public void LayerTransformPreviewsCommitsAndUndoes()
    {
        var doc = ImageDocument.Blank(40, 40, Rgba.Transparent);
        doc.ActiveLayer!.Pixels.Fill(Rgba.Transparent);
        for (int y = 10; y < 20; y++)
        for (int x = 10; x < 20; x++)
            doc.ActiveLayer.Pixels.Set(x, y, Red);

        doc.SetSelection(Mask.Rectangle(40, 40, 10, 10, 20, 20));
        var t = LayerTransform.Begin(doc)!;
        Assert.That(t.Box, Is.EqualTo(new IntRect(10, 10, 10, 10)));
        Assert.That(t.Center, Is.EqualTo(new Pt(15, 15)));
        t.SetMatrix(Affine.Translate(10, 5));
        Assert.That(doc.ActiveLayer.Shown.Get(25, 20), Is.EqualTo(Red));
        Assert.That(doc.ActiveLayer.Shown.Get(12, 12).A, Is.EqualTo(0));
        Assert.That(doc.ActiveLayer.Pixels.Get(12, 12), Is.EqualTo(Red)); // not final yet
        Assert.That(doc.SelectionShown!.Bounds(), Is.EqualTo(new IntRect(20, 15, 10, 10)));
        int steps = doc.UndoNames.Count;
        t.Commit("Move");
        Assert.That(doc.ActiveLayer.Preview, Is.Null);
        Assert.That(doc.ActiveLayer.Pixels.Get(25, 20), Is.EqualTo(Red));
        Assert.That(doc.ActiveLayer.Pixels.Get(12, 12).A, Is.EqualTo(0));
        Assert.That(doc.Selection!.Bounds(), Is.EqualTo(new IntRect(20, 15, 10, 10)));
        Assert.That(doc.UndoNames.Count, Is.EqualTo(steps + 1));
        doc.Undo();
        Assert.That(doc.ActiveLayer.Pixels.Get(12, 12), Is.EqualTo(Red));
        Assert.That(doc.ActiveLayer.Pixels.Get(25, 20).A, Is.EqualTo(0));
        Assert.That(doc.Selection!.Bounds(), Is.EqualTo(new IntRect(10, 10, 10, 10)));
        doc.Redo();
        Assert.That(doc.ActiveLayer.Pixels.Get(25, 20), Is.EqualTo(Red));
    }

    [Test]
    public void CancelledTransformChangesNothing()
    {
        var doc = ImageDocument.Blank(20, 20, Red);
        int steps = doc.UndoNames.Count;
        var t = LayerTransform.Begin(doc)!;
        t.SetMatrix(Affine.Rotate(0.5));
        t.Cancel();
        Assert.That(doc.ActiveLayer!.Preview, Is.Null);
        Assert.That(doc.UndoNames.Count, Is.EqualTo(steps));
        var u = LayerTransform.Begin(doc)!;
        u.Commit("Nothing"); // identity: dropped
        Assert.That(doc.UndoNames.Count, Is.EqualTo(steps));
    }

    // ---- the document ---------------------------------------------------------------------------------------------

    [Test]
    public void BlankImageHasOneBackgroundLayer()
    {
        var doc = ImageDocument.Blank(30, 20, Rgba.White, "Sketch");
        Assert.That(doc.Layers.Count, Is.EqualTo(1));
        Assert.That(doc.Layers[0].Name, Is.EqualTo("Background"));
        Assert.That(doc.Layers[0].Pixels.Get(5, 5), Is.EqualTo(Rgba.White));
        Assert.That((doc.Width, doc.Height), Is.EqualTo((30, 20)));
        Assert.That(doc.CanUndo, Is.False);
        Assert.That(ImageDocument.Blank(5, 5, Rgba.Transparent).Layers[0].Pixels.Get(1, 1).A, Is.EqualTo(0));
    }

    [Test]
    public void LayersAreAddedMovedAndRemovedWithHistory()
    {
        var doc = ImageDocument.Blank(10, 10, Rgba.White);
        var a = doc.AddLayer("Ink");
        var b = doc.AddLayer("Ink");
        Assert.That(b.Name, Is.EqualTo("Ink 2"));
        Assert.That(doc.Layers.Select(l => l.Name), Is.EqualTo(new[] { "Background", "Ink", "Ink 2" }));
        Assert.That(doc.ActiveIndex, Is.EqualTo(2));
        Assert.That(doc.MoveLayer(2, -1), Is.True);
        Assert.That(doc.Layers.Select(l => l.Name), Is.EqualTo(new[] { "Background", "Ink 2", "Ink" }));
        Assert.That(doc.MoveLayer(0, -1), Is.False);
        doc.DuplicateLayer(1);
        Assert.That(doc.Layers.Count, Is.EqualTo(4));
        doc.DeleteLayer(2);
        Assert.That(doc.Layers.Count, Is.EqualTo(3));
        doc.RenameLayer(1, "Lines");
        Assert.That(doc.Layers[1].Name, Is.EqualTo("Lines"));
        while (doc.Undo()) { }
        Assert.That(doc.Layers.Select(l => l.Name), Is.EqualTo(new[] { "Background" }));
        while (doc.Redo()) { }
        Assert.That(doc.Layers.Count, Is.EqualTo(3));
        Assert.That(doc.Layers[1].Name, Is.EqualTo("Lines"));
        Assert.That(doc.Layers.Contains(a) && doc.Layers.Contains(b), Is.True);
    }

    [Test]
    public void DeletingTheLastLayerLeavesAnEmptyOne()
    {
        var doc = ImageDocument.Blank(10, 10, Red);
        doc.DeleteLayer(0);
        Assert.That(doc.Layers.Count, Is.EqualTo(1));
        Assert.That(doc.Layers[0].Pixels.Get(1, 1).A, Is.EqualTo(0));
        doc.Undo();
        Assert.That(doc.Layers[0].Pixels.Get(1, 1), Is.EqualTo(Red));
    }

    [Test]
    public void PaintingIsOneStepAndStoresOnlyTheChange()
    {
        var doc  = ImageDocument.Blank(200, 200, Rgba.White);
        var snap = doc.BeginPaint()!;
        var s    = new BrushStroke(doc.ActiveLayer!.Pixels, doc.Selection, new BrushSettings(10), Red, false);
        s.Begin(new Pt(50, 50));
        s.LineTo(new Pt(80, 50));
        Assert.That(doc.EndPaint(snap, "Brush"), Is.True);
        Assert.That(doc.UndoName, Is.EqualTo("Brush"));
        Assert.That(doc.ActiveLayer.Pixels.Get(60, 50), Is.EqualTo(Red));
        doc.Undo();
        Assert.That(doc.ActiveLayer.Pixels.Get(60, 50), Is.EqualTo(Rgba.White));
        doc.Redo();
        Assert.That(doc.ActiveLayer.Pixels.Get(60, 50), Is.EqualTo(Red));

        var again = doc.BeginPaint()!;
        Assert.That(doc.EndPaint(again, "Nothing"), Is.False); // nothing changed: no step
        Assert.That(doc.UndoName, Is.EqualTo("Brush"));
    }

    [Test]
    public void ApplyingAFilterHonoursTheSelectionAndUndoes()
    {
        var doc = ImageDocument.Blank(20, 20, Red);
        doc.SetSelection(Mask.Rectangle(20, 20, 0, 0, 10, 20));
        doc.ApplyFilter("Invert", ImageFilters.Invert);
        Assert.That(doc.ActiveLayer!.Pixels.Get(2, 2), Is.EqualTo(new Rgba(0, 255, 255)));
        Assert.That(doc.ActiveLayer.Pixels.Get(15, 2), Is.EqualTo(Red));
        doc.Undo();
        Assert.That(doc.ActiveLayer.Pixels.Get(2, 2), Is.EqualTo(Red));
        doc.Undo(); // the selection
        Assert.That(doc.Selection, Is.Null);
    }

    [Test]
    public void FillSelectionAndErase()
    {
        var doc = ImageDocument.Blank(20, 20, Rgba.White);
        doc.SetSelection(Mask.Rectangle(20, 20, 5, 5, 10, 10));
        doc.FillSelection(Red, "Fill");
        Assert.That(doc.ActiveLayer!.Pixels.Get(6, 6), Is.EqualTo(Red));
        Assert.That(doc.ActiveLayer.Pixels.Get(1, 1), Is.EqualTo(Rgba.White));
        doc.FillSelection(Rgba.Transparent, "Clear");
        Assert.That(doc.ActiveLayer.Pixels.Get(6, 6).A, Is.EqualTo(0));
        Assert.That(doc.ActiveLayer.Pixels.Get(1, 1).A, Is.EqualTo(255));
    }

    [Test]
    public void MergeDownAndFlatten()
    {
        var doc = ImageDocument.Blank(10, 10, Blue);
        var top = doc.AddLayer("Top", pixels: Solid(10, 10, Red.WithAlpha(128)));
        Assert.That(doc.MergeDown(1), Is.True);
        Assert.That(doc.Layers.Count, Is.EqualTo(1));
        var c = doc.Layers[0].Pixels.Get(3, 3);
        Assert.That(c.A, Is.EqualTo(255));
        Assert.That(Math.Abs(c.R - 128) <= 2 && Math.Abs(c.B - 127) <= 2, Is.True);
        doc.Undo();
        Assert.That(doc.Layers.Count, Is.EqualTo(2));
        Assert.That(doc.Layers[0].Pixels.Get(3, 3), Is.EqualTo(Blue));
        Assert.That(doc.Layers.Contains(top), Is.True);
        doc.Flatten();
        Assert.That(doc.Layers.Count, Is.EqualTo(1));
        Assert.That(doc.MergeDown(0), Is.False);
    }

    [Test]
    public void CompositeHonoursVisibilityOpacityAndBlendModes()
    {
        var doc = ImageDocument.Blank(4, 4, new Rgba(200, 200, 200));
        doc.AddLayer("Ink", pixels: Solid(4, 4, new Rgba(100, 100, 100)));
        doc.SetBlend(1, BlendMode.Multiply);
        var c = doc.Composite().Get(1, 1);
        Assert.That(Math.Abs(c.R - 200 * 100 / 255) <= 1, Is.True);

        doc.SetBlend(1, BlendMode.Normal);
        doc.SetOpacity(1, 0.5);
        Assert.That(Math.Abs(doc.Composite().Get(1, 1).R - 150) <= 1, Is.True);
        doc.SetVisible(1, false);
        Assert.That(doc.Composite().Get(1, 1).R, Is.EqualTo(200));
        Assert.That(doc.Composite(Rgba.White).Get(1, 1).A, Is.EqualTo(255));

        var screen = ImageDocument.Blank(1, 1, new Rgba(100, 100, 100));
        screen.AddLayer("S", pixels: Solid(1, 1, new Rgba(100, 100, 100)));
        screen.SetBlend(1, BlendMode.Screen);
        Assert.That(Math.Abs(screen.Composite().Get(0, 0).R - 161) <= 1, Is.True); // .392 + .392 - .392 * .392
        var diff = ImageDocument.Blank(1, 1, new Rgba(200, 200, 200));
        diff.AddLayer("D", pixels: Solid(1, 1, new Rgba(50, 50, 50)));
        diff.SetBlend(1, BlendMode.Difference);
        Assert.That(diff.Composite().Get(0, 0).R, Is.EqualTo(150));

        var empty = new ImageDocument(2, 2);
        Assert.That(empty.Composite().Get(0, 0).A, Is.EqualTo(0));
    }

    [Test]
    public void PartialCompositeMatchesTheWholeOne()
    {
        var doc = ImageDocument.Blank(30, 30, Rgba.White);
        doc.AddLayer("A", pixels: Solid(30, 30, Red.WithAlpha(100)));
        var whole = doc.Composite();
        var into  = new Raster(30, 30);
        doc.CompositeInto(into, new IntRect(5, 5, 10, 10));
        Assert.That(into.Get(8, 8), Is.EqualTo(whole.Get(8, 8)));
        Assert.That(into.Get(20, 20).A, Is.EqualTo(0));
    }

    [Test]
    public void OpacityDragIsOneStep()
    {
        var doc = ImageDocument.Blank(4, 4, Red);
        doc.AddLayer("A");
        int steps = doc.UndoNames.Count;
        for (int i = 9; i >= 3; i--) doc.SetOpacity(1, i / 10.0);
        Assert.That(doc.UndoNames.Count, Is.EqualTo(steps + 1));
        doc.Undo();
        Assert.That(doc.Layers[1].Opacity, Is.EqualTo(1));
    }

    [Test]
    public void CanvasAndImageSizeChanges()
    {
        var doc = ImageDocument.Blank(10, 10, Red);
        doc.SetSelection(Mask.Rectangle(10, 10, 0, 0, 5, 5));
        doc.ResizeCanvas(20, 30, 0, 0);
        Assert.That((doc.Width, doc.Height), Is.EqualTo((20, 30)));
        Assert.That(doc.Layers[0].Pixels.Get(5, 5), Is.EqualTo(Red));
        Assert.That(doc.Layers[0].Pixels.Get(15, 15).A, Is.EqualTo(0));
        Assert.That(doc.Selection!.Width, Is.EqualTo(20));
        doc.Undo();
        Assert.That((doc.Width, doc.Height), Is.EqualTo((10, 10)));
        Assert.That(doc.Layers[0].Pixels.Width, Is.EqualTo(10));

        doc.ResizeCanvas(14, 14, 0.5, 0.5);
        Assert.That(doc.Layers[0].Pixels.Get(1, 1).A, Is.EqualTo(0));
        Assert.That(doc.Layers[0].Pixels.Get(1, 7).A, Is.EqualTo(0));
        Assert.That(doc.Layers[0].Pixels.Get(7, 7), Is.EqualTo(Red));
        doc.ScaleImage(28, 28);
        Assert.That(doc.Layers[0].Pixels.Get(14, 14), Is.EqualTo(Red));
        Assert.That(doc.Selection!.Width, Is.EqualTo(28));
        doc.Undo();
        Assert.That(doc.Width, Is.EqualTo(14));
    }

    [Test]
    public void CropToSelection()
    {
        var doc = ImageDocument.Blank(20, 20, Rgba.White);
        doc.Layers[0].Pixels.Set(7, 7, Red);
        Assert.That(doc.CropToSelection(), Is.False);
        doc.SetSelection(Mask.Rectangle(20, 20, 5, 5, 10, 10));
        Assert.That(doc.CropToSelection(), Is.True);
        Assert.That((doc.Width, doc.Height), Is.EqualTo((5, 5)));
        Assert.That(doc.Layers[0].Pixels.Get(2, 2), Is.EqualTo(Red));
        Assert.That(doc.Selection!.Bounds(), Is.EqualTo(new IntRect(0, 0, 5, 5)));
        doc.Undo();
        Assert.That(doc.Width, Is.EqualTo(20));
    }

    [Test]
    public void FlipAndTurnTheWholeImage()
    {
        var doc = ImageDocument.Blank(4, 2, Rgba.Transparent);
        doc.AddLayer("L");
        doc.ActiveLayer!.Pixels.Set(0, 0, Red);
        doc.FlipImage(true);
        Assert.That(doc.ActiveLayer.Pixels.Get(3, 0), Is.EqualTo(Red));
        doc.RotateImage90(true);
        Assert.That((doc.Width, doc.Height), Is.EqualTo((2, 4)));
        Assert.That(doc.Layers[0].Pixels.Width, Is.EqualTo(2));
        doc.Undo();
        Assert.That(doc.Width, Is.EqualTo(4));
        doc.FlipLayer(1, false);
        Assert.That(doc.ActiveLayer.Pixels.Get(3, 1), Is.EqualTo(Red));
    }

    [Test]
    public void SelectionEditsAreHistory()
    {
        var doc = ImageDocument.Blank(20, 20, Rgba.White);
        doc.SelectAll();
        Assert.That(doc.Selection!.Bounds(), Is.EqualTo(new IntRect(0, 0, 20, 20)));
        doc.InvertSelection();
        Assert.That(doc.Selection, Is.Null); // nothing left selected is no selection
        doc.Select(Mask.Rectangle(20, 20, 0, 0, 10, 10), SelectionMode.Replace);
        doc.Select(Mask.Rectangle(20, 20, 5, 5, 15, 15), SelectionMode.Add);
        Assert.That(doc.Selection!.Bounds(), Is.EqualTo(new IntRect(0, 0, 15, 15)));
        doc.Select(Mask.Rectangle(20, 20, 0, 0, 5, 5), SelectionMode.Subtract);
        Assert.That(doc.Selection![2, 2], Is.EqualTo(0));
        doc.SelectNone();
        Assert.That(doc.Selection, Is.Null);
        doc.Undo();
        Assert.That(doc.Selection![7, 7], Is.EqualTo(255));
        doc.Layers[0].Pixels.Set(3, 3, Red);
        doc.SelectNone();
        doc.SelectLayerContents();
        Assert.That(doc.Selection!.Bounds(), Is.EqualTo(new IntRect(0, 0, 20, 20))); // the background is opaque
        Assert.Throws<ArgumentException>(() => doc.SetSelection(Mask.Full(5, 5)));
        var d2 = ImageDocument.Blank(10, 10, Rgba.White);
        d2.Select(Mask.Rectangle(10, 10, 2, 2, 4, 4), SelectionMode.Subtract);
        Assert.That(d2.Selection, Is.Null);
        d2.Select(Mask.Rectangle(10, 10, 2, 2, 4, 4), SelectionMode.Add);
        Assert.That(d2.Selection!.Bounds(), Is.EqualTo(new IntRect(2, 2, 2, 2)));
    }

    [Test]
    public void HistoryIsCappedAndRedoIsClearedByANewEdit()
    {
        var doc = ImageDocument.Blank(4, 4, Rgba.White);
        for (int i = 0; i < 130; i++) doc.SetVisible(0, i % 2 == 1);
        Assert.That(doc.UndoNames.Count <= 100, Is.True);
        doc.Undo();
        Assert.That(doc.CanRedo, Is.True);
        doc.SelectAll();
        Assert.That(doc.CanRedo, Is.False);
        doc.ClearHistory();
        Assert.That(doc.CanUndo, Is.False);
    }

    [Test]
    public void LayerAtFindsTheTopmostVisibleLayerWithPixels()
    {
        var doc = ImageDocument.Blank(10, 10, Rgba.Transparent);
        var top = doc.AddLayer("Top");
        top.Pixels.Set(2, 2, Red);
        Assert.That(doc.LayerAt(2, 2), Is.EqualTo(1));
        Assert.That(doc.LayerAt(5, 5), Is.EqualTo(-1));
        doc.SetVisible(1, false);
        Assert.That(doc.LayerAt(2, 2), Is.EqualTo(-1));
    }

    // ---- keeping and writing ----------------------------------------------------------------------------------------

    [Test]
    public void ImageRoundTripsThroughItsSavedForm()
    {
        var doc = ImageDocument.Blank(33, 17, new Rgba(10, 20, 30), "Roundtrip");
        var layer = doc.AddLayer("Ink", pixels: Solid(33, 17, Red.WithAlpha(90)));
        doc.SetBlend(1, BlendMode.Overlay);
        doc.SetOpacity(1, 0.4);
        doc.SetVisible(1, false);
        doc.SetSelection(Mask.Rectangle(33, 17, 3, 3, 9, 9));
        string json = JsonConvert.SerializeObject(doc.ToData());
        var back = ImageDocument.FromData(JsonConvert.DeserializeObject<ImageData>(json)!);
        Assert.That(back.Name, Is.EqualTo("Roundtrip"));
        Assert.That((back.Width, back.Height), Is.EqualTo((33, 17)));
        Assert.That(back.Layers.Count, Is.EqualTo(2));
        Assert.That(back.Layers[1].Name, Is.EqualTo("Ink"));
        Assert.That(back.Layers[1].Blend, Is.EqualTo(BlendMode.Overlay));
        Assert.That(back.Layers[1].Opacity, Is.EqualTo(0.4));
        Assert.That(back.Layers[1].Visible, Is.False);
        Assert.That(back.Layers[0].Pixels.Pixels.SequenceEqual(doc.Layers[0].Pixels.Pixels), Is.True);
        Assert.That(back.Layers[1].Pixels.Pixels.SequenceEqual(doc.Layers[1].Pixels.Pixels), Is.True);
        Assert.That(back.Selection!.Bounds(), Is.EqualTo(new IntRect(3, 3, 6, 6)));
        Assert.That(back.ActiveIndex, Is.EqualTo(1));
        Assert.That(json.Length < 2000, Is.True); // flat pictures compress
    }

    [Test]
    public void DamagedSavedFormIsRefused()
    {
        var data = ImageDocument.Blank(10, 10, Red).ToData();
        data.Width = 20;
        Assert.Throws<InvalidOperationException>(() => ImageDocument.FromData(data));
        Assert.Throws<InvalidOperationException>(() => ImageDocument.FromData(new ImageData { Width = 5, Height = 5 }));
        Assert.That(new ImageData().IsEmpty, Is.True);
    }

    [Test]
    public void PngWriterMakesAValidFile()
    {
        var r = new Raster(7, 5);
        for (int y = 0; y < 5; y++)
        for (int x = 0; x < 7; x++)
            r.Set(x, y, new Rgba((byte)(x * 30), (byte)(y * 40), 99, (byte)(100 + x * 20)));

        var png = ImageEncoders.Png(r);
        Assert.That(png.Take(8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), Is.True);

        // walk the chunks, check each CRC, and unpack the pixels
        int at = 8;
        var idat = new MemoryStream();
        var types = new List<string>();
        while (at < png.Length)
        {
            uint len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            types.Add(type);
            uint crc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + (int)len));
            Assert.That(crc, Is.EqualTo(Crc32(png.AsSpan(at + 4, 4 + (int)len))));
            if (type == "IDAT") idat.Write(png, at + 8, (int)len);
            at += 12 + (int)len;
        }

        Assert.That(types, Is.EqualTo(new[] { "IHDR", "IDAT", "IEND" }));
        Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)), Is.EqualTo(7));
        Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)), Is.EqualTo(5));
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        var bytes = raw.ToArray();
        Assert.That(bytes.Length, Is.EqualTo((7 * 4 + 1) * 5));
        Assert.That(bytes[0], Is.EqualTo(0));
        var p = r.Get(3, 2);
        int o = (7 * 4 + 1) * 2 + 1 + 3 * 4;
        Assert.That((bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3]), Is.EqualTo((p.R, p.G, p.B, p.A)));
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c ^ 0xFFFFFFFFu;
    }

    [Test]
    public void IconHoldsEachSizeAsAPng()
    {
        var r   = Solid(64, 32, Red);
        var ico = ImageEncoders.Ico(r, [16, 32, 256]);
        Assert.That(BitConverter.ToUInt16(ico, 0), Is.EqualTo(0));
        Assert.That(BitConverter.ToUInt16(ico, 2), Is.EqualTo(1));
        Assert.That(BitConverter.ToUInt16(ico, 4), Is.EqualTo(3));
        Assert.That(ico[6], Is.EqualTo(16));   // first entry: 16 wide
        Assert.That(ico[6 + 16], Is.EqualTo(32));
        Assert.That(ico[6 + 32], Is.EqualTo(0)); // 256 is written as 0
        uint size   = BitConverter.ToUInt32(ico, 6 + 8);
        uint offset = BitConverter.ToUInt32(ico, 6 + 12);
        Assert.That(ico.Skip((int)offset).Take(4).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }), Is.True);
        Assert.That(offset + size <= ico.Length, Is.True);
        Assert.That(ImageEncoders.Ico(r, [999]).Length > 0, Is.True);
    }

    [Test]
    public void FlattenOverAColor()
    {
        var r = new Raster(2, 1);
        r.Set(0, 0, Red);
        var f = ImageEncoders.Flatten(r, Rgba.White);
        Assert.That(f.Get(0, 0), Is.EqualTo(Red));
        Assert.That(f.Get(1, 0), Is.EqualTo(Rgba.White));
        var half = new Raster(1, 1);
        half.Set(0, 0, Rgba.Black.WithAlpha(128));
        Assert.That(Math.Abs(ImageEncoders.Flatten(half, Rgba.White).Get(0, 0).R - 127) <= 1, Is.True);
    }

    // ---- transform state: dragging handles and turning --------------------------------------------------------------

    private static readonly IntRect TBox = new(10, 20, 100, 50);

    private static void Near(Pt actual, Pt expected, double tolerance = 1e-6)
    {
        Assert.That(Math.Abs(actual.X - expected.X) < tolerance && Math.Abs(actual.Y - expected.Y) < tolerance, Is.True, $"expected ({expected.X}, {expected.Y}) but got ({actual.X}, {actual.Y})");
    }

    private static Pt Middle => new(TBox.X + TBox.Width / 2.0, TBox.Y + TBox.Height / 2.0);

    [Test]
    public void DraggingACornerKeepsTheOppositeCornerAndFollowsThePointer()
    {
        var state = new TransformState().DragHandle(TBox, 2, new Pt(210, 120));
        var m     = state.Matrix(Middle);
        Near(m.Apply(TransformState.HandlePoint(TBox, 0)), new Pt(10, 20));
        Near(m.Apply(TransformState.HandlePoint(TBox, 2)), new Pt(210, 120));
        Assert.That(state.ScaleX, Is.EqualTo(2).Within(1e-9));
        Assert.That(state.ScaleY, Is.EqualTo(2).Within(1e-9));
    }

    [Test]
    public void DraggingACornerOfATurnedAndSkewedBoxStillPinsTheAnchor()
    {
        var start  = new TransformState(30, 1.5, 0.8, 0.2, -0.1, 12, -7);
        var before = start.Matrix(Middle);
        var anchor = before.Apply(TransformState.HandlePoint(TBox, 0));
        var pointer = new Pt(150, 90);
        var after  = start.DragHandle(TBox, 2, pointer);
        var m      = after.Matrix(Middle);
        Near(m.Apply(TransformState.HandlePoint(TBox, 0)), anchor);
        Near(m.Apply(TransformState.HandlePoint(TBox, 2)), pointer);
        Assert.That(after.Angle, Is.EqualTo(30));
        Assert.That(after.SkewX, Is.EqualTo(0.2));
    }

    [Test]
    public void DraggingAnEdgeScalesOneWayOnly()
    {
        var state = new TransformState().DragHandle(TBox, 5, new Pt(210, 999));
        var m     = state.Matrix(Middle);
        Assert.That(state.ScaleY, Is.EqualTo(1).Within(1e-9));
        Assert.That(state.ScaleX, Is.EqualTo(2).Within(1e-9));
        Near(m.Apply(TransformState.HandlePoint(TBox, 7)), new Pt(10, 45));
        Near(m.Apply(TransformState.HandlePoint(TBox, 5)), new Pt(210, 45));
    }

    [Test]
    public void ScalingAboutTheCenterKeepsTheCenter()
    {
        var state = new TransformState().DragHandle(TBox, 2, new Pt(Middle.X + 100, Middle.Y + 50), aboutCenter: true);
        var m     = state.Matrix(Middle);
        Near(m.Apply(Middle), Middle);
        Near(m.Apply(TransformState.HandlePoint(TBox, 0)), new Pt(Middle.X - 100, Middle.Y - 50));
    }

    [Test]
    public void UniformScalingKeepsTheShape()
    {
        var start = new TransformState(0, 2, 1);
        var state = start.DragHandle(TBox, 2, new Pt(400, 90), uniform: true);
        Assert.That(state.ScaleX / state.ScaleY, Is.EqualTo(2).Within(1e-9));
        Assert.That(state.ScaleX, Is.Not.EqualTo(2));
    }

    [Test]
    public void AltOnAnEdgeSkewsAndPinsTheOppositeEdge()
    {
        var state = new TransformState().DragHandle(TBox, 4, new Pt(Middle.X + 20, TBox.Y), skew: true);
        var m     = state.Matrix(Middle);
        Near(m.Apply(TransformState.HandlePoint(TBox, 6)), TransformState.HandlePoint(TBox, 6));
        Near(m.Apply(TransformState.HandlePoint(TBox, 4)), new Pt(Middle.X + 20, TBox.Y));
        Assert.That(state.ScaleX, Is.EqualTo(1));
        Assert.That(state.ScaleY, Is.EqualTo(1));

        var side = new TransformState().DragHandle(TBox, 5, new Pt(TBox.Right, Middle.Y - 15), skew: true);
        var n    = side.Matrix(Middle);
        Near(n.Apply(TransformState.HandlePoint(TBox, 7)), TransformState.HandlePoint(TBox, 7));
        Near(n.Apply(TransformState.HandlePoint(TBox, 5)), new Pt(TBox.Right, Middle.Y - 15));
    }

    [Test]
    public void SkewIsLimited()
    {
        var state = new TransformState().DragHandle(TBox, 4, new Pt(Middle.X + 100000, TBox.Y), skew: true);
        // the top edge sliding right slants the box with a negative tangent (x moves by SkewX for each step down)
        Assert.That(state.SkewX, Is.EqualTo(-Math.Tan(80 * Math.PI / 180)).Within(1e-9));
    }

    [Test]
    public void ScalesNeverReachZero()
    {
        var state = new TransformState().DragHandle(TBox, 2, new Pt(10, 20));
        Assert.That(Math.Abs(state.ScaleX) >= 0.01 && Math.Abs(state.ScaleY) >= 0.01, Is.True);
        Assert.That(state.Matrix(Middle).IsInvertible, Is.True);
        Assert.That(TransformState.Away(-0.0001), Is.EqualTo(-0.01));
    }

    [Test]
    public void DraggingPastTheAnchorMirrors()
    {
        var state = new TransformState().DragHandle(TBox, 2, new Pt(-90, 20 + 50));
        Assert.That(state.ScaleX, Is.LessThan(0.0));
        Assert.That(state.ScaleY, Is.GreaterThan(0.0));
        Near(state.Matrix(Middle).Apply(TransformState.HandlePoint(TBox, 0)), new Pt(10, 20));
    }

    [Test]
    public void TurningFollowsThePointerAroundTheMiddle()
    {
        var state = new TransformState().Turned(Middle, new Pt(Middle.X + 50, Middle.Y), new Pt(Middle.X, Middle.Y + 50), false);
        Assert.That(state.Angle, Is.EqualTo(90).Within(1e-6));
        Near(state.Matrix(Middle).Apply(new Pt(Middle.X + 10, Middle.Y)), new Pt(Middle.X, Middle.Y + 10));

        var snapped = new TransformState().Turned(Middle, new Pt(Middle.X + 50, Middle.Y), new Pt(Middle.X + 50, Middle.Y + 15), true);
        Assert.That(snapped.Angle % 15, Is.EqualTo(0).Within(1e-9));

        var moved = new TransformState(0, 1, 1, 0, 0, 30, 0).Turned(Middle, new Pt(Middle.X + 30 + 50, Middle.Y), new Pt(Middle.X + 30, Middle.Y + 50), false);
        Assert.That(moved.Angle, Is.EqualTo(90).Within(1e-6));
        Near(moved.Matrix(Middle).Apply(Middle), new Pt(Middle.X + 30, Middle.Y));
    }

    [Test]
    public void MirroringFlipsTheScreenAboutTheMiddle()
    {
        var start = new TransformState(25, 1.2, 0.7, 0.1, 0.05, 4, 6);
        foreach (bool horizontal in new[] { true, false })
        {
            var flipped = start.Mirrored(horizontal);
            var a       = start.Matrix(Middle);
            var b       = flipped.Matrix(Middle);
            var mid     = a.Apply(Middle);
            foreach (var p in new[] { new Pt(10, 20), new Pt(110, 20), new Pt(60, 70), new Pt(33, 41) })
            {
                var was = a.Apply(p);
                var now = b.Apply(p);
                Near(now, horizontal ? new Pt(2 * mid.X - was.X, was.Y) : new Pt(was.X, 2 * mid.Y - was.Y), 1e-6);
            }

            Near(start.Mirrored(horizontal).Mirrored(horizontal).Matrix(Middle).Apply(new Pt(40, 40)), a.Apply(new Pt(40, 40)));
        }
    }

    [Test]
    public void MovingRoundsToWholePixels()
    {
        var state = new TransformState().Moved(2.4, -3.6);
        Assert.That(state.MoveX, Is.EqualTo(2));
        Assert.That(state.MoveY, Is.EqualTo(-4));
        Assert.That(new TransformState().IsNone, Is.True);
        Assert.That(state.IsNone, Is.False);
    }

    [Test]
    public void ASkewThatCannotBeUndoneChangesNothing()
    {
        // a pair of skews whose product is 1 flattens the box; the drag is ignored rather than throwing
        var start = new TransformState(0, 1, 1, 2, 0.5);
        Assert.That(start.DragHandle(TBox, 2, new Pt(300, 300)), Is.EqualTo(start));
    }
}
