using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using Serilog;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     Expands a <c>.silo</c> archive (a zip of SQLite database files, per the layout documented in README.md) into a
///     working temp directory, and re-compresses that directory back into a <c>.silo</c> file. Also owns the working
///     directories themselves: creating them, holding a lock on each while a Silo is open, and sweeping up the ones a
///     crashed process left behind.
/// </summary>
/// <remarks>
///     Encrypted Silos -- a nested 'encrypted' inner zip wrapping a stub <c>z.db</c> in an outer
///     unencrypted zip -- are out of scope for this pass; only the flat, unencrypted layout is handled here.
/// </remarks>
internal static class SiloArchive
{
    /// <summary>Held open (no sharing) for as long as a working directory is in use; lets the sweep tell live from orphaned.</summary>
    private const string LockFileName = ".lock";

    private static readonly ConcurrentDictionary<string, FileStream> Locks = new(StringComparer.OrdinalIgnoreCase);

    private static string Root => Path.Combine(Path.GetTempPath(), "SiloWorking");

    /// <summary>Creates a fresh, empty working directory for a Silo to be extracted into or built in.</summary>
    public static string CreateWorkingDirectory()
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Locks[dir] = new FileStream(Path.Combine(dir, LockFileName), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        return dir;
    }

    /// <summary>Releases the lock and deletes a working directory. Returns false (and logs) if it could not be fully removed.</summary>
    public static bool DeleteWorkingDirectory(string dir)
    {
        if (Locks.TryRemove(dir, out var fs)) fs.Dispose();

        try
        {
            Directory.Delete(dir, true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clean up temp Silo directory '{Dir}'; it will be swept on the next start", dir);
            return false;
        }
    }

    /// <summary>
    ///     Deletes working directories nobody holds a lock on any more -- left behind by a crash or kill. Safe to run while
    ///     other Silo processes are alive (theirs are locked).
    /// </summary>
    public static void SweepOrphans()
    {
        try
        {
            if (!Directory.Exists(Root)) return;

            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                if (Locks.ContainsKey(dir)) continue;

                try
                {
                    var lockFile = Path.Combine(dir, LockFileName);
                    if (System.IO.File.Exists(lockFile))
                        using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        {
                            // we got the exclusive lock: nobody is using this directory
                        }

                    Directory.Delete(dir, true);
                    Log.Debug("Swept orphaned Silo working directory '{Dir}'", dir);
                }
                catch (IOException)
                {
                    /* in use by another Silo process */
                }
                catch (UnauthorizedAccessException)
                {
                    /* in use or not ours */
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Sweeping orphaned Silo working directories failed");
        }
    }

    /// <summary>Expands <paramref name="siloPath" /> into <paramref name="workingDirectory" />.</summary>
    public static void Extract(string siloPath, string workingDirectory)
    {
        ZipFile.ExtractToDirectory(siloPath, workingDirectory, true);
    }

    /// <summary>
    ///     Compresses every file currently in <paramref name="workingDirectory" /> into a zip archive written to
    ///     <paramref name="siloPath" />, replacing whatever was there. The write goes through a temp file and an atomic move so
    ///     a failure partway through never leaves a corrupt/truncated .silo file behind.
    /// </summary>
    public static async Task RepackAsync(string workingDirectory, string siloPath)
    {
        await Task.Run(() =>
                       {
                           var dir = Path.GetDirectoryName(Path.GetFullPath(siloPath));
                           if (!string.IsNullOrEmpty(dir))
                               Directory.CreateDirectory(dir);

                           var tmpPath = siloPath + ".tmp";
                           WriteArchive(workingDirectory, tmpPath);

                           if (System.IO.File.Exists(siloPath))
                               System.IO.File.Delete(siloPath);
                           System.IO.File.Move(tmpPath, siloPath);
                       });
    }

    /// <summary>Writes the zip of <paramref name="workingDirectory" /> to exactly <paramref name="archivePath" /> (replacing it).</summary>
    public static void WriteArchive(string workingDirectory, string archivePath)
    {
        if (System.IO.File.Exists(archivePath))
            System.IO.File.Delete(archivePath);

        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(workingDirectory, "*", SearchOption.AllDirectories))
        {
            var entryName = Path.GetRelativePath(workingDirectory, file).Replace('\\', '/');
            if (entryName.Equals(LockFileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (entryName.EndsWith("-journal", StringComparison.OrdinalIgnoreCase)) continue;

            // The databases are still open (SQLite holds them with write access), so the default
            // CreateEntryFromFile -- which opens with FileShare.Read -- would fail with a sharing violation.
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = System.IO.File.GetLastWriteTime(file);
            using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var target = entry.Open();
            source.CopyTo(target);
        }
    }
}
