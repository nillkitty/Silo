using Silo.Storage;

namespace Silo.Tests.Storage;

[TestFixture]
public class YourConstantNameTests
{
    [TestCase("Permission Modes", "Permission_Modes")]
    [TestCase("Track Commands (23)", "Track_Commands_23")]
    [TestCase("  --arrows--  ", "arrows")]
    [TestCase("1st row", "_1st_row")]
    [TestCase("a__b", "a_b")]
    [TestCase("green", "green")]
    public void A_group_name_becomes_a_valid_constant_name(string group, string expected)
    {
        Assert.That(YourConstants.NameFrom(group), Is.EqualTo(expected));
        Assert.That(YourConstants.IsValidName(expected), Is.True);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("★ ✓")]
    public void A_name_with_nothing_usable_in_it_gives_none(string? group)
    {
        Assert.That(YourConstants.NameFrom(group), Is.Null);
    }

    [Test]
    public void A_long_name_is_cut_to_a_valid_length()
    {
        string name = YourConstants.NameFrom(new string('a', 100) + " tail")!;
        Assert.That(name, Has.Length.LessThanOrEqualTo(64));
        Assert.That(YourConstants.IsValidName(name), Is.True);
    }
}
