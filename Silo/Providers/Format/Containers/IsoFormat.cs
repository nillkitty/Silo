using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Silo.Providers.Archive;
using Silo.Providers.Reflection;
using Telefrag.DI;
using ComStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace Silo.Providers.Format.Containers;

/// <summary>
///     An ISO 9660 disc image (<c>.iso</c>). It is read here directly -- the Joliet names when the image has them (long,
///     Unicode), else the plain ISO 9660 ones -- and written with the image builder that comes with Windows (IMAPI2), as
///     ISO 9660 with Joliet.
/// </summary>
[Singleton(typeof(IFileFormat))]
public class IsoFormat() : RebuiltContainerFormat("ISO Disc Image", "iso", "application/x-iso9660-image"), IContainerFormat
{
    public override bool CanWrite => OperatingSystem.IsWindows();

    public override IReadOnlyList<ContainerEntry> List(string container, CancellationToken cancel = default)
    {
        using var file = File.OpenRead(container);
        return ContainerPaths.Complete(IsoReader.Walk(file, cancel).Select(r => new ContainerEntry(r.Path, r.IsDirectory, r.IsDirectory ? 0 : r.Length, r.Modified)));
    }

    public override Stream OpenEntry(string container, string entry)
    {
        string    wanted = ContainerPaths.Normalize(entry);
        using var file   = File.OpenRead(container);
        foreach (var record in IsoReader.Walk(file, CancellationToken.None))
            if (!record.IsDirectory && string.Equals(record.Path, wanted, StringComparison.OrdinalIgnoreCase))
                return ContainerPaths.Buffer(IsoReader.Open(file, record), record.Length);

        throw new FileNotFoundException($"\"{entry}\" is not in the image.", entry);
    }

    void IContainerFormat.ExtractTo(string container, string directory, string? entry, CancellationToken cancel)
    {
        string    root   = Path.GetFullPath(directory);
        string?   prefix = entry is null ? null : ContainerPaths.Normalize(entry);
        using var file   = File.OpenRead(container);
        foreach (var record in IsoReader.Walk(file, cancel).ToList())
        {
            cancel.ThrowIfCancellationRequested();
            if (prefix is { Length: > 0 } && !ContainerPaths.IsSameOrInside(record.Path, prefix)) continue;

            string relative = prefix is { Length: > 0 } ? ContainerPaths.Relative(record.Path, prefix, true) : record.Path;
            if (ContainerPaths.Resolve(root, relative) is not { } target) continue;

            if (record.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = File.Create(target);
            IsoReader.Open(file, record).CopyTo(output);
        }
    }

    /// <summary>Builds the image with IMAPI2: ISO 9660 and Joliet, named after the file.</summary>
    public override void CreateFrom(string container, string directory, CancellationToken cancel = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ISO images are built with IMAPI2, which only Windows has.");

        var type = Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage") ??
                   throw new NotSupportedException("The Windows image builder (IMAPI2) is not available on this computer.");
        dynamic image = Activator.CreateInstance(type)!;
        try
        {
            image.ChooseImageDefaultsForMediaType(12); // IMAPI_MEDIA_TYPE_DISK: no disc-sized limit
            image.FileSystemsToCreate = 3;             // ISO 9660 + Joliet
            image.VolumeName          = VolumeName(container);
            if (Directory.EnumerateFileSystemEntries(directory).Any()) image.Root.AddTree(directory, false);
            cancel.ThrowIfCancellationRequested();

            dynamic   result = image.CreateResultImage();
            var       source = (ComStream)result.ImageStream;
            using var output = new FileStream(container, FileMode.Create, FileAccess.Write, FileShare.None);
            Copy(source, output, cancel);
            Marshal.ReleaseComObject(source);
            Marshal.ReleaseComObject(result);
        }
        catch (COMException ex)
        {
            throw new IOException("The ISO image could not be built: " + ex.Message, ex);
        }
        finally
        {
            Marshal.ReleaseComObject(image);
        }
    }

    private static void Copy(ComStream source, Stream output, CancellationToken cancel)
    {
        var    buffer = new byte[1 << 20];
        IntPtr read   = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                source.Read(buffer, buffer.Length, read);
                int count = Marshal.ReadInt32(read);
                if (count <= 0) break;

                output.Write(buffer, 0, count);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(read);
        }
    }

    /// <summary>The volume label: the file's name, in the letters a label may have, at most 16 of them.</summary>
    private static string VolumeName(string container)
    {
        var name = new StringBuilder();
        foreach (char c in Path.GetFileNameWithoutExtension(container))
            if (char.IsLetterOrDigit(c) || c is '_' or '-' or ' ')
                name.Append(c);

        string label = name.ToString().Trim();
        return label.Length == 0 ? "SILO" : label.Length > 16 ? label[..16] : label;
    }
}

/// <summary>Reads the directory tree of an ISO 9660 image (with Joliet names when there are any) and the files in it.</summary>
public static class IsoReader
{
    private const int SectorSize = 2048;

