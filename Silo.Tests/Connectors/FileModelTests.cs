using System.Diagnostics;
using System.IO;
using Silo.Connectors.File;
using Silo.ViewModel;

namespace Silo.Tests.Connectors;

public class FileModelTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "silo-filemodel-" + Guid.NewGuid().ToString("N"));
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

    private string MakeFile(string name, string content = "x")
    {
        string path = Path.Combine(_dir, name);
        global::System.IO.File.WriteAllText(path, content);
        return path;
    }

    private string MakeFolder(string name)
    {
        string path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ---- kinds -------------------------------------------------------------------------------

    [Test]
    public void A_file_is_a_file_with_the_document_icon()
    {
        var m = Model(MakeFile("a.txt"));

        Assert.That(m.Kind, Is.EqualTo(FileKind.File));
        Assert.That(m.IsContainer, Is.False);
        Assert.That(m.IconKey, Is.EqualTo("Document"));
    }

    [Test]
    public void A_folder_is_a_directory_with_the_folder_icon()
    {
        var m = Model(MakeFolder("sub"));

        Assert.That(m.Kind, Is.EqualTo(FileKind.Directory));
        Assert.That(m.IsContainer, Is.True);
        Assert.That(m.IconKey, Is.EqualTo("Folder17"));
    }

    [Test]
    public void A_path_that_is_not_there_is_missing()
    {
        var m = Model(Path.Combine(_dir, "nope"));

        Assert.That(m.Kind, Is.EqualTo(FileKind.Missing));
        Assert.That(m.IsContainer, Is.False);
        Assert.That(m.IconKey, Is.EqualTo("FsFileMissing"));
        Assert.That(m.GetChildren(), Is.Empty);
    }

    [Test]
    public void The_root_of_a_drive_is_a_volume_named_by_its_path()
    {
        string root = Path.GetPathRoot(_dir)!;
        var    m    = Model(root);

        Assert.That(m.Kind, Is.EqualTo(FileKind.Volume));
        Assert.That(m.IsContainer, Is.True);
        Assert.That(m.Name, Is.EqualTo(root));
        Assert.That(m.DriveType, Is.Not.Null);
    }

    [TestCase(DriveType.Fixed, "FsDrive")]
    [TestCase(DriveType.Removable, "FsRemovable")]
    [TestCase(DriveType.CDRom, "FsOptical")]
    [TestCase(DriveType.Network, "FsNetworkDrive")]
    [TestCase(DriveType.Unknown, "FsDrive")]
    public void A_volume_icon_follows_the_drive_type(DriveType type, string expected)
    {
        Assert.That(FileKinds.IconKey(new FileKindInfo(FileKind.Volume, type)), Is.EqualTo(expected));
    }

    [TestCase(FileKind.Junction, "FsFolderJunction")]
    [TestCase(FileKind.DirectorySymlink, "FsFolderLink")]
    [TestCase(FileKind.FileSymlink, "FsFileLink")]
    public void A_link_has_its_own_icon(FileKind kind, string expected)
    {
        Assert.That(FileKinds.IconKey(new FileKindInfo(kind)), Is.EqualTo(expected));
    }

    [Test]
    public void IsRoot_recognises_drive_and_share_roots_but_not_folders()
    {
        Assert.That(FileKinds.IsRoot(Path.GetPathRoot(_dir)!), Is.True);
        Assert.That(FileKinds.IsRoot(_dir), Is.False);
        if (OperatingSystem.IsWindows())
        {
            Assert.That(FileKinds.IsRoot(@"\\server\share"), Is.True);
            Assert.That(FileKinds.IsRoot(@"\\server\share\folder"), Is.False);
        }
    }

    [Test]
    public void A_symbolic_link_to_a_folder_is_a_directory_symlink()
    {
        string target = MakeFolder("target");
        string link   = Path.Combine(_dir, "link");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Ignore("Symbolic links cannot be created here: " + ex.Message);
        }

        var m = Model(link);

        Assert.That(m.Kind, Is.EqualTo(FileKind.DirectorySymlink));
        Assert.That(m.IsContainer, Is.True);
        Assert.That(m.LinkTarget, Is.Not.Null);
    }

    [Test]
    public void A_symbolic_link_to_a_file_is_a_file_symlink()
    {
        string target = MakeFile("target.txt");
        string link   = Path.Combine(_dir, "link.txt");
        try
        {
            global::System.IO.File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Ignore("Symbolic links cannot be created here: " + ex.Message);
        }

        Assert.That(Model(link).Kind, Is.EqualTo(FileKind.FileSymlink));
    }

    [Test]
    public void A_junction_is_a_junction_and_not_a_symbolic_link()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Junctions are a Windows feature.");

        string target   = MakeFolder("target");
        string junction = Path.Combine(_dir, "junction");
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using (var p = Process.Start(start)!)
        {
            p.WaitForExit();
            if (p.ExitCode != 0) Assert.Ignore("mklink /J failed");
        }

        var m = Model(junction);

        Assert.That(m.Kind, Is.EqualTo(FileKind.Junction));
        Assert.That(m.IsContainer, Is.True);
        Assert.That(m.IconKey, Is.EqualTo("FsFolderJunction"));
    }

    // ---- children ----------------------------------------------------------------------------

    [Test]
    public void A_folders_children_are_its_folders_then_its_files_each_sorted_by_name()
    {
        MakeFile("b.txt");
        MakeFolder("zeta");
        MakeFile("a.txt");
        MakeFolder("Alpha");

        var names = Model(_dir).GetChildren().Select(c => c.Name).ToList();

        Assert.That(names, Is.EqualTo(new[] { "Alpha", "zeta", "a.txt", "b.txt" }));
    }

    [Test]
    public void Children_know_their_own_kind_and_parent()
    {
        MakeFile("a.txt");
        MakeFolder("sub");
        var parent = Model(_dir);

        var children = parent.GetChildren();

        Assert.That(children[0].Kind, Is.EqualTo(FileKind.Directory));
        Assert.That(children[1].Kind, Is.EqualTo(FileKind.File));
        Assert.That(children.All(c => ReferenceEquals(c.Parent, parent)), Is.True);
    }

    [Test]
    public void A_file_has_no_children()
    {
        Assert.That(Model(MakeFile("a.txt")).GetChildren(), Is.Empty);
    }

    [Test]
    public void Hidden_items_are_left_out_unless_asked_for()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The Hidden attribute is a Windows feature.");

        MakeFile("shown.txt");
        string hidden = MakeFile("hidden.txt");
        global::System.IO.File.SetAttributes(hidden, FileAttributes.Hidden);

        var model = Model(_dir);
        Assert.That(model.GetChildren().Select(c => c.Name), Is.EqualTo(new[] { "shown.txt" }));

        model.IncludeHidden = true;
        Assert.That(model.GetChildren().Select(c => c.Name), Is.EquivalentTo(new[] { "shown.txt", "hidden.txt" }));
    }

    [Test]
    public async Task Children_can_be_listed_without_holding_up_the_caller()
    {
        MakeFile("a.txt");

        var children = await Model(_dir).GetChildrenAsync();

        Assert.That(children, Has.Count.EqualTo(1));
    }

    // ---- nodes -------------------------------------------------------------------------------

    [Test]
    public void The_node_carries_the_path_the_icon_and_is_expanded()
    {
        var node = Model(_dir).GetNode()!;

        Assert.That(node.DataContext, Is.InstanceOf<FileModel>());
        Assert.That(node.Icon!.ToString(), Is.EqualTo("Folder17"));
        Assert.That(node.IsExpanded, Is.True);
        Assert.That(node.ChildLoader, Is.Not.Null);
    }

    [Test]
    public void A_file_node_has_no_child_loader()
    {
        Assert.That(Model(MakeFile("a.txt")).GetNode()!.ChildLoader, Is.Null);
    }

    [Test]
    public void The_kind_of_item_picks_the_kind_of_node_and_each_keeps_the_model_text_and_uri()
    {
        var folder = Model(_dir).GetNode()!;
        var file   = Model(MakeFile("a.txt")).GetNode()!;

        Assert.That(folder, Is.InstanceOf<FolderNode>());
        Assert.That(folder.TypeName, Is.EqualTo("Directory"));
        Assert.That(file, Is.InstanceOf<FileNode>());
        Assert.That(file.TypeName, Is.EqualTo("File"));
        Assert.That(file.DataContext, Is.InstanceOf<FileModel>());
        Assert.That(file.Text, Is.EqualTo("a.txt"));
        Assert.That(file.Uri, Is.Not.Null);
        Assert.That(file.Icon!.ToString(), Is.EqualTo("Document"));
    }

    [Test]
    public void A_missing_item_gets_a_blocked_node_with_the_missing_icon_and_its_name()
    {
        var node = Model(Path.Combine(_dir, "gone.txt")).GetNode()!;

        Assert.That(node, Is.InstanceOf<BlockedNode>());
        Assert.That(node.Icon!.ToString(), Is.EqualTo("FsFileMissing"));
        Assert.That(node.Text, Is.EqualTo("gone.txt (inaccessible)"));
        Assert.That(node.ChildLoader, Is.Null);
    }

    [Test]
    public async Task Refreshing_a_folder_reads_its_contents_again()
    {
        MakeFile("a.txt");
        var model = Model(_dir);
        var node  = model.GetNode()!;
        await node.LoadChildrenAsync();
        MakeFile("b.txt");

        await model.RefreshAsync();

        Assert.That(node.Children.Select(c => c.Text), Is.EqualTo(new[] { "a.txt", "b.txt" }));
    }

    [Test]
    public async Task Loading_a_folder_node_adds_a_collapsed_node_per_child_and_each_folder_can_be_loaded_in_turn()
    {
        MakeFolder("sub");
        global::System.IO.File.WriteAllText(Path.Combine(_dir, "sub", "inner.txt"), "x");
        MakeFile("a.txt");

        var root = Model(_dir).GetNode()!;
        await root.LoadChildrenAsync();

        Assert.That(root.Children.Select(c => c.Text), Is.EqualTo(new[] { "sub", "a.txt" }));
        Assert.That(root.Children.All(c => !c.IsExpanded), Is.True);
        Assert.That(root.Children[1].ChildLoader, Is.Null);

        var sub = root.Children[0];
        await sub.LoadChildrenAsync();
        Assert.That(sub.Children.Select(c => c.Text), Is.EqualTo(new[] { "inner.txt" }));
        Assert.That(sub.Children[0].Parent, Is.SameAs(sub));
    }

    [Test]
    public void The_connection_node_shows_the_name_the_connection_was_given()
    {
        var connection = FileConnection.FromPath(new FileConnector(), _dir);
        connection.DisplayText = "My data";

        var node = connection.GetNode()!;

        Assert.That(node.Text, Is.EqualTo("My data"));
        Assert.That(connection.Node, Is.SameAs(node));
        Assert.That(connection.GetNode(), Is.SameAs(node));
    }

    [Test]
    public void The_details_name_the_path_and_the_kind()
    {
        var details = ((INodeDetails)Model(MakeFile("a.txt", "hello"))).GetDetails().ToDictionary(r => r.Name, r => r.Value);

        Assert.That(details["Type"], Is.EqualTo("File"));
        Assert.That(details["Path"], Does.EndWith("a.txt"));
        Assert.That(details["Size"], Does.StartWith("5"));
    }
}
