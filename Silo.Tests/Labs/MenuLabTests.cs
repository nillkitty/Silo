using System.Xml.Linq;
using Silo.Labs.Menus;

namespace Silo.Tests.Labs;

public class MenuLabTests
{
    private static MenuItemDef Item(string header, string? id = null, string? gesture = null) => new() { Header = header, Id = id ?? header.Replace("_", "").ToLowerInvariant(), Gesture = gesture };

    private static MenuDoc Sample() => new()
                                       {
                                           Name = "Sample", Kind = MenuKind.Context,
                                           Items =
                                           [
                                               Item("_One"), Item("_Two"), MenuItemDef.Line(), new MenuItemDef { Header = "_Three", Children = [Item("_Four"), Item("F_ive")] }, Item("S_ix")
                                           ]
                                       };

    private static string Names(MenuDoc doc) => string.Join(",", doc.Walk().Select(w => w.Item.Separator ? "-" : w.Item.Display));

    [TestCase("ctrl+shift+o", "Ctrl+Shift+O")]
    [TestCase("shift+ctrl+o", "Ctrl+Shift+O")]
    [TestCase("Control+S", "Ctrl+S")]
    [TestCase("f5", "F5")]
    [TestCase("esc", "Escape")]
    [TestCase("Ctrl++", "Ctrl+OemPlus")]
    [TestCase("Alt+Return", "Alt+Enter")]
    [TestCase("del", "Delete")]
    [TestCase("win+d", "Win+D")]
    [TestCase("Ctrl+1", "Ctrl+1")]
    [TestCase("  ", "")]
    public void A_shortcut_is_written_in_the_one_way(string text, string expected)
    {
        Assert.That(MenuGesture.TryNormalize(text, out var normal, out var problem), Is.True, problem);
        Assert.That(normal, Is.EqualTo(expected));
    }

    [TestCase("O", "alone")]
    [TestCase("Shift+O", "alone")]
    [TestCase("Ctrl", "needs a key")]
    [TestCase("Ctrl+O+P", "two keys")]
    [TestCase("Ctrl+Banana", "not a key")]
    [TestCase("Ctrl++O", "empty part")]
    [TestCase("F25", "not a key")]
    public void A_shortcut_that_cannot_work_is_refused_with_the_reason(string text, string reason)
    {
        Assert.That(MenuGesture.TryNormalize(text, out _, out var problem), Is.False);
        Assert.That(problem, Does.Contain(reason));
    }

    [Test]
    public void Access_keys_are_the_letter_after_a_single_underscore()
    {
        Assert.That(MenuItemDef.AccessKeyOf("_Open"), Is.EqualTo('O'));
        Assert.That(MenuItemDef.AccessKeyOf("Save _as"), Is.EqualTo('A'));
        Assert.That(MenuItemDef.AccessKeyOf("snake__case"), Is.Null);
        Assert.That(MenuItemDef.AccessKeyOf("snake__case _x"), Is.EqualTo('X'));
        Assert.That(MenuItemDef.AccessKeyOf("none"), Is.Null);
        Assert.That(MenuItemDef.Unmark("_Open"), Is.EqualTo("Open"));
        Assert.That(MenuItemDef.Unmark("a__b"), Is.EqualTo("a_b"));
    }

    [Test]
    public void Entries_are_added_moved_indented_and_removed_in_the_tree()
    {
        var doc = Sample();
        Assert.That(Names(doc), Is.EqualTo("One,Two,-,Three,Four,Five,Six"));

        // add after an entry, into a submenu, and at the end
        var added = MenuEdit.Add(doc, Item("_New"), [1]);
        Assert.That(added, Is.EqualTo(new[] { 2 }));
        Assert.That(Names(doc), Is.EqualTo("One,Two,New,-,Three,Four,Five,Six"));
        var into = MenuEdit.Add(doc, Item("_Inside"), [4], into: true);
        Assert.That(into, Is.EqualTo(new[] { 4, 2 }));
        Assert.That(MenuEdit.Find(doc, into)!.Display, Is.EqualTo("Inside"));
        Assert.That(MenuEdit.Add(doc, Item("_Last")), Is.EqualTo(new[] { 6 }));

        // moves stay within the menu they are in and report where the entry went
        Assert.That(MenuEdit.Move(doc, [0], 1), Is.EqualTo(new[] { 1 }));
        Assert.That(MenuEdit.Move(doc, [1], -5), Is.EqualTo(new[] { 0 }));
        Assert.That(MenuEdit.Move(doc, [0], -1), Is.EqualTo(new[] { 0 }));
        Assert.That(MenuEdit.Move(doc, [9], 1), Is.Null);
    }

