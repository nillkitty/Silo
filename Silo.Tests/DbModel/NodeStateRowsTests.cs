using System.Linq;
using Microsoft.Data.Sqlite;
using Silo.Contracts.Providers.Data;
using Silo.DbModel.Sqlite;

namespace Silo.Tests.DbModel;

public class NodeStateRowsTests
{
    private const string Table = "[main].[Node]";

    private SqliteConnection _conn = null!;

    [SetUp]
    public void SetUp()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        Exec("CREATE TABLE [Node] ([Id] INTEGER PRIMARY KEY, [Uri] TEXT, [Name] TEXT, [IconUri] TEXT, [Expanded] INTEGER, [IsVisible] INTEGER, [Created] TEXT, [Modified] TEXT, [UserId] INTEGER, [IsOpen] INTEGER, [IsSelected] INTEGER)");
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
        cmd.CommandText = "SELECT COUNT(*) FROM [Node]";
        return (long)cmd.ExecuteScalar()!;
    }

    private static NodeStateRecord Rec(string key, bool expanded = false, bool open = false, bool selected = false)
        => new(key, "name of " + key, expanded, open, selected);

    [Test]
    public void The_key_survives_the_round_trip_through_the_uri_even_with_awkward_characters()
    {
        foreach (string key in new[] { "node/app/1", "file:///C:/a b/c&d=e?f#g", "über/ключ", "100%/ok" })
            Assert.That(NodeStateRows.KeyFrom(NodeStateRows.UriFor(key)), Is.EqualTo(key));
    }

    [Test]
    public void Other_uris_are_not_remembered_nodes()
    {
        Assert.That(NodeStateRows.KeyFrom("http://example.com/"), Is.Null);
        Assert.That(NodeStateRows.KeyFrom(null),                  Is.Null);
        Assert.That(NodeStateRows.KeyFrom(""),                    Is.Null);
    }

    [Test]
    public void What_was_saved_is_read_back_with_its_flags()
    {
        NodeStateRows.Save(_conn, null, Table, null, [Rec("a", expanded: true), Rec("b", open: true), Rec("c", selected: true), Rec("d")]);

        var read = NodeStateRows.Get(_conn, Table, null);

        Assert.That(read, Is.EqualTo(new[] { Rec("a", expanded: true), Rec("b", open: true), Rec("c", selected: true), Rec("d") }));
    }

    [Test]
    public void Saving_replaces_what_was_saved_before()
    {
        NodeStateRows.Save(_conn, null, Table, null, [Rec("a", expanded: true), Rec("b", expanded: true)]);
        NodeStateRows.Save(_conn, null, Table, null, [Rec("b", open: true)]);

        Assert.That(NodeStateRows.Get(_conn, Table, null), Is.EqualTo(new[] { Rec("b", open: true) }));
        Assert.That(Count(), Is.EqualTo(1));
    }

    [Test]
    public void Saving_nothing_forgets_everything()
    {
        NodeStateRows.Save(_conn, null, Table, null, [Rec("a", expanded: true)]);
        NodeStateRows.Save(_conn, null, Table, null, []);

        Assert.That(NodeStateRows.Get(_conn, Table, null), Is.Empty);
    }

    [Test]
    public void A_users_own_rows_win_over_the_shared_ones_and_leave_them_alone()
    {
        NodeStateRows.Save(_conn, null, Table, null, [Rec("shared", expanded: true)]);
        Assert.That(NodeStateRows.Get(_conn, Table, 7).Select(r => r.Key), Is.EqualTo(new[] { "shared" }), "falls back to the shared rows");

        NodeStateRows.Save(_conn, null, Table, 7, [Rec("mine", expanded: true)]);

        Assert.That(NodeStateRows.Get(_conn, Table, 7).Select(r => r.Key),   Is.EqualTo(new[] { "mine" }));
        Assert.That(NodeStateRows.Get(_conn, Table, null).Select(r => r.Key), Is.EqualTo(new[] { "shared" }));
        Assert.That(NodeStateRows.Get(_conn, Table, 8).Select(r => r.Key),   Is.EqualTo(new[] { "shared" }), "another user sees the shared rows, not mine");
    }

    [Test]
    public void Saving_leaves_the_tables_other_rows_alone()
    {
        Exec("INSERT INTO [Node] (Uri, Name, IsVisible, UserId) VALUES ('silo:somewhere-else', 'other', 1, NULL)");

        NodeStateRows.Save(_conn, null, Table, null, [Rec("a", expanded: true)]);
        NodeStateRows.Save(_conn, null, Table, null, []);

        Assert.That(Count(), Is.EqualTo(1));
    }

    [Test]
    public void Rows_from_before_the_open_and_selected_columns_read_as_false()
    {
        Exec($"INSERT INTO [Node] (Uri, Name, Expanded, UserId) VALUES ('{NodeStateRows.UriFor("old")}', 'old', 1, NULL)");

        var read = NodeStateRows.Get(_conn, Table, null);

        Assert.That(read, Is.EqualTo(new[] { new NodeStateRecord("old", "old", true, false, false) }));
    }

    [Test]
    public void A_transaction_that_is_rolled_back_keeps_the_previous_state()
    {
        NodeStateRows.Save(_conn, null, Table, null, [Rec("a", expanded: true)]);

        using (var tx = _conn.BeginTransaction())
        {
            NodeStateRows.Save(_conn, tx, Table, null, [Rec("z", expanded: true)]);
            tx.Rollback();
        }

        Assert.That(NodeStateRows.Get(_conn, Table, null).Select(r => r.Key), Is.EqualTo(new[] { "a" }));
    }
}
