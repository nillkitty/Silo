using Microsoft.Data.Sqlite;
using Silo.DbModel.Meta;
using Silo.DbModel.Sqlite;
using Silo.Providers.Data;
using Silo.Providers.Definitions;

namespace Silo.Tests.DbModel;

public class SyntaxRowsTests
{
    private const string Table = "[main].[MvSyntax]";

    private SqliteConnection _conn = null!;

    [SetUp]
    public void SetUp()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        Exec("CREATE TABLE [MvSyntax] ([Id] INTEGER PRIMARY KEY, [Name] TEXT, [Alias] TEXT, [Description] TEXT, [Kind] INTEGER, [Parser] TEXT, [Pattern] TEXT, [Example] TEXT, [Flags] INTEGER)");
    }

    [TearDown]
    public void TearDown()
    {
        _conn.Dispose();
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Count()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM " + Table;
        return (long)cmd.ExecuteScalar()!;
    }

    private SyntaxRecord Save(SyntaxRecord s)
    {
        return SyntaxRows.Save(_conn, null, Table, s);
    }

    private List<SyntaxRecord> All()
    {
        return SyntaxRows.GetAll(_conn, null, Table);
    }

    [Test]
    public void The_predefined_syntaxes_are_added_once_and_come_back_in_name_order()
    {
        Assert.That(SyntaxRows.InsertMissing(_conn, null, Table, SyntaxSeeds.Defaults), Is.EqualTo(19));
        Assert.That(SyntaxRows.InsertMissing(_conn, null, Table, SyntaxSeeds.Defaults), Is.EqualTo(0));
        Assert.That(Count(),                                                            Is.EqualTo(19));

        var all = All();
        Assert.That(all.Select(s => s.Name),
                    Is.EqualTo(SyntaxSeeds.Defaults.Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)));
        Assert.That(all.All(s => s.IsSeeded && s.Kind == SyntaxKind.Parser && s.Id > 0), Is.True);
    }

    [Test]
    public void A_deleted_predefined_syntax_is_restored_and_a_taken_name_is_left_alone()
    {
        SyntaxRows.InsertMissing(_conn, null, Table, SyntaxSeeds.Defaults);
        var guid = SyntaxRows.Find(_conn, null, Table, "guid")!;
        var tsv  = SyntaxRows.Find(_conn, null, Table, "TSV")!;

        SyntaxRows.Delete(_conn, null, Table, guid.Id);
        SyntaxRows.Save(_conn, null, Table, tsv with { Name = "Tabs", Alias = "tsv" }); // renamed, but the alias still holds the old name
        Assert.That(Count(), Is.EqualTo(18));

        Assert.That(SyntaxRows.InsertMissing(_conn, null, Table, SyntaxSeeds.Defaults),
                    Is.EqualTo(1)); // Guid is back; Tsv is not, its name is the alias of Tabs
        Assert.That(SyntaxRows.Find(_conn, null, Table, "Guid"),      Is.Not.Null);
        Assert.That(SyntaxRows.Find(_conn, null, Table, "Tsv")!.Name, Is.EqualTo("Tabs"));
        Assert.That(Count(),                                          Is.EqualTo(19));
    }

    [Test]
    public void A_syntax_is_found_by_name_then_alias_without_regard_to_case()
    {
        SyntaxRows.InsertMissing(_conn, null, Table, SyntaxSeeds.Defaults);

        Assert.That(SyntaxRows.Find(_conn, null, Table, "jobject")!.Name, Is.EqualTo("JObject"));
        Assert.That(SyntaxRows.Find(_conn, null, Table, "JOBJ")!.Name,    Is.EqualTo("JObject"));
        Assert.That(SyntaxRows.Find(_conn, null, Table, "nothing"),       Is.Null);
        Assert.That(SyntaxRows.Get(_conn, null, Table, 9999),             Is.Null);
    }

    [Test]
    public void A_custom_syntax_round_trips_every_field()
    {
        var saved =
            Save(new SyntaxRecord(0, " Phone ", " ph ", "A phone number", SyntaxKind.Pattern, "ignored", @"\d{3}-\d{4}", "555-1234"));

        Assert.That(saved.Id,          Is.GreaterThan(0));
        Assert.That(saved.Name,        Is.EqualTo("Phone"));
        Assert.That(saved.Alias,       Is.EqualTo("ph"));
        Assert.That(saved.Description, Is.EqualTo("A phone number"));
        Assert.That(saved.Kind,        Is.EqualTo(SyntaxKind.Pattern));
        Assert.That(saved.Parser,      Is.Null); // a pattern syntax keeps no parser key
        Assert.That(saved.Pattern,     Is.EqualTo(@"\d{3}-\d{4}"));
        Assert.That(saved.Example,     Is.EqualTo("555-1234"));
        Assert.That(saved.IsSeeded,    Is.False);
    }

    [Test]
    public void Saving_again_updates_in_place_and_may_keep_the_same_name()
    {
        var saved = Save(SyntaxRecord.ForParser("When", "timestamp", "w"));
        var again = Save(saved with { Description = "when it happened", Parser = "duration", Example = "PT5M" });

        Assert.That(again.Id,          Is.EqualTo(saved.Id));
        Assert.That(again.Description, Is.EqualTo("when it happened"));
        Assert.That(again.Parser,      Is.EqualTo("duration"));
        Assert.That(Count(),           Is.EqualTo(1));
    }

    [Test]
    public void A_seeded_syntax_stays_seeded_when_edited()
    {
        SyntaxRows.InsertMissing(_conn, null, Table, SyntaxSeeds.Defaults);
        var timestamp = SyntaxRows.Find(_conn, null, Table, "Timestamp")!;

        var edited = Save(timestamp with { Description = "mine now" });
        Assert.That(edited.IsSeeded,    Is.True);
        Assert.That(edited.Description, Is.EqualTo("mine now"));
    }

    [Test]
    public void Names_and_aliases_cannot_repeat_and_definitions_must_hold_together()
    {
        Save(SyntaxRecord.ForParser("Taken", "guid", "tk"));

        Assert.Throws<DefinitionException>(() => Save(SyntaxRecord.ForParser("TAKEN", "guid")));
        Assert.Throws<DefinitionException>(() => Save(SyntaxRecord.ForParser("Fresh", "guid", "TK")));
        Assert.Throws<DefinitionException>(() => Save(SyntaxRecord.ForParser("tk",    "guid")));
        Assert.Throws<DefinitionException>(() => Save(SyntaxRecord.ForParser("Fresh", "nosuchparser")));
        Assert.Throws<DefinitionException>(() => Save(SyntaxRecord.ForPattern("Fresh", "(unclosed")));
        Assert.Throws<DefinitionException>(() => Save(SyntaxRecord.ForPattern("Fresh", "a+", example: "bbb")));
        Assert.That(Count(), Is.EqualTo(1));
    }

    [Test]
    public void Updating_a_syntax_that_was_deleted_in_the_meantime_says_so()
    {
        var saved = Save(SyntaxRecord.ForParser("Gone", "guid"));
        SyntaxRows.Delete(_conn, null, Table, saved.Id);

        var ex = Assert.Throws<DefinitionException>(() => Save(saved));
        Assert.That(ex!.Message.Contains("no longer exists"), Is.True);
        Assert.That(Count(),                                  Is.EqualTo(0));
    }

    [Test]
    public void Delete_says_whether_there_was_a_row()
    {
        var saved = Save(SyntaxRecord.ForParser("Once", "guid"));
        Assert.That(SyntaxRows.Delete(_conn, null, Table, saved.Id), Is.True);
        Assert.That(SyntaxRows.Delete(_conn, null, Table, saved.Id), Is.False);
    }
}