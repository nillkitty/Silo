using System.Globalization;
using Microsoft.Data.Sqlite;
using Silo.DbModel.Global;

namespace Silo.DbModel.Sqlite;

/// <summary>The SQL behind <see cref="IWindowStore" />: rows of the <c>Window</c> table. Kept apart from the provider so it can be tested on its own.</summary>
public static class WindowRows
{
    private const string Columns = "X, Y, Width, Height, State, Layout";

    /// <summary>The id of the Silo user with this SID (else this name), or null when the person is not one of the Silo's Users.</summary>
    public static long? FindUserId(SqliteConnection conn, string userTable, string name, string sid)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT [Id] FROM {userTable} WHERE (@sid <> '' AND [SID] = @sid) OR [Name] = @name ORDER BY [Id] LIMIT 1";
        cmd.Parameters.AddWithValue("@sid",  sid);
        cmd.Parameters.AddWithValue("@name", name);
        return cmd.ExecuteScalar() is { } v and not DBNull ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The user's own row if there is one, else the shared one (no user).</summary>
    public static WindowRecord? Get(SqliteConnection conn, string table, long? userId, string type, string uri)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {table} WHERE [Type] = @type AND [Uri] = @uri AND ([UserId] IS NULL OR [UserId] = @uid) " +
                          "ORDER BY ([UserId] IS NULL) LIMIT 1";
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@uri",  uri);
        cmd.Parameters.AddWithValue("@uid",  userId is { } id ? id : DBNull.Value);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        return new WindowRecord(type, uri, r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3),
                                r.IsDBNull(4) ? WindowPlacementState.Normal : (WindowPlacementState)r.GetInt32(4),
                                r.IsDBNull(5) ? null : r.GetString(5));
    }

    /// <summary>Writes the windows as the user's rows: a changed one is updated in place, a new one added.</summary>
    public static void Save(SqliteConnection conn, SqliteTransaction? tx, string table, long? userId, IEnumerable<WindowRecord> records)
    {
        string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        object uid = userId is { } id ? id : DBNull.Value;

        foreach (var w in records)
        {
            int changed;
            using (var update = conn.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = $"UPDATE {table} SET X = @x, Y = @y, Width = @w, Height = @h, State = @state, Layout = @layout, Modified = @now " +
                                     "WHERE [Type] = @type AND [Uri] = @uri AND [UserId] IS @uid";
                Bind(update, w, uid, now);
                changed = update.ExecuteNonQuery();
            }

            if (changed > 0) continue;

            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = $"INSERT INTO {table} ([Type], [Uri], X, Y, Width, Height, State, Layout, Created, Modified, [UserId]) " +
                                 "VALUES (@type, @uri, @x, @y, @w, @h, @state, @layout, @now, @now, @uid)";
            Bind(insert, w, uid, now);
            insert.ExecuteNonQuery();
        }
    }

    private static void Bind(SqliteCommand cmd, WindowRecord w, object uid, string now)
    {
        cmd.Parameters.AddWithValue("@type",   w.Type);
        cmd.Parameters.AddWithValue("@uri",    w.Uri);
        cmd.Parameters.AddWithValue("@x",      w.X);
        cmd.Parameters.AddWithValue("@y",      w.Y);
        cmd.Parameters.AddWithValue("@w",      w.Width);
        cmd.Parameters.AddWithValue("@h",      w.Height);
        cmd.Parameters.AddWithValue("@state",  (int)w.State);
        cmd.Parameters.AddWithValue("@layout", w.Layout is { } layout ? layout : DBNull.Value);
        cmd.Parameters.AddWithValue("@now",    now);
        cmd.Parameters.AddWithValue("@uid",    uid);
    }

    /// <summary>Deletes the user's rows (the shared rows when <paramref name="userId" /> is null); returns how many.</summary>
    public static int Clear(SqliteConnection conn, string table, long? userId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE [UserId] IS @uid";
        cmd.Parameters.AddWithValue("@uid", userId is { } id ? id : DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes a window's rows of every user.</summary>
    public static int Forget(SqliteConnection conn, string table, string type, string uri, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {table} WHERE [Type] = @type AND [Uri] = @uri";
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@uri",  uri);
        return cmd.ExecuteNonQuery();
    }
}
