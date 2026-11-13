using System.IO;
using System.Text;
using Silo.Providers.Archive;
using Silo.Providers.Format;
using Silo.Providers.Format.Containers;

namespace Silo.Tests.Containers;

public class ContainerFormatTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "silo-container-tests", Guid.NewGuid().ToString("N"));
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

    public static IEnumerable<IContainerFormat> WritableFormats() => [new ZipFormat(), new TarFormat(), new TarGzFormat(), new IsoFormat(), new CabFormat()];

    /// <summary>A folder with a file at the top, one in a folder, one with a name outside ASCII, and an empty folder.</summary>
    private string Source()
    {
        string src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(src, "docs", "deep"));
        Directory.CreateDirectory(Path.Combine(src, "empty"));
        File.WriteAllText(Path.Combine(src, "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(src, "docs", "deep", "notes.md"), "# notes\nsecond line");
        File.WriteAllText(Path.Combine(src, "docs", "naïve ☃.txt"), "snow");
        return src;
    }

    private static string ReadEntry(IContainerFormat format, string container, string entry)
    {
        using var stream = format.OpenEntry(container, entry);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [TestCaseSource(nameof(WritableFormats))]
    public void A_container_made_from_a_folder_lists_and_reads_back_its_files(IContainerFormat format)
    {
        string container = Path.Combine(_dir, ContainerFormats.FileName(format, "made"));
        format.CreateFrom(container, Source());

        var entries = format.List(container);
        var files   = entries.Where(e => !e.IsDirectory).Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();

        Assert.That(files, Is.EqualTo(new[] { "docs/deep/notes.md", "docs/naïve ☃.txt", "readme.txt" }));
        Assert.That(entries.Where(e => e.IsDirectory).Select(e => e.Path), Does.Contain("docs").And.Contain("docs/deep"));
        Assert.That(entries.Single(e => e.Path == "readme.txt").Size, Is.EqualTo(5));
        Assert.That(ReadEntry(format, container, "docs/deep/notes.md"), Is.EqualTo("# notes\nsecond line"));
        Assert.That(ReadEntry(format, container, "docs/naïve ☃.txt"), Is.EqualTo("snow"));
    }

    [TestCaseSource(nameof(WritableFormats))]
    public void An_empty_container_can_be_made_and_then_filled_and_emptied(IContainerFormat format)
    {
        string container = Path.Combine(_dir, ContainerFormats.FileName(format, "empty"));
        format.Create(container);
        Assert.That(format.List(container), Is.Empty);

        string file = Path.Combine(_dir, "added.json");
        File.WriteAllText(file, "{\"a\":1}");
        format.Update(container, [new ContainerChange.AddFile("data/added.json", file), new ContainerChange.AddFile("top.json", file)]);
        Assert.That(format.List(container).Where(e => !e.IsDirectory).Select(e => e.Path), Is.EquivalentTo(new[] { "data/added.json", "top.json" }));
        Assert.That(ReadEntry(format, container, "data/added.json"), Is.EqualTo("{\"a\":1}"));

        format.Update(container, [new ContainerChange.Remove("data")]);
        Assert.That(format.List(container).Select(e => e.Path), Is.EquivalentTo(new[] { "top.json" }));
    }

    [TestCaseSource(nameof(WritableFormats))]
    public void Extracting_a_folder_keeps_its_name_and_what_is_in_it(IContainerFormat format)
    {
        string container = Path.Combine(_dir, ContainerFormats.FileName(format, "x"));
        format.CreateFrom(container, Source());

        string target = Path.Combine(_dir, "out");
        format.ExtractTo(container, target, "docs");

        Assert.That(File.ReadAllText(Path.Combine(target, "docs", "deep", "notes.md")), Is.EqualTo("# notes\nsecond line"));
        Assert.That(File.Exists(Path.Combine(target, "readme.txt")), Is.False);
    }

    [Test]
    public void A_missing_entry_is_reported_as_not_found()
    {
        var    zip       = new ZipFormat();
        string container = Path.Combine(_dir, "a.zip");
        zip.CreateFrom(container, Source());

        Assert.Throws<FileNotFoundException>(() => zip.OpenEntry(container, "nope.txt"));
    }

    [Test]
    public void The_format_is_found_by_the_longest_extension()
    {
        Assert.That(ContainerFormats.For("logs.tar.gz"), Is.InstanceOf<TarGzFormat>());
        Assert.That(ContainerFormats.For("logs.tgz"), Is.InstanceOf<TarGzFormat>());
        Assert.That(ContainerFormats.For("logs.tar"), Is.InstanceOf<TarFormat>());
        Assert.That(ContainerFormats.For("disc.ISO"), Is.InstanceOf<IsoFormat>());
        Assert.That(ContainerFormats.For("setup.cab"), Is.InstanceOf<CabFormat>());
        Assert.That(ContainerFormats.For("notes.txt"), Is.Null);
    }

    [Test]
    public void Entry_paths_cannot_escape_the_folder_they_are_taken_into()
    {
        string root = Path.Combine(_dir, "root");
        Assert.That(ContainerPaths.Resolve(root, "../evil.txt"), Is.Null);
        Assert.That(ContainerPaths.Resolve(root, "a/../../evil.txt"), Is.Null);
        Assert.That(ContainerPaths.Resolve(root, "a/b.txt"), Is.EqualTo(Path.Combine(root, "a", "b.txt")));
    }

    [Test]
    public void Folders_that_are_only_implied_are_listed()
    {
        var entries = ContainerPaths.Complete([new ContainerEntry("a/b/c.txt", false, 3)]);
        Assert.That(entries.Select(e => (e.Path, e.IsDirectory)), Is.EqualTo(new[] { ("a", true), ("a/b", true), ("a/b/c.txt", false) }));
    }
}
