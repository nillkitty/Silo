using Silo.Contracts.Providers.Data;
using Silo.DbModel.Meta;
using Silo.Providers.Definitions;

namespace Silo.Tests.Definitions;

[TestFixture]
public class EnumRulesTests
{
    private static string Norm(EnumType type, string text)
    {
        Assert.That(EnumRules.TryNormalizeValue(type, text, out string canonical, out _), Is.True, text);
        return canonical;
    }

    private static bool Bad(EnumType type, string? text) => !EnumRules.TryNormalizeValue(type, text, out _, out string? error) && !string.IsNullOrEmpty(error);

    [Test]
    public void Integral_values_are_canonical_decimal()
    {
        Assert.That(Norm(EnumType.Integral, " 42 "), Is.EqualTo("42"));
        Assert.That(Norm(EnumType.Integral, "0x1F"), Is.EqualTo("31"));
        Assert.That(Norm(EnumType.Integral, "18446744073709551615"), Is.EqualTo("18446744073709551615"));
        Assert.That(Bad(EnumType.Integral, "-1"), Is.True);
        Assert.That(Bad(EnumType.Integral, "1.5"), Is.True);
        Assert.That(Bad(EnumType.Integral, "18446744073709551616"), Is.True);
        Assert.That(Bad(EnumType.Integral, ""), Is.True);
        Assert.That(Bad(EnumType.Integral, null), Is.True);
    }

    [Test]
    public void Byte_values_stop_at_255()
    {
        Assert.That(Norm(EnumType.Byte, "255"), Is.EqualTo("255"));
        Assert.That(Norm(EnumType.Byte, "0xff"), Is.EqualTo("255"));
        Assert.That(Bad(EnumType.Byte, "256"), Is.True);
    }

    [Test]
    public void Signed_values_may_be_negative()
    {
        Assert.That(Norm(EnumType.Signed, "-5"), Is.EqualTo("-5"));
        Assert.That(Norm(EnumType.Signed, "+7"), Is.EqualTo("7"));
        Assert.That(Norm(EnumType.Signed, "0x10"), Is.EqualTo("16"));
        Assert.That(Bad(EnumType.Signed, "0xFFFFFFFFFFFFFFFF"), Is.True);
        Assert.That(Bad(EnumType.Signed, "abc"), Is.True);
    }

    [Test]
    public void Float_bool_guid_and_string_values()
    {
        Assert.That(Norm(EnumType.Float, "1.50"), Is.EqualTo("1.5"));
        Assert.That(Bad(EnumType.Float, "NaN"), Is.True);
        Assert.That(Bad(EnumType.Float, "Infinity"), Is.True);

        Assert.That(Norm(EnumType.Bool, "Yes"), Is.EqualTo("true"));
        Assert.That(Norm(EnumType.Bool, "0"), Is.EqualTo("false"));
        Assert.That(Bad(EnumType.Bool, "maybe"), Is.True);

        Assert.That(Norm(EnumType.Guid, "{3F2504E0-4F89-11D3-9A0C-0305E82C3301}"), Is.EqualTo("3f2504e0-4f89-11d3-9a0c-0305e82c3301"));
        Assert.That(Bad(EnumType.Guid, "nope"), Is.True);

        Assert.That(Norm(EnumType.String, "Hello there"), Is.EqualTo("Hello there"));
        Assert.That(Bad(EnumType.String, "   "), Is.True);
    }

    [Test]
    public void A_good_enum_has_no_problems_and_is_normalized()
    {
        var e = new EnumRecord(0, " Colors ", "  ", EnumType.Integral, EnumFlags.None,
                               [new EnumValueRecord(0, " Red ", "0x1"), new EnumValueRecord(0, "Green", "2", " the green one ")]);
        Assert.That(EnumRules.Validate(e, ["Other"]), Is.Empty);

        var n = EnumRules.Normalize(e);
        Assert.That(n.Name, Is.EqualTo("Colors"));
        Assert.That(n.Description, Is.Null);
        Assert.That(n.Values[0].Name, Is.EqualTo("Red"));
        Assert.That(n.Values[0].Value, Is.EqualTo("1"));
        Assert.That(n.Values[1].Description, Is.EqualTo("the green one"));
    }

