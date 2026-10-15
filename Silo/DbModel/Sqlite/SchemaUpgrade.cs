using Microsoft.Data.Sqlite;

namespace Silo.DbModel.Sqlite;

/// <summary>Brings a table of an older Silo up to the current model.</summary>
public static class SchemaUpgrade
{
    /// <summary>
    ///     Adds the columns of <paramref name="columns" /> (name, SQL type) that <paramref name="table" /> does not have yet.
    ///     Existing rows get NULL in them. Returns the names of the columns that were added.
    /// </summary>
    /// <param name="table">The bracket-quoted, qualified table name, as <c>SiloLayout.Table</c> builds it.</param>
    public static IReadOnlyList<string> AddMissingColumns(SqliteConnection conn, string table, IEnumerable<(string Name, string SqlType)> columns)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = $"SELECT * FROM {table} LIMIT 0";
            using var reader = probe.ExecuteReader();
            for (int i = 0; i < reader.FieldCount; i++) existing.Add(reader.GetName(i));
        }

        var added = new List<string>();
        foreach (var (name, sqlType) in columns)
        {
            if (existing.Contains(name)) continue;

            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN [{name}] {sqlType}";
            alter.ExecuteNonQuery();
            added.Add(name);
        }

        return added;
    }
}
