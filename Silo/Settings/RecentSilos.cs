using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Serilog;
using Silo.Model;

namespace Silo.Settings;

/// <summary>One entry of the most-recently-used list: where a Silo was opened from and when.</summary>
/// <param name="Target">The full path of the .silo file, or the URL it was opened from.</param>
/// <param name="Name">What to show for it: the file name without its extension.</param>
/// <param name="LastOpenedUtc">When it was last opened (or created).</param>
public sealed record RecentSilo(string Target, string Name, DateTime LastOpenedUtc)
{
    /// <summary>True for a Silo that lives at a URL.</summary>
    public bool IsRemote => Uri.TryCreate(Target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>False only for a local file that is no longer there (moved, deleted, drive not connected). A URL is assumed reachable.</summary>
    public bool Exists => IsRemote || File.Exists(Target);
}

/// <summary>
///     The most-recently-used list of Silos, newest first, persisted to <c>%AppData%\Silo\recent.json</c> so it survives
///     restarts. <see cref="OpenSilo" /> records every Silo it opens or creates; the "Recent" navigation view lists them and
///     opens one with a double-click.
/// </summary>
/// <remarks>
///     Several Silo processes can run at once (Solo Silos and URL Silos get a process of their own), all sharing the one
///     file, so every change re-reads it first and writes the merged result back; <see cref="Items" /> is then refreshed
///     from that result. Like the other settings files, persistence is best-effort: a missing, corrupt or locked file just
///     leaves the list as it was in memory.
/// </remarks>
public sealed class RecentSilos
{
    /// <summary>The most entries kept; the oldest fall off the end.</summary>
    public const int MaxEntries = 30;

    // declared before Instance: its constructor reads the file, and static initializers run in order
    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "recent.json");

    public static RecentSilos Instance { get; } = new();

    private readonly object _gate = new();

    private RecentSilos()
    {
        Refresh(list => list);
    }

    /// <summary>The list, newest first. Only changed through this class, and only on the UI thread.</summary>
    public ObservableCollection<RecentSilo> Items { get; } = [];

    /// <summary>Notes that <paramref name="silo" /> was just opened: a Silo opened from a URL is listed by that URL, not by its local cache file.</summary>
    public void Record(OpenSilo silo)
    {
        string? target = silo.RemoteUri?.ToString() ?? silo.FilePath;
        if (string.IsNullOrWhiteSpace(target))
            return;

        string? cache = silo.RemoteUri is not null ? silo.FilePath : null;
        Refresh(list =>
                {
                    if (cache is not null) list.RemoveAll(r => Same(r.Target, cache));
                    return Promote(list, target, silo.RemoteUri is { } uri ? NameOf(uri) : null);
                });
    }

    /// <summary>Notes that the Silo at <paramref name="target" /> (a path or a URL) was just opened.</summary>
    public void Record(string target, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;

        Refresh(list => Promote(list, target, name));
    }

    /// <summary>Takes one entry off the list (it does not touch the file).</summary>
    public void Remove(RecentSilo entry)
    {
        Refresh(list =>
                {
                    list.RemoveAll(r => Same(r.Target, entry.Target));
                    return list;
                });
    }

    /// <summary>Empties the list.</summary>
    public void Clear()
    {
        Refresh(_ => []);
    }

    private static List<RecentSilo> Promote(List<RecentSilo> list, string target, string? name)
    {
        string key = Normalize(target);
        list.RemoveAll(r => Same(r.Target, key));
        list.Insert(0, new RecentSilo(key, name ?? NameOf(key), DateTime.UtcNow));
        if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
        return list;
    }

    private static string Normalize(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return uri.ToString();

        try
        {
            return Path.GetFullPath(target);
        }
        catch
        {
            return target;
        }
    }

    private static bool Same(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string NameOf(Uri uri)
    {
        string last = Path.GetFileNameWithoutExtension(uri.AbsolutePath.TrimEnd('/'));
        return string.IsNullOrEmpty(last) ? uri.Host : last;
    }

    private static string NameOf(string target)
    {
        return Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                   ? NameOf(uri)
                   : Path.GetFileNameWithoutExtension(target);
    }

    /// <summary>Re-reads the file, applies <paramref name="change" />, saves the result and shows it in <see cref="Items" />.</summary>
    private void Refresh(Func<List<RecentSilo>, List<RecentSilo>> change)
    {
        List<RecentSilo> result;
        lock (_gate)
        {
            var list = Load();
            result = change(list);
            Save(result);
        }

        Items.Clear();
        foreach (var entry in result)
            Items.Add(entry);
    }

    private List<RecentSilo> Load()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(SettingsFile)) is { } entries)
                return entries.Where(e => !string.IsNullOrWhiteSpace(e.Target))
                              .Select(e => new RecentSilo(e.Target!, e.Name ?? NameOf(e.Target!), e.LastOpenedUtc))
                              .OrderByDescending(e => e.LastOpenedUtc)
                              .Take(MaxEntries)
                              .ToList();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Recent Silos list could not be read; starting from what is in memory");
        }

        // unreadable file: keep what this process already knows rather than dropping the list
        return Items.ToList();
    }

    private static void Save(List<RecentSilo> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile,
                              JsonSerializer.Serialize(list.Select(r => new Entry { Target = r.Target, Name = r.Name, LastOpenedUtc = r.LastOpenedUtc }),
                                                       new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Recent Silos list could not be saved");
        }
    }

    private sealed class Entry
    {
        public string?  Target        { get; set; }
        public string?  Name          { get; set; }
        public DateTime LastOpenedUtc { get; set; }
    }
}
