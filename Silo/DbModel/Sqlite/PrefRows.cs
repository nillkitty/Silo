using System.Globalization;
using Microsoft.Data.Sqlite;
using Silo.Providers.Data;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     The SQL behind <see cref="IPreferenceStore" />: the <c>Pref</c> table, one row per key and user. Kept apart
///     from the provider so it can be tested on its own.
/// </summary>
public static class PrefRows
{
    /// <summary>
    ///     The preferences that apply to the user: the Silo's shared rows (no user), each replaced by the user's own row
    ///     for the same key.
    /// </summary>
    public static List<PreferenceRecord> Get(SqliteConnection conn, string table, long? userId)
    {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT [Key], [Data] FROM {table} WHERE [UserId] IS NULL OR [UserId] IS @uid " +
                          "ORDER BY CASE WHEN [UserId] IS NULL THEN 0 ELSE 1 END, [Id]"; // the user's own rows last, so they win
        cmd.Parameters.AddWithValue("@uid", userId is { } id ? id : DBNull.Value);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0)) continue;

            string key = r.GetString(0);
            if (!byKey.ContainsKey(key)) order.Add(key);
            byKey[key] = r.IsDBNull(1) ? string.Empty : r.GetString(1);
        }

        return order.Select(k => new PreferenceRecord(k, byKey[k])).ToList();
    }

    /// <summary>
    ///     Writes the user's value for the key (the shared one when <paramref name="userId" /> is null). False if it
    ///     already was that.
    /// </summary>
    public static bool Set(SqliteConnection conn, SqliteTransaction? tx, string table, long? userId, string key, string data)
    {
        string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        object uid = userId is { } id ? id : DBNull.Value;

        long?   rowId = null;
        string? held  = null;
        using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = $"SELECT [Id], [Data] FROM {table} WHERE [Key] = @key AND [UserId] IS @uid ORDER BY [Id] LIMIT 1";
            find.Parameters.AddWithValue("@key", key);
            find.Parameters.AddWithValue("@uid", uid);
            using var r = find.ExecuteReader();
            if (r.Read())
            {
                rowId = r.GetInt64(0);
                held  = r.IsDBNull(1) ? string.Empty : r.GetString(1);
            }
        }

        if (rowId is not null && held == data) return false;

        using var write = conn.CreateCommand();
        write.Transaction = tx;
        if (rowId is { } existing)
        {
            write.CommandText = $"UPDATE {table} SET [Data] = @data, [Modified] = @now WHERE [Id] = @id";
            write.Parameters.AddWithValue("@id", existing);
        }
        else
        {
            write.CommandText =
                $"INSERT INTO {table} ([Key], [Data], [Created], [Modified], [UserId]) VALUES (@key, @data, @now, @now, @uid)";
            write.Parameters.AddWithValue("@key", key);
            write.Parameters.AddWithValue("@uid", uid);
        }

        write.Parameters.AddWithValue("@data", data);
        write.Parameters.AddWithValue("@now",  now);
        write.ExecuteNonQuery();
        return true;
    }
}