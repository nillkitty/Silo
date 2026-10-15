using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Serilog;

namespace Silo.Settings;

/// <summary>One destination that was connected to: where it is, what it was called and when it was last used.</summary>
public sealed record RecentDestination(string Uri, string Name, DateTime LastUsedUtc);

/// <summary>
///     What the New Connection dialog remembers between runs: the destinations connected to (newest first) and the URIs typed
///     into its URI tab (the MRU list), persisted to <c>%AppData%\Silo\connections.json</c>. Like <see cref="RecentSilos" />,
///     several Silo processes share the one file, so every change re-reads it first; persistence is best-effort.
/// </summary>
public sealed class ConnectionHistory
{
    public const int MaxDestinations = 20;
    public const int MaxUris         = 15;

    private static readonly string DefaultFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "connections.json");

    public static ConnectionHistory Instance { get; } = new(DefaultFile);

    private readonly string _file;
    private readonly object _gate = new();

    /// <summary>Opens the history kept in <paramref name="file" /> (tests use a temporary one).</summary>
    public ConnectionHistory(string file)
    {
        _file = file;
        Refresh(state => state);
    }

    /// <summary>Destinations connected to, newest first. Only changed through this class, on the UI thread.</summary>
    public ObservableCollection<RecentDestination> Destinations { get; } = [];

    /// <summary>URIs typed into the URI tab, newest first.</summary>
    public ObservableCollection<string> Uris { get; } = [];

    /// <summary>Notes that a connection to <paramref name="uri" /> was just made.</summary>
    public void RecordDestination(Uri uri, string name)
    {
        Refresh(state =>
                {
                    state.Destinations = PromoteDestination(state.Destinations, uri.ToString(), name, DateTime.UtcNow, MaxDestinations);
                    return state;
                });
    }

    /// <summary>Notes that <paramref name="text" /> was typed into the URI tab and used.</summary>
    public void RecordUri(string text)
    {
        Refresh(state =>
                {
                    state.Uris = PromoteText(state.Uris, text, MaxUris);
                    return state;
                });
    }

    /// <summary>Takes a destination off the list.</summary>
    public void RemoveDestination(RecentDestination destination)
    {
        Refresh(state =>
                {
                    state.Destinations.RemoveAll(d => Same(d.Uri, destination.Uri));
                    return state;
                });
    }

    /// <summary>Empties both lists.</summary>
    public void Clear()
    {
        Refresh(_ => new State());
    }

    /// <summary>Puts a destination first in the list (replacing an earlier entry for the same URI) and trims the list to <paramref name="max" />.</summary>
    public static List<RecentDestination> PromoteDestination(List<RecentDestination> list, string uri, string name, DateTime now, int max)
    {
        var result = list.Where(d => !Same(d.Uri, uri)).ToList();
        result.Insert(0, new RecentDestination(uri, string.IsNullOrWhiteSpace(name) ? uri : name, now));
        if (result.Count > max) result.RemoveRange(max, result.Count - max);
        return result;
    }

    /// <summary>Puts text first in the list (replacing an earlier entry that differs only by case) and trims the list to <paramref name="max" />; blank text changes nothing.</summary>
    public static List<string> PromoteText(List<string> list, string text, int max)
    {
        text = text.Trim();
        if (text.Length == 0) return list;

        var result = list.Where(t => !Same(t, text)).ToList();
        result.Insert(0, text);
        if (result.Count > max) result.RemoveRange(max, result.Count - max);
        return result;
    }

    private static bool Same(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private void Refresh(Func<State, State> change)
    {
        State result;
        lock (_gate)
        {
            result = change(Load());
            Save(result);
        }

        Destinations.Clear();
        foreach (var d in result.Destinations)
            Destinations.Add(d);

        Uris.Clear();
        foreach (var u in result.Uris)
            Uris.Add(u);
    }

    private State Load()
    {
        try
        {
            if (File.Exists(_file) && JsonSerializer.Deserialize<State>(File.ReadAllText(_file)) is { } state)
            {
                state.Destinations = state.Destinations.Where(d => !string.IsNullOrWhiteSpace(d.Uri))
                                          .OrderByDescending(d => d.LastUsedUtc).Take(MaxDestinations).ToList();
                state.Uris = state.Uris.Where(u => !string.IsNullOrWhiteSpace(u)).Take(MaxUris).ToList();
                return state;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "The connection history could not be read; starting from what is in memory");
        }

        // unreadable or missing file: keep what this process already knows
        return new State { Destinations = Destinations.ToList(), Uris = Uris.ToList() };
    }

    private void Save(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "The connection history could not be saved");
        }
    }

    /// <summary>What is in the file.</summary>
    private sealed class State
    {
        public List<RecentDestination> Destinations { get; set; } = [];
        public List<string>            Uris         { get; set; } = [];
    }
}
