using Serilog;
using Silo.Connectors.Containers;
using Silo.Providers.Archive;
using Silo.Tasks;
using IOPath = System.IO.Path;

namespace Silo.Connectors.File;

/// <summary>
///     A file or folder on the clipboard and in drag and drop: it is copied (and dragged) as itself, a file of the file system,
///     and a folder takes copies of the files and folders pasted or dropped on it -- from another folder, from another program,
///     or taken out of a container file or a data file.
/// </summary>
public partial class FileModel : ICopyable, IPastable
{
    public bool CanCopy => Kind != FileKind.Missing;

    public IReadOnlyCollection<string> CopyFormats => [ClipboardFormats.Files, ClipboardFormats.Text];

    public void Copy(IClipboardWriter clipboard)
    {
        clipboard.Set(ClipboardFormats.Files, new[] { Path });
        clipboard.Set(ClipboardFormats.Text,  Path);
    }

    public IReadOnlyList<string> PasteFormats => [ClipboardFormats.Files];

    public bool CanPaste(IClipboardData data, PasteMode mode)
    {
        if (!IsContainer || Data is not DirectoryInfo || !DropFiles.Offers(data)) return false;
        if (mode == PasteMode.Move && data.Operation != ClipboardOperation.Cut) return false;

        // not a folder into itself or into a folder inside it
        foreach (var source in data.Sources.OfType<FileModel>())
            if (ContainerPaths.IsSameOrInside(Normal(Path), Normal(source.Path)))
                return false;

        return true;
    }

    private static string Normal(string path) => IOPath.TrimEndingDirectorySeparator(IOPath.GetFullPath(path)).Replace('\\', '/');

    /// <summary>
    ///     Copies what is pasted into this folder, renaming anything whose name is taken: in the background for a copy, before
    ///     returning for a move (the originals only remove themselves once their copies are surely made).
    /// </summary>
    public PasteResult Paste(IClipboardData data, PasteMode mode)
    {
        if (!CanPaste(data, mode)) return PasteResult.None;

        var paths = DropFiles.Read(data);
        if (paths.Count == 0) return PasteResult.None;

        string folder = Path;
        string what   = paths.Count == 1 ? IOPath.GetFileName(IOPath.TrimEndingDirectorySeparator(paths[0])) : $"{paths.Count} items";
        if (mode == PasteMode.Duplicate)
        {
            _ = CopyInAsync(paths, folder, what);
            return PasteResult.Copied(paths.Cast<object>().ToArray());
        }

        var task = TaskCenter.Begin($"Moving {what} to {Name}", "Explorer");
        try
        {
            foreach (string source in paths) DropFiles.CopyInto(source, folder);
            task.Succeed(folder, paths.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            task.Fail(ex);
            App.Status($"Moving {what} to {Name} failed: {ex.Message}");
            return PasteResult.None;
        }
        finally
        {
            _ = RefreshAsync();
        }

        return PasteResult.Copied(paths.Cast<object>().ToArray());
    }

    private async Task CopyInAsync(IReadOnlyList<string> paths, string folder, string what)
    {
        try
        {
            await FileTasks.RunAsync($"Copying {what} to {Name}", "Explorer", task => Task.Run(() =>
                                                                                              {
                                                                                                  foreach (string source in paths)
                                                                                                  {
                                                                                                      task.Token.ThrowIfCancellationRequested();
                                                                                                      DropFiles.CopyInto(source, folder);
                                                                                                  }
                                                                                              }, task.Token), true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Copying into {Folder} failed", folder);
            App.Status($"Copying {what} to {Name} failed: {ex.Message}");
        }

        DropFiles.OnUi(() => _ = RefreshAsync());
    }
}
