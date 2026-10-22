using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Serilog;
using Silo.DbModel.Global;
using Silo.Providers.Data.Validation;
using Silo.Settings;
using Telefrag.DI;
using DataBinding = Silo.DbModel.Global.DataBinding;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     Creates a brand-new Silo document: a fresh working directory holding empty databases (schema created, plus an
///     initial <see cref="Header" /> row), immediately saved to <paramref name="path" /> so the file exists on disk right
///     away. New Silos use the single-file <see cref="SiloStorageFormat.Simple" /> format unless
///     <see cref="SiloFileSettings.NewSilosAreArchives" /> says otherwise.
/// </summary>
[Singleton(typeof(INewSiloBuilder))]
public class SqliteNewSiloBuilder : INewSiloBuilder
{
    private static SqliteDatabaseProvider _newSilo(string path)
    {
        var format     = SiloFileSettings.Instance.NewSilosAreArchives ? SiloStorageFormat.Archive : SiloStorageFormat.Simple;
        var workingDir = SiloArchive.CreateWorkingDirectory();

        SqliteConnection conn;
        string?          workingFile = null;
        if (format == SiloStorageFormat.Simple)
        {
            workingFile = Path.Combine(workingDir, "working.silo");
            conn        = SqliteConnectionFactory.OpenSimple(workingFile);
        }
        else
        {
            conn = SqliteConnectionFactory.OpenAndAttachFixed(workingDir);
        }

        var layout = SiloLayout.For(format);
        EntitySchema.EnsureFixedSchemas(conn, layout);
        if (format == SiloStorageFormat.Simple) SiloProbe.WriteSoloFlag(conn, false); // stamps the SILO application id

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
                $"INSERT INTO {layout.Table("main", "Header")} ([Type],[Created],[Modified],[Opened],[LastCheck],[Version],[Build],[Creator],[Owner]) " +
                "VALUES (@type,@created,@modified,@opened,@lastCheck,@version,@build,@creator,@owner)";
            cmd.Parameters.AddWithValue("@type",      (long)SiloFileType.GlobalDb);
            cmd.Parameters.AddWithValue("@created",   now.ToString("O"));
            cmd.Parameters.AddWithValue("@modified",  now.ToString("O"));
            cmd.Parameters.AddWithValue("@opened",    now.ToString("O"));
            cmd.Parameters.AddWithValue("@lastCheck", now.ToString("O"));
            cmd.Parameters.AddWithValue("@version",   0);
            cmd.Parameters.AddWithValue("@build",     0);
            cmd.Parameters.AddWithValue("@creator",   who);
            cmd.Parameters.AddWithValue("@owner",     who);
            cmd.ExecuteNonQuery();
        }

        var provider = new SqliteDatabaseProvider(conn, workingDir, path, layout, workingFile);

        // First save (version 1). Run off the UI thread: the save is async and this method is not.
        Task.Run(() => provider.SaveAsync()).GetAwaiter().GetResult();
        Log.Information("Created new {Format} Silo at '{Path}'", format, path);
        return provider;
    }

    public IDatabaseProvider Build(string path)
    {
        path.Required();

        return _newSilo(path);
    }

    public IDatabaseProvider Build(string path, SiloTemplate template)
    {
        var x = _newSilo(path.Required());
        if (template != null)
        {
            foreach (var i in template.Items)
            {
                // Diagrams and data bindings get their ids from their stores (several in one template would all be Id 0).
                bool added;
                try
                {
                    added = i switch
                            {
                                Diagram d         => x.SaveDiagram(d) is not null,
                                DiagramTemplate t => x.SaveDiagram(t.ToDiagram()) is not null,
                                DataBinding b     => x.SaveDataBinding(b) is not null,
                                _                 => x.AddItem(i)
                            };
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Adding '{item}' from template '{template}' failed", i, template.Name);
                    added = false;
                }

                if (!added)
                    Log.Error("Failed to add item '{item}' to new Silo, per template '{template}'.", i, template.Name);
            }

            Task.Run(() => x.SaveAsync()).GetAwaiter().GetResult();
        }

        return x;
    }
}

/// <summary>
///     Loads an existing Silo document, whatever its format (see <see cref="SiloProbe" />). The original file is only
///     read (shared) -- never locked -- and a private copy is what gets opened:
///     <list type="bullet">
///         <item>
///             <b>Simple</b>: the file is copied to a working file and opened as the single connection; no schemas are
///             attached.
///         </item>
///         <item>
///             <b>Archive</b>: the zip is copied (so extraction sees one consistent version) and extracted into a working
///             directory; z/x/u/y are attached and every <c>w-*.db</c> found alongside them.
///         </item>
///     </list>
///     In both cases the copy's hash and time are remembered so a later save can tell whether someone else changed the
///     original in the meantime.
/// </summary>
[Singleton(typeof(ISiloLoader))]
public class SqliteSiloLoader : ISiloLoader
{
    public IDatabaseProvider Load(FileInfo fi)
    {
        fi.Required();

        var probe = SiloProbe.Probe(fi.FullName);
        var dir   = SiloArchive.CreateWorkingDirectory();
        try
        {
            SqliteDatabaseProvider provider;
            if (probe.Format == SiloStorageFormat.Simple)
            {
                var workingFile = Path.Combine(dir, "working.silo");
                var disk        = DiskState.CopyShared(fi.FullName, workingFile);

                var conn = SqliteConnectionFactory.OpenSimple(workingFile);
                EntitySchema.EnsureFixedSchemas(conn, SiloLayout.Simple);
                provider = new SqliteDatabaseProvider(conn, dir, fi.FullName, SiloLayout.Simple, workingFile, disk);
            }
            else
            {
                var       sourceCopy = dir + ".src";
                DiskState disk;
                try
                {
                    disk = DiskState.CopyShared(fi.FullName, sourceCopy);
                    SiloArchive.Extract(sourceCopy, dir);
                }
                finally
                {
                    if (File.Exists(sourceCopy)) File.Delete(sourceCopy);
                }

                var conn = SqliteConnectionFactory.OpenAndAttachFixed(dir);
                EntitySchema.EnsureFixedSchemas(conn, SiloLayout.Archive);
                provider = new SqliteDatabaseProvider(conn, dir, fi.FullName, SiloLayout.Archive, null, disk);
            }

            provider.AttachExistingWorldDatabases();
            Log.Information("Loaded {Format} Silo from '{Path}'", probe.Format, fi.FullName);
            return provider;
        }
        catch
        {
            SiloArchive.DeleteWorkingDirectory(dir);
            throw;
        }
    }
}