    [Test]
    public void Names_must_be_usable_and_unique()
    {
        Assert.That(EnumRules.Validate(EnumRecord.Create(""), []), Has.Count.EqualTo(1));
        Assert.That(EnumRules.Validate(EnumRecord.Create("1abc"), []), Has.Count.EqualTo(1));
        Assert.That(EnumRules.Validate(EnumRecord.Create("a b"), []), Has.Count.EqualTo(1));
        Assert.That(EnumRules.Validate(EnumRecord.Create("Fine_name-1.x"), []), Is.Empty);
        Assert.That(EnumRules.Validate(EnumRecord.Create("colors"), ["Colors"]), Has.Count.EqualTo(1));
        Assert.That(EnumRules.Validate(EnumRecord.Create(new string('a', 65)), []), Has.Count.EqualTo(1));
    }

    [Test]
    public void Values_must_be_unique_by_name_and_by_value()
    {
        var e = new EnumRecord(0, "E", null, EnumType.Integral, EnumFlags.None,
                               [new EnumValueRecord(0, "A", "1"), new EnumValueRecord(0, "a", "2"), new EnumValueRecord(0, "B", "0x1")]);
        var problems = EnumRules.Validate(e, []);
        Assert.That(problems, Has.Count.EqualTo(2)); // name "a" repeats "A"; value 1 repeats A's
    }

    [Test]
    public void Bad_values_are_reported_with_the_name_of_the_value()
    {
        var e = new EnumRecord(0, "E", null, EnumType.Byte, EnumFlags.None, [new EnumValueRecord(0, "Big", "300"), new EnumValueRecord(0, "", "1")]);
        var problems = EnumRules.Validate(e, []);
        Assert.That(problems, Has.Count.EqualTo(2));
        Assert.That(problems[0].StartsWith("Value \"Big\""), Is.True);
    }

    [Test]
    public void Bool_enums_have_at_most_two_values_and_bitmasks_need_integers()
    {
        var three = new EnumRecord(0, "B", null, EnumType.Bool, EnumFlags.None,
                                   [new EnumValueRecord(0, "Yes", "true"), new EnumValueRecord(0, "No", "false"), new EnumValueRecord(0, "Maybe", "1")]);
        Assert.That(EnumRules.Validate(three, []).Count >= 1, Is.True);

        Assert.That(EnumRules.Validate(EnumRecord.Create("S", EnumType.String, EnumFlags.Bitmask), []), Has.Count.EqualTo(1));
        Assert.That(EnumRules.Validate(EnumRecord.Create("I", EnumType.Integral, EnumFlags.Bitmask | EnumFlags.KnownValuesOnly), []), Is.Empty);
        Assert.That(EnumRules.Validate(EnumRecord.Create("Y", EnumType.Byte, EnumFlags.Bitmask), []), Is.Empty);
    }

