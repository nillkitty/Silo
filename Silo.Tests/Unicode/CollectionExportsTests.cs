using Newtonsoft.Json.Linq;
using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class CollectionExportsTests
{
    private static readonly CollectedCharacter[] Sample =
    [
        new(new CodePoint(0x1F7E2), "green"),
        new(new CodePoint(0x41)),
        new(new CodePoint(0x0)),         // NUL: cannot be written as XML text
        new(new CodePoint(0xD800))       // a lone surrogate: cannot be written in a UTF-8 file
    ];

    [Test]
    public void XmlRoundTrips()
    {
        string xml = CollectionExports.WriteXml(Sample);

        Assert.That(xml, Does.Contain("codePoint=\"U+1F7E2\""));
        Assert.That(xml, Does.Contain("label=\"green\""));
        Assert.That(xml, Does.Contain("🟢"));

        var back = CollectionExports.ReadXml(xml);
        Assert.That(back, Is.EqualTo(Sample));
    }

    [Test]
    public void XmlLeavesOutTextItCannotHold()
    {
        string xml = CollectionExports.WriteXml([new CollectedCharacter(new CodePoint(0))]);
        Assert.That(xml, Does.Contain("codePoint=\"U+0000\""));
        Assert.That(CollectionExports.ReadXml(xml).Single().CodePoint.Value, Is.EqualTo(0));
    }

    [Test]
    public void ALabelWithIllegalCharactersDoesNotBreakTheXml()
    {
        string xml = CollectionExports.WriteXml([new CollectedCharacter(new CodePoint(0x41), "a\u0001b")]);
        Assert.That(CollectionExports.ReadXml(xml).Single().Label, Is.EqualTo("ab"));
    }

    [Test]
    public void XmlThatIsNotXmlIsReportedAsAFormatProblem()
    {
        Assert.Throws<FormatException>(() => CollectionExports.ReadXml("<characters><character"));
    }

    [Test]
    public void YamlIsAListOfMappingsThatReadsBackAsJson()
    {
        string yaml = CollectionExports.WriteYaml(Sample);

        Assert.That(yaml, Does.StartWith("# Unicode Explorer collection\ncharacters:\n  - codePoint: \"U+1F7E2\"\n"));
        Assert.That(yaml, Does.Contain("    label: \"green\"\n"));

        // the JSON a YAML reader would produce for it
        var token = new JObject
                    {
                        ["characters"] = new JArray(Sample.Select(s => new JObject { ["codePoint"] = s.CodePoint.Hex, ["label"] = s.Label }))
                    };
        Assert.That(CollectionFormats.FromJson(token), Is.EqualTo(Sample));
    }

    [Test]
    public void AnEmptyCollectionIsAnEmptyYamlList()
    {
        Assert.That(CollectionExports.WriteYaml([]), Does.Contain("characters: []"));
    }

    [Test]
    public void TheHtmlGalleryHasACardPerCharacterWithTheColor()
    {
        string html = CollectionExports.WriteHtmlGallery([new CollectedCharacter(new CodePoint(0x3C), "less")], "My <set>", cp => "#E53935", ColorMode.Glyph);

        Assert.That(html, Does.StartWith("<!doctype html>"));
        Assert.That(html, Does.Contain("<title>My &lt;set&gt;</title>"));
        Assert.That(html, Does.Contain("U+003C"));
        Assert.That(html, Does.Contain("&lt;"));
        Assert.That(html, Does.Contain("less"));
        Assert.That(html, Does.Contain("style=\"color:#E53935\""));
        Assert.That(html, Does.Contain("1 character"));
    }

    [Test]
    public void TheGalleryShowsTheColorModes()
    {
        string Page(ColorMode mode) => CollectionExports.WriteHtmlGallery([new CollectedCharacter(new CodePoint(0x41))], "t", _ => "#43A047", mode);

        Assert.That(Page(ColorMode.Background), Does.Contain("background:#43A04766"));
        Assert.That(Page(ColorMode.Accent), Does.Contain("border-bottom:4px solid #43A047"));
    }

    [Test]
    public void JsonCanBeWrappedInACharactersObject()
    {
        var token = JToken.Parse("{\"characters\":[\"U+41\",{\"codePoint\":\"U+42\",\"label\":\"b\"}]}");
        var read  = CollectionFormats.FromJson(token);

        Assert.That(read.Select(c => c.CodePoint.Value), Is.EqualTo(new[] { 0x41, 0x42 }));
        Assert.That(read[1].Label, Is.EqualTo("b"));
    }
}
