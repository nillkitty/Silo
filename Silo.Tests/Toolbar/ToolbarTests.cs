using Silo.Commanding;
using Silo.Contracts.Core;
using Silo.Explorer;
using Silo.Toolbar;

namespace Silo.Tests.Toolbar;

public class ToolbarLayoutTests
{
    [Test]
    public void Normalize_drops_separators_at_the_ends_and_doubled_ones()
    {
        var layout = new ToolbarLayout
                     {
                         Slots = [new("-"), new("a"), new("-"), new("-"), new("b"), new("-"), new(""), new("-")]
                     };

        layout.Normalize();

        Assert.That(layout.Slots.Select(s => s.Id), Is.EqualTo(new[] { "a", "-", "b" }));
    }

    [Test]
    public void Json_round_trip_keeps_the_persons_changes()
    {
        var layout = new ToolbarLayout { Visible = false };
        layout.Slots.Add(new ToolbarSlot("file.save") { Header = "Keep it", IconKey = "Key", ShowText = true, ToolTip = "tip" });
        layout.Slots.Add(new ToolbarSlot("-"));
        layout.Slots.Add(new ToolbarSlot("file.open"));

        var back = ToolbarLayout.FromJson(layout.ToJson());

        Assert.That(back, Is.Not.Null);
        Assert.That(back!.Visible, Is.False);
        Assert.That(back.Slots.Select(s => s.Id), Is.EqualTo(new[] { "file.save", "-", "file.open" }));
        Assert.That(back.Slots[0].Header, Is.EqualTo("Keep it"));
        Assert.That(back.Slots[0].IconKey, Is.EqualTo("Key"));
        Assert.That(back.Slots[0].ShowText, Is.True);
        Assert.That(back.Slots[1].IsSeparator, Is.True);
    }

    [TestCase("not json")]
    [TestCase("{}")]
    [TestCase("{\"Visible\":true,\"Slots\":[]}")]
    public void FromJson_rejects_what_is_not_a_usable_layout(string json)
    {
        Assert.That(ToolbarLayout.FromJson(json), Is.Null);
    }

    [Test]
    public void The_default_layout_has_no_doubled_separators_and_does_not_start_or_end_with_one()
    {
        var slots = ToolbarLayout.Default().Slots;

        Assert.That(slots[0].IsSeparator, Is.False);
        Assert.That(slots[^1].IsSeparator, Is.False);
        for (int i = 1; i < slots.Count; i++)
            Assert.That(slots[i].IsSeparator && slots[i - 1].IsSeparator, Is.False, "separators at " + i);
    }

    [Test]
    public void Clone_is_independent_of_the_original()
    {
        var a = ToolbarLayout.Default();
        var b = a.Clone();

        b.Slots[0].Header = "changed";
        b.Slots.RemoveAt(1);

        Assert.That(a.Slots[0].Header, Is.Null);
        Assert.That(a.Slots.Count, Is.EqualTo(b.Slots.Count + 1));
    }
}

