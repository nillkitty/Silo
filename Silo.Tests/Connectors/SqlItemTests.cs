using System.Text.RegularExpressions;
using Silo.Connectors.Sql;

namespace Silo.Tests.Connectors;

public class SqlItemTests
{
    // ---- the product's name for a server version ------------------------------------------------

    [TestCase("8.00.2039", "2000")]
    [TestCase("9.00.5000", "2005")]
    [TestCase("10.00.6000", "2008")]
    [TestCase("10.50.6000", "2008 R2")]
    [TestCase("11.0.7001", "2012")]
    [TestCase("12.0.6024", "2014")]
    [TestCase("13.00.6300", "2016")]
    [TestCase("14.00.3465", "2017")]
    [TestCase("15.00.4435", "2019")]
    [TestCase("16.00.4135", "2022")]
    [TestCase("17.00.1000", "2025")]
    public void A_server_version_is_named_for_the_product(string reported, string product)
    {
        Assert.That(SqlNames.ProductVersion(reported), Is.EqualTo(product));
    }

    [Test]
    public void A_version_nobody_knows_is_left_as_it_was_reported()
    {
        Assert.That(SqlNames.ProductVersion("99.0.1"),   Is.EqualTo("99.0.1"));
        Assert.That(SqlNames.ProductVersion("preview"),  Is.EqualTo("preview"));
        Assert.That(SqlNames.ProductVersion(" 16.0.1 "), Is.EqualTo("2022"));
    }

    [Test]
    public void No_version_is_an_empty_name_and_not_a_failure()
    {
        Assert.That(SqlNames.ProductVersion(null),  Is.Empty);
        Assert.That(SqlNames.ProductVersion(""),    Is.Empty);
        Assert.That(SqlNames.ProductVersion("  "),  Is.Empty);
    }

    // ---- icons ---------------------------------------------------------------------------------

    [Test]
    public void Every_kind_of_item_has_an_icon_and_an_unreachable_one_is_marked_so()
    {
        foreach (var kind in Enum.GetValues<SqlItemKind>())
        {
            Assert.That(SqlItem.IconKeyOf(kind, true), Is.Not.Empty, kind.ToString());
            Assert.That(SqlItem.IconKeyOf(kind, false), Is.EqualTo("FsFileMissing"), kind.ToString());
        }
    }

    [Test]
    public void Kinds_that_differ_do_not_all_share_the_catch_all_icon()
    {
        var keys = Enum.GetValues<SqlItemKind>().Select(k => SqlItem.IconKeyOf(k, true)).ToList();

        Assert.That(keys.Count(k => k == "Record"), Is.Zero, "a kind fell through to the catch-all icon");
    }

    [Test]
    public void Every_icon_the_items_use_is_drawn_in_the_icon_dictionary()
    {
        string? file = FindNavIcons();
        if (file is null) Assert.Ignore("Silo/Resources/NavIcons.xaml is not above the test output folder.");

        var drawn = Regex.Matches(File.ReadAllText(file!), "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var kind in Enum.GetValues<SqlItemKind>())
        {
            string key = SqlItem.IconKeyOf(kind, true);
            Assert.That(drawn, Does.Contain(key), $"{kind} uses the icon \"{key}\", which NavIcons.xaml does not define");
        }
    }

    private static string? FindNavIcons()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "Silo", "Resources", "NavIcons.xaml");
            if (File.Exists(path)) return path;
        }

        return null;
    }
}
