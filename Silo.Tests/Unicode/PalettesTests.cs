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
}
