using System.IO;
using System.IO.Compression;

namespace Silo.Providers.Format;

public class ZipFormat : IFileFormat, IArchiveFormat
{
    public string DisplayName   { get; } = "Zip Archive";
    public string FileExtension { get; } = "zip";

    public IArchivedDirectory Read(FileStream stream)
    {
        stream.Required();

        // Update mode (read + write) only works when the stream itself supports both; a
        // read-only stream falls back to Read mode, which makes every IArchivedFile.Overwrite()/
        // Append() on the resulting tree return null, per those methods' documented contract.
        var mode    = stream.CanRead && stream.CanWrite ? ZipArchiveMode.Update : ZipArchiveMode.Read;
        var archive = new ZipArchive(stream, mode, leaveOpen: true);

        var root = new ZipArchivedDirectory(null, ArchivedDirectoryFlags.Archive, archive);

        foreach (var entry in archive.Entries)
        {
            // Zip entries are flat; a directory tree is implied by '/'-separated FullName
            // segments (always '/', regardless of OS, per the zip spec). A FullName ending in
            // '/' is a directory-only entry (commonly used to record an otherwise-empty
            // directory) rather than a file.
            var isDirectoryEntry = entry.FullName.EndsWith('/');
            var parts            = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue; // a bare "/" entry has nothing to represent

            var dir = root;
            for (var i = 0; i < parts.Length - 1; i++)
                dir = dir.GetOrAddDirectory(parts[i]);

            if (isDirectoryEntry)
                dir.GetOrAddDirectory(parts[^1]);
            else
                dir.AddFile(parts[^1], entry);
        }

        return root;
    }
}

/// <summary>
///     A directory inside a zip archive -- either a real (explicit, trailing-'/') entry, or one
///     implied by the path segments of the files/directories under it.
/// </summary>
/// <remarks>
///     The <see cref="ZipArchive" /> backing this tree has to stay open for as long as any
///     <see cref="IArchivedFile.Read" />/<see cref="IArchivedFile.Overwrite" />/
///     <see cref="IArchivedFile.Append" /> call against it might still happen, which is what
///     <see cref="IArchivedDirectory" />'s disposal hook is for. The root instance returned by
///     <see cref="ZipFormat.Read" /> owns the archive, so disposing it closes that archive (and,
///     with it, the <see cref="FileStream" /> passed to <see cref="ZipFormat.Read" /> stays open,
///     since that stream was opened by the caller, not by this class -- see the
///     <c>leaveOpen: true</c> above). <see cref="Dispose" /> also cascades to every directory
///     reachable from this one -- relevant for the <see cref="ArchivedDirectoryFlags.Archive" />
///     case where a directory further down the tree is itself the root of a *nested* archive of
///     the same format (see that flag's own doc comment) and so owns a second, inner
///     <see cref="ZipArchive" /> of its own that also needs closing. Disposing a plain
///     (non-archive-root) directory is a no-op beyond that cascade, since it owns nothing itself.
///     Safe to call more than once on any instance.
/// </remarks>
public sealed class ZipArchivedDirectory : IArchivedDirectory, IDisposable
{
    private readonly Dictionary<string, ZipArchivedDirectory> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ZipArchivedFile>      _files       = new(StringComparer.OrdinalIgnoreCase);
    private readonly ZipArchive?                              _ownedArchive;

    public ArchivedDirectoryFlags Flags { get; }
    public string?                Name  { get; }

    internal ZipArchivedDirectory(string? name, ArchivedDirectoryFlags flags, ZipArchive? ownedArchive = null)
    {
        Name          = name;
        Flags         = flags;
        _ownedArchive = ownedArchive;
    }

    public IEnumerable<IArchivedDirectory> GetDirectories() => _directories.Values;

    public IEnumerable<IArchivedFile> GetFiles() => _files.Values;

    internal ZipArchivedDirectory GetOrAddDirectory(string name)
    {
        if (!_directories.TryGetValue(name, out var dir))
        {
            dir                = new ZipArchivedDirectory(name, ArchivedDirectoryFlags.None);
            _directories[name] = dir;
        }

        return dir;
    }

    internal void AddFile(string name, ZipArchiveEntry entry) => _files[name] = new ZipArchivedFile(entry);

    /// <summary>
    ///     Closes the underlying <see cref="ZipArchive" />, if this is a directory that owns one
    ///     (the root, or the root of a nested archive further down the tree), and cascades to
    ///     every child directory so a nested archive anywhere under this one is closed too. Safe
    ///     to call on a directory that owns nothing itself (no-op beyond the cascade), and safe
    ///     to call more than once.
    /// </summary>
    public void Dispose()
    {
        _ownedArchive?.Dispose();

        foreach (var dir in _directories.Values)
            dir.Dispose();
    }
}

/// <summary>
///     A file entry inside a zip archive.
/// </summary>
public sealed class ZipArchivedFile : IArchivedFile
{
    private readonly ZipArchiveEntry _entry;

    internal ZipArchivedFile(ZipArchiveEntry entry)
    {
        _entry = entry.Required();
    }

    public long BytesExpanded => _entry.Length;

    public long BytesOnDisk => _entry.CompressedLength;

    public ArchivedFileFlags Flags
    {
        get
        {
            var flags = ArchivedFileFlags.None;
            if (_entry.Length == 0) flags |= ArchivedFileFlags.Zero;
            if (_entry.CompressedLength < _entry.Length) flags |= ArchivedFileFlags.Compressed;
            return flags;
        }
    }

    public Stream? Read()
    {
        try { return _entry.Open(); }
        catch (Exception) { return null; }
    }

    public Stream? Overwrite()
    {
        try
        {
            var stream = _entry.Open();
            // The entry's stream (in ZipArchiveMode.Update) opens over its *existing* content;
            // truncate it first so a shorter overwrite doesn't leave trailing bytes behind. This
            // throws (caught below, returning null) if the archive was opened read-only.
            stream.SetLength(0);
            return stream;
        }
        catch (Exception) { return null; }
    }

    public Stream? Append()
    {
        try
        {
            var stream = _entry.Open();
            stream.Seek(0, SeekOrigin.End);
            return stream;
        }
        catch (Exception) { return null; }
    }
}
