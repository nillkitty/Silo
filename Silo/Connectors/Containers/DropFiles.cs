using System.Collections.Specialized;
using Serilog;
using IOFile = System.IO.File;

namespace Silo.Connectors.Containers;

/// <summary>
///     Files on the way into or out of a container or a data file: the folders things are taken out into so they can be
///     dragged or pasted elsewhere as files, the file list of a paste or drop, and names that do not clash.
/// </summary>
public static class DropFiles
{
    private static int _cleaned;

    /// <summary>Where things taken out for a drag or a copy go. Folders older than a day are deleted the first time one is made.</summary>
    public static string Root => Path.Combine(Path.GetTempPath(), "Silo", "drag");

    /// <summary>A new, empty folder of its own under <see cref="Root" />.</summary>
    public static string NewDirectory()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) == 0) _ = Task.Run(CleanOld);

        string path = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanOld()
    {
        try
        {
            if (!Directory.Exists(Root)) return;

            foreach (var dir in new DirectoryInfo(Root).EnumerateDirectories())
                if (dir.CreationTimeUtc < DateTime.UtcNow.AddDays(-1))
                    dir.Delete(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Could not clear the old drag folders");
        }
    }

    /// <summary>
    ///     The files and folders a paste or a drop offers (the <see cref="ClipboardFormats.Files" /> format); empty when it offers
    ///     none. Asking renders the format, which can mean taking things out of a container.
    /// </summary>
    public static IReadOnlyList<string> Read(IClipboardData data)
    {
        if (!data.Contains(ClipboardFormats.Files)) return [];

        IEnumerable<string> paths = data.Get(ClipboardFormats.Files) switch
                                    {
                                        string one                  => [one],
                                        string[] many               => many,
                                        StringCollection collection => collection.Cast<string>(),
                                        IEnumerable<string> list    => list,
                                        _                           => []
                                    };
        return paths.Where(p => !string.IsNullOrWhiteSpace(p) && (IOFile.Exists(p) || Directory.Exists(p))).ToList();
    }

    /// <summary>Whether the paste or drop offers files, without rendering them (cheap enough for a drag-over).</summary>
    public static bool Offers(IClipboardData data) => data.Contains(ClipboardFormats.Files);

    /// <summary>
    ///     <paramref name="name" />, or -- when <paramref name="taken" /> says it is in use -- "name (2).ext", "name (3).ext"
    ///     and so on.
    /// </summary>
    public static string Unique(string name, Func<string, bool> taken)
    {
        if (!taken(name)) return name;

        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2;; i++)
        {
            string next = $"{stem} ({i}){ext}";
            if (!taken(next)) return next;
        }
    }

    /// <summary>A file name made of <paramref name="text" />: the characters a name cannot have are replaced; "item" when nothing is left.</summary>
    public static string SafeName(string? text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = new string((text ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return name.Length == 0 ? "item" : name.Length > 120 ? name[..120] : name;
    }

    /// <summary>Copies a file, or a folder with everything in it, into <paramref name="folder" /> under a name that is not taken there.</summary>
    /// <returns>Where the copy is.</returns>
    public static string CopyInto(string source, string folder)
    {
        string name   = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        string unique = Unique(name, n => IOFile.Exists(Path.Combine(folder, n)) || Directory.Exists(Path.Combine(folder, n)));
        string target = Path.Combine(folder, unique);

        if (IOFile.Exists(source))
        {
            IOFile.Copy(source, target);
            return target;
        }

        string full = Path.GetFullPath(source);
        if (Path.GetFullPath(target).StartsWith(Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"\"{name}\" cannot be copied into itself.");

        Directory.CreateDirectory(target);
        foreach (string dir in Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(full, dir)));
        foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            IOFile.Copy(file, Path.Combine(target, Path.GetRelativePath(full, file)));
        return target;
    }

    /// <summary>Runs <paramref name="action" /> on the UI thread (now, when this is it).</summary>
    public static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
