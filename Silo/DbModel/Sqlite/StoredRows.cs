using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Silo.DbModel.Sqlite;

/// <summary>The SQL behind <see cref="IStoredStore" />: rows of the <c>Stored</c> table, one per key and person. Kept apart from the provider so it can be tested on its own.</summary>
/// <remarks>
///     Every method takes the transaction the caller is in (or null) and the id of the person the rows belong to (null: the Silo's shared rows).
///     Keys are compared without regard to case (ASCII).
/// </remarks>
public static class StoredRows
{
    private const string Columns = "[Key], [TypeName], [Format], [Data], [Created], [Modified]";

    private static SqliteCommand Command(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static object Uid(long? userId) => userId is { } id ? id : DBNull.Value;

    private static string Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? string.Empty : r.GetString(i);

    private static DateTime Time(SqliteDataReader r, int i)
    {
        return !r.IsDBNull(i) && DateTime.TryParse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
                   ? t
                   : DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
    }

    private static StoredRecord Read(SqliteDataReader r, StoredScope scope)
    {
        return new StoredRecord(Text(r, 0), Text(r, 1), Text(r, 2), Text(r, 3), Time(r, 4), Time(r, 5), scope);
    }

    /// <summary>The text for a LIKE pattern that matches the keys starting with <paramref name="prefix" /> (<c>%</c>, <c>_</c> and the escape character in it match themselves).</summary>
    public static string LikePrefix(string prefix)
    {
        return prefix.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
    }

    /// <summary>The item with the key (any case) of the person (or the shared one); null if there is none.</summary>
    public static StoredRecord? Get(SqliteConnection conn, SqliteTransaction? tx, string table, long? userId, StoredScope scope, string key)
    {
        using var cmd = Command(conn, tx, $"SELECT {Columns} FROM {table} WHERE [Key] = @key COLLATE NOCASE AND [UserId] IS @uid ORDER BY [Id] LIMIT 1");
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@uid", Uid(userId));

        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r, scope) : null;
    }

    /// <summary>The items of the person (or the shared ones) whose key starts with the prefix (all of them when it is null or empty), ordered by key.</summary>
    public static List<StoredRecord> List(SqliteConnection conn, SqliteTransaction? tx, string table, long? userId, StoredScope scope, string? prefix)
    {
        bool all = string.IsNullOrEmpty(prefix);
        using var cmd = Command(conn, tx, $"SELECT {Columns} FROM {table} WHERE [UserId] IS @uid" + (all ? string.Empty : " AND [Key] LIKE @pattern ESCAPE '\\'") + " ORDER BY [Key] COLLATE NOCASE, [Id]");
        cmd.Parameters.AddWithValue("@uid", Uid(userId));
        if (!all) cmd.Parameters.AddWithValue("@pattern", LikePrefix(prefix!));

        var result = new List<StoredRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(Read(r, scope));
        return result;
    }

    /// <summary>
    ///     Stores the value under the key, replacing the row that has it (any case), or adding one. Nothing is written when the row already holds just this
    ///     (the modified time stays). Returns the item as it is now, and whether the table changed.
    /// </summary>
    public static (StoredRecord Record, bool Changed) Set(SqliteConnection conn, SqliteTransaction? tx, string table, long? userId, StoredScope scope, string key, string typeName, string format, string data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var existing = Get(conn, tx, table, userId, scope, key);
        if (existing is not null && existing.TypeName == typeName && existing.Format == format && existing.Data == data) return (existing, false);

        string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        if (existing is not null)
        {
            using var update = Command(conn, tx, $"UPDATE {table} SET [Key] = @key, [TypeName] = @type, [Format] = @format, [Data] = @data, [Modified] = @now WHERE [Key] = @old COLLATE NOCASE AND [UserId] IS @uid");
            update.Parameters.AddWithValue("@old", key);
            update.Parameters.AddWithValue("@key", key);
            Bind(update, typeName, format, data, now, userId);
            update.ExecuteNonQuery();
        }
        else
        {
            using var insert = Command(conn, tx, $"INSERT INTO {table} ([Key], [TypeName], [Format], [Data], [Created], [Modified], [UserId]) VALUES (@key, @type, @format, @data, @now, @now, @uid)");
            insert.Parameters.AddWithValue("@key", key);
            Bind(insert, typeName, format, data, now, userId);
            insert.ExecuteNonQuery();
        }

        return (Get(conn, tx, table, userId, scope, key)!, true);
    }

    private static void Bind(SqliteCommand cmd, string typeName, string format, string data, string now, long? userId)
    {
        cmd.Parameters.AddWithValue("@type",   typeName);
        cmd.Parameters.AddWithValue("@format", format);
        cmd.Parameters.AddWithValue("@data",   data);
        cmd.Parameters.AddWithValue("@now",    now);
        cmd.Parameters.AddWithValue("@uid",    Uid(userId));
    }

    /// <summary>Removes the item (any case of the key); false if there was none.</summary>
    public static bool Delete(SqliteConnection conn, SqliteTransaction? tx, string table, long? userId, string key)
    {
        using var cmd = Command(conn, tx, $"DELETE FROM {table} WHERE [Key] = @key COLLATE NOCASE AND [UserId] IS @uid");
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@uid", Uid(userId));
        return cmd.ExecuteNonQuery() > 0;
    }
}
