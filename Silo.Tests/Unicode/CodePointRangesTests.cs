using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class CodePointRangesTests
{
    [TestCase("U+0041-U+005A", 0x41, 0x5A)]
    [TestCase("U+0041..U+005A", 0x41, 0x5A)]
    [TestCase("41-5A", 0x41, 0x5A)]
    [TestCase("0x41-0x5A", 0x41, 0x5A)]
    [TestCase("1F600-1F64F", 0x1F600, 0x1F64F)]
    [TestCase("5A-41", 0x41, 0x5A)]
    [TestCase("a-z", 0x61, 0x7A)]
    public void ReadsARange(string text, int first, int last)
    {
        var parsed = CodePointRanges.Parse(text);
        Assert.That(parsed.Problems, Is.Empty);
        Assert.That(parsed.Ranges, Is.EqualTo(new[] { new CodeRange(first, last) }));
    }

    [Test]
    public void ReadsSeveralPartsInAnyOfTheSeparators()
    {
        var parsed = CodePointRanges.Parse("U+0041, 0x2600; 1F600-1F602\n🟢");

        Assert.That(parsed.Problems, Is.Empty);
        Assert.That(parsed.Enumerate().ToArray(), Is.EqualTo(new[] { 0x41, 0x2600, 0x1F600, 0x1F601, 0x1F602, 0x1F7E2 }));
        Assert.That(parsed.Count, Is.EqualTo(6));
    }

    [Test]
    public void JoinsRunsThatOverlapOrTouch()
    {
        var parsed = CodePointRanges.Parse("U+41-45 U+44-50 U+51 U+60");
        Assert.That(parsed.Ranges, Is.EqualTo(new[] { new CodeRange(0x41, 0x51), new CodeRange(0x60, 0x60) }));
    }

    [Test]
    public void ReportsWhatItCouldNotRead()
    {
        var parsed = CodePointRanges.Parse("U+41 nonsense U+110000 12-xyz");

        Assert.That(parsed.Ranges, Is.EqualTo(new[] { new CodeRange(0x41, 0x41) }));
        Assert.That(parsed.Problems, Is.EqualTo(new[] { "nonsense", "U+110000", "12-xyz" }));
    }

    [Test]
    public void EmptyTextIsNothing()
    {
        Assert.That(CodePointRanges.Parse(null).Count, Is.EqualTo(0));
        Assert.That(CodePointRanges.Parse("  ").Ranges, Is.Empty);
    }

    [Test]
    public void ALoneHyphenIsTheCharacter()
    {
        Assert.That(CodePointRanges.Parse("-").Ranges, Is.EqualTo(new[] { new CodeRange('-', '-') }));
    }
}
