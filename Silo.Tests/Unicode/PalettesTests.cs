using Newtonsoft.Json;
using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class PalettesTests
{
    [Test]
    public void ANameThatIsFreeIsKept()
    {
        Assert.That(PaletteRules.UniqueName(["Arrows"], "Math"), Is.EqualTo("Math"));
    }

    [Test]
    public void ANameThatIsTakenGetsANumber()
    {
        Assert.That(PaletteRules.UniqueName(["Arrows", "arrows 2"], "Arrows"), Is.EqualTo("Arrows 3"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ANameIsNeverEmpty(string? wanted)
    {
        Assert.That(PaletteRules.UniqueName([], wanted), Is.EqualTo("Palette"));
        Assert.That(PaletteRules.UniqueName(["Palette"], wanted), Is.EqualTo("Palette 2"));
    }

    [Test]
    public void AWindowThatIsFarOffTheScreenIsMovedBack()
    {
        var (left, top) = PaletteRules.KeepOnScreen(5000, -300, 200, 100, 0, 0, 1920, 1080);
        Assert.That(left, Is.EqualTo(1720));
        Assert.That(top, Is.EqualTo(0));
    }

    [Test]
    public void AWindowThatIsOnTheScreenStaysWhereItIs()
    {
        Assert.That(PaletteRules.KeepOnScreen(100, 200, 200, 100, 0, 0, 1920, 1080), Is.EqualTo((100d, 200d)));
    }

    [Test]
    public void AWindowOnASecondMonitorToTheLeftStaysThere()
    {
        // a virtual screen that starts at -1920
        Assert.That(PaletteRules.KeepOnScreen(-1500, 50, 200, 100, -1920, 0, 3840, 1080), Is.EqualTo((-1500d, 50d)));
    }

    [Test]
    public void AWindowBiggerThanTheScreenGoesToItsCorner()
    {
        var (left, top) = PaletteRules.KeepOnScreen(300, 300, 5000, 5000, 0, 0, 1920, 1080);
        Assert.That((left, top), Is.EqualTo((0d, 0d)));
    }

    [Test]
    public void TheSuggestedSizeGrowsWithTheCharactersButIsLimited()
    {
        var (w1, h1) = PaletteRules.SuggestSize(1, 40);
        var (w2, h2) = PaletteRules.SuggestSize(200, 40);
        Assert.That(w2, Is.GreaterThan(w1));
        Assert.That(h2, Is.GreaterThan(h1));
        Assert.That(h2, Is.LessThanOrEqualTo(10 * 40 + 48));
    }

    [Test]
    public void APaletteSurvivesBeingKeptAsJson()
    {
        var palette = new PaletteDefinition { Name = "Arrows", CodePoints = [0x2190, 0x1F7E2], ShowOnLoad = true, Left = 10.5, Top = 20 };
        var back    = JsonConvert.DeserializeObject<List<PaletteDefinition>>(JsonConvert.SerializeObject(new List<PaletteDefinition> { palette }))!;
        Assert.That(back, Has.Count.EqualTo(1));
        Assert.That(back[0].Name, Is.EqualTo("Arrows"));
        Assert.That(back[0].CodePoints, Is.EqualTo(new[] { 0x2190, 0x1F7E2 }));
        Assert.That(back[0].ShowOnLoad, Is.True);
        Assert.That(back[0].AlwaysOnTop, Is.True);
        Assert.That(back[0].Left, Is.EqualTo(10.5));
        Assert.That(back[0].Width, Is.Null);
    }

    [Test]
    public void APaletteKeptBeforePagesGetsItsCharactersOnAFirstPage()
    {
        var palette = new PaletteDefinition { CodePoints = [0x41, 0x42, 0x41] };
        PaletteRules.Normalize(palette);
        Assert.That(palette.Pages, Has.Count.EqualTo(1));
        Assert.That(palette.Page().CodePoints, Is.EqualTo(new[] { 0x41, 0x42 }));
        Assert.That(palette.CodePoints, Is.Empty);
        Assert.That(palette.TileSize, Is.EqualTo(PaletteRules.DefaultTileSize));
    }

    [Test]
    public void PagesSurviveBeingKeptAsJson()
    {
        var palette = new PaletteDefinition
                      {
                          Pages = [new PalettePage { Name = "A", CodePoints = [1, 2] }, new PalettePage { Name = "B", CodePoints = [3] }],
                          CurrentPage = 1, Locked = true, LockTabs = false, TileSize = 56
                      };
        var back = JsonConvert.DeserializeObject<PaletteDefinition>(JsonConvert.SerializeObject(palette))!;
        PaletteRules.Normalize(back);
        Assert.That(back.Pages.Select(p => p.Name), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(back.Page().CodePoints, Is.EqualTo(new[] { 3 }));
        Assert.That(back.TilesAreLocked(), Is.True);
        Assert.That(back.TabsAreLocked(), Is.False);
        Assert.That(back.TileSize, Is.EqualTo(56));
    }

    [Test]
    public void MovingItemsPutsThemWhereTheyWereDropped()
    {
        var list  = new List<int> { 0, 1, 2, 3, 4 };
        int first = PaletteRules.Move(list, [0, 1], 4); // before the 4
        Assert.That(list, Is.EqualTo(new[] { 2, 3, 0, 1, 4 }));
        Assert.That(first, Is.EqualTo(2));

        PaletteRules.Move(list, [4], 0);
        Assert.That(list, Is.EqualTo(new[] { 4, 2, 3, 0, 1 }));
    }

    [Test]
    public void InsertingLeavesOutWhatIsThereAlready()
    {
        var list = new List<int> { 1, 2 };
        Assert.That(PaletteRules.InsertNew(list, [2, 3, 3], 1), Is.EqualTo(1));
        Assert.That(list, Is.EqualTo(new[] { 1, 3, 2 }));
    }

    [TestCase(110, 40, 3)]
    [TestCase(10, 40, 1)]
    [TestCase(59, 40, 1)]
    [TestCase(61, 40, 2)]
    public void TheTilesThatFitAreTheNearestWholeNumber(double room, double pitch, int expected)
    {
        Assert.That(PaletteRules.Fit(room, pitch), Is.EqualTo(expected));
    }
}
