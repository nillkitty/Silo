using System.IO;
using System.Windows.Controls;
using Silo.Commanding;
using Silo.Connectors.File;
using Silo.Contracts.Core;
using Silo.Explorer;
using Silo.Providers.Action;
using Silo.Toolbar;

namespace Silo.Tests.Toolbar;

public class ActionProviderTests
{
    [Test]
    public void Providers_are_asked_most_specific_type_first()
    {
        ActionProviders.Register(new AnimalActions());
        ActionProviders.Register(new DogActions());

        var ids = ActionProviders.CommandsFor(new Dog()).Select(c => c.Id).ToList();

        Assert.That(ids,                                                         Is.EqualTo(new[] { "walk", "feed" }));
        Assert.That(ActionProviders.CommandsFor(new Animal()).Select(c => c.Id), Is.EqualTo(new[] { "feed" }));
    }

    [Test]
    public void A_provider_that_throws_does_not_stop_the_others()
    {
        ActionProviders.Register(new AnimalActions());
        ActionProviders.Register(new ThrowingActions());

        Assert.That(ActionProviders.CommandsFor(new Dog()).Select(c => c.Id), Does.Contain("feed"));
    }

    [Test]
    public void The_non_generic_interface_only_answers_for_its_own_type()
    {
        IActionProvider provider = new DogActions();

        Assert.That(provider.GetCommands(new Dog()),  Is.Not.Empty);
        Assert.That(provider.GetCommands("a string"), Is.Empty);
    }

    [Test]
    public void No_model_has_no_provided_commands()
    {
        Assert.That(ActionProviders.CommandsFor(null), Is.Empty);
    }

    [Test]
    public void The_scan_finds_the_file_provider_for_FileModel()
    {
        var found = ActionProviders.Scan(typeof(FileActions).Assembly).ToList();

        Assert.That(found, Does.Contain((typeof(FileModel), typeof(FileActions))));
    }

    [Test]
    public void The_hub_puts_each_group_together_in_the_order_the_groups_first_appear()
    {
        var node = new ExplorerNode("k", "n", "Node")
                   {
                       Model = new Dog(),
                       Commands = new Provider(ProvidedCommand.From("a", "A", () => { }, group: "One"),
                                               ProvidedCommand.From("b", "B", () => { }, group: "Two"),
                                               ProvidedCommand.From("c", "C", () => { }, group: "One"))
                   };

        Assert.That(NodeCommandHub.CommandsFor(node).Select(c => c.Id), Is.EqualTo(new[] { "a", "c", "b" }));
    }

    [Test]
    public void The_last_group_is_last_whatever_order_the_commands_came_in()
    {
        var node = new ExplorerNode("k", "n", "Node")
                   {
                       Model = new Dog(),
                       Commands =
                           new Provider(ProvidedCommand.From("p", "P", () => { },
                                                             group: ProvidedCommand.LastGroup),
                                        ProvidedCommand.From("a", "A", () => { }, group: "One"),
                                        ProvidedCommand.From("b", "B", () => { }, group: "Two"))
                   };

        Assert.That(NodeCommandHub.CommandsFor(node).Select(c => c.Id), Is.EqualTo(new[] { "a", "b", "p" }));
    }