public class ToolbarCatalogTests
{
    [Test, Apartment(ApartmentState.STA)]
    public void Catalog_ids_are_unique()
    {
        var ids = ToolbarCatalog.BuiltIn.Select(d => d.Id).ToList();

        Assert.That(ids, Is.Unique);
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Every_item_of_the_default_layout_is_in_the_catalog()
    {
        foreach (var slot in ToolbarLayout.Default().Slots.Where(s => !s.IsSeparator))
            Assert.That(ToolbarCatalog.Find(slot.Id), Is.Not.Null, slot.Id);
    }

    [Test, Apartment(ApartmentState.STA)]
    public void The_menu_commands_the_toolbar_promises_are_all_there()
    {
        string[] expected =
        [
            "file.new", "file.open", "file.openurl", "file.save", "file.saveas", "file.close", "file.exit",
            "edit.undo", "edit.redo", "edit.cut", "edit.copy", "edit.paste", "edit.pastetable", "edit.delete", "edit.selectall",
            "help.about",
            "silo.newconnection", "silo.newcredential", "silo.archive", "silo.import", "silo.export.logs", "silo.export.state", "silo.export.all"
        ];

        Assert.That(ToolbarCatalog.BuiltIn.Select(d => d.Id), Is.SupersetOf(expected));
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Edit_commands_are_routed_commands_and_Silo_commands_are_looked_up_when_used()
    {
        Assert.That(ToolbarCatalog.Find("edit.copy")!.Command, Is.SameAs(System.Windows.Input.ApplicationCommands.Copy));

        var save = ToolbarCatalog.Find("file.save")!;
        Assert.That(save.Command, Is.Null);
        Assert.That(save.CreateCommand(), Is.TypeOf<LateBoundCommand>());
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Structural_items_have_their_kinds()
    {
        Assert.That(ToolbarCatalog.Find(ToolbarLayout.NodeCommandsId)!.Kind, Is.EqualTo(ToolbarItemKind.NodeCommands));
        Assert.That(ToolbarCatalog.Find(ToolbarLayout.ToolsDropdownId)!.Kind, Is.EqualTo(ToolbarItemKind.ToolsDropdown));
        Assert.That(ToolbarCatalog.Find(ToolbarLayout.CustomizeId)!.Kind, Is.EqualTo(ToolbarItemKind.Customize));
    }

    [TestCase("_Open Silo...", "Open Silo")]
    [TestCase("E_xit", "Exit")]
    [TestCase("Snake__case _item…", "Snake_case item")]
    [TestCase("", "")]
    [TestCase(null, "")]
    public void Plain_removes_access_keys_and_trailing_dots(string? header, string expected)
    {
        Assert.That(ToolbarText.Plain(header), Is.EqualTo(expected));
    }
}

public class NodeCommandTests
{
    private sealed class Provider(params ProvidedCommand[] commands) : ICommandProvider
    {
        public IEnumerable<ProvidedCommand> GetCommands() => commands;
    }

    [Test]
    public void No_node_has_no_commands()
    {
        Assert.That(NodeCommandHub.CommandsFor(null), Is.Empty);
    }

    [Test]
    public void A_node_with_nothing_to_offer_has_no_commands()
    {
        var node = new ExplorerNode("k", "Plain", "Node");

        Assert.That(NodeCommandHub.CommandsFor(node), Is.Empty);
    }

    [Test]
    public void Provided_commands_come_first_and_built_in_actions_follow_as_text_only_entries()
    {
        int ran = 0;
        var node = new ExplorerNode("k", "n", "Node")
                   {
                       Commands = new Provider(ProvidedCommand.From("a", "_A", () => { }, "Plug"),
                                               ProvidedCommand.From("b", "B", () => { }, placement: CommandPlacement.Menu)),
                       Actions = () => [new ExplorerAction("_Open", () => ran++)]
                   };

        var commands = NodeCommandHub.CommandsFor(node);

        Assert.That(commands.Select(c => c.Header), Is.EqualTo(new[] { "_A", "B", "_Open" }));
        Assert.That(commands.Select(c => c.OnToolbar), Is.EqualTo(new[] { true, false, true }));
        Assert.That(commands[0].IconKey, Is.EqualTo("Plug"));
        Assert.That(commands[2].IconKey, Is.Null);

        commands[2].Command.Execute(null);
        Assert.That(ran, Is.EqualTo(1));
    }

    [Test]
    public void A_provider_that_throws_does_not_take_the_actions_down_with_it()
    {
        var node = new ExplorerNode("k", "n", "Node")
                   {
                       Commands = new ThrowingProvider(),
                       Actions  = () => [new ExplorerAction("Still here", () => { })]
                   };

        Assert.That(NodeCommandHub.CommandsFor(node).Select(c => c.Header), Is.EqualTo(new[] { "Still here" }));
    }

    private sealed class ThrowingProvider : ICommandProvider
    {
        public IEnumerable<ProvidedCommand> GetCommands() => throw new InvalidOperationException("boom");
    }

    [Test]
    public void SetCurrent_raises_Changed_and_remembers_the_node()
    {
        var node  = new ExplorerNode("k", "n", "Node");
        int calls = 0;
        EventHandler handler = (_, _) => calls++;
        NodeCommandHub.Changed += handler;
        try
        {
            NodeCommandHub.SetCurrent(node);
            Assert.That(NodeCommandHub.Current, Is.SameAs(node));
            NodeCommandHub.SetCurrent(null);
            Assert.That(NodeCommandHub.Current, Is.Null);
            Assert.That(calls, Is.EqualTo(2));
        }
        finally
        {
            NodeCommandHub.Changed -= handler;
        }
    }

    [Test]
    public void ProvidedCommand_From_enables_by_the_predicate()
    {
        bool allowed = false;
        var  c       = ProvidedCommand.From("x", "X", () => { }, canRun: () => allowed);

        Assert.That(c.Command.CanExecute(null), Is.False);
        allowed = true;
        Assert.That(c.Command.CanExecute(null), Is.True);
    }

    [Test]
    public void The_menu_groups_commands_with_separators_between_groups()
    {
        NodeCommand Make(string id, string group) => new(id, id, new DelegateCommand(_ => { }), null, null, null, group, true);

        // Items() builds WPF controls, which need a single-threaded apartment
        System.Windows.Controls.Control[]? built = null;
        var thread = new Thread(() => built = CommandMenu.Items([Make("1", "g1"), Make("2", "g1"), Make("3", "g2")]).ToArray());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.That(built!.Select(c => c.GetType().Name), Is.EqualTo(new[] { "MenuItem", "MenuItem", "Separator", "MenuItem" }));
    }
}
