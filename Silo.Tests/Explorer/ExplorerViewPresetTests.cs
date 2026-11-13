using Silo.Explorer;

namespace Silo.Tests.Explorer;

public class ExplorerViewPresetTests
{
    private static ExplorerNode Node(string key, string header, string kind, params ExplorerNode[] children)
    {
        var node = new ExplorerNode(key, header, kind);
        foreach (var child in children) node.Children.Add(child);
        return node;
    }

    private static IReadOnlyList<ExplorerNode> Grouped()
    {
        var silo = Node("silo", "Your Silo", "Silo",
                        Node("local/tables", "Tables", "Folder", Node("t/1", "Zebra", "Table")),
                        Node("types", "Types", "Folder",
                             Node("enums", "Enums", "Folder", Node("e/1", "Colour", "Enum", Node("e/1/red", "Red", "Enum value")))),
                        Node("saved", "Saved Objects", "Saved Objects",
                             Node("saved/linear", "Linear (2)", "Group", Node("saved/linear/a", "alpha", "Saved Object"))));
        var connection = Node("conn/1", "Backup.zip", "Connection", Node("conn/1/x", "inside.txt", "File in Archive"));
        var tool       = Node("app/tool", "Unicode Explorer", "Tool");
        silo.LinkParents();
        return [silo, connection, tool];
    }

    [Test]
    public void Grouped_shows_the_tree_as_it_is_built()
    {
        var grouped = Grouped();
        Assert.That(ExplorerViewPresets.Grouped.Arrange(grouped), Is.SameAs(grouped));
    }

    [Test]
    public void Flat_lists_every_named_item_by_name_without_the_folders()
    {
        var flat = ExplorerViewPresets.Flat.Arrange(Grouped());

        Assert.That(flat.Select(n => n.Header), Is.EqualTo(new[] { "alpha", "Backup.zip", "Colour", "Zebra" }));
        Assert.That(flat.All(n => n.Parent is null), Is.True);
    }

    [Test]
    public void Flat_items_keep_what_is_in_them()
    {
        var flat = ExplorerViewPresets.Flat.Arrange(Grouped());

        Assert.That(flat.Single(n => n.Header == "Colour").Children.Select(c => c.Header), Is.EqualTo(new[] { "Red" }));
        Assert.That(flat.Single(n => n.Header == "Backup.zip").Children, Has.Count.EqualTo(1));
    }

    [Test]
    public void An_unknown_view_falls_back_to_grouped()
    {
        Assert.That(ExplorerViewPresets.Find("nope"), Is.SameAs(ExplorerViewPresets.Grouped));
        Assert.That(ExplorerViewPresets.Find("FLAT"), Is.SameAs(ExplorerViewPresets.Flat));
    }
}
