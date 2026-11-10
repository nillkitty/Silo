using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class PaletteFromCollectionTests
{
    private static CollectedCharacter C(int cp, string? group = null) => new(new CodePoint(cp), null, group);

    private static readonly CollectedCharacter[] Items = [C(0x41, "Letters"), C(0x42, "Letters"), C(0x31, "Digits"), C(0x2022), C(0x41, "Digits")];

    [Test]
    public void The_groups_are_kept_on_one_page()
    {
        var pages = PaletteRules.PagesFromCollection(Items, ["Digits", "Nothing"], false);

        Assert.That(pages, Has.Count.EqualTo(1));
        Assert.That(pages[0].CodePoints, Is.EqualTo(new[] { 0x41, 0x42, 0x31, 0x2022 }), "A held twice is on the page once");
        Assert.That(pages[0].GroupOf(0x41), Is.EqualTo("Letters"), "the first group wins");
        Assert.That(pages[0].GroupOf(0x31), Is.EqualTo("Digits"));
        Assert.That(pages[0].GroupOf(0x2022), Is.Null);
        Assert.That(pages[0].Collapsed, Is.EqualTo(new[] { "Digits" }), "only the folded groups that are there");
    }

    [Test]
    public void A_page_per_group_puts_the_ungrouped_last()
    {
        var pages = PaletteRules.PagesFromCollection(Items, [], true);

        Assert.That(pages.Select(p => p.Name), Is.EqualTo(new[] { "Letters", "Digits", "Ungrouped" }));
        Assert.That(pages[0].CodePoints, Is.EqualTo(new[] { 0x41, 0x42 }));
        Assert.That(pages[1].CodePoints, Is.EqualTo(new[] { 0x31, 0x41 }));
        Assert.That(pages[2].CodePoints, Is.EqualTo(new[] { 0x2022 }));
    }

    [Test]
    public void A_page_per_group_with_no_groups_is_the_one_page()
    {
        var pages = PaletteRules.PagesFromCollection([C(0x41), C(0x42)], [], true);

        Assert.That(pages, Has.Count.EqualTo(1));
        Assert.That(pages[0].Name, Is.EqualTo("Glyphs"));
    }
}
