using System.Globalization;
using Microsoft.Data.Sqlite;
using Silo.Providers.Data;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     The SQL behind <see cref="INodeStateStore" />: the Explorer's remembered state in the <c>Node</c> table. Kept
///     apart from the provider so it can be tested on its own.
/// </summary>
public static class NodeStateRows
{
    /// <summary>What the <c>Uri</c> of each remembered node starts with; the rest is the node's Explorer key, escaped.</summary>
    public const string UriPrefix = "silo:node?key=";

    /// <summary>The row's <c>Uri</c> for an Explorer key.</summary>
    public static string UriFor(string key)
    {
        return UriPrefix + Uri.EscapeDataString(key);
    }

    /// <summary>The Explorer key a row's <c>Uri</c> stands for; null for any other row of the table.</summary>
    public static string? KeyFrom(string? uri)
    {
        return uri is not null && uri.StartsWith(UriPrefix, StringComparison.Ordinal)
                   ? Uri.UnescapeDataString(uri[UriPrefix.Length..])
                   : null;
    }

    /// <summary>The user's own rows if there are any, else the shared ones (no user).</summary>
    public static List<NodeStateRecord> Get(SqliteConnection conn, string table, long? userId)
    {
        var own = userId is null ? [] : Read(conn, table, userId);
        return own.Count > 0 ? own : Read(conn, table, null);
    }

    private static List<NodeStateRecord> Read(SqliteConnection conn, string table, long? userId)
    {
        var       result = new List<NodeStateRecord>();
        using var cmd    = conn.CreateCommand();
        cmd.CommandText = $"SELECT [Uri], [Name], [Expanded], [IsOpen], [IsSelected] FROM {table} " +
                          "WHERE substr([Uri], 1, @len) = @prefix AND [UserId] IS @uid ORDER BY [Id]";
        cmd.Parameters.AddWithValue("@len",    UriPrefix.Length);
        cmd.Parameters.AddWithValue("@prefix", UriPrefix);
        cmd.Parameters.AddWithValue("@uid",    userId is { } id ? id : DBNull.Value);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (KeyFrom(r.IsDBNull(0) ? null : r.GetString(0)) is not { } key) continue;

            result.Add(new NodeStateRecord(key, r.IsDBNull(1) ? string.Empty : r.GetString(1), Flag(r, 2), Flag(r, 3), Flag(r, 4)));
        }

        return result;
    }

    // a column added by an upgrade is NULL in rows written before it existed
    private static bool Flag(SqliteDataReader r, int column)
    {
        return !r.IsDBNull(column) && r.GetInt64(column) != 0;
    }

    /// <summary>Replaces the user's remembered nodes (the shared ones when <paramref name="userId" /> is null) with these.</summary>
    public static void Save(SqliteConnection conn, SqliteTransaction? tx, string table, long? userId, IEnumerable<NodeStateRecord> states)
    {
        string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        object uid = userId is { } id ? id : DBNull.Value;

        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = $"DELETE FROM {table} WHERE substr([Uri], 1, @len) = @prefix AND [UserId] IS @uid";
            delete.Parameters.AddWithValue("@len",    UriPrefix.Length);
            delete.Parameters.AddWithValue("@prefix", UriPrefix);
            delete.Parameters.AddWithValue("@uid",    uid);
            delete.ExecuteNonQuery();
        }

        foreach (var s in states)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                $"INSERT INTO {table} ([Uri], [Name], [Expanded], [IsOpen], [IsSelected], [IsVisible], [Created], [Modified], [UserId]) " +
                "VALUES (@uri, @name, @expanded, @open, @selected, 1, @now, @now, @uid)";
            insert.Parameters.AddWithValue("@uri",      UriFor(s.Key));
            insert.Parameters.AddWithValue("@name",     s.Name);
            insert.Parameters.AddWithValue("@expanded", s.Expanded ? 1 : 0);
            insert.Parameters.AddWithValue("@open",     s.Open ? 1 : 0);
            insert.Parameters.AddWithValue("@selected", s.Selected ? 1 : 0);
            insert.Parameters.AddWithValue("@now",      now);
            insert.Parameters.AddWithValue("@uid",      uid);
            insert.ExecuteNonQuery();
        }
    }
}