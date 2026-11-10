using Silo.Unicode;

namespace Silo.Tests.Unicode;

public class GlyphGroupsTests
{
    private static (List<int> List, Dictionary<int, string> Groups) Page(params (int Cp, string? Group)[] items)
    {
        var groups = new Dictionary<int, string>();
        foreach (var (cp, group) in items)
            if (group is not null)
                groups[cp] = group;

        return (items.Select(i => i.Cp).ToList(), groups);
    }

    private static int Assign(List<int> list, Dictionary<int, string> groups, IEnumerable<int> indexes, string? group) =>
        GlyphGroups.Assign(list, indexes, group, cp => groups.GetValueOrDefault(cp), (cp, g) =>
                                                                                      {
                                                                                          if (g is null) groups.Remove(cp);
                                                                                          else groups[cp] = g;
                                                                                          return cp;
                                                                                      });

    [Test]
    public void Neighbours_in_the_same_group_make_one_run()
    {
        var (list, groups) = Page((1, "A"), (2, "A"), (3, null), (4, "B"), (5, "A"));
        var runs = GlyphGroups.Runs(list.Count, i => groups.GetValueOrDefault(list[i]));

        Assert.That(runs.Select(r => (r.Name, r.Start, r.Count)), Is.EqualTo(new (string?, int, int)[] { ("A", 0, 2), (null, 2, 1), ("B", 3, 1), ("A", 4, 1) }));
    }

    [Test]
    public void Nothing_is_grouped_when_no_item_has_a_group()
    {
        Assert.That(GlyphGroups.Any(3, _ => null), Is.False);
        Assert.That(GlyphGroups.Any(3, i => i == 1 ? "  " : null), Is.False, "a blank name is no group");
        Assert.That(GlyphGroups.Any(3, i => i == 1 ? "x" : null), Is.True);
    }

    [Test]
    public void Putting_items_in_a_new_group_puts_them_at_the_end()
    {
        var (list, groups) = Page((10, null), (20, null), (30, null), (40, null));
        int first = Assign(list, groups, [2, 0], "New");

        Assert.That(list, Is.EqualTo(new[] { 20, 40, 10, 30 }));
        Assert.That(first, Is.EqualTo(2));
        Assert.That(groups, Is.EquivalentTo(new Dictionary<int, string> { [10] = "New", [30] = "New" }));
    }

    [Test]
    public void Putting_an_item_in_an_existing_group_places_it_after_the_last_of_that_group()
    {
        var (list, groups) = Page((1, "A"), (2, "A"), (3, "B"), (4, null));
        int first = Assign(list, groups, [3], "A");

        Assert.That(list, Is.EqualTo(new[] { 1, 2, 4, 3 }));
        Assert.That(first, Is.EqualTo(2));
        Assert.That(groups[4], Is.EqualTo("A"));
    }

    [Test]
    public void Taking_items_out_of_a_group_leaves_them_where_they_are()
    {
        var (list, groups) = Page((1, "A"), (2, "A"), (3, "A"));
        Assign(list, groups, [1], null);

        Assert.That(list, Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(groups.ContainsKey(2), Is.False);
        Assert.That(groups.ContainsKey(1) && groups.ContainsKey(3), Is.True);
    }

    [Test]
    public void Indexes_out_of_range_are_ignored()
    {
        var (list, groups) = Page((1, null));
        Assert.That(Assign(list, groups, [5, -1], "X"), Is.EqualTo(-1));
        Assert.That(groups, Is.Empty);
    }

    [Test]
    public void Renaming_a_group_to_the_name_of_another_joins_them()
    {
        var items = new List<(int Cp, string? G)> { (1, "A"), (2, "B"), (3, "A") };
        GlyphGroups.Rename(items, "A", "B", t => t.G, (t, g) => (t.Cp, g));

        Assert.That(items.Select(t => t.G), Is.EqualTo(new[] { "B", "B", "B" }));
    }

    [Test]
    public void Sorting_keeps_the_groups_in_place_and_orders_inside_each()
    {
        var items = new List<(int Cp, string? G)> { (9, "Z"), (3, null), (5, "Z"), (1, null), (2, "Y"), (8, "Z") };
        var sorted = GlyphGroups.SortWithin(items, t => t.G, t => t.Cp);

        Assert.That(sorted.Select(t => t.Cp), Is.EqualTo(new[] { 5, 8, 9, 1, 3, 2 }));
    }

    [Test]
    public void Normalizing_drops_groups_of_characters_that_are_not_on_the_page()
    {
        var palette = new PaletteDefinition { Pages = [new PalettePage { CodePoints = [65, 66], Groups = new Dictionary<int, string> { [65] = "Letters", [99] = "Gone", [66] = " " } }] };
        PaletteRules.Normalize(palette);

        Assert.That(palette.Pages[0].Groups, Is.EquivalentTo(new Dictionary<int, string> { [65] = "Letters" }));
    }

    [Test]
    public void A_palette_without_groups_reads_back_with_none()
    {
        var palette = System.Text.Json.JsonSerializer.Deserialize<PaletteDefinition>("{\"Name\":\"Old\",\"Pages\":[{\"Name\":\"Glyphs\",\"CodePoints\":[65]}]}")!;
        PaletteRules.Normalize(palette);

        Assert.That(palette.Pages[0].Groups, Is.Empty);
        Assert.That(palette.Pages[0].Collapsed, Is.Empty);
        Assert.That(palette.Pages[0].GroupOf(65), Is.Null);
    }
}