    [Test]
    public void Indenting_makes_a_submenu_of_the_entry_above_and_outdenting_brings_it_back()
    {
        var doc = Sample();

        var path = MenuEdit.Indent(doc, [1]);
        Assert.That(path, Is.EqualTo(new[] { 0, 0 }));
        Assert.That(Names(doc), Is.EqualTo("One,Two,-,Three,Four,Five,Six"));
        Assert.That(doc.Items[0].IsSubmenu, Is.True);

        Assert.That(MenuEdit.Outdent(doc, path!), Is.EqualTo(new[] { 1 }));
        Assert.That(doc.Items[0].IsSubmenu, Is.False);

        // nothing above the first entry; a line cannot hold a submenu; a top entry cannot be outdented
        Assert.That(MenuEdit.Indent(doc, [0]), Is.Null);
        Assert.That(MenuEdit.Indent(doc, [3]), Is.Null);
        Assert.That(MenuEdit.Outdent(doc, [0]), Is.Null);
    }

    [Test]
    public void Removing_and_duplicating_report_what_to_select_next()
    {
        var doc = Sample();
        Assert.That(MenuEdit.Duplicate(doc, [0]), Is.EqualTo(new[] { 1 }));
        Assert.That(doc.Items[1].Id, Is.EqualTo("one.copy"));

        Assert.That(MenuEdit.Remove(doc, [1]), Is.EqualTo(new[] { 1 }));
        Assert.That(MenuEdit.Remove(doc, [4]), Is.EqualTo(new[] { 3 }));

        // taking out the only entry of a submenu selects the submenu
        var tree = new MenuDoc { Items = [new MenuItemDef { Header = "_A", Children = [Item("_B")] }] };
        Assert.That(MenuEdit.Remove(tree, [0, 0]), Is.EqualTo(new[] { 0 }));
        Assert.That(MenuEdit.Remove(tree, [0]), Is.Null);
        Assert.That(MenuEdit.Remove(tree, [3]), Is.Null);
    }

    [Test]
    public void A_copy_of_a_menu_shares_nothing_with_it()
    {
        var doc  = Sample();
        var copy = doc.Clone();
        copy.Items[3].Children[0].Header = "changed";
        copy.Items.RemoveAt(0);

        Assert.That(doc.Items[3].Children[0].Header, Is.EqualTo("_Four"));
        Assert.That(doc.Items.Count, Is.EqualTo(5));
    }

    [Test]
    public void The_check_finds_what_is_wrong_with_a_menu()
    {
        var doc = new MenuDoc
                  {
                      Kind = MenuKind.Context,
                      Items =
                      [
                          MenuItemDef.Line(),
                          new MenuItemDef { Header = "", Id = "a" },
                          Item("_Open", "dup", "Ctrl+O"),
                          Item("O_pen again", "dup", "ctrl+o"),
                          new MenuItemDef { Header = "Bad", Id = "bad", Gesture = "Q" },
                          new MenuItemDef { Header = "No id" },
                          new MenuItemDef { Header = "Mark", Id = "m", Checked = true },
                          MenuItemDef.Line(), MenuItemDef.Line(),
                          new MenuItemDef { Header = "Both", Id = "both", Default = true, Destructive = true },
                          MenuItemDef.Line()
                      ]
                  };

        var problems = MenuCheck.Validate(doc);
        string All() => string.Join("\n", problems.Select(p => $"{p.Severity} {p.Where}: {p.Text}"));

        Assert.That(All(), Does.Contain("Warning 1: A menu should not start with a line."));
        Assert.That(All(), Does.Contain("Error 2: The entry has no text."));
        Assert.That(All(), Does.Contain("Error 4: The id dup is also the id of entry 3."));
        Assert.That(All(), Does.Contain("Warning 4: Ctrl+O is also the shortcut of entry 3."));
        Assert.That(All(), Does.Contain("Error 5: The shortcut: Q alone"));
        Assert.That(All(), Does.Contain("Warning 6: The entry has no id"));
        Assert.That(All(), Does.Contain("Warning 7: The check mark is on but the entry is not checkable"));
        Assert.That(All(), Does.Contain("Warning 9: Two lines in a row"));
        Assert.That(All(), Does.Contain("Warning 10: An entry that is both the default"));
        Assert.That(All(), Does.Contain("Warning 11: A menu should not end with a line."));
        Assert.That(problems.Count(p => p.Severity == MenuSeverity.Error), Is.EqualTo(3));
    }

