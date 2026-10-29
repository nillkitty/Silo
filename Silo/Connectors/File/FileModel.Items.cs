using System.IO;
using Silo.Providers.Reflection;

namespace Silo.Connectors.File;

/// <summary>A folder or drive as an <see cref="IItemContainer" />: its files are the items.</summary>
public partial class FileModel
{
    /// <summary>The most items one listing returns (a drive can hold far more than anyone wants added in one go).</summary>
    private const int ItemLimit = 100_000;

    /// <inheritdoc />
    public bool CanEnumerate => IsContainer && Data is DirectoryInfo { Exists: true };

    /// <inheritdoc />
    public Uri? ContainerUri => Uri;

    /// <inheritdoc />
    public string ContainerName => Data.Name.Length > 0 ? Data.Name : Path;

    /// <inheritdoc />
    public IEnumerable<ContainerItem> EnumerateItems(bool recursive, CancellationToken cancel = default)
    {
        if (!CanEnumerate || Data is not DirectoryInfo directory) yield break;

        var options = new EnumerationOptions
                      {
                          IgnoreInaccessible       = true,
                          RecurseSubdirectories    = recursive,
                          ReturnSpecialDirectories = false,
                          AttributesToSkip         = (IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System) | FileAttributes.ReparsePoint
                      };

        int count = 0;
        foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
        {
            cancel.ThrowIfCancellationRequested();
            if (++count > ItemLimit) yield break;

            bool isDirectory = entry is DirectoryInfo;
            var  path        = entry.FullName;
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri)) continue;

            yield return new ContainerItem(path, uri, isDirectory, Signature(entry),
                                           isDirectory ? null : () => OpenForRead(path), isDirectory ? null : path);
        }
    }

    private static Stream? OpenForRead(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // in use exclusively, or not ours to read: classified by name alone
        }
    }
}
