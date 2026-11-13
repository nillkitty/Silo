using System.IO;
using Silo.Providers.Reflection;

namespace Silo.Providers.Archive;

/// <summary>
///     One file or folder inside a container file (a zip, a tar, an ISO image, a cabinet).
/// </summary>
/// <param name="Path">Where it is in the container: names separated by '/', with no leading or trailing '/'.</param>
/// <param name="IsDirectory">True for a folder.</param>
/// <param name="Size">The size of a file once taken out of the container, in bytes; 0 for a folder.</param>
/// <param name="Modified">When it was last changed, when the container says.</param>
public sealed record ContainerEntry(string Path, bool IsDirectory, long Size, DateTimeOffset? Modified = null)
{
    /// <summary>The last part of <see cref="Path" />.</summary>
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>The folder it is in ("" for the top of the container).</summary>
    public string Parent => Path.LastIndexOf('/') is var at and >= 0 ? Path[..at] : string.Empty;
}

/// <summary>A change to make to a container file (see <see cref="IContainerFormat.Update" />).</summary>
public abstract record ContainerChange
{
    /// <summary>Puts a copy of the file at <paramref name="SourceFile" /> in the container at <paramref name="Path" />, replacing what is there.</summary>
    public sealed record AddFile(string Path, string SourceFile) : ContainerChange;

    /// <summary>Makes an (empty) folder at <paramref name="Path" />, if there is none.</summary>
    public sealed record AddDirectory(string Path) : ContainerChange;

    /// <summary>Takes the file at <paramref name="Path" /> out of the container; a folder goes with everything in it.</summary>
    public sealed record Remove(string Path) : ContainerChange;
}

/// <summary>
///     A file format that holds files and folders rather than one document: an archive or a disc image. It lists what is in a
///     container file, reads one file out of it, and -- when <see cref="CanWrite" /> -- makes new containers and changes
///     existing ones. Paths inside the container always use '/'.
/// </summary>
/// <remarks>
///     Every call opens the container file afresh and closes it again before it returns (a stream from <see cref="OpenEntry" />
///     is a copy), so nothing holds the file open between calls and the methods can be used from any thread.
/// </remarks>
public interface IContainerFormat : IFileFormat
{
    /// <summary>Whether <see cref="Create" />, <see cref="CreateFrom" /> and <see cref="Update" /> work for this format.</summary>
    bool CanWrite { get; }

    /// <summary>
    ///     Everything in the container: its files and its folders, including folders that are only implied by the paths of the
    ///     files in them.
    /// </summary>
    IReadOnlyList<ContainerEntry> List(string container, CancellationToken cancel = default);

    /// <summary>The contents of one file of the container, as a stream of its own (the caller disposes it).</summary>
    /// <exception cref="FileNotFoundException">There is no such file in the container.</exception>
    Stream OpenEntry(string container, string entry);

    /// <summary>
    ///     Takes files out of the container into <paramref name="directory" />, keeping their folders: everything, or only
    ///     <paramref name="entry" /> (a file, or a folder and all in it) when given. An entry whose path would land outside
    ///     <paramref name="directory" /> is skipped.
    /// </summary>
    void ExtractTo(string container, string directory, string? entry = null, CancellationToken cancel = default)
    {
        string root   = Path.GetFullPath(directory);
        string? prefix = entry is null ? null : ContainerPaths.Normalize(entry);
        foreach (var e in List(container, cancel))
        {
            cancel.ThrowIfCancellationRequested();
            if (prefix is { Length: > 0 } && !ContainerPaths.IsSameOrInside(e.Path, prefix)) continue;

            string relative = prefix is { Length: > 0 } ? ContainerPaths.Relative(e.Path, prefix, true) : e.Path;
            if (ContainerPaths.Resolve(root, relative) is not { } target) continue;

            if (e.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = OpenEntry(container, e.Path);
            using var output = File.Create(target);
            source.CopyTo(output);
        }
    }

    /// <summary>Makes an empty container at <paramref name="container" /> (replacing any file there).</summary>
    void Create(string container);

    /// <summary>
    ///     Makes a container at <paramref name="container" /> (replacing any file there) that holds what is in
    ///     <paramref name="directory" />, with its folders.
    /// </summary>
    void CreateFrom(string container, string directory, CancellationToken cancel = default)
    {
        Create(container);
        Update(container, ContainerPaths.ChangesToAdd(directory), cancel);
    }

    /// <summary>Makes the changes, in order, to the container. Either they all happen or the container is left as it was.</summary>
    void Update(string container, IReadOnlyList<ContainerChange> changes, CancellationToken cancel = default);
}

/// <summary>Working with the '/'-separated paths inside a container.</summary>
public static class ContainerPaths
{
    /// <summary>The path with '/' separators and no leading, trailing or doubled '/', and no "." parts.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".");
        return string.Join('/', parts);
    }

