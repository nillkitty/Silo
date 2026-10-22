using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Serilog;
using Silo.Providers.Data;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     Maps a logical (alias, table) pair onto a physical table name for a storage format. The provider builds every SQL
///     statement through <see cref="Table" />, so the same code serves both formats.
/// </summary>
/// <remarks>
///     <b>Archive</b>: one attached database per alias -- <c>[x].[Scalar]</c>, <c>[w_acme].[World]</c>.
///     <b>Simple</b>: everything is in the one file; the global tables keep their plain names and every other alias
///     becomes
///     a table-name prefix -- <c>[main].[Header]</c>, <c>[main].[x_Scalar]</c>, <c>[main].[w_acme_World]</c>.
/// </remarks>
public sealed class SiloLayout
{
    public static readonly SiloLayout Simple  = new(SiloStorageFormat.Simple);
    public static readonly SiloLayout Archive = new(SiloStorageFormat.Archive);

    public SiloStorageFormat Format { get; }

    public static SiloLayout For(SiloStorageFormat format)
    {
        return format == SiloStorageFormat.Simple ? Simple : Archive;
    }

    /// <summary>
    ///     The bracket-quoted, qualified physical name of logical table <paramref name="table" /> in
    ///     <paramref name="alias" />.
    /// </summary>
    /// <param name="simpleSchema">
    ///     Simple layout only: the connection schema the single file is attached as (default
    ///     <c>main</c>).
    /// </param>
    public string Table(string alias, string table, string? simpleSchema = null)
    {
        if (Format == SiloStorageFormat.Archive) return $"[{alias}].[{table}]";

        var schema = simpleSchema ?? EntitySchema.MainAlias;
        return alias == EntitySchema.MainAlias ? $"[{schema}].[{table}]" : $"[{schema}].[{alias}_{table}]";
    }

    /// <summary>
    ///     The key of a World recovered from a simple-format table name (<c>w_&lt;key&gt;_World</c>), or null if it isn't
    ///     one.
    /// </summary>
    public static string? WorldKeyFromTable(string tableName)
    {
        const string prefix = "w_";
        const string suffix = "_World";
        if (tableName.Length <= prefix.Length + suffix.Length) return null;
        if (!tableName.StartsWith(prefix, StringComparison.Ordinal) || !tableName.EndsWith(suffix, StringComparison.Ordinal)) return null;

        return tableName[prefix.Length..^suffix.Length];
    }

    private SiloLayout(SiloStorageFormat format)
    {
        Format = format;
    }
}

/// <summary>Result of cheaply probing a .silo file.</summary>
public sealed record SiloProbeResult(SiloStorageFormat Format, bool IsSolo, bool Exists);

/// <summary>
///     Works out what a .silo file is without extracting or opening it: which storage format and whether it is a Solo Silo
///     (must run in its own process). Used by the startup flow to decide which process opens a document.
/// </summary>
/// <remarks>
///     <b>Simple</b> files are bare SQLite: the SQLite file header holds <c>user_version</c> (offset 60) and
///     <c>application_id</c> (offset 68); a Silo sets <c>application_id</c> to <see cref="ApplicationId" /> and bit 0 of
///     <c>user_version</c> (<see cref="SoloBit" />) when it is Solo -- 100 bytes to read, no SQLite needed.
///     <b>Archive</b> files are Solo when the zip's central directory lists an <c>assemblies/</c> entry or a
///     <c>.solo</c> marker -- only the directory is read, nothing is extracted.
/// </remarks>
public static class SiloProbe
{
    /// <summary>"SILO" -- stamped into SQLite's <c>application_id</c> so tools can recognise the file.</summary>
    public const int ApplicationId = 0x53494C4F;

    /// <summary>Bit 0 of SQLite's <c>user_version</c>: the Silo is Solo.</summary>
    public const int SoloBit = 1;

    public const string SoloMarker      = ".solo";
    public const string AssembliesEntry = "assemblies/";

    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    public static SiloProbeResult Probe(string path)
    {
        try
        {
            if (!File.Exists(path)) return new SiloProbeResult(SiloStorageFormat.Simple, false, false);

            using var fs   = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var       head = new byte[100];
            int       n    = fs.Read(head, 0, head.Length);

            if (n >= 72 && head.AsSpan(0, SqliteMagic.Length).SequenceEqual(SqliteMagic))
            {
                int userVersion = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(60, 4));
                int appId       = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(68, 4));
                return new SiloProbeResult(SiloStorageFormat.Simple, appId == ApplicationId && (userVersion & SoloBit) != 0, true);
            }

            if (n >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K')
            {
                fs.Position = 0;
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, true);
                bool solo = zip.Entries.Any(e => e.FullName.StartsWith(AssembliesEntry, StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(e.FullName, SoloMarker, StringComparison.OrdinalIgnoreCase));
                return new SiloProbeResult(SiloStorageFormat.Archive, solo, true);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not probe '{Path}'; treating it as a normal Silo", path);
        }

        // Unknown or unreadable: let the normal open path report the real problem.
        return new SiloProbeResult(SiloStorageFormat.Simple, false, File.Exists(path));
    }

    /// <summary>
    ///     Stamps (<paramref name="solo" /> true/false) the Solo bit and application id on an open simple-format
    ///     connection.
    /// </summary>
    public static void WriteSoloFlag(SqliteConnection conn, bool solo)
    {
        long userVersion;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            userVersion     = Convert.ToInt64(cmd.ExecuteScalar());
        }

        userVersion = solo ? userVersion | SoloBit : userVersion & ~(long)SoloBit;
        using var set = conn.CreateCommand();
        set.CommandText = $"PRAGMA application_id = {ApplicationId}; PRAGMA user_version = {userVersion};";
        set.ExecuteNonQuery();
    }

    public static bool ReadSoloFlag(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return (Convert.ToInt64(cmd.ExecuteScalar()) & SoloBit) != 0;
    }
}

/// <summary>What was on disk the last time this session read or wrote the file (for external-change detection).</summary>
public sealed record DiskState(string Hash, long Length, long LastWriteTicksUtc)
{
    public DateTime LastWriteUtc => new(LastWriteTicksUtc, DateTimeKind.Utc);

    /// <summary>Reads the current size/time of <paramref name="path" /> (no hashing); null if it doesn't exist.</summary>
    public static (long Length, long Ticks)? Stat(string path)
    {
        var fi = new FileInfo(path);
        return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc.Ticks) : null;
    }

    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>
    ///     Copies <paramref name="source" /> to <paramref name="destination" /> without ever locking the source against
    ///     other readers or writers (shared read/write/delete), hashing the bytes as they go. If the source changes while it
    ///     is being copied the copy is retried so the hash, time and bytes all describe one consistent version.
    /// </summary>
    public static DiskState CopyShared(string source, string destination)
    {
        for (int attempt = 0;; attempt++)
        {
            var before = new FileInfo(source);
            using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                using (var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            sha.AppendData(buffer, 0, read);
                            dst.Write(buffer, 0, read);
                        }

                        dst.Flush(true);
                        var after = new FileInfo(source);
                        if ((after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc) && attempt < 3)
                            continue; // changed while copying: go again

                        return new DiskState(Convert.ToHexString(sha.GetHashAndReset()), after.Length, after.LastWriteTimeUtc.Ticks);
                    }
                }
            }
        }
    }
}