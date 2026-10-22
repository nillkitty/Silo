using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class BlockPolicyTests
{
    private static UnicodeDatabase Db => UnicodeDatabase.Default;

    [TestCase("CJK Unified Ideographs")]
    [TestCase("CJK Unified Ideographs Extension A")]
    [TestCase("CJK Unified Ideographs Extension B")]
    [TestCase("CJK Compatibility Ideographs")]
    [TestCase("CJK Compatibility Ideographs Supplement")]
    [TestCase("CJK Radicals Supplement")]
    [TestCase("Kangxi Radicals")]
    [TestCase("CJK Strokes")]
    [TestCase("Hangul Syllables")]
    [TestCase("Yi Syllables")]
    [TestCase("Yi Radicals")]
    [TestCase("Tangut")]
    [TestCase("Tangut Components")]
    [TestCase("Tangut Supplement")]
    [TestCase("Nushu")]
    [TestCase("Khitan Small Script")]
    public void TheIdeographBlocksAreOffByDefault(string block)
    {
        Assert.That(BlockPolicy.IsOffByDefault(block), Is.True);
    }

    [TestCase("Basic Latin")]
    [TestCase("Hiragana")]
    [TestCase("Katakana")]
    [TestCase("Bopomofo")]
    [TestCase("Hangul Jamo")]
    [TestCase("Hangul Compatibility Jamo")]
    [TestCase("CJK Symbols and Punctuation")]
    [TestCase("CJK Compatibility")]
    [TestCase("CJK Compatibility Forms")]
    [TestCase("Enclosed CJK Letters and Months")]
    [TestCase("Egyptian Hieroglyphs")]
    [TestCase("Cuneiform")]
    [TestCase("Emoticons")]
    [TestCase("Arrows")]
    [TestCase("Linear B Syllabary")]
    public void EverythingElseIsOnByDefault(string block)
    {
        Assert.That(BlockPolicy.IsOffByDefault(block), Is.False);
    }

    [Test]
    public void TheRealBlockListIsMostlyOnAndTheBigOnesAreOff()
    {
        var off = BlockPolicy.DisabledNames(Db.Blocks, null);

        Assert.That(off, Does.Contain("CJK Unified Ideographs"));
        Assert.That(off, Does.Contain("Hangul Syllables"));
        Assert.That(off, Does.Not.Contain("Hiragana"));
        Assert.That(off, Does.Not.Contain("Basic Latin"));
        Assert.That(off.Count, Is.LessThan(40), "only the ideograph-like blocks are off");
        Assert.That(Db.Blocks.Where(b => b.Name.StartsWith("CJK Unified Ideographs", StringComparison.Ordinal)).All(b => off.Contains(b.Name)), Is.True);
    }

    [Test]
    public void ChoicesAreDifferencesFromTheDefault()
    {
        var overrides = new BlockOverrides { ForceOn = ["Hangul Syllables"], ForceOff = ["Cuneiform"] };

        Assert.That(BlockPolicy.IsEnabled("Hangul Syllables", overrides), Is.True);
        Assert.That(BlockPolicy.IsEnabled("Cuneiform", overrides), Is.False);
        Assert.That(BlockPolicy.IsEnabled("Basic Latin", overrides), Is.True);
        Assert.That(BlockPolicy.IsEnabled("Nushu", overrides), Is.False);
        Assert.That(BlockPolicy.IsEnabled("cuneiform", overrides), Is.False, "block names are compared without regard to case");
    }

    [Test]
    public void ABlockTheDefaultSwitchesOffCanBeSwitchedOnAndBack()
    {
        var blocks = Db.Blocks;
        var on     = BlockPolicy.FromEnabled(blocks, _ => true);
        Assert.That(on.ForceOff, Is.Empty);
        Assert.That(on.ForceOn, Does.Contain("Hangul Syllables"));

        var off = BlockPolicy.FromEnabled(blocks, _ => false);
        Assert.That(off.ForceOn, Is.Empty);
        Assert.That(off.ForceOff, Does.Contain("Basic Latin"));

        var same = BlockPolicy.FromEnabled(blocks, n => !BlockPolicy.IsOffByDefault(n));
        Assert.That(same.IsDefault, Is.True);
    }

    [Test]
    public void TheDisabledCodePointsAreTheOnesInTheDisabledBlocks()
    {
        var set = BlockPolicy.DisabledCodePoints(Db.Blocks, null);

        Assert.That(set.Contains(0x4E00), Is.True, "a Han ideograph");
        Assert.That(set.Contains(0xAC00), Is.True, "a Hangul syllable");
        Assert.That(set.Contains(0x41), Is.False);
        Assert.That(set.Contains(0x3042), Is.False, "Hiragana stays");
        Assert.That(set.Contains(0x1F600), Is.False);
    }

    [TestCase("漢", true)]
    [TestCase("U+4E00", true)]
    [TestCase("U+4E00-9FFF", true)]
    [TestCase("0x4E00", true)]
    [TestCase("\\u4e00", true)]
    [TestCase("&#20013;", true)]
    [TestCase("block:\"CJK Unified Ideographs\"", true)]
    [TestCase("arrow block:arrows", true)]
    [TestCase("plane:2", true)]
    [TestCase("moon", false)]
    [TestCase("green circle", false)]
    [TestCase("cat:Lu age:>=6.0", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void TextThatNamesSomethingParticularLooksPastTheSwitchedOffBlocks(string? text, bool expected)
    {
        Assert.That(BlockPolicy.TextAsksForHiddenBlocks(text), Is.EqualTo(expected));
    }
}
