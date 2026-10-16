using System.IO;
using System.Text;

namespace Silo.Connectors.File;

/// <summary>What the view of a <see cref="FileModel" /> shows; worked out off the UI thread by <see cref="FilePreviews.Compute" />.</summary>
public abstract record FilePreview;

/// <summary>One line of a <see cref="FolderPreview" />.</summary>
public sealed record FileRow(string Name, string Type, string Size, string Modified);

/// <summary>A folder, drive or link to a folder: what is in it.</summary>
public sealed record FolderPreview(IReadOnlyList<FileRow> Rows, bool Truncated) : FilePreview;

/// <summary>A text file: its (possibly cut short) text.</summary>
public sealed record TextPreview(string Text, bool Truncated) : FilePreview;

/// <summary>An image file the app can show: its bytes.</summary>
public sealed record ImagePreview(byte[] Bytes) : FilePreview;

/// <summary>Nothing to show but a sentence saying why.</summary>
public sealed record MessagePreview(string Text) : FilePreview;

/// <summary>Decides what to show for a path and reads what it needs. Reads the file system: call off the UI thread.</summary>
public static class FilePreviews
{
    /// <summary>The most of a text file that is read (and shown).</summary>
    public const int TextLimit = 256 * 1024;

    /// <summary>The largest image file that is loaded.</summary>
    public const long ImageLimit = 25L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
                                                              {
                                                                  ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff"
                                                              };

    public static FilePreview Compute(FileModel model, CancellationToken cancel = default)
    {
        try
        {
            var info = model.Info;
            if (info.Kind == FileKind.Missing)
                return new MessagePreview("Nothing is at this path, or it cannot be reached.");

            if (info.IsContainer)
                return Folder(model, cancel);

            if (model.Data is not FileInfo file)
                return new MessagePreview("This item cannot be previewed.");

            long length = file.Length;
            if (ImageExtensions.Contains(file.Extension))
                return length <= ImageLimit
                           ? new ImagePreview(ReadBytes(file, (int)length))
                           : new MessagePreview($"This image is too large to preview ({length:N0} bytes).");

            return Text(file, length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new MessagePreview(ex.Message);
        }
    }

    private static FilePreview Folder(FileModel model, CancellationToken cancel)
    {
        var children = model.GetChildren(cancel);
        var rows = new List<FileRow>(children.Count);
        foreach (var child in children)
        {
            cancel.ThrowIfCancellationRequested();
            string size     = string.Empty;
            string modified = string.Empty;
            try
            {
                if (child.Kind == FileKind.File && child.Data is FileInfo fi) size = FormatSize(fi.Length);
                modified = child.Data.LastWriteTime.ToString("g");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // an entry that cannot be read is listed without a size or date
            }

            rows.Add(new FileRow(child.Name, FileKinds.Describe(child.Info), size, modified));
        }

        return new FolderPreview(rows, children.Count >= FileModel.EntryLimit);
    }

    private static FilePreview Text(FileInfo file, long length)
    {
        if (length == 0) return new TextPreview(string.Empty, false);

        int wanted = (int)Math.Min(length, TextLimit);
        var bytes  = ReadBytes(file, wanted);

        // a NUL byte near the start means it is not text
        int probe = Math.Min(bytes.Length, 8192);
        for (int i = 0; i < probe; i++)
            if (bytes[i] == 0)
                return new MessagePreview($"No preview for this kind of file ({FormatSize(length)}).");

        string text;
        using (var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), true))
        {
            text = reader.ReadToEnd();
        }

        return new TextPreview(text, length > TextLimit);
    }

    private static byte[] ReadBytes(FileInfo file, int count)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var       buffer = new byte[count];
        int       read   = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n == 0) break;
            read += n;
        }

        return read == count ? buffer : buffer[..read];
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double   value = bytes;
        int      unit  = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} bytes" : $"{value:0.#} {units[unit]}";
    }
}
