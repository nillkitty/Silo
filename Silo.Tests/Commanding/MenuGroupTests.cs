using Silo.Commanding;
using Silo.Contracts.Core;
using Silo.Toolbar;

namespace Silo.Tests.Commanding;

public class MenuGroupTests
{
    private static IEnumerable<string> GroupConstants() =>
        typeof(MenuGroups).GetFields().Where(f => f is { IsLiteral: true } && f.GetRawConstantValue() is string s && s.Contains('.') && s != MenuGroups.Default)
                          .Select(f => (string)f.GetRawConstantValue()!);

    [Test]
    public void Every_group_is_placed_in_exactly_one_menu()
    {
        var layout = MenuLayoutDef.Default();
        foreach (string group in GroupConstants())
        {
            var homes = layout.Menus.Where(m => m.Sections.Any(s => s.Group == group)).Select(m => m.Id).ToList();
            Assert.That(homes, Has.Count.EqualTo(1), $"{group} is in {homes.Count} menus");
        }
    }

    [Test]
    public void The_default_layout_has_the_menus_of_the_design_in_order()
    {
        Assert.That(MenuLayoutDef.Default().Menus.Select(m => m.Id),
                    Is.EqualTo(new[] { "File", "Edit", "View", "Silo", "Connection", "Node", "Diagram", "Lab", "Tools", "Help" }));
    }

    [Test]
    public void A_layout_survives_JSON()
    {
        var layout = MenuLayoutDef.Default();
        var read   = MenuSettings.FromJson(layout.ToJson());

        Assert.That(read, Is.Not.Null);
        Assert.That(read!.Menus.Select(m => m.Id), Is.EqualTo(layout.Menus.Select(m => m.Id)));
        Assert.That(read.Menus.First(m => m.Id == "Silo").Sections.Select(s => s.Submenu), Does.Contain("_Export"));
        Assert.That(MenuSettings.FromJson("not json"), Is.Null);
    }

    [TestCase(MenuGroups.DocMgmt, "file.new")]
    [TestCase(MenuGroups.DocMgmt, "file.open")]
    [TestCase(MenuGroups.FileMgmt, "file.save")]
    [TestCase(MenuGroups.ExitCmds, "file.exit")]
    [TestCase(MenuGroups.UndoRedo, "edit.undo")]
    [TestCase(MenuGroups.ClipboardCmds, "edit.paste")]
    [TestCase(MenuGroups.ConnectionMgmt, "silo.newconnection")]
    [TestCase(MenuGroups.FormatList, "silo.archive")]
    [TestCase(MenuGroups.ExportList, "silo.export.all")]
    [TestCase(MenuGroups.ImportList, "silo.import")]
    [TestCase(MenuGroups.AddDataCmds, "silo.adddata")]
    [TestCase(MenuGroups.NewDiagramCmds, "diagram.new")]
    [TestCase(MenuGroups.AboutCmds, "help.about")]
    public void The_stock_commands_are_in_their_groups(string group, string id)
    {
        Assert.That(MenuCommands.Commands(group).Select(c => c.Id), Does.Contain(id));
    }

    [Test]
    public void A_command_with_a_menu_id_goes_in_that_menus_default_group_only()
    {
        string id = "test." + Guid.NewGuid().ToString("N");
        MenuCommands.Add("Tools", ProvidedCommand.From(id, "_Mine", () => { }));

        Assert.That(MenuCommands.Commands("Tools").Select(c => c.Id), Does.Contain(id));
        Assert.That(MenuCommands.Commands(MenuGroups.ToolsMgmt).Select(c => c.Id), Does.Not.Contain(id));
    }

    [Test]
    public void A_custom_command_can_join_a_stock_group()
    {
        string id = "test." + Guid.NewGuid().ToString("N");
        MenuCommands.Add(MenuGroups.ExportList, () => [ProvidedCommand.From(id, "Export _Mine", () => { })]);

        var ids = MenuCommands.Commands(MenuGroups.ExportList).Select(c => c.Id).ToList();
        Assert.That(ids, Does.Contain(id));
        Assert.That(ids.IndexOf("silo.export.all"), Is.LessThan(ids.IndexOf(id)), "stock commands come first, then what components add");
    }

    [Test]
    public void A_node_command_is_the_Node_menus_unless_it_names_another_group()
    {
        NodeCommand Make(string? group) => NodeCommand.From(ProvidedCommand.From("x", "X", () => { }, menuGroup: group));

        Assert.That(MenuCommands.IsPlainNodeCommand(Make(null)), Is.True);
        Assert.That(MenuCommands.IsPlainNodeCommand(Make(MenuGroups.OtherNodeCmds)), Is.True);
        Assert.That(MenuCommands.IsPlainNodeCommand(Make(MenuGroups.NodeDiagramGenCmds)), Is.False);
        Assert.That(MenuCommands.IsPlainNodeCommand(Make(MenuGroups.NodeLabCmds)), Is.False);
    }

    [Test]
    public void The_toolbar_can_hold_a_group()
    {
        var def = ToolbarCatalog.Find(ToolbarLayout.GroupPrefix + MenuGroups.ExportList);

        Assert.That(def, Is.Not.Null);
        Assert.That(def!.Kind, Is.EqualTo(ToolbarItemKind.Group));
    }
}
