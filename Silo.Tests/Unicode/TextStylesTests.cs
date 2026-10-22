using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class TextStylesTests
{
    [TestCase("negative-circled", "hello", "\U0001F157\U0001F154\U0001F15B\U0001F15B\U0001F15E")]
    [TestCase("circled", "Hello 1", "Ⓗⓔⓛⓛⓞ ①")]
    [TestCase("squared", "hi", "\U0001F137\U0001F138")]
    [TestCase("fullwidth", "Hi 1!", "Ｈｉ　１！")]
    [TestCase("bold", "Hi1", "\U0001D407\U0001D422\U0001D7CF")]
    [TestCase("small-caps", "Hello", "ʜᴇʟʟᴏ")]
    [TestCase("regional", "us", "\U0001F1FA\U0001F1F8")]
    [TestCase("reversed", "abc", "cba")]
    public void WritesTheTextInTheStyle(string id, string text, string expected)
    {
        Assert.That(TextStyles.Apply(id, text), Is.EqualTo(expected));
    }

    [Test]
    public void LettersThatLiveElsewhereInTheScriptAlphabetsAreUsed()
    {
        // MATHEMATICAL ITALIC SMALL H is a hole: PLANCK CONSTANT stands in for it
        Assert.That(TextStyles.Apply("italic", "h"), Is.EqualTo("ℎ"));
        Assert.That(TextStyles.Apply("script", "B"), Is.EqualTo("ℬ"));
        Assert.That(TextStyles.Apply("double-struck", "R"), Is.EqualTo("ℝ"));
        Assert.That(TextStyles.Apply("fraktur", "H"), Is.EqualTo("ℌ"));
    }

    [Test]
    public void CharactersWithNoStyledFormStayAsTheyAre()
    {
        Assert.That(TextStyles.Apply("bold", "é!"), Is.EqualTo("é!"));
        Assert.That(TextStyles.Apply("negative-circled", "?"), Is.EqualTo("?"));
    }

    [Test]
    public void StyledTextCanBeTakenBackToPlainText()
    {
        Assert.That(TextStyles.Apply("plain", TextStyles.Apply("bold", "Hello 2026")), Is.EqualTo("Hello 2026"));
        Assert.That(TextStyles.Apply("plain", TextStyles.Apply("fullwidth", "Hello")), Is.EqualTo("Hello"));
        Assert.That(TextStyles.Apply("plain", TextStyles.Apply("circled", "Hello")), Is.EqualTo("Hello"));
    }

    [Test]
    public void MarksFollowEveryCharacterButWhiteSpace()
    {
        Assert.That(TextStyles.Apply("strikethrough", "a b"), Is.EqualTo("a̶ b̶"));
    }

    [Test]
    public void UpsideDownTurnsTheTextAround()
    {
        Assert.That(TextStyles.Apply("upside-down", "hello"), Is.EqualTo("oןןǝɥ"));
    }

    [Test]
    public void EveryStyleHasAUniqueIdAndWorksOnAnyText()
    {
        Assert.That(TextStyles.All.Select(s => s.Id).Distinct().Count(), Is.EqualTo(TextStyles.All.Count));
        foreach (var style in TextStyles.All)
        {
            Assert.That(style.Apply(string.Empty), Is.Empty, style.Id);
            Assert.That(() => style.Apply("Hello, \U0001F7E2 Wörld 2026"), Throws.Nothing, style.Id);
        }
    }

    [Test]
    public void AnUnknownStyleIsAnError()
    {
        Assert.That(() => TextStyles.Apply("nope", "x"), Throws.ArgumentException);
    }
}

[TestFixture]
public class CharacterColorsTests
{
    [Test]
    public void ThePaletteHasSixteenDistinctColors()
    {
        Assert.That(CharacterColors.Palette, Has.Count.EqualTo(16));
        Assert.That(CharacterColors.Palette.Select(c => c.Hex).Distinct().Count(), Is.EqualTo(16));
        Assert.That(CharacterColors.Palette.Select((c, i) => c.Index == i).All(x => x));
        Assert.That(CharacterColors.Named("green")!.Name, Is.EqualTo("Green"));
    }

    [Test]
    public void MarksCanBeSetChangedAndRemoved()
    {
        var map = new CharacterColorMap();
        Assert.That(map.Set(0x1F7E2, 5), Is.True);
        Assert.That(map.Set(0x1F7E2, 5), Is.False, "nothing changed");
        Assert.That(map.ColorOf(0x1F7E2)!.Name, Is.EqualTo("Green"));
        Assert.That(map.Set(0x1F7E2, null), Is.True);
        Assert.That(map.Get(0x1F7E2), Is.Null);
        Assert.That(map.Count, Is.EqualTo(0));
    }

    [Test]
    public void APlaceOutsideThePaletteIsNoMark()
    {
        var map = CharacterColorMap.From(new Dictionary<int, int> { [65] = 3, [66] = 99, [67] = -1 });
        Assert.That(map.Count, Is.EqualTo(1));
        Assert.That(map.Get(65), Is.EqualTo(3));
    }

    [Test]
    public void TheFilterTellsMarkedFromUnmarked()
    {
        var map = new CharacterColorMap();
        map.Set(65, 3);
        Assert.That(map.Passes(65, null), Is.True);
        Assert.That(map.Passes(66, null), Is.True);
        Assert.That(map.Passes(65, -1), Is.True);
        Assert.That(map.Passes(66, -1), Is.False);
        Assert.That(map.Passes(66, -2), Is.True);
        Assert.That(map.Passes(65, 3), Is.True);
        Assert.That(map.Passes(65, 4), Is.False);
    }
}

[TestFixture]
public class CodePointSetTests
{
    [Test]
    public void KeepsWhatWasAddedAndCountsIt()
    {
        var set = new CodePointSet();
        Assert.That(set.Add(0x1F600), Is.True);
        Assert.That(set.Add(0x1F600), Is.False);
        set.AddRange([65, 66, 0x10FFFF]);
        Assert.That(set.Count, Is.EqualTo(4));
        Assert.That(set.Contains(66), Is.True);
        Assert.That(set.Contains(67), Is.False);
        Assert.That(set.Contains(0x10FFFF), Is.True);
        Assert.That(set.Contains(-1), Is.False);
        Assert.That(set.Add(0x110000), Is.False);
    }

    [Test]
    public void UnionCountsEverythingOnce()
    {
        var a = new CodePointSet();
        var b = new CodePointSet();
        a.AddRange([1, 2, 3]);
        b.AddRange([3, 4]);
        a.UnionWith(b);
        Assert.That(a.Count, Is.EqualTo(4));
    }

    [Test]
    public void InvisibleByDesignCharactersAreKeptWhenHidingGlyphlessOnes()
    {
        Assert.That(GlyphVisibility.Keep("Lo", drawable: false, keepUnassigned: false), Is.False);
        Assert.That(GlyphVisibility.Keep("Lo", drawable: true, keepUnassigned: false), Is.True);
        Assert.That(GlyphVisibility.Keep("Cc", drawable: false, keepUnassigned: false), Is.True);
        Assert.That(GlyphVisibility.Keep("Cf", drawable: false, keepUnassigned: false), Is.True);
        Assert.That(GlyphVisibility.Keep("Cn", drawable: false, keepUnassigned: false), Is.False);
        Assert.That(GlyphVisibility.Keep("Cn", drawable: false, keepUnassigned: true), Is.True);
    }
}