    [Test]
    public void Two_entries_of_a_menu_with_the_same_access_key_and_a_deep_menu_are_warned_of()
    {
        var doc = new MenuDoc { Kind = MenuKind.Bar, Items = [new MenuItemDef { Header = "_File", Children = [Item("_Save", "s"), Item("Sa_ve as", "sa").Also(i => i.Header = "_Save as")] }] };
        Assert.That(MenuCheck.Validate(doc).Any(p => p.Text.Contains("also chooses entry 1")), Is.True);

        var deep = new MenuItemDef { Header = "_A", Children = [] };
        var at   = deep;
        for (int i = 0; i < 5; i++)
        {
            var next = new MenuItemDef { Header = "L" + i, Children = [] };
            at.Children.Add(next);
            at = next;
        }

        at.Id = "leaf";
        Assert.That(MenuCheck.Validate(new MenuDoc { Items = [deep] }).Any(p => p.Text.Contains("deep")), Is.True);
        Assert.That(MenuCheck.Validate(new MenuDoc { Kind = MenuKind.Bar, Items = [MenuItemDef.Line(), Item("x")] }).Any(p => p.Text.Contains("stray")), Is.True);
    }

    [Test]
    public void The_templates_that_come_with_the_lab_have_nothing_wrong_with_them()
    {
        foreach (var (title, make) in MenuLibrary.Templates)
        {
            var problems = MenuCheck.Validate(make());
            Assert.That(problems.Select(p => p.Text), Is.Empty, title);
        }
    }

    [Test]
    public void A_menu_is_kept_as_short_json_and_read_back_whole()
    {
        var doc = MenuLibrary.Templates[2].Make();
        doc.Items[0].Checkable = true;
        doc.Items[0].Checked   = true;
        doc.Items[0].Enabled   = false;
        doc.Items[1].ToolTip   = "Choose a program";

        var json = MenuJson.ToJson(doc);
        Assert.That(json, Does.Contain("\"kind\": \"context\""));
        Assert.That(json, Does.Not.Contain("\"children\""));
        Assert.That(json, Does.Not.Contain("\"enabled\": true"));

        Assert.That(MenuJson.TryFromJson(json, out var back, out var error), Is.True, error);
        Assert.That(MenuJson.ToJson(back), Is.EqualTo(json));
        Assert.That(back.Items[0].Enabled, Is.False);
        Assert.That(back.Items[0].Checked, Is.True);
        Assert.That(back.Items[2].Separator, Is.True);
        Assert.That(back.Items[0].Default, Is.True);
    }

    [Test]
    public void Json_that_is_not_a_menu_is_refused_with_a_reason_and_small_gaps_are_filled()
    {
        Assert.That(MenuJson.TryFromJson("", out _, out var empty), Is.False);
        Assert.That(empty, Is.Not.Empty);
        Assert.That(MenuJson.TryFromJson("{ not json", out _, out var broken), Is.False);
        Assert.That(broken, Is.Not.Empty);
        Assert.That(MenuJson.TryFromJson("{\"kind\": \"sideways\"}", out _, out var kind), Is.False);
        Assert.That(kind, Is.Not.Empty);

        Assert.That(MenuJson.TryFromJson("{\"items\": [{\"header\": \"_A\", \"children\": null}, null, {\"separator\": true}]}", out var doc, out var error), Is.True, error);
        Assert.That(doc.Items.Count, Is.EqualTo(2));
        Assert.That(doc.Items[0].Children, Is.Empty);
        Assert.That(doc.Items[0].Enabled, Is.True);
        Assert.That(doc.Kind, Is.EqualTo(MenuKind.Context));
    }

