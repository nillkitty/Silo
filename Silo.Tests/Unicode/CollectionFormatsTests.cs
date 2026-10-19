using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class CollectionFormatsTests
{
    private static readonly CollectedCharacter[] Sample =
    [
        new(new CodePoint(0x1F7E2), "green"), new(new CodePoint(0x25B6)), new(new CodePoint(0x2C)), new(new CodePoint(0x22), "quote, \"mark\"")
    ];

    private static List<(int, string?)> Flat(IEnumerable<CollectedCharacter> items) => items.Select(i => (i.CodePoint.Value, i.Label)).ToList();

    [Test]
    public void WritesThePlainCharacters()
    {
        Assert.That(CollectionFormats.Write(Sample, CollectionFormat.Text), Is.EqualTo("\U0001F7E2\u25B6,\""));
    }

    [Test]
    public void WritesTheCodePointList()
    {
        string text = CollectionFormats.Write(Sample.Take(2), CollectionFormat.CodePointList);
        Assert.That(text, Is.EqualTo("U+1F7E2  green (LARGE GREEN CIRCLE)\nU+25B6  BLACK RIGHT-POINTING TRIANGLE\n"));
    }

    [Test]
    public void EveryFormatReadsBackWhatItWrote()
    {
        foreach (var format in new[] { CollectionFormat.Json, CollectionFormat.Tsv, CollectionFormat.Csv, CollectionFormat.CodePointList })
        {
            string text = CollectionFormats.Write(Sample, format);
            var    back = CollectionFormats.Read(text, format);
            Assert.That(back.Select(b => b.CodePoint.Value).ToList(), Is.EqualTo(Sample.Select(s => s.CodePoint.Value).ToList()), format.ToString());

            // the code point list has no room for a label of its own, so it does not keep them
            if (format != CollectionFormat.CodePointList)
                Assert.That(Flat(back), Is.EqualTo(Flat(Sample)), format.ToString());

            // and the format is recognised without being told
            Assert.That(Flat(CollectionFormats.Read(text)).Select(x => x.Item1).ToList(), Is.EqualTo(Flat(Sample).Select(x => x.Item1).ToList()), "sniffed " + format);
        }
    }

    [Test]
    public void WritesJsonWithTheNameCategoryAndBlock()
    {
        string json = CollectionFormats.Write([Sample[0]], CollectionFormat.Json);
        Assert.That(json, Does.Contain("\"codePoint\": \"U+1F7E2\""));
        Assert.That(json, Does.Contain("\"name\": \"LARGE GREEN CIRCLE\""));
        Assert.That(json, Does.Contain("\"label\": \"green\""));
        Assert.That(json, Does.Contain("\"category\": \"So\""));
        Assert.That(json, Does.Contain("\"block\": \"Geometric Shapes Extended\""));
    }

    [Test]
    public void ReadsTextWhichIsJustCharactersOrCodePoints()
    {
        Assert.That(CollectionFormats.Read("\u25B6\u221A\r\n\u00AE").Select(c => c.CodePoint.Value).ToList(), Is.EqualTo(new List<int> { 0x25B6, 0x221A, 0xAE }));
        Assert.That(CollectionFormats.Read("U+25B6 U+221A").Select(c => c.CodePoint.Value).ToList(), Is.EqualTo(new List<int> { 0x25B6, 0x221A }));
        Assert.That(CollectionFormats.Read("").Count, Is.EqualTo(0));
        Assert.That(CollectionFormats.Read(null).Count, Is.EqualTo(0));
        Assert.That(CollectionFormats.Read("\uFEFFU+41\nU+42").Select(c => c.CodePoint.Value).ToList(), Is.EqualTo(new List<int> { 0x41, 0x42 }));
    }

    [Test]
    public void ReadsJsonOfSeveralShapes()
    {
        Assert.That(CollectionFormats.Read("[\"U+41\", 66, {\"codePoint\":\"U+43\",\"label\":\"c\"}]").Select(c => c.CodePoint.Value).ToList(), Is.EqualTo(new List<int> { 0x41, 66, 0x43 }));
        Assert.That(CollectionFormats.Read("{\"codePoint\":\"U+43\"}").Count, Is.EqualTo(1));
    }

    [Test]
    public void RefusesBrokenFiles()
    {
        Assert.Throws<FormatException>(() => CollectionFormats.Read("[ not json", CollectionFormat.Json));
        Assert.Throws<FormatException>(() => CollectionFormats.Read("[{\"name\":\"x\"}]"));
        Assert.Throws<FormatException>(() => CollectionFormats.Read("Name\tLabel\nx\ty", CollectionFormat.Tsv));
        Assert.Throws<FormatException>(() => CollectionFormats.Read("CodePoint\tName\nnot a code point\ty"));
        Assert.Throws<FormatException>(() => CollectionFormats.Read("U+41\nnot", CollectionFormat.CodePointList));
    }

    [Test]
    public void KnowsFormatsByExtension()
    {
        Assert.That(CollectionFormats.FromExtension("a.json"), Is.EqualTo(CollectionFormat.Json));
        Assert.That(CollectionFormats.FromExtension("C:\\x\\a.TSV"), Is.EqualTo(CollectionFormat.Tsv));
        Assert.That(CollectionFormats.FromExtension("a.csv"), Is.EqualTo(CollectionFormat.Csv));
        Assert.That(CollectionFormats.FromExtension("a.txt"), Is.EqualTo(CollectionFormat.Text));
        Assert.That(CollectionFormats.FromExtension("a.bin"), Is.Null);
        Assert.That(CollectionFormats.FromExtension(null), Is.Null);
    }
}
