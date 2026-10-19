using System.Linq;
using Microsoft.Data.Sqlite;
using Silo.Contracts.Providers.Data;
using Silo.DbModel.Sqlite;

namespace Silo.Tests.DbModel;

public class StoredRowsTests
{
    private const string Table = "[main].[Stored]";

    private SqliteConnection _conn = null!;

    [SetUp]
    public void SetUp()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        Exec("CREATE TABLE [Stored] ([Id] INTEGER PRIMARY KEY, [Data] TEXT, [Format] TEXT, [Key] TEXT, [TypeName] TEXT, [Created] TEXT, [Modified] TEXT, [UserId] INTEGER)");
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Count()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM [Stored]";
        return (long)cmd.ExecuteScalar()!;
    }

    private static StoredScope ScopeOf(long? user) => user is null ? StoredScope.Shared : StoredScope.Personal;

    private (StoredRecord Record, bool Changed) Set(long? user, string key, string data, string type = "System.String", string format = "json")
        => StoredRows.Set(_conn, null, Table, user, ScopeOf(user), key, type, format, data);

    private StoredRecord? Get(long? user, string key) => StoredRows.Get(_conn, null, Table, user, ScopeOf(user), key);

    private List<StoredRecord> List(long? user, string? prefix = null) => StoredRows.List(_conn, null, Table, user, ScopeOf(user), prefix);

    [Test]
    public void What_was_stored_is_read_back_with_its_type_and_format()
    {
        var (record, changed) = Set(1, "your:green", "\"U+1F7E2\"", "Silo.Unicode.CodePoint, Silo");

        Assert.That(changed, Is.True);
        Assert.That(record.Key, Is.EqualTo("your:green"));
        Assert.That(record.TypeName, Is.EqualTo("Silo.Unicode.CodePoint, Silo"));
        Assert.That(record.Format, Is.EqualTo("json"));
        Assert.That(record.Data, Is.EqualTo("\"U+1F7E2\""));
        Assert.That(record.Scope, Is.EqualTo(StoredScope.Personal));
        Assert.That(record.Created, Is.EqualTo(record.Modified));
        Assert.That(record.Modified.Kind, Is.EqualTo(DateTimeKind.Utc));
        Assert.That((record.Modified - DateTime.UtcNow).Duration() < TimeSpan.FromMinutes(1), Is.True);
    }

    [Test]
    public void The_key_is_found_in_any_case()
    {
        Set(1, "Your:Green", "1");
        Assert.That(Get(1, "your:GREEN")!.Data, Is.EqualTo("1"));
        Assert.That(Get(1, "your:green")!.Key, Is.EqualTo("Your:Green"));
    }

    [Test]
    public void Storing_again_replaces_the_row_and_keeps_when_it_was_created()
    {
        var first = Set(1, "k", "\"a\"").Record;
        Exec("UPDATE [Stored] SET [Created] = '2020-01-01T00:00:00.0000000Z', [Modified] = '2020-01-01T00:00:00.0000000Z'");

        var (second, changed) = Set(1, "K", "\"b\"");

        Assert.That(changed, Is.True);
        Assert.That(Count(), Is.EqualTo(1));
        Assert.That(second.Data, Is.EqualTo("\"b\""));
        Assert.That(second.Created.Year, Is.EqualTo(2020));
        Assert.That(second.Modified, Is.GreaterThan(second.Created));
        Assert.That(first.Key, Is.EqualTo("k"));
    }

    [Test]
    public void Storing_the_same_again_changes_nothing()
    {
        Set(1, "k", "\"a\"");
        Exec("UPDATE [Stored] SET [Modified] = '2020-01-01T00:00:00.0000000Z'");

        var (record, changed) = Set(1, "k", "\"a\"");

        Assert.That(changed, Is.False);
        Assert.That(record.Modified.Year, Is.EqualTo(2020));
    }

    [Test]
    public void A_different_type_or_format_is_a_change_too()
    {
        Set(1, "k", "1", "System.Int32");
        Assert.That(Set(1, "k", "1", "System.Int64").Changed, Is.True);
        Assert.That(Set(1, "k", "1", "System.Int64", "text").Changed, Is.True);
        Assert.That(Count(), Is.EqualTo(1));
    }

    [Test]
    public void Personal_and_shared_rows_are_apart_and_so_are_two_people()
    {
        Set(1, "k", "\"mine\"");
        Set(2, "k", "\"theirs\"");
        Set(null, "k", "\"shared\"");

        Assert.That(Count(), Is.EqualTo(3));
        Assert.That(Get(1, "k")!.Data,    Is.EqualTo("\"mine\""));
        Assert.That(Get(2, "k")!.Data,    Is.EqualTo("\"theirs\""));
        Assert.That(Get(null, "k")!.Data, Is.EqualTo("\"shared\""));
        Assert.That(Get(3, "k"), Is.Null);
    }

    [Test]
    public void Listing_gives_the_keys_with_the_prefix_in_order()
    {
        foreach (string key in new[] { "your:green", "Your:Blue", "shortcut:a", "your:red" }) Set(1, key, "1");
        Set(null, "your:shared", "1");
        Set(2, "your:theirs", "1");

        Assert.That(List(1, "your:").Select(r => r.Key), Is.EqualTo(new[] { "Your:Blue", "your:green", "your:red" }));
        Assert.That(List(1).Select(r => r.Key),          Is.EqualTo(new[] { "shortcut:a", "Your:Blue", "your:green", "your:red" }));
        Assert.That(List(1, "").Count,                   Is.EqualTo(4));
        Assert.That(List(null, "your:").Select(r => r.Key), Is.EqualTo(new[] { "your:shared" }));
    }

    [Test]
    public void The_prefix_is_text_not_a_pattern()
    {
        foreach (string key in new[] { "a_b", "aXb", "100%", "1000", "a\\b" }) Set(1, key, "1");

        Assert.That(List(1, "a_").Select(r => r.Key),  Is.EqualTo(new[] { "a_b" }));
        Assert.That(List(1, "100%").Select(r => r.Key), Is.EqualTo(new[] { "100%" }));
        Assert.That(List(1, "a\\").Select(r => r.Key), Is.EqualTo(new[] { "a\\b" }));
        Assert.That(StoredRows.LikePrefix("a_b%\\"), Is.EqualTo("a\\_b\\%\\\\%"));
    }

    [Test]
    public void Deleting_takes_only_that_persons_row()
    {
        Set(1, "k", "1");
        Set(null, "k", "1");

        Assert.That(StoredRows.Delete(_conn, null, Table, 1, "K"), Is.True);
        Assert.That(StoredRows.Delete(_conn, null, Table, 1, "k"), Is.False);
        Assert.That(Get(null, "k"), Is.Not.Null);
        Assert.That(Count(), Is.EqualTo(1));
    }

    [Test]
    public void It_works_inside_a_transaction()
    {
        using var tx = _conn.BeginTransaction();
        var (record, changed) = StoredRows.Set(_conn, tx, Table, 1, StoredScope.Personal, "k", "System.String", "json", "\"a\"");
        Assert.That(changed, Is.True);
        Assert.That(StoredRows.Get(_conn, tx, Table, 1, StoredScope.Personal, "k")!.Data, Is.EqualTo("\"a\""));
        tx.Rollback();
        Assert.That(Count(), Is.EqualTo(0));
        Assert.That(record.Key, Is.EqualTo("k"));
    }

    [Test]
    public void A_key_is_required()
    {
        Assert.Throws<ArgumentException>(() => Set(1, " ", "1"));
    }
}
