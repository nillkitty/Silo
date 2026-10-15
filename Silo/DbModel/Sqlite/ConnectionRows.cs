using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Silo.DbModel.Sqlite;

/// <summary>The SQL behind <see cref="IConnectionStore" />: rows of the <c>Connection</c> table. Kept apart from the provider so it can be tested on its own.</summary>
public static class ConnectionRows
{
    /// <summary>Every row, oldest first.</summary>
    public static List<ConnectionRecord> GetAll(SqliteConnection conn, string table)
    {
        var result = new List<ConnectionRecord>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT [Id], [DisplayName], [SourceUri] FROM {table} ORDER BY [Id]";

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            Uri? uri = null;
            if (!r.IsDBNull(2) && Uri.TryCreate(r.GetString(2), UriKind.Absolute, out var parsed)) uri = parsed;

            result.Add(new ConnectionRecord(r.GetInt64(0), r.IsDBNull(1) ? string.Empty : r.GetString(1), uri));
        }

        return result;
    }

    /// <summary>
    ///     Adds the connection, or renames the existing row for the same destination (compared case-insensitively). A
    ///     connection without a destination is always added.
    /// </summary>
    public static ConnectionRecord Save(SqliteConnection conn, SqliteTransaction? tx, string table, long? userId, string displayName, Uri? sourceUri)
    {
        string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        object uri = sourceUri is null ? DBNull.Value : sourceUri.ToString();
        object uid = userId is { } id ? id : DBNull.Value;

        if (sourceUri is not null)
        {
            long? existing;
            using (var find = conn.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText = $"SELECT [Id] FROM {table} WHERE [SourceUri] = @uri COLLATE NOCASE ORDER BY [Id] LIMIT 1";
                find.Parameters.AddWithValue("@uri", uri);
                existing = find.ExecuteScalar() is { } v and not DBNull ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;
            }

            if (existing is { } rowId)
            {
                using var update = conn.CreateCommand();
                update.Transaction = tx;
                update.CommandText = $"UPDATE {table} SET [DisplayName] = @name, [Modified] = @now WHERE [Id] = @id";
                update.Parameters.AddWithValue("@name", displayName);
                update.Parameters.AddWithValue("@now",  now);
                update.Parameters.AddWithValue("@id",   rowId);
                update.ExecuteNonQuery();
                return new ConnectionRecord(rowId, displayName, sourceUri);
            }
        }

        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = $"INSERT INTO {table} ([DisplayName], [SourceUri], [Created], [Modified], [UserId]) VALUES (@name, @uri, @now, @now, @uid)";
        insert.Parameters.AddWithValue("@name", displayName);
        insert.Parameters.AddWithValue("@uri",  uri);
        insert.Parameters.AddWithValue("@now",  now);
        insert.Parameters.AddWithValue("@uid",  uid);
        insert.ExecuteNonQuery();

        using var last = conn.CreateCommand();
        last.Transaction = tx;
        last.CommandText = "SELECT last_insert_rowid()";
        long newId = Convert.ToInt64(last.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new ConnectionRecord(newId, displayName, sourceUri);
    }

    /// <summary>Deletes the row; returns whether there was one.</summary>
    public static bool Forget(SqliteConnection conn, SqliteTransaction? tx, string table, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {table} WHERE [Id] = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() > 0;
    }
}
