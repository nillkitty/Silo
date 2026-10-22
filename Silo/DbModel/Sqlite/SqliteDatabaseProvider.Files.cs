using System.IO;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Serilog;
using Silo.Providers.Data;
using Silo.Settings;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     File-level behaviour of the SQLite provider: the working copy, the memorialized save, conflict detection, the
///     Solo flag and the simple-to-archive conversion. Everything here is identical for both storage formats except how
///     the bytes of a snapshot are produced.
/// </summary>
/// <remarks>
///     <para>
///         <b>Never locks the original.</b> The document is edited in a working copy under the temp directory (a copy of
///         the single file for the simple format, the extracted files for an archive). The original is only ever opened
///         with shared read access (to copy it, to hash it) and replaced atomically on save, so other programs can open,
///         copy, sync or back it up while it is open here.
///     </para>
///     <para>
///         <b>A save is one event.</b> (1) if the file changed on disk since it was loaded/last saved, ask what to do
///         (<see cref="SaveConflictPolicy" />); (2) bump <c>Header.Version</c>/<c>Modified</c> and append a
///         <c>SaveLog</c> row, in the document; (3) write a snapshot beside the target (SQLite online-backup for the
///         simple format, a zip for the archive); (4) stamp the snapshot's last-write time with the very timestamp in the
///         <c>SaveLog</c> row; (5) atomically move it over the target; (6) remember its hash/time/size as "what is on
///         disk"; (7) raise <see cref="Saved" />; (8) push to the remote, if any. A failure before (5) removes the row
///         again, so the log only ever describes saves that happened.
///     </para>
///     <para>
///         The <see cref="Saved" /> event is raised while the provider's lock is held: handlers must be quick and must not
///         call back into the provider.
///     </para>
/// </remarks>
public sealed partial class SqliteDatabaseProvider
{
    private DiskState? _disk;
    private bool       _isSolo;
    private string?    _workingFile;

    /// <inheritdoc />
    public ISaveConflictResolver? ConflictResolver { get; set; }

    /// <inheritdoc />
    public string FilePath { get; private set; }

    /// <inheritdoc />
    public SiloStorageFormat Format => _layout.Format;

    // ---- Solo ----------------------------------------------------------------------------------

