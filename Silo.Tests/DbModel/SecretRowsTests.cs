using System.Linq;
using Microsoft.Data.Sqlite;
using Silo.Contracts.Providers.Data;
using Silo.DbModel.Sqlite;

namespace Silo.Tests.DbModel;

public class SecretRowsTests
{
    private const string Table = "[main].[Secret]";

    private SqliteConnection _conn = null!;

    [SetUp]
    public void SetUp()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        Exec("CREATE TABLE [Secret] ([Id] INTEGER PRIMARY KEY, [Key] TEXT, [Kind] TEXT, [Provider] TEXT, [Data] BLOB, [Created] TEXT, [Modified] TEXT, [UserId] INTEGER)");
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SecretRecord Row(string key, string kind = "credential", byte[]? data = null, string provider = "") =>
        new(0, key, kind, provider, data ?? [1, 2, 3], DateTime.UtcNow, DateTime.UtcNow);

    [Test]
    public void A_row_is_added_with_an_id_and_read_back_with_its_bytes()
    {
        var added = SecretRows.Insert(_conn, null, Table, null, Row("Sales", data: [0, 255, 7, 0, 9]));

        Assert.That(added.Id, Is.GreaterThan(0));
        var got = SecretRows.GetOne(_conn, Table, null, added.Id)!;
        Assert.That(got.Key, Is.EqualTo("Sales"));
        Assert.That(got.Kind, Is.EqualTo("credential"));
        Assert.That(got.Data, Is.EqualTo(new byte[] { 0, 255, 7, 0, 9 }));
        Assert.That(got.Created, Is.EqualTo(added.Created).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public void Rows_can_be_listed_by_kind()
    {
        SecretRows.Insert(_conn, null, Table, null, Row("a"));
        SecretRows.Insert(_conn, null, Table, null, Row("$datakey", "datakey", provider: "dpapi-user"));

        Assert.That(SecretRows.Get(_conn, Table, null, null), Has.Count.EqualTo(2));
        Assert.That(SecretRows.Get(_conn, Table, null, "datakey").Single().Provider, Is.EqualTo("dpapi-user"));
    }

    [Test]
    public void Update_replaces_the_fields_and_stamps_the_time_but_keeps_the_creation_time()
    {
        var added = SecretRows.Insert(_conn, null, Table, null, Row("a"));

        Assert.That(SecretRows.Update(_conn, null, Table, null, added with { Key = "b", Data = [9] }, out var modified), Is.True);

        var got = SecretRows.GetOne(_conn, Table, null, added.Id)!;
        Assert.That(got.Key, Is.EqualTo("b"));
        Assert.That(got.Data, Is.EqualTo(new byte[] { 9 }));
        Assert.That(got.Modified, Is.EqualTo(modified).Within(TimeSpan.FromMilliseconds(1)));
        Assert.That(got.Created, Is.EqualTo(added.Created).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public void Updating_a_row_that_is_not_there_changes_nothing()
    {
        Assert.That(SecretRows.Update(_conn, null, Table, null, Row("a") with { Id = 99 }, out _), Is.False);
    }

    [Test]
    public void Delete_removes_the_row()
    {
        var added = SecretRows.Insert(_conn, null, Table, null, Row("a"));

        Assert.That(SecretRows.Delete(_conn, null, Table, null, added.Id), Is.True);
        Assert.That(SecretRows.Delete(_conn, null, Table, null, added.Id), Is.False);
        Assert.That(SecretRows.Get(_conn, Table, null, null), Is.Empty);
    }

    [Test]
    public void A_person_sees_their_own_rows_and_the_shared_ones_but_not_someone_elses()
    {
        SecretRows.Insert(_conn, null, Table, 1, Row("mine"));
        SecretRows.Insert(_conn, null, Table, 2, Row("theirs"));
        SecretRows.Insert(_conn, null, Table, null, Row("shared"));

        Assert.That(SecretRows.Get(_conn, Table, 1, null).Select(r => r.Key), Is.EquivalentTo(new[] { "mine", "shared" }));
        Assert.That(SecretRows.Get(_conn, Table, 2, null).Select(r => r.Key), Is.EquivalentTo(new[] { "theirs", "shared" }));
        Assert.That(SecretRows.Get(_conn, Table, null, null).Select(r => r.Key), Is.EqualTo(new[] { "shared" }));
    }

    [Test]
    public void The_shared_rows_come_first_so_the_last_row_of_a_kind_is_the_persons_own()
    {
        SecretRows.Insert(_conn, null, Table, 1, Row("own-first", "datakey"));
        SecretRows.Insert(_conn, null, Table, null, Row("shared-later", "datakey"));

        Assert.That(SecretRows.Get(_conn, Table, 1, "datakey").Select(r => r.Key), Is.EqualTo(new[] { "shared-later", "own-first" }));
    }

    [Test]
    public void Someone_elses_row_cannot_be_read_changed_or_deleted()
    {
        var theirs = SecretRows.Insert(_conn, null, Table, 2, Row("theirs"));

        Assert.That(SecretRows.GetOne(_conn, Table, 1, theirs.Id), Is.Null);
        Assert.That(SecretRows.Update(_conn, null, Table, 1, theirs with { Key = "mine now" }, out _), Is.False);
        Assert.That(SecretRows.Delete(_conn, null, Table, 1, theirs.Id), Is.False);
        Assert.That(SecretRows.GetOne(_conn, Table, 2, theirs.Id)!.Key, Is.EqualTo("theirs"));
    }

    [Test]
    public void A_row_from_an_older_Silo_with_empty_new_columns_reads_with_empty_strings()
    {
        Exec("INSERT INTO [Secret] ([Key], [Data]) VALUES ('old', x'0102')");

        var row = SecretRows.Get(_conn, Table, null, null).Single();

        Assert.That(row.Key, Is.EqualTo("old"));
        Assert.That(row.Kind, Is.Empty);
        Assert.That(row.Provider, Is.Empty);
        Assert.That(row.Data, Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(row.Created, Is.EqualTo(DateTime.MinValue));
    }
}
