using System.IO;
using Microsoft.Data.Sqlite;

namespace Silo.DbModel.Sqlite;

/// <summary>
/// Opens the single ATTACH-based connection shared by a <see cref="SqliteDatabaseProvider"/>:
/// the Global database (z.db) as the connection's 'main' schema, with the Temp (x.db -&gt; 'x'),
/// Universal (u.db -&gt; 'u') and Metaverse (y.db -&gt; 'y') databases attached as named schemas.
/// World databases (w-*.db) are attached separately and dynamically, one per connector -- see
/// <see cref="SqliteDatabaseProvider"/>.
/// </summary>
internal static class SqliteConnectionFactory
{
    public static SqliteConnection OpenAndAttachFixed(string workingDirectory)
    {
        var zPath = Path.Combine(workingDirectory, "z.db");
        var csb = new SqliteConnectionStringBuilder { DataSource = zPath, Mode = SqliteOpenMode.ReadWriteCreate };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();

        // ReadWriteCreate is used for x/u/y regardless of whether this is a brand-new Silo or
        // one being loaded, so that a Silo whose zip is missing one of these (otherwise always
        // present) files is tolerated rather than failing to open outright.
        AttachFixed(conn, workingDirectory, "x.db", EntitySchema.TempAlias);
        AttachFixed(conn, workingDirectory, "u.db", EntitySchema.UniversalAlias);
        AttachFixed(conn, workingDirectory, "y.db", EntitySchema.MetaverseAlias);

        return conn;
    }

    private static void AttachFixed(SqliteConnection conn, string workingDirectory, string fileName, string alias)
    {
        var path = Path.Combine(workingDirectory, fileName);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ATTACH DATABASE '{path.Replace("'", "''")}' AS [{alias}];";
        cmd.ExecuteNonQuery();
    }
}