    [Test]
    public void Properties_is_in_the_last_group_for_every_kind_of_item()
    {
        string dir = Path.Combine(Path.GetTempPath(), "silo-last-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string path in new[] { dir, Path.Combine(dir, "missing.txt") })
            {
                var commands = new FileActions().GetCommands(FileConnection.FromPath(new FileConnector(), path)).ToList();

                Assert.That(commands.Single(c => c.Id == "file.properties").Group, Is.EqualTo(ProvidedCommand.LastGroup));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void A_submenu_command_carries_its_children_into_the_node_command()
    {
        var parent =
            ProvidedCommand.Submenu("p", "P", [ProvidedCommand.From("c1", "C1", () => { }), ProvidedCommand.From("c2", "C2", () => { })],
                                    "G");

        var node = NodeCommand.From(parent);

        Assert.That(node.Children!.Select(c => c.Id),            Is.EqualTo(new[] { "c1", "c2" }));
        Assert.That(node.SelfAndDescendants().Select(c => c.Id), Is.EqualTo(new[] { "p", "c1", "c2" }));
    }

    [Test]
    public void Menu_entries_get_a_heading_per_named_group_and_a_separator_between_unnamed_ones()
    {
        NodeCommand Make(string id, string? group)
        {
            return new NodeCommand(id, id, new DelegateCommand(_ => { }), null, null, null, group, true);
        }

        Control[]? built = null;
        var thread = new Thread(() => built = CommandMenu
                                             .Items([Make("1", "Dir"), Make("2", "Dir"), Make("3", "Loc"), Make("4", ""), Make("5", null)])
                                             .ToArray());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.That(built!.Select(c => c.GetType().Name),
                    Is.EqualTo(new[]
                               {
                                   "MenuItem", "MenuItem", "MenuItem", "MenuItem", "MenuItem", "MenuItem", "Separator", "MenuItem",
                                   "Separator", "MenuItem"
                               }));
    }

    [Test]
    public void The_last_group_gets_a_separator_and_no_heading()
    {
        NodeCommand Make(string id, string? group)
        {
            return new NodeCommand(id, id, new DelegateCommand(_ => { }), null, null, null, group, true);
        }

        Control[]? built  = null;
        var        thread = new Thread(() => built = CommandMenu.Items([Make("1", "Dir"), Make("2", ProvidedCommand.LastGroup)]).ToArray());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.That(built!.Select(c => c.GetType().Name), Is.EqualTo(new[] { "MenuItem", "MenuItem", "Separator", "MenuItem" }));
    }

    [Test]
    public void A_heading_is_upper_case_accent_text_that_cannot_be_chosen()
    {
        MenuItem? heading = null;
        var       thread  = new Thread(() => heading = CommandMenu.Heading("Contents"));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.That(heading!.Header,          Is.EqualTo("CONTENTS"));
        Assert.That(heading.IsHitTestVisible, Is.False);
        Assert.That(heading.Focusable,        Is.False);
    }

    private class Animal;

    private sealed class Dog : Animal;

    private sealed class AnimalActions : IActionProvider<Animal>
    {
        public IEnumerable<ProvidedCommand> GetCommands(Animal node)
        {
            yield return ProvidedCommand.From("feed", "Feed", () => { }, group: "General");
        }
    }

    private sealed class DogActions : IActionProvider<Dog>
    {
        public IEnumerable<ProvidedCommand> GetCommands(Dog node)
        {
            yield return ProvidedCommand.From("walk", "Walk", () => { }, group: "Dog");
        }
    }

    private sealed class ThrowingActions : IActionProvider<Dog>
    {
        public IEnumerable<ProvidedCommand> GetCommands(Dog node)
        {
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class Provider(params ProvidedCommand[] commands) : ICommandProvider
    {
        public IEnumerable<ProvidedCommand> GetCommands()
        {
            return commands;
        }
    }
}

public class FileActionsTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "silo-fileactions-" + Guid.NewGuid().ToString("N"));
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

    private static FileModel Model(string path)
    {
        return FileConnection.FromPath(new FileConnector(), path);
    }

    private static IEnumerable<string> Ids(FileModel m)
    {
        return new FileActions().GetCommands(m).Select(c => c.Id);
    }

    /// <summary>The commands that do something, without the disabled placeholders.</summary>
    private static List<ProvidedCommand> Working(FileModel m)
    {
        return new FileActions().GetCommands(m).Where(c => c.Command is not PlannedFileCommand).ToList();
    }

    [Test]
    public void Planned_items_are_disabled_placeholders_with_distinct_ids()
    {
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "x");

        var planned = new FileActions().GetCommands(Model(file)).Where(c => c.Command is PlannedFileCommand).ToList();

        Assert.That(planned.Select(c => c.Header), Does.Contain("_Preview Contents").And.Contain("_Import...").And.Contain("Pro_perties"));
        Assert.That(planned.Select(c => c.Id), Is.Unique);
        Assert.That(planned.All(c => !c.Command.CanExecute(null)), Is.True);
        Assert.That(planned.All(c => c.ToolTip!.StartsWith("Not available yet")), Is.True);
        Assert.That(planned.Select(c => c.Id), Does.Not.Contain("file.find"));
    }

    [Test]
    public void A_folder_also_has_Go_here_in_Terminal_and_Find_placeholders()
    {
        var planned = new FileActions().GetCommands(Model(_dir)).Where(c => c.Command is PlannedFileCommand).Select(c => c.Id).ToList();

        Assert.That(planned, Does.Contain("file.go.terminal").And.Contain("file.find"));
    }

    [Test]
    public void A_file_gets_the_File_section_and_the_Location_section()
    {
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "x");

        var commands = Working(Model(file));

        Assert.That(commands.Select(c => c.Id),
                    Is.EqualTo(new[] { "file.launch", "file.openwith", "file.reveal", "file.refresh", "file.copypath", "file.copyuri" }));
        Assert.That(commands.Select(c => c.Group),        Is.EqualTo(new[] { "File", "File", "File", "File", "Location", "Location" }));
        Assert.That(commands.Single(c => c.IsDefault).Id, Is.EqualTo("file.launch"));
    }

    [Test]
    public void A_folder_gets_Directory_Contents_and_Location()
    {
        var commands = Working(Model(_dir));

        Assert.That(commands.Select(c => c.Group).Distinct(), Is.EqualTo(new[] { "Directory", "Contents", "Location" }));
        Assert.That(commands.Single(c => c.IsDefault).Id, Is.EqualTo("file.reveal"));
        Assert.That(commands.Single(c => c.Id == "file.refresh.contents").Gesture, Is.EqualTo("F5"));
    }

    [Test]
    public void Commands_carry_header_tooltip_and_icon_from_the_command()
    {
        var launch = new FileActions().GetCommands(Model(_dir)).First(c => c.Id == "file.reveal");

        Assert.That(launch.Header,  Is.EqualTo("_Reveal in Explorer"));
        Assert.That(launch.IconKey, Is.EqualTo("Folder17"));
        Assert.That(launch.ToolTip, Is.Not.Empty);
    }

    [Test]
    public void Launching_is_only_enabled_for_a_file_that_is_there()
    {
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "x");

        Assert.That(new LaunchFileCommand(Model(file)).CanExecute(null),                              Is.True);
        Assert.That(new LaunchFileCommand(Model(Path.Combine(_dir, "missing.txt"))).CanExecute(null), Is.False);
        Assert.That(new LaunchFileCommand(Model(_dir)).CanExecute(null),                              Is.False);
    }