    /// <summary>Joins a folder and a name ("" is the top of the container).</summary>
    public static string Combine(string folder, string name)
    {
        folder = Normalize(folder);
        name   = Normalize(name);
        return folder.Length == 0 ? name : name.Length == 0 ? folder : folder + "/" + name;
    }

    /// <summary>Whether <paramref name="path" /> is <paramref name="folder" /> or somewhere in it ("" holds everything).</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        if (folder.Length == 0) return true;

        return string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     <paramref name="path" /> relative to <paramref name="folder" />; with <paramref name="keepFolderName" />, relative to
    ///     the folder's parent (so a folder taken out keeps its own name).
    /// </summary>
    public static string Relative(string path, string folder, bool keepFolderName = false)
    {
        string from = keepFolderName ? folder.LastIndexOf('/') is var at and >= 0 ? folder[..at] : string.Empty : folder;
        return from.Length == 0 ? path : path.Length > from.Length ? path[(from.Length + 1)..] : string.Empty;
    }

    /// <summary>
    ///     The file system path for a container path under <paramref name="root" /> (a full path); null when it would land
    ///     outside <paramref name="root" /> ("../" parts, a drive letter) or names nothing.
    /// </summary>
    public static string? Resolve(string root, string path)
    {
        string normal = Normalize(path);
        if (normal.Length == 0) return null;

        try
        {
            string full = Path.GetFullPath(Path.Combine(root, normal.Replace('/', Path.DirectorySeparatorChar)));
            string top  = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            return full.StartsWith(top, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The entries with the folders that are only implied by the paths below them added, in order (folders before what is
    ///     in them), each path once.
    /// </summary>
    public static IReadOnlyList<ContainerEntry> Complete(IEnumerable<ContainerEntry> entries)
    {
        var byPath = new Dictionary<string, ContainerEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            string path = Normalize(e.Path);
            if (path.Length == 0) continue;

            byPath[path] = e with { Path = path };
            for (string parent = Parent(path); parent.Length > 0; parent = Parent(parent))
                if (!byPath.ContainsKey(parent))
                    byPath[parent] = new ContainerEntry(parent, true, 0);
        }

        return byPath.Values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The folder a path is in ("" for the top).</summary>
    public static string Parent(string path)
    {
        int at = path.LastIndexOf('/');
        return at < 0 ? string.Empty : path[..at];
    }

    /// <summary>The changes that add what is in <paramref name="directory" /> (its folders and files, not the folder itself) to a container.</summary>
    public static IReadOnlyList<ContainerChange> ChangesToAdd(string directory, string under = "")
    {
        var changes = new List<ContainerChange>();
        var root    = new DirectoryInfo(directory);
        foreach (var item in root.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            string relative = Combine(under, Path.GetRelativePath(root.FullName, item.FullName));
            changes.Add(item is DirectoryInfo ? new ContainerChange.AddDirectory(relative) : new ContainerChange.AddFile(relative, item.FullName));
        }

        return changes;
    }

    /// <summary>
    ///     Copies <paramref name="source" /> to a stream of its own that needs nothing else kept open: in memory when it is
    ///     small, else a temporary file that goes away when the stream is closed.
    /// </summary>
    public static Stream Buffer(Stream source, long expectedLength = -1)
    {
        var copy = NewBuffer(expectedLength);
        source.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }

    /// <summary>
    ///     An empty stream to copy something of about <paramref name="expectedLength" /> bytes into (-1: not known): in memory
    ///     when it is small, else a temporary file that goes away when the stream is closed.
    /// </summary>
    public static Stream NewBuffer(long expectedLength)
    {
        const long inMemory = 32L * 1024 * 1024;
        return expectedLength is >= 0 and <= inMemory
                   ? new MemoryStream((int)expectedLength)
                   : new FileStream(Path.Combine(Path.GetTempPath(), "silo-" + Guid.NewGuid().ToString("N") + ".tmp"), FileMode.CreateNew,
                                    FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
    }
}
