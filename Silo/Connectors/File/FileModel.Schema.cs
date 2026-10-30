using System.IO;
using Silo.DbModel.Worlds;

namespace Silo.Connectors.File;

/// <summary>A folder as a data source: one type, <c>FileEntry</c>, with a row for each file and folder in it.</summary>
public partial class FileModel : ISchema
{
    /// <summary>The one type a file system source has.</summary>
    public const string EntryTypeName = "FileEntry";

    /// <inheritdoc />
    public string SchemaProvider => "file";

    /// <inheritdoc />
    public string SchemaDisplayName => ContainerName;

    /// <inheritdoc />
    public Uri? SchemaUri => Uri;

    /// <inheritdoc />
    public IReadOnlyList<SchemaType> GetSchema()
    {
        return
        [
            new SchemaType(EntryTypeName, "FileSystemInfo",
            [
                new SchemaField("Path",        "string",         typeof(string),   WorldFieldFlags.Key | WorldFieldFlags.AbsolutePath),
                new SchemaField("Name",        "string",         typeof(string),   WorldFieldFlags.RelativePath),
                new SchemaField("Extension",   "string",         typeof(string),   WorldFieldFlags.Nullable),
                new SchemaField("IsDirectory", "bool",           typeof(bool)),
                new SchemaField("Length",      "long",           typeof(long),     WorldFieldFlags.Nullable),
                new SchemaField("Created",     "DateTime",       typeof(DateTime), WorldFieldFlags.Creation),
                new SchemaField("Modified",    "DateTime",       typeof(DateTime), WorldFieldFlags.ModifyDate),
                new SchemaField("Attributes",  "FileAttributes", typeof(string))
            ])
        ];
    }

    /// <inheritdoc />
    public IEnumerable<WorldObject> GetObjects(string typeName, CancellationToken cancel = default)
    {
        if (!string.Equals(typeName, EntryTypeName, StringComparison.OrdinalIgnoreCase)) yield break;

        IEnumerable<FileSystemInfo> entries = [];
        if (Data is DirectoryInfo { Exists: true } directory)
        {
            var options = new EnumerationOptions
                          {
                              IgnoreInaccessible = true,
                              AttributesToSkip   = IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System
                          };
            entries = directory.EnumerateFileSystemInfos("*", options);
        }
        else if (Data is FileInfo { Exists: true } single)
        {
            entries = [single];
        }

        int count = 0;
        foreach (var entry in entries)
        {
            cancel.ThrowIfCancellationRequested();
            if (++count > EntryLimit * 5) yield break;

            var file = entry as FileInfo;
            yield return new WorldObject(entry.FullName, entry.FullName,
                                         new Dictionary<string, object?>
                                         {
                                             ["Path"]        = entry.FullName,
                                             ["Name"]        = entry.Name,
                                             ["Extension"]   = file is null ? null : entry.Extension,
                                             ["IsDirectory"] = file is null,
                                             ["Length"]      = file?.Length,
                                             ["Created"]     = entry.CreationTimeUtc,
                                             ["Modified"]    = entry.LastWriteTimeUtc,
                                             ["Attributes"]  = entry.Attributes.ToString()
                                         });
        }
    }
}
