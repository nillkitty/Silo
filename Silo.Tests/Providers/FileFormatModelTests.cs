using System.IO;
using Newtonsoft.Json.Linq;
using Silo.Providers.Diagramming;
using Silo.Providers.Document;
using Silo.Providers.Format;
using Silo.TextEditing;

namespace Silo.Tests.Providers;

[TestFixture]
public class FileFormatModelTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "silo-formats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private Uri Write(string name, string text)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return new Uri(path);
    }

    [Test]
    public void A_format_claims_its_extensions_with_or_without_the_dot()
    {
        var jpeg = Formats.Of<JpegFormat>();

        Assert.That(jpeg.Claims("photo.JPEG"), Is.True);
        Assert.That(jpeg.Claims(".jpg"), Is.True);
        Assert.That(jpeg.Claims("jpg"), Is.True);
        Assert.That(jpeg.Claims("photo.png"), Is.False);
        Assert.That(jpeg.Claims(null), Is.False);
    }

    [Test]
    public void Dialog_pieces_come_from_the_format()
    {
        var jpeg = Formats.Of<JpegFormat>();

        Assert.That(jpeg.DefaultExtension(), Is.EqualTo(".jpg"));
        Assert.That(jpeg.Pattern(), Is.EqualTo("*.jpg;*.jpeg"));
        Assert.That(jpeg.Filter(), Is.EqualTo("JPEG Image (*.jpg;*.jpeg)|*.jpg;*.jpeg"));
        Assert.That(jpeg.Filter(true), Does.EndWith("|" + Formats.AllFiles));
        Assert.That(jpeg.FileName("photo"), Is.EqualTo("photo.jpg"));
        Assert.That(Formats.Of<SiloFileFormat>().WithExtension("remote"), Is.EqualTo("remote.silo"));
        Assert.That(Formats.Of<SiloFileFormat>().WithExtension("remote.SILO"), Is.EqualTo("remote.SILO"));
    }

    [Test]
    public void A_filter_of_several_formats_can_lead_with_all_of_them()
    {
        string filter = Formats.Filter([Formats.Of<DrawioFormat>(), Formats.Of<XmlFormat>()], "draw.io diagrams", true);

        Assert.That(filter, Does.StartWith("draw.io diagrams|*.drawio;*.xml|"));
        Assert.That(filter, Does.EndWith("|" + Formats.AllFiles));
    }

    [TestCase("site/app.js?v=3", "application/javascript")]
    [TestCase("a.CSS", "text/css")]
    [TestCase("a.woff2", "font/woff2")]
    [TestCase("a.svg#frag", "image/svg+xml")]
    [TestCase("a.png", "image/png")]
    [TestCase("a.unknown", null)]
    public void The_content_type_comes_from_the_format(string path, string? expected)
    {
        Assert.That(Formats.ContentTypeOf(path), Is.EqualTo(expected));
    }

    [TestCase("a.json", TextLanguage.Json)]
    [TestCase("a.har", TextLanguage.Json)]
    [TestCase("a.csproj", TextLanguage.Xml)]
    [TestCase("a.drawio", TextLanguage.Xml)]
    [TestCase("a.yml", TextLanguage.Yaml)]
    [TestCase("a.csv", TextLanguage.PlainText)]
    public void The_editor_language_comes_from_the_format_or_its_base(string path, TextLanguage expected)
    {
        Assert.That(TextLanguages.FromFileName(path), Is.EqualTo(expected));
    }

    [Test]
    public void Kinds_of_format_are_found_by_what_they_are()
    {
        Assert.That(Formats.Is<IImageFormat>("a.webp"), Is.True);
        Assert.That(Formats.Is<Silo.Providers.Reflection.IWebDocumentFormat>("a.pdf"), Is.True);
        Assert.That(Formats.Is<Silo.Providers.Reflection.IProgramFormat>("lib/a.DLL"), Is.True);
        Assert.That(Formats.Is<Silo.Providers.Reflection.IProgramFormat>("a.txt"), Is.False);
    }

    [Test]
    public void Delimited_files_bind_as_rows()
    {
        var uri  = Write("t.tsv", "Name\tAge\nAda\t36\n");
        var rows = Formats.ForFile(uri.LocalPath).OfType<IBindableFormat>().First().Bind(uri);

        Assert.That(rows, Is.InstanceOf<DelimitedRows>());
        Assert.That(((DelimitedRows)rows!).Columns, Is.EqualTo(new[] { "Name", "Age" }));
    }

    [Test]
    public void Json_binds_as_data()
    {
        var uri   = Write("d.json", "{\"a\": 1}");
        var bound = Formats.ForFile(uri.LocalPath).OfType<IBindableFormat>().First().Bind(uri);

        Assert.That(bound, Is.InstanceOf<JObject>());
    }
}
