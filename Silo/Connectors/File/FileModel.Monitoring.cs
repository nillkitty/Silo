using System.IO;
using Silo.Providers.Monitoring;

namespace Silo.Connectors.File;

/// <summary>
///     Change monitoring for the file system: passive through a <see cref="FileSystemWatcher" />, active by comparing
///     listings of the folder (or the file's own size and time) on an interval.
/// </summary>
public partial class FileModel
{
    /// <summary>Passive for any folder or file that is there; active as well, except for a drive that is not ready.</summary>
    public MonitorModes SupportedModes =>
        Kind == FileKind.Missing || !(Data.Exists || Directory.Exists(Path)) ? MonitorModes.None : MonitorModes.Passive | MonitorModes.Active;

    /// <inheritdoc />
    public IDisposable? StartMonitoring(MonitorModes mode, Action<NodeChange> onChange, bool recursive = false, TimeSpan? interval = null)
    {
        if (mode is not (MonitorModes.Passive or MonitorModes.Active) || !SupportedModes.HasFlag(mode)) return null;

        return mode == MonitorModes.Passive ? Watch(onChange, recursive) : ChangeMonitoring.Poll(Snapshot, UriOf, onChange, interval);
    }

    private static Uri? UriOf(string path)
    {
        return Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri : null;
    }

    private IDisposable? Watch(Action<NodeChange> onChange, bool recursive)
    {
        // a folder is watched for what happens in it; a file through its folder, for its name only
        string folder = IsContainer ? Path : global::System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
        if (folder.Length == 0) return null;

        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(folder)
                      {
                          IncludeSubdirectories = recursive && IsContainer,
                          NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size |
                                         NotifyFilters.CreationTime,
                          InternalBufferSize = 64 * 1024
                      };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (!IsContainer) watcher.Filter = global::System.IO.Path.GetFileName(Path);

        void Raise(NodeChangeKind kind, string path, string? old = null)
        {
            if (!IncludeHidden && IsHidden(path)) return;

            try
            {
                onChange(new NodeChange(kind, path, UriOf(path), old));
            }
            catch (Exception)
            {
                // a listener's fault must not stop the watcher
            }
        }

        watcher.Created += (_, e) => Raise(NodeChangeKind.Created, e.FullPath);
        watcher.Changed += (_, e) => Raise(NodeChangeKind.Changed, e.FullPath);
        watcher.Deleted += (_, e) => Raise(NodeChangeKind.Deleted, e.FullPath);
        watcher.Renamed += (_, e) => Raise(NodeChangeKind.Renamed, e.FullPath, e.OldFullPath);
        watcher.Error   += (_, _) => onChange(new NodeChange(NodeChangeKind.Reset, Path, Uri)); // overflowed: the caller must look again

        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            watcher.Dispose();
            return null;
        }

        return watcher;
    }

    private static bool IsHidden(string path)
    {
        try
        {
            return (global::System.IO.File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch (Exception)
        {
            return false; // gone already, or unreadable
        }
    }

    /// <summary>The item's children (or the item itself, for a file), each with the size and time that change when its content does.</summary>
    private IReadOnlyDictionary<string, string> Snapshot()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Data is DirectoryInfo directory && IsContainer)
        {
            var options = new EnumerationOptions
                          {
                              IgnoreInaccessible = true,
                              AttributesToSkip   = IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System
                          };
            foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
            {
                if (snapshot.Count >= EntryLimit) break;

                snapshot[entry.FullName] = Signature(entry);
            }
        }
        else
        {
            var info = new FileInfo(Path);
            if (info.Exists) snapshot[info.FullName] = Signature(info);
        }

        return snapshot;
    }

    private static string Signature(FileSystemInfo entry)
    {
        long length = entry is FileInfo file ? file.Length : -1;
        return $"{length}|{entry.LastWriteTimeUtc.Ticks}|{(int)entry.Attributes}";
    }
}
