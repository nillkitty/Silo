using Microsoft.Data.Sqlite;
using Silo.DbModel.Sqlite;

namespace Silo.Tests.DbModel;

public class ConnectionRowsTests
{
    private const string Table = "[main].[Connection]";

    private SqliteConnection _conn = null!;

    [SetUp]
    public void SetUp()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE [Connection] ([Id] INTEGER PRIMARY KEY, [UserId] INTEGER, [DisplayName] TEXT, [SourceUri] TEXT, [Created] TEXT, [Modified] TEXT)";
        cmd.ExecuteNonQuery();
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    [Test]
    public void A_saved_connection_is_listed_with_its_name_and_uri()
    {
        var saved = ConnectionRows.Save(_conn, null, Table, null, "Data", new Uri("file:///C:/Data"));

        var all = ConnectionRows.GetAll(_conn, Table);
        Assert.That(all, Has.Count.EqualTo(1));
        Assert.That(all[0].Id, Is.EqualTo(saved.Id));
        Assert.That(all[0].DisplayName, Is.EqualTo("Data"));
        Assert.That(all[0].SourceUri, Is.EqualTo(new Uri("file:///C:/Data")));
    }

    [Test]
    public void The_same_destination_is_renamed_not_added_twice_whatever_its_case()
    {
        var first  = ConnectionRows.Save(_conn, null, Table, null, "Data", new Uri("reg:///hkey_users"));
        var second = ConnectionRows.Save(_conn, null, Table, 7, "Users hive", new Uri("REG:///HKEY_USERS"));

        Assert.That(second.Id, Is.EqualTo(first.Id));
        var all = ConnectionRows.GetAll(_conn, Table);
        Assert.That(all, Has.Count.EqualTo(1));
        Assert.That(all[0].DisplayName, Is.EqualTo("Users hive"));
    }

    [Test]
    public void Connections_without_a_destination_are_always_added()
    {
        ConnectionRows.Save(_conn, null, Table, null, "Local", null);
        ConnectionRows.Save(_conn, null, Table, null, "Local", null);

        Assert.That(ConnectionRows.GetAll(_conn, Table), Has.Count.EqualTo(2));
    }

    [Test]
    public void A_forgotten_connection_is_gone_and_forgetting_twice_says_so()
    {
        var saved = ConnectionRows.Save(_conn, null, Table, null, "Data", new Uri("file:///C:/Data"));

        Assert.That(ConnectionRows.Forget(_conn, null, Table, saved.Id), Is.True);
        Assert.That(ConnectionRows.Forget(_conn, null, Table, saved.Id), Is.False);
        Assert.That(ConnectionRows.GetAll(_conn, Table), Is.Empty);
    }

    [Test]
    public void The_user_who_made_the_connection_is_recorded()
    {
        ConnectionRows.Save(_conn, null, Table, 3, "Data", new Uri("file:///C:/Data"));

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT [UserId] FROM [Connection]";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(3L));
    }
}