    [Test]
    public void The_xaml_is_one_resource_with_the_entries_nested_as_they_are_in_the_menu()
    {
        var doc = MenuLibrary.Templates[0].Make();
        var xaml = MenuXaml.Generate(doc);

        var root = XDocument.Parse(xaml).Root!;
        Assert.That(root.Name, Is.EqualTo(MenuXaml.Presentation + "ResourceDictionary"));
        Assert.That(root.Elements().Count(), Is.EqualTo(1));
        var menu = root.Elements().Single();
        Assert.That(menu.Name, Is.EqualTo(MenuXaml.Presentation + "Menu"));
        Assert.That((string?)menu.Attribute(MenuXaml.X + "Key"), Is.EqualTo("FileMenu"));

        var file = menu.Elements(MenuXaml.Presentation + "MenuItem").Single();
        Assert.That((string?)file.Attribute("Header"), Is.EqualTo("_File"));
        var inside = file.Elements().Where(e => e.Name.LocalName is "MenuItem" or "Separator").ToList();
        Assert.That(inside.Select(e => e.Name.LocalName), Is.EqualTo(new[] { "MenuItem", "MenuItem", "MenuItem", "Separator", "MenuItem", "MenuItem", "Separator", "MenuItem" }));

        var open = inside[1];
        Assert.That((string?)open.Attribute("Tag"), Is.EqualTo("file.open"));
        Assert.That((string?)open.Attribute("InputGestureText"), Is.EqualTo("Ctrl+O"));
        var icon = open.Element(MenuXaml.Presentation + "MenuItem.Icon")!.Element(MenuXaml.Silo + "AppIcon")!;
        Assert.That((string?)icon.Attribute("Icon"), Is.EqualTo("TbOpen"));
        Assert.That(xaml, Does.Contain("xmlns:silo=\"clr-namespace:Silo\""));
    }

    [Test]
    public void The_xaml_says_checks_state_bold_red_and_escapes_what_needs_it()
    {
        var doc = new MenuDoc
                  {
                      Name = "My menu 1",
                      Kind = MenuKind.Context,
                      Items =
                      [
                          new MenuItemDef { Header = "Q&A <tips> \"x\"", Id = "qa", Checkable = true, Checked = true, Enabled = false, Default = true, Destructive = true, ToolTip = "a & b" }
                      ]
                  };

        var xaml = MenuXaml.Generate(doc, wrap: false);
        var root = XDocument.Parse(xaml).Root!;

        Assert.That(root.Name, Is.EqualTo(MenuXaml.Presentation + "ContextMenu"));
        Assert.That((string?)root.Attribute(MenuXaml.X + "Key"), Is.EqualTo("My_menu_1"));
        var item = root.Elements().Single();
        Assert.That((string?)item.Attribute("Header"), Is.EqualTo("Q&A <tips> \"x\""));
        Assert.That((string?)item.Attribute("IsCheckable"), Is.EqualTo("True"));
        Assert.That((string?)item.Attribute("IsChecked"), Is.EqualTo("True"));
        Assert.That((string?)item.Attribute("IsEnabled"), Is.EqualTo("False"));
        Assert.That((string?)item.Attribute("FontWeight"), Is.EqualTo("Bold"));
        Assert.That((string?)item.Attribute("Foreground"), Is.EqualTo("#E5484D"));
        Assert.That((string?)item.Attribute("ToolTip"), Is.EqualTo("a & b"));

        // no icons: the Silo namespace is not declared
        Assert.That(xaml, Does.Not.Contain("silo"));
        Assert.That(MenuXaml.KeyOf(new MenuDoc { Name = "1st" }), Is.EqualTo("_1st"));
        Assert.That(MenuXaml.KeyOf(new MenuDoc { Kind = MenuKind.Bar }), Is.EqualTo("MainMenu"));
    }

    [Test]
    public void The_outline_and_the_addresses_agree()
    {
        var doc = MenuLibrary.Templates[0].Make();
        var outline = MenuLibrary.Outline(doc);

        Assert.That(outline, Does.Contain("1.2"));
        Assert.That(outline, Does.Contain("Open...  [Ctrl+O]  file.open"));
        Assert.That(outline, Does.Contain("Open Recent  >"));

        Assert.That(MenuLibrary.ParsePath("1.3.2"), Is.EqualTo(new[] { 0, 2, 1 }));
        Assert.That(MenuLibrary.ParsePath("2"), Is.EqualTo(new[] { 1 }));
        Assert.That(MenuLibrary.ParsePath("0"), Is.Null);
        Assert.That(MenuLibrary.ParsePath("a.b"), Is.Null);
        Assert.That(MenuLibrary.ParsePath(""), Is.Null);
        Assert.That(MenuEdit.Find(doc, MenuLibrary.ParsePath("1.3.2")!)!.Id, Is.EqualTo("file.recent.2"));
    }
}

internal static class MenuTestExtensions
{
    public static T Also<T>(this T value, Action<T> act)
    {
        act(value);
        return value;
    }
}
