using System.Globalization;
using Microsoft.Data.Sqlite;
using Silo.Providers.Data;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     The SQL behind <see cref="ISecretStore" />: the <c>Secret</c> table, one row per secret. Kept apart from the
///     provider so it can be tested on its own.
/// </summary>
/// <remarks>
///     A person sees the rows made for them and the Silo's shared ones (no user), shared first, so for rows of one
///     kind the last is the person's own.
/// </remarks>
public static class SecretRows
{
    private const string Columns = "[Id], [Key], [Kind], [Provider], [Data], [Created], [Modified]";

    public static List<SecretRecord> Get(SqliteConnection conn, string table, long? userId, string? kind)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {table} WHERE ([UserId] IS NULL OR [UserId] IS @uid) " +
                          (kind is null ? string.Empty : "AND [Kind] IS @kind ")                         +
                          "ORDER BY CASE WHEN [UserId] IS NULL THEN 0 ELSE 1 END, [Id]";
        cmd.Parameters.AddWithValue("@uid", userId is { } id ? id : DBNull.Value);
        if (kind is not null) cmd.Parameters.AddWithValue("@kind", kind);

        return Read(cmd);
    }

    public static SecretRecord? GetOne(SqliteConnection conn, string table, long? userId, int id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {table} WHERE [Id] = @id AND ([UserId] IS NULL OR [UserId] IS @uid)";
        cmd.Parameters.AddWithValue("@id",  id);
        cmd.Parameters.AddWithValue("@uid", userId is { } uid ? uid : DBNull.Value);

        return Read(cmd).FirstOrDefault();
    }

    /// <summary>
    ///     Adds the row for the person (the Silo's shared row when <paramref name="userId" /> is null). Returns it with
    ///     its new id.
    /// </summary>
    public static SecretRecord Insert(SqliteConnection conn, SqliteTransaction? tx, string table, long? userId, SecretRecord secret)
    {
        var now = DateTime.UtcNow;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO {table} ([Key], [Kind], [Provider], [Data], [Created], [Modified], [UserId]) " +
                              "VALUES (@key, @kind, @provider, @data, @now, @now, @uid)";
            Fill(cmd, secret);
            cmd.Parameters.AddWithValue("@now", Text(now));
            cmd.Parameters.AddWithValue("@uid", userId is { } id ? id : DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        using var last = conn.CreateCommand();
        last.Transaction = tx;
        last.CommandText = "SELECT last_insert_rowid()";
        int newId = Convert.ToInt32(last.ExecuteScalar(), CultureInfo.InvariantCulture);
        return secret with { Id = newId, Created = now, Modified = now };
    }

    /// <summary>Replaces the row's name, kind, provider and data. False if the person has no such row.</summary>
    public static bool Update(SqliteConnection conn, SqliteTransaction? tx, string table, long? userId, SecretRecord secret,
                              out DateTime     modified)
    {
        modified = DateTime.UtcNow;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE {table} SET [Key] = @key, [Kind] = @kind, [Provider] = @provider, [Data] = @data, [Modified] = @now " +
                          "WHERE [Id] = @id AND ([UserId] IS NULL OR [UserId] IS @uid)";
        Fill(cmd, secret);
        cmd.Parameters.AddWithValue("@id",  secret.Id);
        cmd.Parameters.AddWithValue("@now", Text(modified));
        cmd.Parameters.AddWithValue("@uid", userId is { } id ? id : DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }

    public static bool Delete(SqliteConnection conn, SqliteTransaction? tx, string table, long? userId, int id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {table} WHERE [Id] = @id AND ([UserId] IS NULL OR [UserId] IS @uid)";
        cmd.Parameters.AddWithValue("@id",  id);
        cmd.Parameters.AddWithValue("@uid", userId is { } uid ? uid : DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static void Fill(SqliteCommand cmd, SecretRecord secret)
    {
        cmd.Parameters.AddWithValue("@key",      secret.Key      ?? string.Empty);
        cmd.Parameters.AddWithValue("@kind",     secret.Kind     ?? string.Empty);
        cmd.Parameters.AddWithValue("@provider", secret.Provider ?? string.Empty);
        cmd.Parameters.Add("@data", SqliteType.Blob).Value = secret.Data ?? [];
    }

    private static List<SecretRecord> Read(SqliteCommand cmd)
    {
        var       rows = new List<SecretRecord>();
        using var r    = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new SecretRecord(r.GetInt32(0), r.IsDBNull(1) ? string.Empty : r.GetString(1),
                                      r.IsDBNull(2) ? string.Empty : r.GetString(2), r.IsDBNull(3) ? string.Empty : r.GetString(3),
                                      r.IsDBNull(4) ? [] : (byte[])r.GetValue(4), Time(r, 5), Time(r, 6)));

        return rows;
    }

    private static string Text(DateTime utc)
    {
        return utc.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTime Time(SqliteDataReader r, int column)
    {
        if (r.IsDBNull(column)) return DateTime.MinValue;

        return DateTime.TryParse(r.GetString(column), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                   ? t.ToUniversalTime()
                   : DateTime.MinValue;
    }
}