    [Test]
    public void A_missing_item_still_offers_the_Location_commands()
    {
        Assert.That(Ids(Model(Path.Combine(_dir, "nope.bin"))), Does.Contain("file.copypath"));
    }
}

public class ShellExecutorTests
{
    [Test]
    public void A_shell_namespace_object_is_opened_as_shell_colons()
    {
        Assert.That(ShellExecutor.ShellNamespaceTarget(new Guid("26EE0668-A00A-44D7-9371-BEB064C98683")),
                    Is.EqualTo("shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}"));
    }

    [Test]
    public void Reveal_selects_a_file_opens_a_folder_and_falls_back_to_the_nearest_folder_above()
    {
        string dir = Path.Combine(Path.GetTempPath(), "silo-reveal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "a b.txt");
            File.WriteAllText(file, "x");

            Assert.That(ShellExecutor.RevealStart(file)!.Arguments,                               Is.EqualTo($"/select,\"{file}\""));
            Assert.That(ShellExecutor.RevealStart(dir)!.Arguments,                                Is.EqualTo($"\"{dir}\""));
            Assert.That(ShellExecutor.RevealStart(Path.Combine(dir, "gone", "x.txt"))!.Arguments, Is.EqualTo($"\"{dir}\""));
            Assert.That(ShellExecutor.RevealStart(""),                                            Is.Null);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}