using System.IO;
using System.Security.Principal;
using Serilog;
using Silo.DbModel.Global;
using Telefrag.DI;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     Creates a brand-new Silo document: a fresh working directory holding empty
///     <c>z.db</c>/<c>x.db</c>/<c>u.db</c>/<c>y.db</c> databases (schema created, plus an initial
///     <see cref="Header" /> row), immediately zipped to <paramref name="path" /> so the file exists
///     on disk right away.
/// </summary>
[Singleton(typeof(INewSiloBuilder))]
public class SqliteNewSiloBuilder : INewSiloBuilder
{
    public IDatabaseProvider Build(string path)
    {
        path.Required();

        var workingDir = SiloArchive.CreateWorkingDirectory();
        var conn       = SqliteConnectionFactory.OpenAndAttachFixed(workingDir);
        EntitySchema.EnsureFixedSchemas(conn);

        string who;
        try
        {
            who = WindowsIdentity.GetCurrent()?.Name ?? $"{Environment.UserDomainName}\\{Environment.UserName}";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unable to determine the current Windows identity while creating a new Silo");
            who = Environment.UserName;
        }

        var now = DateTime.UtcNow;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO [main].[Header] ([Type],[Created],[Modified],[Opened],[LastCheck],[Version],[Build],[Creator],[Owner]) " +
                "VALUES (@type,@created,@modified,@opened,@lastCheck,@version,@build,@creator,@owner)";
            cmd.Parameters.AddWithValue("@type",      (long)SiloFileType.GlobalDb);
            cmd.Parameters.AddWithValue("@created",   now.ToString("O"));
            cmd.Parameters.AddWithValue("@modified",  now.ToString("O"));
            cmd.Parameters.AddWithValue("@opened",    now.ToString("O"));
            cmd.Parameters.AddWithValue("@lastCheck", now.ToString("O"));
            cmd.Parameters.AddWithValue("@version",   1);
            cmd.Parameters.AddWithValue("@build",     0);
            cmd.Parameters.AddWithValue("@creator",   who);
            cmd.Parameters.AddWithValue("@owner",     who);
            cmd.ExecuteNonQuery();
        }

        var provider = new SqliteDatabaseProvider(conn, workingDir, path);
        SiloArchive.RepackAsync(workingDir, path).GetAwaiter().GetResult();
        Log.Information("Created new Silo at '{Path}'", path);
        return provider;
    }
}

/// <summary>
///     Loads an existing Silo document: extracts its zip archive into a working directory, opens
///     the ATTACH-based connection against the extracted <c>z.db</c>/<c>x.db</c>/<c>u.db</c>/
///     <c>y.db</c>, and attaches every <c>w-*.db</c> found alongside them.
/// </summary>
[Singleton(typeof(ISiloLoader))]
public class SqliteSiloLoader : ISiloLoader
{
    public IDatabaseProvider Load(FileInfo fi)
    {
        fi.Required();

        var workingDir = SiloArchive.CreateWorkingDirectory();
        SiloArchive.Extract(fi.FullName, workingDir);

        var conn = SqliteConnectionFactory.OpenAndAttachFixed(workingDir);
        EntitySchema.EnsureFixedSchemas(conn);

        var provider = new SqliteDatabaseProvider(conn, workingDir, fi.FullName);
        provider.AttachExistingWorldDatabases();

        Log.Information("Loaded Silo from '{Path}'", fi.FullName);
        return provider;
    }
}