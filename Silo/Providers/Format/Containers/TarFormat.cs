using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using Silo.Providers.Archive;
using Silo.Providers.Reflection;
using Telefrag.DI;

namespace Silo.Providers.Format.Containers;

/// <summary>A tape archive (<c>.tar</c>), read and written with <see cref="System.Formats.Tar" />. New archives are written in the PAX format.</summary>
[Singleton(typeof(IFileFormat))]
public class TarFormat() : TarFormatBase("Tar Archive", "tar", "application/x-tar", false);

/// <summary>A gzip-compressed tape archive (<c>.tgz</c>, <c>.tar.gz</c>).</summary>
[Singleton(typeof(IFileFormat))]
public class TarGzFormat() : TarFormatBase("Gzipped Tar Archive", "tgz,tar.gz", "application/gzip", true);

public abstract class TarFormatBase(string displayName, string fileExtension, string contentType, bool gzip)
    : RebuiltContainerFormat(displayName, fileExtension, contentType), IContainerFormat
{
    /// <summary>Whether the archive is gzip-compressed.</summary>
    public bool Gzip { get; } = gzip;

    private Stream OpenRead(string container)
    {
        var file = new FileStream(container, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length == 0) return file; // an empty file is an empty archive

        return Gzip ? new GZipStream(file, CompressionMode.Decompress) : file;
    }

    /// <summary>The entries, as the reader goes: files and folders only (links and the like are left out).</summary>
    private IEnumerable<(TarEntry Entry, string Path)> Read(string container, CancellationToken cancel)
    {
        using var stream = OpenRead(container);
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is { } entry)
        {
            cancel.ThrowIfCancellationRequested();
            if (!IsFile(entry) && entry.EntryType != TarEntryType.Directory) continue;

            string path = ContainerPaths.Normalize(entry.Name);
            if (path.Length > 0) yield return (entry, path);
        }
    }

    private static bool IsFile(TarEntry entry)
    {
        return entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile;
    }

    public override IReadOnlyList<ContainerEntry> List(string container, CancellationToken cancel = default)
    {
        return ContainerPaths.Complete(Read(container, cancel).Select(e => new ContainerEntry(e.Path, !IsFile(e.Entry), IsFile(e.Entry) ? e.Entry.Length : 0,
                                                                                              e.Entry.ModificationTime)));
    }

    public override Stream OpenEntry(string container, string entry)
    {
        string wanted = ContainerPaths.Normalize(entry);
        foreach (var (e, path) in Read(container, CancellationToken.None))
            if (IsFile(e) && string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase))
                return e.DataStream is { } data ? ContainerPaths.Buffer(data, e.Length) : new MemoryStream();

        throw new FileNotFoundException($"\"{entry}\" is not in the archive.", entry);
    }

    void IContainerFormat.ExtractTo(string container, string directory, string? entry, CancellationToken cancel)
    {
        string  root   = Path.GetFullPath(directory);
        string? prefix = entry is null ? null : ContainerPaths.Normalize(entry);
        foreach (var (e, path) in Read(container, cancel))
        {
            if (prefix is { Length: > 0 } && !ContainerPaths.IsSameOrInside(path, prefix)) continue;

            string relative = prefix is { Length: > 0 } ? ContainerPaths.Relative(path, prefix, true) : path;
            if (ContainerPaths.Resolve(root, relative) is not { } target) continue;

            if (!IsFile(e))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = File.Create(target);
            e.DataStream?.CopyTo(output);
        }
    }

    public override void CreateFrom(string container, string directory, CancellationToken cancel = default)
    {
        using var file   = new FileStream(container, FileMode.Create, FileAccess.Write, FileShare.None);
        using var stream = Gzip ? new GZipStream(file, CompressionLevel.Optimal) : (Stream)file;
        using var writer = new TarWriter(stream, TarEntryFormat.Pax);

        var root    = new DirectoryInfo(directory);
        bool written = false;
        foreach (var item in root.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                                 .OrderBy(i => i.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancel.ThrowIfCancellationRequested();
            string name = Path.GetRelativePath(root.FullName, item.FullName).Replace('\\', '/');
            writer.WriteEntry(item.FullName, item is DirectoryInfo ? name + "/" : name);
            written = true;
        }

        // the writer only ends an archive it wrote entries to: an empty one is just the end marker, two zeroed records
        if (!written) stream.Write(new byte[1024]);
    }
}
