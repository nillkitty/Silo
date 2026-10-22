using Microsoft.Data.Sqlite;
using Silo.DbModel.Global;
using Silo.DbModel.Sqlite;
using Silo.Providers.Data;

namespace Silo.Tests.DbModel;

public class WindowRowsTests
{
    private const string Table = "[main].[Window]";
    private const string Users = "[main].[User]";

    private SqliteConnection _conn = null!;

    [SetUp]
    public void SetUp()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        Exec("CREATE TABLE [Window] ([Id] INTEGER PRIMARY KEY, [Type] TEXT, [Uri] TEXT, [X] INTEGER, [Y] INTEGER, [Width] INTEGER, [Height] INTEGER, [Created] TEXT, [Modified] TEXT, [UserId] INTEGER, [State] INTEGER, [Layout] TEXT)");
        Exec("CREATE TABLE [User] ([Id] INTEGER PRIMARY KEY, [Domain] TEXT, [Name] TEXT, [SID] TEXT, [Group] INTEGER)");
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
        cmd.CommandText = "SELECT COUNT(*) FROM [Window]";
        return (long)cmd.ExecuteScalar()!;
    }

    private static WindowRecord Main(int x, string? layout = null, WindowPlacementState state = WindowPlacementState.Normal)
    {
        return new WindowRecord("Main", "silo:main", x, 20, 800, 600, state, layout);
    }

    [Test]
    public void Without_a_user_the_shared_row_is_written_in_place_and_read_back()
    {
        WindowRows.Save(_conn, null, Table, null, [Main(10, "<layout/>", WindowPlacementState.Maximized)]);
        WindowRows.Save(_conn, null, Table, null, [Main(11, "<layout/>", WindowPlacementState.Maximized)]);

        Assert.That(Count(), Is.EqualTo(1));
        var read = WindowRows.Get(_conn, Table, null, "Main", "silo:main");
        Assert.That(read, Is.EqualTo(Main(11, "<layout/>", WindowPlacementState.Maximized)));
    }

    [Test]
    public void A_users_own_row_wins_over_the_shared_one_and_leaves_it_alone()
    {
        Exec("INSERT INTO [User] (Domain, Name, SID, [Group]) VALUES ('D', 'D\\me', 'S-1', 0)");
        long? me = WindowRows.FindUserId(_conn, Users, "D\\me", "S-1");
        Assert.That(me, Is.Not.Null);

        WindowRows.Save(_conn, null, Table, null, [Main(1)]);
        Assert.That(WindowRows.Get(_conn, Table, me, "Main", "silo:main")!.X, Is.EqualTo(1), "falls back to the shared row");

        WindowRows.Save(_conn, null, Table, me, [Main(2)]);
        Assert.That(WindowRows.Get(_conn, Table, me,   "Main", "silo:main")!.X, Is.EqualTo(2));
        Assert.That(WindowRows.Get(_conn, Table, null, "Main", "silo:main")!.X, Is.EqualTo(1));
        Assert.That(Count(),                                                    Is.EqualTo(2));
    }

    [Test]
    public void Users_are_found_by_sid_then_by_name_and_strangers_are_not_found()
    {
        Exec("INSERT INTO [User] (Domain, Name, SID, [Group]) VALUES ('D', 'D\\me', 'S-1', 0)");

        Assert.That(WindowRows.FindUserId(_conn, Users, "other",    "S-1"), Is.Not.Null);
        Assert.That(WindowRows.FindUserId(_conn, Users, "D\\me",    ""),    Is.Not.Null);
        Assert.That(WindowRows.FindUserId(_conn, Users, "D\\other", "S-2"), Is.Null);
        Assert.That(WindowRows.FindUserId(_conn, Users, "D\\other", ""),    Is.Null);
    }

    [Test]
    public void Clear_forgets_only_the_users_rows()
    {
        Exec("INSERT INTO [User] (Domain, Name, SID, [Group]) VALUES ('D', 'D\\me', 'S-1', 0)");
        long? me = WindowRows.FindUserId(_conn, Users, "D\\me", "S-1");

        WindowRows.Save(_conn, null, Table, null, [Main(1)]);
        WindowRows.Save(_conn, null, Table, me,
                        [Main(2), new WindowRecord("Table", "silo:table/5", 1, 1, 400, 300, WindowPlacementState.Normal)]);

        Assert.That(WindowRows.Clear(_conn, Table, me),                       Is.EqualTo(2));
        Assert.That(Count(),                                                  Is.EqualTo(1));
        Assert.That(WindowRows.Get(_conn, Table, me, "Main", "silo:main")!.X, Is.EqualTo(1));
    }

    [Test]
    public void Forget_removes_a_window_for_everyone()
    {
        Exec("INSERT INTO [User] (Domain, Name, SID, [Group]) VALUES ('D', 'D\\me', 'S-1', 0)");
        long? me    = WindowRows.FindUserId(_conn, Users, "D\\me", "S-1");
        var   table = new WindowRecord("Table", "silo:table/5", 1, 1, 400, 300, WindowPlacementState.Normal);

        WindowRows.Save(_conn, null, Table, null, [table]);
        WindowRows.Save(_conn, null, Table, me,   [table, Main(3)]);

        Assert.That(WindowRows.Forget(_conn, Table, "Table", "silo:table/5"), Is.EqualTo(2));
        Assert.That(Count(),                                                  Is.EqualTo(1));
    }

    [Test]
    public void Rows_from_before_the_state_and_layout_columns_read_as_normal_without_a_layout()
    {
        Exec("INSERT INTO [Window] (Type, Uri, X, Y, Width, Height, Created, Modified) VALUES ('Main', 'silo:main', 1, 2, 300, 400, 'a', 'a')");

        var read = WindowRows.Get(_conn, Table, null, "Main", "silo:main");
        Assert.That(read, Is.EqualTo(new WindowRecord("Main", "silo:main", 1, 2, 300, 400, WindowPlacementState.Normal)));
    }
}

public class SchemaUpgradeTests
{
    [Test]
    public void Missing_columns_are_added_once_and_existing_rows_keep_their_data()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE [Window] ([Id] INTEGER PRIMARY KEY, [Type] TEXT); INSERT INTO [Window] (Type) VALUES ('Main')";
            create.ExecuteNonQuery();
        }

        (string, string)[] wanted = [("Type", "TEXT"), ("State", "INTEGER"), ("Layout", "TEXT")];

        Assert.That(SchemaUpgrade.AddMissingColumns(conn, "[main].[Window]", wanted), Is.EquivalentTo(new[] { "State", "Layout" }));
        Assert.That(SchemaUpgrade.AddMissingColumns(conn, "[main].[Window]", wanted), Is.Empty);

        using var read = conn.CreateCommand();
        read.CommandText = "SELECT Type, State, Layout FROM [Window]";
        using var reader = read.ExecuteReader();
        Assert.That(reader.Read());
        Assert.That(reader.GetString(0), Is.EqualTo("Main"));
        Assert.That(reader.IsDBNull(1));
        Assert.That(reader.IsDBNull(2));
    }
}