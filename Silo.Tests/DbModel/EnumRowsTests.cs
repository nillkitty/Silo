using System.Linq;
using Microsoft.Data.Sqlite;
using Silo.Contracts.Providers.Data;
using Silo.DbModel.Meta;
using Silo.DbModel.Sqlite;

namespace Silo.Tests.DbModel;

public class EnumRowsTests
{
    private const string EnumTable  = "[main].[MvEnum]";
    private const string ValueTable = "[main].[MvEnumValue]";

    private SqliteConnection _conn = null!;

    [SetUp]
    public void SetUp()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        Exec("CREATE TABLE [MvEnum] ([Id] INTEGER PRIMARY KEY, [Flags] INTEGER, [Name] TEXT, [Type] INTEGER, [Description] TEXT)");
        Exec("CREATE TABLE [MvEnumValue] ([Id] INTEGER PRIMARY KEY, [EnumId] INTEGER, [Name] TEXT, [Value] TEXT, [Description] TEXT)");
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Count(string table)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM " + table;
        return (long)cmd.ExecuteScalar()!;
    }

    private EnumRecord Save(EnumRecord e) => EnumRows.Save(_conn, null, EnumTable, ValueTable, e);

    private static EnumRecord Colors() => new(0, "Colors", "Some colors", EnumType.Integral, EnumFlags.KnownValuesOnly,
                                              [new EnumValueRecord(0, "Red", "0x1"), new EnumValueRecord(0, "Green", "2", "the green one")]);

    [Test]
    public void A_new_enum_is_stored_with_its_values_in_canonical_form()
    {
        var saved = Save(Colors());

        Assert.That(saved.Id, Is.GreaterThan(0));
        Assert.That(saved.Name, Is.EqualTo("Colors"));
        Assert.That(saved.Description, Is.EqualTo("Some colors"));
        Assert.That(saved.Type, Is.EqualTo(EnumType.Integral));
        Assert.That(saved.Flags, Is.EqualTo(EnumFlags.KnownValuesOnly));
        Assert.That(saved.Values.Select(v => v.Name), Is.EqualTo(new[] { "Red", "Green" }));
        Assert.That(saved.Values.Select(v => v.Value), Is.EqualTo(new[] { "1", "2" }));
        Assert.That(saved.Values.All(v => v.Id > 0), Is.True);
        Assert.That(saved.Values[1].Description, Is.EqualTo("the green one"));
        Assert.That(Count(EnumTable), Is.EqualTo(1));
        Assert.That(Count(ValueTable), Is.EqualTo(2));
    }

    [Test]
    public void Enums_are_listed_by_name_and_found_without_regard_to_case()
    {
        Save(EnumRecord.Create("Zebra"));
        Save(EnumRecord.Create("apple"));
        Save(EnumRecord.Create("Mango"));

        Assert.That(EnumRows.GetAll(_conn, null, EnumTable, ValueTable).Select(e => e.Name), Is.EqualTo(new[] { "apple", "Mango", "Zebra" }));
        Assert.That(EnumRows.Find(_conn, null, EnumTable, ValueTable, "MANGO")?.Name, Is.EqualTo("Mango"));
        Assert.That(EnumRows.Find(_conn, null, EnumTable, ValueTable, "nothing"), Is.Null);
        Assert.That(EnumRows.Get(_conn, null, EnumTable, ValueTable, 9999), Is.Null);
    }

    [Test]
    public void Saving_again_updates_in_place_keeping_the_ids_of_values_that_stay()
    {
        var first = Save(Colors());
        var red   = first.Values[0];
        var green = first.Values[1];

        var changed = Save(first with
                           {
                               Name = "Hues",
                               Type = EnumType.Integral,
                               Values = [red with { Name = "Crimson" }, new EnumValueRecord(0, "Blue", "4")] // Green is gone, Blue is new
                           });

        Assert.That(changed.Id, Is.EqualTo(first.Id));
        Assert.That(changed.Name, Is.EqualTo("Hues"));
        Assert.That(changed.Values.Select(v => v.Name), Is.EqualTo(new[] { "Crimson", "Blue" }));
        Assert.That(changed.Values[0].Id, Is.EqualTo(red.Id));
        Assert.That(changed.Values.Any(v => v.Id == green.Id), Is.False);
        Assert.That(Count(EnumTable), Is.EqualTo(1));
        Assert.That(Count(ValueTable), Is.EqualTo(2));
    }

    [Test]
    public void An_enum_can_keep_its_own_name_when_saved_again()
    {
        var first = Save(Colors());
        Assert.That(Save(first with { Description = "changed" }).Description, Is.EqualTo("changed"));
    }

    [Test]
    public void A_definition_that_breaks_the_rules_is_refused_and_nothing_is_written()
    {
        Save(Colors());

        var duplicateName = Assert.Throws<DefinitionException>(() => Save(EnumRecord.Create("COLORS")));
        Assert.That(duplicateName!.Message.Contains("already"), Is.True);

        Assert.Throws<DefinitionException>(() => Save(EnumRecord.Create("Bad name")));
        Assert.Throws<DefinitionException>(() => Save(new EnumRecord(0, "Bytes", null, EnumType.Byte, EnumFlags.None, [new EnumValueRecord(0, "Big", "300")])));
        Assert.Throws<DefinitionException>(() => Save(new EnumRecord(0, "Twice", null, EnumType.Integral, EnumFlags.None,
                                                                    [new EnumValueRecord(0, "A", "1"), new EnumValueRecord(0, "B", "0x1")])));

        Assert.That(Count(EnumTable), Is.EqualTo(1));
        Assert.That(Count(ValueTable), Is.EqualTo(2));
    }

    [Test]
    public void Updating_an_enum_that_was_deleted_in_the_meantime_says_so()
    {
        var saved = Save(Colors());
        EnumRows.Delete(_conn, null, EnumTable, ValueTable, saved.Id);

        var ex = Assert.Throws<DefinitionException>(() => Save(saved));
        Assert.That(ex!.Message.Contains("no longer exists"), Is.True);
        Assert.That(Count(EnumTable), Is.EqualTo(0));
    }

    [Test]
    public void Deleting_an_enum_deletes_its_values_and_only_its_values()
    {
        var colors = Save(Colors());
        Save(new EnumRecord(0, "Sizes", null, EnumType.String, EnumFlags.None, [new EnumValueRecord(0, "Small", "S")]));

        Assert.That(EnumRows.Delete(_conn, null, EnumTable, ValueTable, colors.Id), Is.True);
        Assert.That(EnumRows.Delete(_conn, null, EnumTable, ValueTable, colors.Id), Is.False);

        Assert.That(Count(EnumTable), Is.EqualTo(1));
        Assert.That(Count(ValueTable), Is.EqualTo(1));
        Assert.That(EnumRows.GetAll(_conn, null, EnumTable, ValueTable)[0].Values[0].Name, Is.EqualTo("Small"));
    }

    [Test]
    public void A_single_value_can_be_added_changed_and_deleted()
    {
        var colors = Save(Colors());

        var blue = EnumRows.SaveValue(_conn, null, EnumTable, ValueTable, colors.Id, new EnumValueRecord(0, "Blue", "4"));
        Assert.That(blue.Id, Is.GreaterThan(0));
        Assert.That(blue.Value, Is.EqualTo("4"));
        Assert.That(EnumRows.Get(_conn, null, EnumTable, ValueTable, colors.Id)!.Values, Has.Count.EqualTo(3));

        var renamed = EnumRows.SaveValue(_conn, null, EnumTable, ValueTable, colors.Id, blue with { Name = "Navy", Value = "0x8" });
        Assert.That(renamed.Id, Is.EqualTo(blue.Id));
        Assert.That(renamed.Name, Is.EqualTo("Navy"));
        Assert.That(renamed.Value, Is.EqualTo("8"));

        Assert.That(EnumRows.DeleteValue(_conn, null, ValueTable, blue.Id), Is.True);
        Assert.That(EnumRows.DeleteValue(_conn, null, ValueTable, blue.Id), Is.False);
        Assert.That(EnumRows.Get(_conn, null, EnumTable, ValueTable, colors.Id)!.Values, Has.Count.EqualTo(2));
    }

    [Test]
    public void A_value_that_breaks_the_enums_rules_is_refused()
    {
        var colors = Save(Colors());

        Assert.Throws<DefinitionException>(() => EnumRows.SaveValue(_conn, null, EnumTable, ValueTable, colors.Id, new EnumValueRecord(0, "Red", "9")));   // name taken
        Assert.Throws<DefinitionException>(() => EnumRows.SaveValue(_conn, null, EnumTable, ValueTable, colors.Id, new EnumValueRecord(0, "Other", "2")));  // value taken
        Assert.Throws<DefinitionException>(() => EnumRows.SaveValue(_conn, null, EnumTable, ValueTable, colors.Id, new EnumValueRecord(0, "Neg", "-1")));   // not a valid Integral
        Assert.Throws<DefinitionException>(() => EnumRows.SaveValue(_conn, null, EnumTable, ValueTable, colors.Id, new EnumValueRecord(9999, "Ghost", "7"))); // stale id
        Assert.Throws<DefinitionException>(() => EnumRows.SaveValue(_conn, null, EnumTable, ValueTable, 9999, new EnumValueRecord(0, "Orphan", "7")));      // no such enum

        Assert.That(Count(ValueTable), Is.EqualTo(2));
    }

    [Test]
    public void Reading_inside_a_transaction_sees_what_the_transaction_wrote()
    {
        using var tx = _conn.BeginTransaction();
        var saved = EnumRows.Save(_conn, tx, EnumTable, ValueTable, Colors());
        Assert.That(EnumRows.Get(_conn, tx, EnumTable, ValueTable, saved.Id)!.Values, Has.Count.EqualTo(2));
        tx.Rollback();

        Assert.That(Count(EnumTable), Is.EqualTo(0));
        Assert.That(Count(ValueTable), Is.EqualTo(0));
    }
}
