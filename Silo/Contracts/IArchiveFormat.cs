using System.IO;

namespace Silo.Contracts;

public interface IArchiveFormat
{
    IArchivedDirectory Read(FileStream stream);
}

/// <summary>
///     A directory inside an opened archive.
/// </summary>
/// <remarks>
///     The root <see cref="IArchivedDirectory" /> returned by <see cref="IArchiveFormat.Read" />
///     is the owner of whatever resource (the archive handle, and in turn the
///     <see cref="FileStream" /> passed to <c>Read</c> when the implementation doesn't
///     <c>leaveOpen</c>) backs the whole tree, so this interface is disposable: callers that are
///     done with an archive should dispose the root once they're finished reading/writing
///     entries from it, same as any other owned resource. Disposing a non-root
///     <see cref="IArchivedDirectory" /> (one reached via <see cref="GetDirectories" />) is a
///     no-op -- only the root owns anything -- so it's always safe to call, and safe to call more
///     than once on any instance.
/// </remarks>
public interface IArchivedDirectory : IDisposable
{
    /// <summary>
    ///     Flags related to the directory in the index
    /// </summary>
    ArchivedDirectoryFlags Flags { get; }

    /// <summary>
    ///     Gets the relative name for this directory (or NULL if
    ///     this entry does not contribute to the file path)
    /// </summary>
    string? Name { get; }

    /// <summary>
    ///     Gets the child directory entries (if any) inside this directory
    /// </summary>
    IEnumerable<IArchivedDirectory> GetDirectories();

    /// <summary>
    ///     Gets the child file entries (if any) inside this directory
    /// </summary>
    /// <returns></returns>
    IEnumerable<IArchivedFile> GetFiles();
}

/// <summary>
///     A file found in an archive
/// </summary>
public interface IArchivedFile
{
    /// <summary>
    ///     The number of bytes the file expands to after decryption/
    ///     decompression
    /// </summary>
    long BytesExpanded { get; }

    /// <summary>
    ///     The number of bytes the file uses in the archive file
    /// </summary>
    long BytesOnDisk { get; }

    /// <summary>
    ///     Flags related to this entry in the archive index
    /// </summary>
    ArchivedFileFlags Flags { get; }

    /// <summary>
    ///     Attempts to read the contents of the file from the archive.
    /// </summary>
    /// <returns>
    ///     Returns an IO.Stream if successful, or null if the
    ///     operation is not supported.
    /// </returns>
    Stream? Read();

    /// <summary>
    ///     Attempts to write to the contents of the file in-place (overwriting
    ///     the existing entry)
    /// </summary>
    /// <returns>
    ///     Returns an IO.Stream if successful, or null if the
    ///     operation is not supported.
    /// </returns>
    Stream? Overwrite();

    /// <summary>
    ///     Attempts to append to the contents of the file in-place.
    /// </summary>
    /// <returns>
    ///     Returns an IO.Stream if successful, or null if the
    ///     operation is not supported.
    /// </returns>
    Stream? Append();
}

/// <summary>
///     Flags for archived files
/// </summary>
[Flags]
public enum ArchivedDirectoryFlags
{
    None = 0,

    /// <summary>
    ///     The directory contents are password-protected / encrypted
    /// </summary>
    Protected = 1,

    /// <summary>
    ///     The directory is the root of an archive,  either the top level
    ///     file being opened, or a nested archive of the same format.
    /// </summary>
    Archive = 2
}

/// <summary>
///     Flags for archived files
/// </summary>
[Flags]
public enum ArchivedFileFlags
{
    None = 0,

    /// <summary>
    ///     The item is password-protected / encrypted
    /// </summary>
    Protected = 1,

    /// <summary>
    ///     The item is compressed
    /// </summary>
    Compressed = 2,

    /// <summary>
    ///     The file contains zeroes or is empty (see <see cref="IArchivedFile.BytesExpanded") />
    /// </summary>
    Zero = 4,

    /// <summary>
    ///     The file is a nested archive of a differet type (or cannot
    ///     be expanded due to encryption).
    /// </summary>
    Archive = 8
}