    /// <inheritdoc />
    public bool IsSolo
    {
        get => _isSolo;
        set
        {
            _gate.Wait();
            try
            {
                if (_isSolo == value) return;

                if (_layout.Format == SiloStorageFormat.Simple)
                {
                    SiloProbe.WriteSoloFlag(_conn, value);
                }
                else
                {
                    var marker = Path.Combine(_tempDir, SiloProbe.SoloMarker);
                    if (value)
                    {
                        File.WriteAllBytes(marker, []);
                    }
                    else
                    {
                        File.Delete(marker);
                        if (Directory.Exists(Path.Combine(_tempDir, "assemblies")))
                        {
                            Log.Warning("This Silo carries assemblies, so it stays a Solo Silo");
                            return;
                        }
                    }
                }

                _isSolo = value;
                RaiseChanged();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <inheritdoc />
    public bool IsUnsynced { get; private set; }

    /// <inheritdoc />
    public ISiloRemote? Remote { get; set; }

    /// <inheritdoc />
    public long WorkingSizeBytes
    {
        get
        {
            try
            {
                if (_layout.Format == SiloStorageFormat.Simple)
                    return _workingFile is not null ? new FileInfo(_workingFile).Length : 0;

                return Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories)
                                .Where(f => !f.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                                .Sum(f => new FileInfo(f).Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    private bool ReadIsSolo()
    {
        try
        {
            if (_layout.Format == SiloStorageFormat.Simple) return SiloProbe.ReadSoloFlag(_conn);

            return Directory.Exists(Path.Combine(_tempDir, "assemblies")) || File.Exists(Path.Combine(_tempDir, SiloProbe.SoloMarker));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the Solo flag");
            return false;
        }
    }

    /// <summary>
    ///     The ordinary save: conflict check (which may prompt, so it runs <i>before</i> the lock is taken -- a modal prompt
    ///     must never be shown while holding the lock other UI work waits on), the save itself, then the remote push (same
    ///     reasoning).
    /// </summary>
    private async Task SaveCurrentAsync(string? note)
    {
        var target   = FilePath;
        var diverted = false;
        if (_disk is not null && await ResolveDiskConflictAsync(target) is { } copyPath)
        {
            note     = $"Conflicted copy of {Path.GetFileName(target)}";
            target   = copyPath;
            diverted = true;
        }

        await _gate.WaitAsync();
        try
        {
            await SaveCoreAsync(target, diverted, note);
        }
        finally
        {
            _gate.Release();
        }

        if (diverted)
        {
            Remote = null; // the document now lives in the conflicted copy; it no longer syncs with the original's remote
            return;
        }

        if (Remote is { } remote) await PushToRemoteAsync(remote, FilePath);
    }

    /// <summary>
    ///     Steps 2-7 of a save; the caller holds the gate. <paramref name="relocate" />: write to a new path without
    ///     checking the old one.
    /// </summary>
    private async Task SaveCoreAsync(string target, bool relocate, string? note)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var savedUtc     = DateTime.UtcNow;
        var previousHash = _disk?.Hash;
        var sequence     = StampSave(savedUtc, previousHash, target, note);
        var tmp          = target + ".saving";

        string hash;
        long   length;
        try
        {
            await Task.Run(() =>
                           {
                               if (File.Exists(tmp)) File.Delete(tmp);
                               WriteSnapshot(tmp);
                               File.SetLastWriteTimeUtc(tmp, savedUtc); // the file's time IS the SaveLog time
                           });

            hash = await Task.Run(() => DiskState.HashFile(tmp));
            await MoveIntoPlaceAsync(tmp, target);
            length = new FileInfo(target).Length;
        }
        catch
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch
            {
                /* leftover temp; harmless */
            }

            UnstampSave(sequence, savedUtc);
            throw;
        }

        FilePath = target;
        _disk    = new DiskState(hash, length, new FileInfo(target).LastWriteTimeUtc.Ticks);

        var evt = new SiloSaveEvent(savedUtc, sequence, _layout.Format, target, hash, previousHash, length, CurrentUserName(),
                                    Environment.MachineName, Remote?.Uri.ToString());
        Log.Information("Silo saved: {Path} ({Format}, save #{Sequence}, {Size} bytes, sha256 {Hash}, previous {Previous})", target,
                        evt.Format, sequence, length, hash, previousHash);
        Saved?.Invoke(this, evt);
    }

    /// <summary>
    ///     Detects a change to the file since this session last read/wrote it and asks how to proceed. Returns null to
    ///     carry on saving to <paramref name="target" />, or the path of a conflicted copy to save to instead.
    /// </summary>
    private async Task<string?> ResolveDiskConflictAsync(string target)
    {
        var stat = DiskState.Stat(target);
        if (stat is null) return null; // deleted or moved away: simply write it again

        var (length, ticks) = stat.Value;
        if (length == _disk!.Length && ticks == _disk.LastWriteTicksUtc) return null; // untouched

        // Time or size differ -- but a "touch" or a re-save of identical bytes is not a conflict.
        var hash = await Task.Run(() => DiskState.HashFile(target));
        if (hash == _disk.Hash)
        {
            _disk = _disk with { Length = length, LastWriteTicksUtc = ticks };
            return null;
        }

        var conflict = new SaveConflict(SaveConflictKind.FileChangedOnDisk, target, new DateTime(ticks, DateTimeKind.Utc),
                                        _disk.LastWriteUtc);
        switch (await SaveConflictPolicy.DecideAsync(ConflictResolver, conflict))
        {
            case SaveConflictChoice.Overwrite:
                return null;

            case SaveConflictChoice.SaveCopy:
                var name = Path.GetFileNameWithoutExtension(target);
                var copy = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target)) ?? string.Empty,
                                        $"{name} (conflicted copy {DateTime.Now:yyyy-MM-dd HHmmss}){Path.GetExtension(target)}");
                return copy;

            default:
                throw new OperationCanceledException("Not saved: the file was changed by someone else.");
        }
    }

    private async Task PushToRemoteAsync(ISiloRemote remote, string localFile)
    {
        remote.HasUnpushedChanges = true;
        IsUnsynced                = true;
        try
        {
            try
            {
                await remote.PushAsync(localFile, false);
            }
            catch (RemoteConflictException)
            {
                var conflict = new SaveConflict(SaveConflictKind.RemoteChanged, remote.Uri.ToString(), null, _disk?.LastWriteUtc);
                if (await SaveConflictPolicy.DecideAsync(ConflictResolver, conflict) != SaveConflictChoice.Overwrite)
                    throw new
                        RemoteSyncException("Saved on this computer only: the remote copy was changed by someone else and was left alone.",
                                            new RemoteConflictException("remote changed"));

                await remote.PushAsync(localFile, true);
            }

            remote.HasUnpushedChanges = false;
            IsUnsynced                = false;
        }
        catch (RemoteSyncException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Uploading {Path} to {Remote} failed", localFile, remote.Uri);
            throw new RemoteSyncException($"Saved on this computer only; uploading to {remote.Uri} failed: {ex.Message}", ex);
        }
    }

    private static async Task MoveIntoPlaceAsync(string tmp, string target)
    {
        for (var attempt = 0;; attempt++)
            try
            {
                File.Move(tmp, target, true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                // Another program (an indexer, a sync client, antivirus) has the target open for a moment.
                await Task.Delay(150 * (attempt + 1));
            }
    }

    /// <summary>Writes a consistent snapshot of the whole document to exactly <paramref name="path" />. Caller holds the gate.</summary>
    private void WriteSnapshot(string path)
    {
        if (_layout.Format == SiloStorageFormat.Simple)
        {
            using var dest = SqliteConnectionFactory.OpenStandalone(path);
            _conn.BackupDatabase(dest);
        }
        else
        {
            SiloArchive.WriteArchive(_tempDir, path);
        }
    }

    /// <summary>
    ///     Bumps <c>Header.Version</c>/<c>Modified</c> and appends the <c>SaveLog</c> row, atomically. Returns the new
    ///     save counter.
    /// </summary>
    private int StampSave(DateTime savedUtc, string? previousHash, string target, string? note)
    {
        using var tx = _conn.BeginTransaction();

        var sequence = 1;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"UPDATE {T("main", "Header")} SET [Version] = [Version] + 1, [Modified] = @now";
            cmd.Parameters.AddWithValue("@now", savedUtc.ToString("O"));
            if (cmd.ExecuteNonQuery() > 0)
            {
                cmd.Parameters.Clear();
                cmd.CommandText = $"SELECT [Version] FROM {T("main", "Header")} LIMIT 1";
                sequence        = Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                $"INSERT INTO {T("main", "SaveLog")} ([SavedUtc],[Sequence],[Format],[PreviousHash],[User],[Machine],[Source],[Note]) " +
                "VALUES (@t,@seq,@fmt,@prev,@user,@machine,@src,@note)";
            cmd.Parameters.AddWithValue("@t",       savedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@seq",     sequence);
            cmd.Parameters.AddWithValue("@fmt",     _layout.Format.ToString());
            cmd.Parameters.AddWithValue("@prev",    (object?)previousHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@user",    CurrentUserName());
            cmd.Parameters.AddWithValue("@machine", Environment.MachineName);
            cmd.Parameters.AddWithValue("@src",     (object?)Remote?.Uri.ToString() ?? target);
            cmd.Parameters.AddWithValue("@note",    (object?)note                   ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return sequence;
    }

    /// <summary>Undoes <see cref="StampSave" /> after a save failed, so the log only describes saves that happened.</summary>
    private void UnstampSave(int sequence, DateTime savedUtc)
    {
        try
        {
            using var tx = _conn.BeginTransaction();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {T("main", "SaveLog")} WHERE [SavedUtc] = @t AND [Sequence] = @seq";
                cmd.Parameters.AddWithValue("@t",   savedUtc.ToString("O"));
                cmd.Parameters.AddWithValue("@seq", sequence);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"UPDATE {T("main", "Header")} SET [Version] = [Version] - 1";
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not roll back the SaveLog entry of a failed save");
        }
    }

    private static string CurrentUserName()
    {
        try
        {
            return WindowsIdentity.GetCurrent().Name;
        }
        catch
        {
            return $"{Environment.UserDomainName}\\{Environment.UserName}";
        }
    }

    /// <summary>
    ///     Copies every table from the single-file working copy into a new archive-layout working directory and switches
    ///     to it.
    /// </summary>
    /// <remarks>
    ///     SQLite attaches at most 10 databases to a connection, and a full archive already needs 3 +
    ///     <see cref="MaxArchiveWorlds" />
    ///     of them. So the Worlds are copied first, each through its own short-lived connection (one attach: the source), and
    ///     only then are they attached to the connection that becomes the provider's.
    /// </remarks>
    private void ConvertToArchive()
    {
        var keys = _worldAliases.Keys.Select(a => a[2..]).ToList();
        if (keys.Count > MaxArchiveWorlds)
            throw new InvalidOperationException($"This Silo has {keys.Count} Worlds; the archive format holds at most {MaxArchiveWorlds}.");

        var               newDir     = SiloArchive.CreateWorkingDirectory();
        SqliteConnection? newConn    = null;
        var               newAliases = new Dictionary<string, string>();
        try
        {
            // 1. Each World -> its own w-<key>.db.
            foreach (var key in keys)
            {
                var       alias     = "w_" + key;
                var       file      = $"w-{key}.db";
                using var worldConn = SqliteConnectionFactory.OpenStandalone(Path.Combine(newDir, file));
                SqliteConnectionFactory.Attach(worldConn, _workingFile!, "src");
                CopyTables(worldConn, EntitySchema.WorldFamily, _ => EntitySchema.MainAlias, _ => alias);
                newAliases[alias] = file;
            }

            // 2. The fixed databases (main, x, u, y).
            newConn = SqliteConnectionFactory.OpenAndAttachFixed(newDir);
            EntitySchema.EnsureFixedSchemas(newConn, SiloLayout.Archive, false); // the rows come from the simple file, with its syntaxes
            SqliteConnectionFactory.Attach(newConn, _workingFile!, "src");
            CopyTables(newConn, EntitySchema.All.Values.Where(i => i.SchemaKind == SchemaKind.Fixed), i => i.FixedAlias!,
                       i => i.FixedAlias!);
            using (var detach = newConn.CreateCommand())
            {
                detach.CommandText = "DETACH DATABASE [src]";
                detach.ExecuteNonQuery();
            }

            // 3. Attach the Worlds to the new connection.
            foreach (var (alias, file) in newAliases)
            {
                SqliteConnectionFactory.Attach(newConn, Path.Combine(newDir, file), alias);
                foreach (var info in EntitySchema.WorldFamily)
                {
                    using var cmd = newConn.CreateCommand();
                    cmd.CommandText = EntitySchema.BuildCreateTableSql(info, alias, SiloLayout.Archive);
                    cmd.ExecuteNonQuery();
                }
            }

            if (_isSolo) File.WriteAllBytes(Path.Combine(newDir, SiloProbe.SoloMarker), []);
        }
        catch
        {
            newConn?.Dispose();
            SiloArchive.DeleteWorkingDirectory(newDir);
            throw;
        }

        var oldConn = _conn;
        var oldDir  = _tempDir;

        _conn        = newConn;
        _tempDir     = newDir;
        _layout      = SiloLayout.Archive;
        _workingFile = null;
        _worldAliases.Clear();
        foreach (var (alias, file) in newAliases) _worldAliases[alias] = file;

        oldConn.Dispose();
        SiloArchive.DeleteWorkingDirectory(oldDir);
        Log.Information("Converted '{Path}' from the simple to the archive format", FilePath);
    }

    /// <summary>
    ///     Creates (archive layout) and fills the tables of <paramref name="entities" /> in <paramref name="conn" /> from the
    ///     attached simple-layout source (schema <c>src</c>). <paramref name="destAlias" /> is the schema the table is created
    ///     in on <paramref name="conn" />; <paramref name="logicalAlias" /> the logical database it belongs to (which decides
    ///     its prefix in the source).
    /// </summary>
    private static void CopyTables(SqliteConnection         conn, IEnumerable<EntityInfo> entities, Func<EntityInfo, string> destAlias,
                                   Func<EntityInfo, string> logicalAlias)
    {
        using var tx = conn.BeginTransaction();
        foreach (var info in entities)
        {
            var dest = destAlias(info);

            using (var create = conn.CreateCommand())
            {
                create.Transaction = tx;
                create.CommandText = EntitySchema.BuildCreateTableSql(info, dest, SiloLayout.Archive);
                create.ExecuteNonQuery();
            }

            var       cols = string.Join(", ", info.Columns.Select(c => $"[{c.ColumnName}]"));
            using var copy = conn.CreateCommand();
            copy.Transaction = tx;
            copy.CommandText = $"INSERT INTO {SiloLayout.Archive.Table(dest, info.TableName)} ({cols}) " +
                               $"SELECT {cols} FROM {SiloLayout.Simple.Table(logicalAlias(info), info.TableName, "src")}";
            copy.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ---- Shutdown ------------------------------------------------------------------------------

    /// <summary>Shuts down the database connection because the app is closing or unloading the Silo.</summary>
    public async Task<bool> ShutdownAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _conn.Close();
            _conn.Dispose();
            SiloArchive.DeleteWorkingDirectory(_tempDir);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Save ----------------------------------------------------------------------------------

    /// <summary>Saves the Silo to its file (see the class remarks for exactly what a save does).</summary>
    public Task SaveAsync()
    {
        return SaveCurrentAsync(null);
    }

    /// <summary>Migrates the Silo to a copy at the supplied path; the open document continues at the new path.</summary>
    public async Task MigrateToCopyAsync(string path)
    {
        await _gate.WaitAsync();
        try
        {
            await SaveCoreAsync(Path.GetFullPath(path), true, "Save as");
            Remote = null; // a copy saved under a new name is no longer the synced document
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public event EventHandler<SiloSaveEvent>? Saved;

    /// <inheritdoc />
    public async Task RetryUploadAsync()
    {
        if (Remote is { } remote) await PushToRemoteAsync(remote, FilePath);
    }

    // ---- Simple -> Archive ---------------------------------------------------------------------

    /// <inheritdoc />
    public async Task ExplodeToArchiveAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_layout.Format == SiloStorageFormat.Archive) return;

            await Task.Run(ConvertToArchive);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await SaveCurrentAsync("Converted from simple to archive format");
        }
        catch
        {
            RaiseChanged(); // converted in memory but not on disk: the document is dirty
            throw;
        }
    }
}