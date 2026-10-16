using System.Linq;
using Microsoft.Data.Sqlite;
using Silo.Contracts.Providers.Data;
using Silo.DbModel.Sqlite;

namespace Silo.Tests.DbModel;

public class PrefRowsTests
{
    private const string Table = "[main].[Pref]";

    private SqliteConnection _conn = null!;

    [SetUp]
    public void SetUp()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        Exec("CREATE TABLE [Pref] ([Id] INTEGER PRIMARY KEY, [Key] TEXT, [Data] TEXT, [Created] TEXT, [Modified] TEXT, [UserId] INTEGER)");
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
        cmd.CommandText = "SELECT COUNT(*) FROM [Pref]";
        return (long)cmd.ExecuteScalar()!;
    }

    private static string? ValueOf(IEnumerable<PreferenceRecord> records, string key) => records.FirstOrDefault(r => r.Key == key)?.Data;

    [Test]
    public void A_value_is_written_and_read_back()
    {
        Assert.That(PrefRows.Set(_conn, null, Table, null, "silo.a", "true"), Is.True);

        Assert.That(PrefRows.Get(_conn, Table, null), Is.EqualTo(new[] { new PreferenceRecord("silo.a", "true") }));
    }

    [Test]
    public void Writing_the_same_value_again_changes_nothing()
    {
        PrefRows.Set(_conn, null, Table, null, "silo.a", "true");

        Assert.That(PrefRows.Set(_conn, null, Table, null, "silo.a", "true"), Is.False);
        Assert.That(Count(), Is.EqualTo(1));
    }

    [Test]
    public void Writing_a_new_value_replaces_the_row_instead_of_adding_one()
    {
        PrefRows.Set(_conn, null, Table, null, "silo.a", "true");

        Assert.That(PrefRows.Set(_conn, null, Table, null, "silo.a", "false"), Is.True);

        Assert.That(Count(), Is.EqualTo(1));
        Assert.That(ValueOf(PrefRows.Get(_conn, Table, null), "silo.a"), Is.EqualTo("false"));
    }

    [Test]
    public void A_users_own_value_wins_over_the_shared_one_for_that_key_only()
    {
        PrefRows.Set(_conn, null, Table, null, "silo.a", "shared a");
        PrefRows.Set(_conn, null, Table, null, "silo.b", "shared b");
        PrefRows.Set(_conn, null, Table, 7, "silo.a", "mine a");

        var mine = PrefRows.Get(_conn, Table, 7);

        Assert.That(ValueOf(mine, "silo.a"), Is.EqualTo("mine a"));
        Assert.That(ValueOf(mine, "silo.b"), Is.EqualTo("shared b"), "no row of their own: the shared one applies");
        Assert.That(mine, Has.Count.EqualTo(2));
    }

    [Test]
    public void A_users_change_leaves_the_shared_value_and_other_users_alone()
    {
        PrefRows.Set(_conn, null, Table, null, "silo.a", "shared");
        PrefRows.Set(_conn, null, Table, 7, "silo.a", "mine");

        Assert.That(ValueOf(PrefRows.Get(_conn, Table, null), "silo.a"), Is.EqualTo("shared"));
        Assert.That(ValueOf(PrefRows.Get(_conn, Table, 8),    "silo.a"), Is.EqualTo("shared"));
        Assert.That(Count(), Is.EqualTo(2));
    }

    [Test]
    public void A_user_writing_the_shared_value_still_gets_a_row_of_their_own()
    {
        PrefRows.Set(_conn, null, Table, null, "silo.a", "same");

        Assert.That(PrefRows.Set(_conn, null, Table, 7, "silo.a", "same"), Is.True);
        Assert.That(Count(), Is.EqualTo(2));
    }

    [Test]
    public void A_transaction_that_is_rolled_back_keeps_the_previous_value()
    {
        PrefRows.Set(_conn, null, Table, null, "silo.a", "one");

        using (var tx = _conn.BeginTransaction())
        {
            PrefRows.Set(_conn, tx, Table, null, "silo.a", "two");
            tx.Rollback();
        }

        Assert.That(ValueOf(PrefRows.Get(_conn, Table, null), "silo.a"), Is.EqualTo("one"));
    }
}
