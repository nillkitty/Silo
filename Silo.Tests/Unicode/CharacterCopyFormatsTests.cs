using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class CharacterCopyFormatsTests
{
    [TestCase(0x1017, "'\\u1017'")]
    [TestCase(0x41, "'\\u0041'")]
    [TestCase(0x1F7E2, "\"\\U0001F7E2\"")]
    public void WritesAnEscapeForCLikeLanguages(int value, string expected)
    {
        Assert.That(CharacterCopyFormats.CEscape(new CodePoint(value)), Is.EqualTo(expected));
    }

    [TestCase(0x1017, "&#x1017;")]
    [TestCase(0x1F7E2, "&#x1F7E2;")]
    public void WritesACharacterReference(int value, string expected)
    {
        Assert.That(CharacterCopyFormats.Entity(new CodePoint(value)), Is.EqualTo(expected));
    }

    [TestCase(0x1017, "[char]0x1017")]
    [TestCase(0x1F7E2, "[char]::ConvertFromUtf32(0x1F7E2)")]
    public void WritesPowerShell(int value, string expected)
    {
        Assert.That(CharacterCopyFormats.PowerShell(new CodePoint(value)), Is.EqualTo(expected));
    }

    [TestCase(0x1017, "ChrW(&H1017)")]
    [TestCase(0x1F7E2, "Char.ConvertFromUtf32(&H1F7E2)")]
    public void WritesVisualBasic(int value, string expected)
    {
        Assert.That(CharacterCopyFormats.VisualBasic(new CodePoint(value)), Is.EqualTo(expected));
    }

    [Test]
    public void AltCodesComeFromTheWindowsCodePagesOrTheHexForm()
    {
        Assert.That(CharacterCopyFormats.AltCode(new CodePoint(0xE9)), Does.StartWith("Alt+"));
        Assert.That(CharacterCopyFormats.AltCode(new CodePoint(0x1F7E2)), Is.EqualTo("Alt+(+)1F7E2"));
    }

    [Test]
    public void ColoredHtmlPutsTheColorWhereTheModeSays()
    {
        var a = new CodePoint('<');
        Assert.That(CharacterCopyFormats.ColoredHtml(a, "#E53935", ColorMode.Glyph), Is.EqualTo("<span style=\"color:#E53935;\">&lt;</span>"));
        Assert.That(CharacterCopyFormats.ColoredHtml(a, "#E53935", ColorMode.Background), Is.EqualTo("<span style=\"background-color:#E53935;\">&lt;</span>"));
        Assert.That(CharacterCopyFormats.ColoredHtml(a, "#E53935", ColorMode.Accent), Is.EqualTo("<span style=\"border-bottom:3px solid #E53935;\">&lt;</span>"));
        Assert.That(CharacterCopyFormats.ColoredHtml(a, null, ColorMode.Glyph), Is.EqualTo("&lt;"));
    }

    [Test]
    public void ColoredXamlPutsTheColorWhereTheModeSays()
    {
        var g = new CodePoint(0x1F7E2);
        Assert.That(CharacterCopyFormats.ColoredXaml(g, "#43A047", ColorMode.Glyph), Is.EqualTo("<TextBlock Text=\"&#x1F7E2;\" Foreground=\"#43A047\" />"));
        Assert.That(CharacterCopyFormats.ColoredXaml(g, "#43A047", ColorMode.Background), Does.StartWith("<Border Background=\"#43A047\">"));
        Assert.That(CharacterCopyFormats.ColoredXaml(g, "#43A047", ColorMode.Accent), Does.Contain("BorderThickness=\"0,0,0,3\""));
        Assert.That(CharacterCopyFormats.ColoredXaml(g, null, ColorMode.Glyph), Is.EqualTo("<TextBlock Text=\"&#x1F7E2;\" />"));
    }
}
