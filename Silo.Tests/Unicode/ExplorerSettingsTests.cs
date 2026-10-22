using System.Globalization;
using Newtonsoft.Json;
using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class ExplorerSettingsTests
{
    [TestCase("en", "Latn")]
    [TestCase("de", "Latn")]
    [TestCase("ru", "Cyrl")]
    [TestCase("el", "Grek")]
    [TestCase("ar", "Arab")]
    [TestCase("he", "Hebr")]
    [TestCase("hi", "Deva")]
    [TestCase("th", "Thai")]
    [TestCase("ja", "Hira")]
    [TestCase("ko", "Hang")]
    [TestCase("zh", "Hani")]
    [TestCase("xx", "Latn")]
    [TestCase(null, "Latn")]
    public void TheDefaultScriptFollowsTheLanguage(string? language, string expected)
    {
        Assert.That(ScriptDefaults.ForLanguage(language), Is.EqualTo(expected));
    }

    [Test]
    public void TheDefaultScriptOfACultureIsThatOfItsLanguage()
    {
        Assert.That(ScriptDefaults.ForCulture(new CultureInfo("ru-RU")), Is.EqualTo("Cyrl"));
        Assert.That(ScriptDefaults.ForCulture(CultureInfo.InvariantCulture), Is.EqualTo("Latn"));
        Assert.That(ScriptDefaults.ForCulture(null), Is.EqualTo("Latn"));
    }

    [Test]
    public void TheDefaultScriptsExistInTheData()
    {
        var codes = UnicodeDatabase.Default.Scripts.Select(s => s.Code).ToHashSet();
        foreach (string lang in new[] { "en", "ru", "el", "ar", "he", "hi", "bn", "gu", "pa", "ta", "te", "kn", "ml", "si", "th", "lo", "km", "my", "bo", "ka", "hy", "am", "ja", "ko", "zh" })
            Assert.That(codes, Does.Contain(ScriptDefaults.ForLanguage(lang)), lang);
    }

    [Test]
    public void SettingsSurviveBeingWrittenAndRead()
    {
        var settings = new ExplorerSettings
                       {
                           ColorMode = ColorMode.Background, ListView = true, DefaultScript = "Grek", ShowDetails = false,
                           Blocks    = new BlockOverrides { ForceOn = ["Hangul Syllables"], ForceOff = ["Cuneiform"] },
                           Page      = new PageSetup { Landscape = true, GlyphSize = 40 }
                       };

        var back = JsonConvert.DeserializeObject<ExplorerSettings>(JsonConvert.SerializeObject(settings))!;
        Assert.That(back.ColorMode, Is.EqualTo(ColorMode.Background));
        Assert.That(back.ListView, Is.True);
        Assert.That(back.DefaultScript, Is.EqualTo("Grek"));
        Assert.That(back.ShowDetails, Is.False);
        Assert.That(back.ShowCollection, Is.True);
        Assert.That(back.Blocks.ForceOn, Is.EqualTo(new[] { "Hangul Syllables" }));
        Assert.That(back.Blocks.ForceOff, Is.EqualTo(new[] { "Cuneiform" }));
        Assert.That(back.Page.Landscape, Is.True);
        Assert.That(back.Page.GlyphSize, Is.EqualTo(40));
    }

    [Test]
    public void ACopyDoesNotShareTheLists()
    {
        var a = new ExplorerSettings();
        var b = a.Clone();
        b.Blocks.ForceOn.Add("Nushu");
        b.Page.GlyphSize = 99;

        Assert.That(a.Blocks.ForceOn, Is.Empty);
        Assert.That(a.Page.GlyphSize, Is.EqualTo(28));
    }

    [Test]
    public void TheDefaultsAreWhatTheExplorerShipsWith()
    {
        var s = new ExplorerSettings();
        Assert.That(s.ColorMode, Is.EqualTo(ColorMode.Glyph));
        Assert.That(s.Blocks.IsDefault, Is.True);
        Assert.That(s.DefaultScript, Is.Null);
        Assert.That(new[] { s.ShowCodepoints, s.ShowDetails, s.ShowCollection, s.ShowGenerators, s.ShowSaved }, Is.All.True);
    }

    [Test]
    public void PageSetupIsKeptInRange()
    {
        var p = new PageSetup { GlyphSize = 1000, MarginHundredthsInch = -5 }.Normalized();
        Assert.That(p.GlyphSize, Is.EqualTo(144));
        Assert.That(p.MarginHundredthsInch, Is.EqualTo(0));
    }
}
