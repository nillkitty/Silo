using System.IO;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Silo.Connectors.Containers;
using Silo.Contracts.UI;
using Silo.Providers.Conversion;
using Silo.Providers.Format;
using Silo.Providers.Format.Containers;
using Silo.TextEditing;

namespace Silo.Tests.Containers;

public class FileConverterTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "silo-converter-tests", Guid.NewGuid().ToString("N"));
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

    private static byte[] Rom(int blocks)
    {
        var rom = new byte[blocks * SmdConverter.BlockSize];
        for (int i = 0; i < rom.Length; i++) rom[i] = (byte)(i * 7 + (i >> 8));
        Encoding.ASCII.GetBytes("SEGA MEGA DRIVE ").CopyTo(rom, 0x100);
        return rom;
    }

    [Test]
    public void An_smd_dump_is_offered_as_a_rom_and_converted()
    {
        var    rom   = Rom(2);
        string input = Path.Combine(_dir, "game.smd");
        File.WriteAllBytes(input, SmdConverter.FromBin(rom));

        var targets = FileConverters.TargetsFor(input);
        Assert.That(targets.Select(t => t.Target), Has.One.InstanceOf<MegaDriveRomFormat>());

        var (target, converter) = targets.Single(t => t.Target is MegaDriveRomFormat);
        string output = Path.Combine(_dir, FileConverters.OutputName(input, target));
        Assert.That(Path.GetFileName(output), Is.EqualTo("game.bin"));

        converter.Convert(input, target, output);
        Assert.That(File.ReadAllBytes(output), Is.EqualTo(rom));
    }

    [Test]
    public void A_rom_is_offered_as_an_smd_dump()
    {
        string input = Path.Combine(_dir, "game.bin");
        File.WriteAllBytes(input, Rom(1));

        Assert.That(new SmdRomConverter().TargetsFor(input), Has.One.InstanceOf<SmdFormat>());
    }

    [Test]
    public void A_zip_is_converted_to_a_gzipped_tar()
    {
        string src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "a.txt"), "alpha");
        string zip = Path.Combine(_dir, "pack.zip");
        new ZipFormat().CreateFrom(zip, src);

        var (target, converter) = FileConverters.TargetsFor(zip).Single(t => t.Target is TarGzFormat);
        string output = Path.Combine(_dir, FileConverters.OutputName(zip, target));
        converter.Convert(zip, target, output);

        Assert.That(Path.GetFileName(output), Is.EqualTo("pack.tgz"));
        using var reader = new StreamReader(new TarGzFormat().OpenEntry(output, "sub/a.txt"));
        Assert.That(reader.ReadToEnd(), Is.EqualTo("alpha"));
        Assert.That(FileConverters.OutputName(output, new ZipFormat()), Is.EqualTo("pack.zip"));
    }

    [Test]
    public void Json_is_converted_to_yaml_and_xml()
    {
        string input = Path.Combine(_dir, "config.json");
        File.WriteAllText(input, "{\"server\":{\"name\":\"alpha\",\"port\":8080}}");

        var targets = FileConverters.TargetsFor(input);
        Assert.That(targets.Select(t => t.Target), Has.One.InstanceOf<YamlFormat>().And.One.InstanceOf<XmlFormat>());

        var (yaml, converter) = targets.Single(t => t.Target is YamlFormat);
        string yamlOut = Path.Combine(_dir, FileConverters.OutputName(input, yaml));
        converter.Convert(input, yaml, yamlOut);
        Assert.That(File.ReadAllText(yamlOut), Does.Contain("name: alpha"));

        var xml = targets.Single(t => t.Target is XmlFormat).Target;
        string xmlOut = Path.Combine(_dir, FileConverters.OutputName(input, xml));
        converter.Convert(input, xml, xmlOut);
        Assert.That(XDocument.Load(xmlOut).Descendants("port").Single().Value, Is.EqualTo("8080"));
    }

    [Test]
    public void A_file_nothing_can_read_has_no_targets()
    {
        string input = Path.Combine(_dir, "photo.raw");
        File.WriteAllBytes(input, new byte[100]);
        Assert.That(FileConverters.TargetsFor(input), Is.Empty);
    }

    // ---- data files: what a paste or drop does ---------------------------------------------------------------------

    private sealed class Data(Dictionary<string, object> formats, IReadOnlyList<ICopyable>? sources = null) : IClipboardData
    {
        public ClipboardOperation           Operation => ClipboardOperation.Copy;
        public IReadOnlyList<ICopyable>     Sources   => sources ?? [];
        public IReadOnlyCollection<string>  Formats   => formats.Keys;
        public bool                         Contains(string format) => formats.ContainsKey(format);
        public object?                      Get(string format) => formats.GetValueOrDefault(format);
    }

    [Test]
    public void Json_text_pasted_on_an_object_adds_its_properties()
    {
        var target = JObject.Parse("{\"a\":1}");
        int added  = DataPaste.IntoJson(target, new Data(new() { [ClipboardFormats.Text] = "{\"a\":2,\"b\":[1,2]}" }), TextLanguage.Json);

        Assert.That(added, Is.EqualTo(2));
        Assert.That(target.Properties().Select(p => p.Name), Is.EqualTo(new[] { "a", "a (2)", "b" }));
    }

    [Test]
    public void A_dropped_yaml_file_goes_into_an_array_and_an_xml_element()
    {
        string file = Path.Combine(_dir, "server.yaml");
        File.WriteAllText(file, "name: alpha\nport: 8080\n");

        var array = new JArray();
        DataPaste.IntoJson(array, new Data(new() { [ClipboardFormats.Files] = new[] { file } }), TextLanguage.Json);
        Assert.That(array.Single()["name"]!.Value<string>(), Is.EqualTo("alpha"));

        var element = new XElement("servers");
        DataPaste.IntoXml(element, new Data(new() { [ClipboardFormats.Files] = new[] { file } }));
        Assert.That(element.Element("server")!.Element("port")!.Value, Is.EqualTo("8080"));
    }

    [Test]
    public void An_item_of_one_data_file_moves_into_another()
    {
        string from = Path.Combine(_dir, "from.json"), to = Path.Combine(_dir, "to.xml");
        File.WriteAllText(from, "{\"keep\":1,\"move\":{\"x\":\"y\"}}");
        File.WriteAllText(to, "<root />");

        var source = new DataDocument(from, TextLanguage.Json);
        var target = new DataDocument(to, TextLanguage.Xml);
        var item   = new DataItemModel(source, ((JObject)source.Json!)["move"]!, "move");
        var root   = new DataItemModel(target, target.Root, null);

        var data = new Data(new() { [ClipboardFormats.Text] = "ignored" }, [item]);
        Assert.That(root.CanPaste(data, PasteMode.Duplicate), Is.True);

        var result = root.Paste(data, PasteMode.Duplicate);
        Assert.That(result.Effect, Is.EqualTo(PasteEffect.Copied));
        item.CutCompleted(result);

        Assert.That(XDocument.Load(to).Root!.Element("move")!.Element("x")!.Value, Is.EqualTo("y"));
        Assert.That(JObject.Parse(File.ReadAllText(from)).ContainsKey("move"), Is.False);
    }

    [Test]
    public void An_item_cannot_be_moved_into_itself()
    {
        string path = Path.Combine(_dir, "self.json");
        File.WriteAllText(path, "{\"outer\":{\"inner\":{}}}");
        var doc   = new DataDocument(path, TextLanguage.Json);
        var outer = new DataItemModel(doc, doc.Json!["outer"]!, "outer");
        var inner = new DataItemModel(doc, doc.Json!["outer"]!["inner"]!, "inner");

        var cut = new CutData([outer]);
        Assert.That(inner.CanPaste(cut, PasteMode.Move), Is.False);
    }

    private sealed class CutData(IReadOnlyList<ICopyable> sources) : IClipboardData
    {
        public ClipboardOperation          Operation => ClipboardOperation.Cut;
        public IReadOnlyList<ICopyable>    Sources   => sources;
        public IReadOnlyCollection<string> Formats   => [];
        public bool                        Contains(string format) => false;
        public object?                     Get(string format) => null;
    }
}
