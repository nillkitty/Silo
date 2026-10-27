using Newtonsoft.Json;
using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class CodePointTests
{
    [Test]
    public void WritesItselfInTheUsualWays()
    {
        var green = new CodePoint(0x1F7E2);
        Assert.That(green.Hex, Is.EqualTo("U+1F7E2"));
        Assert.That(new CodePoint(0x41).Hex, Is.EqualTo("U+0041"));
        Assert.That(green.Text, Is.EqualTo("\U0001F7E2"));
        Assert.That(green.Text.Length, Is.EqualTo(2));
        Assert.That(green.IsScalar, Is.True);
        Assert.That(new CodePoint(0xD83D).IsScalar, Is.False);
        Assert.That(new CodePoint(0xD83D).Text.Length, Is.EqualTo(1));
    }

    [Test]
    public void RefusesNumbersOutsideTheCodeSpace()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodePoint(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodePoint(0x110000));
    }

    [Test]
    public void SplitsTextIntoCodePointsKeepingPairsTogether()
    {
        var cps = CodePoint.Enumerate("a\U0001F7E2b").Select(c => c.Value).ToList();
        Assert.That(cps, Is.EqualTo(new List<int> { 0x61, 0x1F7E2, 0x62 }));

        var lone = CodePoint.Enumerate("\uD83Dx").Select(c => c.Value).ToList();
        Assert.That(lone, Is.EqualTo(new List<int> { 0xD83D, 0x78 }));

        Assert.That(CodePoint.ToText(CodePoint.Enumerate("a\U0001F7E2b")), Is.EqualTo("a\U0001F7E2b"));
        Assert.That(CodePoint.Enumerate(null).Count(), Is.EqualTo(0));
    }

    [Test]
    public void ReadsTheWrittenForms()
    {
        string[] forms = ["U+1F7E2", "u+1f7e2", "0x1F7E2", "\\u{1F7E2}", "\\U0001F7E2", "\\uD83D\\uDFE2", "&#128994;", "&#x1F7E2;", "1F7E2", "\U0001F7E2", " U+1F7E2 "];
        foreach (string form in forms)
        {
            Assert.That(CodePoint.TryParse(form, out var cp), Is.True, form);
            Assert.That(cp.Value, Is.EqualTo(0x1F7E2), form);
        }
    }

    [Test]
    public void ABareSingleCharacterIsTheCharacterButTwoHexDigitsAreANumber()
    {
        Assert.That(CodePoint.TryParse("A", out var a), Is.True);
        Assert.That(a.Value, Is.EqualTo(0x41));
        Assert.That(CodePoint.TryParse("41", out var n), Is.True);
        Assert.That(n.Value, Is.EqualTo(0x41));
        Assert.That(CodePoint.TryParse("5", out var five), Is.True);
        Assert.That(five.Value, Is.EqualTo(0x35));
    }

    [Test]
    public void RefusesWhatIsNoCodePoint()
    {
        foreach (string bad in new[] { "", "  ", "U+110000", "U+", "0x", "&#999999999;", "\\u{}", "abéz", "U+XYZ" })
            Assert.That(CodePoint.TryParse(bad, out _), Is.False, bad);

        Assert.That(CodePoint.TryParse(null, out _), Is.False);
    }

    [Test]
    public void ParsesLists()
    {
        Assert.That(CodePoint.ParseList("U+1F7E2 U+1F7E1, U+1F7E0").Select(c => c.Value).ToList(), Is.EqualTo(new List<int> { 0x1F7E2, 0x1F7E1, 0x1F7E0 }));
        Assert.That(CodePoint.ParseList("?v").Select(c => c.Value).ToList(), Is.EqualTo(new List<int> { 0x25B6, 0x221A }));
        Assert.That(CodePoint.ParseList("U+41 b").Select(c => c.Value).ToList(), Is.EqualTo(new List<int> { 0x55, 0x2B, 0x34, 0x31, 0x20, 0x62 }));
        Assert.That(CodePoint.ParseList("").Count, Is.EqualTo(0));
    }

    [Test]
    public void IsWrittenToJsonAsItsHexAndReadBack()
    {
        string json = JsonConvert.SerializeObject(new CodePoint(0x1F7E2));
        Assert.That(json, Is.EqualTo("\"U+1F7E2\""));
        Assert.That(JsonConvert.DeserializeObject<CodePoint>(json).Value, Is.EqualTo(0x1F7E2));

        var list = new[] { new CodePoint(0x25B6), new CodePoint(0x221A) };
        string arrayJson = JsonConvert.SerializeObject(list);
        Assert.That(arrayJson, Is.EqualTo("[\"U+25B6\",\"U+221A\"]"));
        Assert.That(JsonConvert.DeserializeObject<CodePoint[]>(arrayJson)!.Select(c => c.Value).ToList(), Is.EqualTo(new List<int> { 0x25B6, 0x221A }));

        Assert.That(JsonConvert.DeserializeObject<CodePoint>("65").Value, Is.EqualTo(65));
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<CodePoint>("\"not a code point at all\""));
    }

    [Test]
    public void OrdersByValue()
    {
        Assert.That(new CodePoint(1) < new CodePoint(2), Is.True);
        Assert.That(new CodePoint(2) >= new CodePoint(2), Is.True);
        Assert.That(new CodePoint(3).CompareTo(new CodePoint(2)), Is.GreaterThan(0));
        var five = new CodePoint(5);
        Assert.That(new CodePoint(5), Is.EqualTo(five));
    }
}
