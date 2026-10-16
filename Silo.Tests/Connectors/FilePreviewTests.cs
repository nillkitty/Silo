using System.IO;
using Silo.Connectors.File;

namespace Silo.Tests.Connectors;

[SetCulture("en-US")]
public class FilePreviewTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "silo-preview-" + Guid.NewGuid().ToString("N"));
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
            // best effort
        }
    }

    private static FileModel Model(string path) => FileConnection.FromPath(new FileConnector(), path);

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        global::System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    [Test]
    public void A_missing_path_gets_a_message()
    {
        var preview = FilePreviews.Compute(Model(Path.Combine(_dir, "nope")));

        Assert.That(preview, Is.InstanceOf<MessagePreview>());
    }

    [Test]
    public void A_text_file_shows_its_text()
    {
        var preview = FilePreviews.Compute(Model(Write("a.txt", "hello\nworld"u8.ToArray())));

        Assert.That(preview, Is.InstanceOf<TextPreview>());
        Assert.That(((TextPreview)preview).Text, Is.EqualTo("hello\nworld"));
        Assert.That(((TextPreview)preview).Truncated, Is.False);
    }

    [Test]
    public void A_text_file_with_a_byte_order_mark_is_decoded()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i'];

        var preview = (TextPreview)FilePreviews.Compute(Model(Write("bom.txt", bytes)));

        Assert.That(preview.Text, Is.EqualTo("hi"));
    }

    [Test]
    public void A_long_text_file_is_cut_short_and_says_so()
    {
        var bytes = new byte[FilePreviews.TextLimit + 10];
        Array.Fill(bytes, (byte)'a');

        var preview = (TextPreview)FilePreviews.Compute(Model(Write("big.txt", bytes)));

        Assert.That(preview.Truncated, Is.True);
        Assert.That(preview.Text, Has.Length.EqualTo(FilePreviews.TextLimit));
    }

    [Test]
    public void An_empty_file_is_empty_text()
    {
        var preview = (TextPreview)FilePreviews.Compute(Model(Write("empty.txt", [])));

        Assert.That(preview.Text, Is.Empty);
    }

    [Test]
    public void A_file_with_zero_bytes_in_it_is_not_shown_as_text()
    {
        var preview = FilePreviews.Compute(Model(Write("data.bin", [1, 2, 0, 3, 4])));

        Assert.That(preview, Is.InstanceOf<MessagePreview>());
    }

    [Test]
    public void An_image_file_gives_its_bytes()
    {
        byte[] bytes = [1, 2, 3, 4];

        var preview = FilePreviews.Compute(Model(Write("pic.PNG", bytes)));

        Assert.That(preview, Is.InstanceOf<ImagePreview>());
        Assert.That(((ImagePreview)preview).Bytes, Is.EqualTo(bytes));
    }

    [Test]
    public void A_folder_lists_its_items_folders_first()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        Write("a.txt", "12345"u8.ToArray());

        var preview = (FolderPreview)FilePreviews.Compute(Model(_dir));

        Assert.That(preview.Rows.Select(r => r.Name), Is.EqualTo(new[] { "sub", "a.txt" }));
        Assert.That(preview.Rows[0].Type, Is.EqualTo("Folder"));
        Assert.That(preview.Rows[1].Type, Is.EqualTo("File"));
        Assert.That(preview.Rows[1].Size, Is.EqualTo("5 bytes"));
        Assert.That(preview.Truncated, Is.False);
    }

    [TestCase(0L, "0 bytes")]
    [TestCase(1023L, "1,023 bytes")]
    [TestCase(1024L, "1 KB")]
    [TestCase(1536L, "1.5 KB")]
    [TestCase(5L * 1024 * 1024, "5 MB")]
    public void Sizes_are_shown_in_the_largest_fitting_unit(long bytes, string expected)
    {
        Assert.That(FilePreviews.FormatSize(bytes).Replace('\u00A0', ' '), Is.EqualTo(expected));
    }
}
