using System.IO;
using Microsoft.Data.Sqlite;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     Opens the connection behind a <see cref="SqliteDatabaseProvider" />.
///     <list type="bullet">
///         <item>
///             <b>Archive layout</b> (<see cref="OpenAndAttachFixed" />): the Global database (z.db) is the connection's
///             <c>main</c> schema, with Temp (x.db -&gt; <c>x</c>), Universal (u.db -&gt; <c>u</c>) and Metaverse
///             (y.db -&gt; <c>y</c>) attached. World databases (w-*.db) are attached on demand by the provider.
///         </item>
///         <item><b>Simple layout</b> (<see cref="OpenSimple" />): one bare SQLite file, nothing attached.</item>
///     </list>
///     Connection pooling is off on purpose: a pooled connection keeps the file handle open after Dispose, which would
///     stop the working directory being deleted on close and make the file impossible to swap on save.
/// </summary>
internal static class SqliteConnectionFactory
{
    public static SqliteConnection OpenAndAttachFixed(string workingDirectory)
    {
        var conn = Open(Path.Combine(workingDirectory, "z.db"));

        // ReadWriteCreate is used for x/u/y regardless of whether this is a brand-new Silo or
        // one being loaded, so that a Silo whose zip is missing one of these (otherwise always
        // present) files is tolerated rather than failing to open outright.
        AttachFixed(conn, workingDirectory, "x.db", EntitySchema.TempAlias);
        AttachFixed(conn, workingDirectory, "u.db", EntitySchema.UniversalAlias);
        AttachFixed(conn, workingDirectory, "y.db", EntitySchema.MetaverseAlias);

        return conn;
    }

    /// <summary>Opens a single-file (simple layout) Silo database; created if missing.</summary>
    public static SqliteConnection OpenSimple(string databaseFile)
    {
        return Open(databaseFile);
    }

    /// <summary>Opens a standalone connection for a snapshot/backup destination.</summary>
    public static SqliteConnection OpenStandalone(string databaseFile)
    {
        return Open(databaseFile);
    }

    private static SqliteConnection Open(string file)
    {
        var csb = new SqliteConnectionStringBuilder
                  {
                      DataSource = file,
                      Mode       = SqliteOpenMode.ReadWriteCreate,
                      Pooling    = false
                  };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        return conn;
    }

    public static void Attach(SqliteConnection conn, string file, string alias)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ATTACH DATABASE '{file.Replace("'", "''")}' AS [{alias}];";
        cmd.ExecuteNonQuery();
    }

    private static void AttachFixed(SqliteConnection conn, string workingDirectory, string fileName, string alias)
    {
        Attach(conn, Path.Combine(workingDirectory, fileName), alias);
    }
}