    /// <summary>One file or folder of the image.</summary>
    public sealed record Record(string Path, bool IsDirectory, long Extent, long Length, DateTimeOffset? Modified);

    /// <summary>Every file and folder, folders before what is in them.</summary>
    public static IEnumerable<Record> Walk(Stream image, CancellationToken cancel)
    {
        var (root, joliet) = FindRoot(image);
        var pending = new Stack<Record>();
        pending.Push(root);
        var seen = new HashSet<long> { root.Extent };

        while (pending.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var dir      = pending.Pop();
            var children = ReadDirectory(image, dir, joliet).ToList();
            foreach (var child in children)
            {
                yield return child;
                if (child.IsDirectory && seen.Add(child.Extent)) pending.Push(child); // a loop in a damaged image is not followed
            }
        }
    }

    /// <summary>The contents of a file of the image, read from <paramref name="image" /> (which must stay open while it is read).</summary>
    public static Stream Open(Stream image, Record record)
    {
        image.Position = record.Extent * SectorSize;
        var data = new byte[Math.Min(record.Length, 1L << 20)];
        var copy = ContainerPaths.NewBuffer(record.Length);
        long left = record.Length;
        while (left > 0)
        {
            int count = image.Read(data, 0, (int)Math.Min(data.Length, left));
            if (count <= 0) throw new EndOfStreamException("The image ends inside a file: it is incomplete.");

            copy.Write(data, 0, count);
            left -= count;
        }

        copy.Position = 0;
        return copy;
    }

    /// <summary>The root folder, from the Joliet volume descriptor when there is one, else from the primary one.</summary>
    private static (Record Root, bool Joliet) FindRoot(Stream image)
    {
        var    sector  = new byte[SectorSize];
        byte[]? primary = null;
        for (int index = 16; index < 64; index++)
        {
            image.Position = (long)index * SectorSize;
            if (image.Read(sector, 0, SectorSize) < SectorSize) break;
            if (sector[1] != 'C' || sector[2] != 'D' || sector[3] != '0' || sector[4] != '0' || sector[5] != '1')
                throw new InvalidDataException("This is not an ISO 9660 image (no volume descriptor at sector 16).");

            byte type = sector[0];
            if (type == 255) break;

            // a supplementary descriptor with a UCS-2 escape sequence (%/@, %/C or %/E) is Joliet
            if (type == 2 && sector[88] == 0x25 && sector[89] == 0x2F && sector[90] is 0x40 or 0x43 or 0x45)
                return (RootOf(sector), true);

            if (type == 1) primary ??= (byte[])sector.Clone();
        }

        return primary is not null ? (RootOf(primary), false) : throw new InvalidDataException("The image has no primary volume descriptor.");
    }

    private static Record RootOf(byte[] descriptor)
    {
        return new Record(string.Empty, true, BitConverter.ToUInt32(descriptor, 156 + 2), BitConverter.ToUInt32(descriptor, 156 + 10), null);
    }

    private static IEnumerable<Record> ReadDirectory(Stream image, Record dir, bool joliet)
    {
        var data = new byte[dir.Length];
        image.Position = dir.Extent * SectorSize;
        int got = 0;
        while (got < data.Length)
        {
            int count = image.Read(data, got, data.Length - got);
            if (count <= 0) break;
            got += count;
        }

        int at = 0;
        while (at < got)
        {
            int length = data[at];
            if (length == 0)
            {
                at = (at / SectorSize + 1) * SectorSize; // records do not cross sectors: the rest of this one is padding
                continue;
            }

            if (at + length > got || length < 34) yield break;

            int nameLength = data[at + 32];
            bool isDirectory = (data[at + 25] & 0x02) != 0;
            if (!(nameLength == 1 && data[at + 33] is 0 or 1)) // not "." or ".."
            {
                string name = Name(data.AsSpan(at + 33, nameLength), joliet, isDirectory);
                if (name.Length > 0)
                    yield return new Record(ContainerPaths.Combine(dir.Path, name), isDirectory, BitConverter.ToUInt32(data, at + 2),
                                            BitConverter.ToUInt32(data, at + 10), Date(data.AsSpan(at + 18, 7)));
            }

            at += length;
        }
    }

    private static string Name(ReadOnlySpan<byte> raw, bool joliet, bool isDirectory)
    {
        string name = joliet ? Encoding.BigEndianUnicode.GetString(raw) : Encoding.ASCII.GetString(raw);
        if (!isDirectory)
        {
            int version = name.LastIndexOf(';');
            if (version >= 0) name = name[..version];
            if (!joliet && name.EndsWith('.')) name = name[..^1];
        }

        return name.Replace('/', '_').Replace('\\', '_');
    }

    private static DateTimeOffset? Date(ReadOnlySpan<byte> d)
    {
        try
        {
            if (d[1] == 0 || d[2] == 0) return null;

            return new DateTimeOffset(1900 + d[0], d[1], d[2], d[3], d[4], d[5], TimeSpan.FromMinutes(15 * (sbyte)d[6]));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