    [Test]
    public void Require_throws_with_every_problem()
    {
        var e = new EnumRecord(0, "", null, EnumType.Integral, EnumFlags.None, [new EnumValueRecord(0, "x", "zzz")]);
        try
        {
            EnumRules.Require(e, []);
            Assert.That(false, Is.True, "should have thrown");
        }
        catch (DefinitionException ex)
        {
            Assert.That(ex.Problems, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void WithValue_adds_or_replaces_and_WithoutValue_removes()
    {
        var e = new EnumRecord(5, "E", null, EnumType.Integral, EnumFlags.None, [new EnumValueRecord(10, "A", "1"), new EnumValueRecord(11, "B", "2")]);

        var added = EnumRules.WithValue(e, new EnumValueRecord(0, "C", "3"));
        Assert.That(added.Values, Has.Count.EqualTo(3));

        var replaced = EnumRules.WithValue(e, new EnumValueRecord(11, "Bee", "2"));
        Assert.That(replaced.Values, Has.Count.EqualTo(2));
        Assert.That(replaced.Values[1].Name, Is.EqualTo("Bee"));

        var stale = EnumRules.WithValue(e, new EnumValueRecord(99, "Z", "9"));
        Assert.That(stale.Values, Has.Count.EqualTo(3));
        Assert.That(stale.Values[2].Id, Is.EqualTo(0));

        Assert.That(EnumRules.WithoutValue(e, 10).Values, Has.Count.EqualTo(1));
        Assert.That(EnumRules.WithoutValue(e, 77).Values, Has.Count.EqualTo(2));
    }

    [Test]
    public void Labels_and_values_are_looked_up_by_canonical_form()
    {
        var e = new EnumRecord(1, "E", null, EnumType.Integral, EnumFlags.None, [new EnumValueRecord(1, "Read", "1"), new EnumValueRecord(2, "Write", "0x2")]);
        Assert.That(EnumRules.LabelFor(e, "0x1"), Is.EqualTo("Read"));
        Assert.That(EnumRules.LabelFor(e, "2"), Is.EqualTo("Write"));
        Assert.That(EnumRules.LabelFor(e, "3"), Is.Null);
        Assert.That(EnumRules.LabelFor(e, "x"), Is.Null);
        Assert.That(EnumRules.ValueFor(e, " write "), Is.EqualTo("2"));
        Assert.That(EnumRules.ValueFor(e, "nothing"), Is.Null);
    }

    [Test]
    public void The_next_value_suggested_follows_the_type()
    {
        EnumRecord Of(EnumType type, EnumFlags flags, params string[] values) =>
            new(1, "E", null, type, flags, values.Select((v, i) => new EnumValueRecord(i + 1, "V" + i, v)).ToList());

        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Integral, EnumFlags.None)), Is.EqualTo("0"));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Integral, EnumFlags.None, "0", "5")), Is.EqualTo("6"));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Integral, EnumFlags.Bitmask)), Is.EqualTo("1"));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Integral, EnumFlags.Bitmask, "1", "2", "4")), Is.EqualTo("8"));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Integral, EnumFlags.Bitmask, "0", "3")), Is.EqualTo("4"));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Integral, EnumFlags.Bitmask, "0")), Is.EqualTo("1"));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Integral, EnumFlags.None, "18446744073709551615")), Is.EqualTo(""));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Integral, EnumFlags.Bitmask, "9223372036854775808")), Is.EqualTo(""));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Byte, EnumFlags.None, "255")), Is.EqualTo(""));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Byte, EnumFlags.Bitmask, "128")), Is.EqualTo(""));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Signed, EnumFlags.None, "-3", "-1")), Is.EqualTo("0"));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Float, EnumFlags.None, "1.5")), Is.EqualTo("2"));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Bool, EnumFlags.None)), Is.EqualTo("false"));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Bool, EnumFlags.None, "false")), Is.EqualTo("true"));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.Bool, EnumFlags.None, "false", "true")), Is.EqualTo(""));
        Assert.That(EnumRules.SuggestNextValue(Of(EnumType.String, EnumFlags.None, "a")), Is.EqualTo(""));
        Assert.That(Guid.TryParse(EnumRules.SuggestNextValue(Of(EnumType.Guid, EnumFlags.None)), out _), Is.True);
    }

    [Test]
    public void The_texts_shown_for_definitions()
    {
        Assert.That(DefinitionText.Describe(EnumFlags.None), Is.EqualTo(""));
        Assert.That(DefinitionText.Describe(EnumFlags.Bitmask | EnumFlags.DontUseName), Is.EqualTo("bitmask, shows numbers"));
        Assert.That(DefinitionText.Source(SyntaxRecord.ForParser("T", "timestamp")), Is.EqualTo("Parser: Timestamp"));
        Assert.That(DefinitionText.Source(SyntaxRecord.ForParser("T", "gone")), Is.EqualTo("Parser: gone"));
        Assert.That(DefinitionText.Source(SyntaxRecord.ForPattern("T", "x")), Is.EqualTo("Pattern"));
        Assert.That(DefinitionText.NameWithAlias(SyntaxRecord.ForParser("JObject", "jobj", "jobj")), Is.EqualTo("JObject (jobj)"));
        Assert.That(DefinitionText.NameWithAlias(SyntaxRecord.ForParser("Guid", "guid")), Is.EqualTo("Guid"));
        Assert.That(DefinitionText.Label(new EnumValueRecord(1, "Open", "0")), Is.EqualTo("Open = 0"));
        Assert.That(DefinitionText.Label(new EnumValueRecord(1, "Open", null)), Is.EqualTo("Open"));

        var e = new EnumRecord(1, "E", null, EnumType.Integral, EnumFlags.None, [new EnumValueRecord(1, "A", "1")]);
        Assert.That(DefinitionText.Stamp(e) != DefinitionText.Stamp(EnumRules.WithValue(e, new EnumValueRecord(1, "A", "2"))), Is.True);
    }
}
