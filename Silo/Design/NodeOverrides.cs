using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Serilog;

namespace Silo.Design;

/// <summary>What the person has changed about one tree node (author mode): a label, an icon size, whether it is shown.</summary>
public sealed class NodeOverride
{
    /// <summary>Replaces the node's text.</summary>
    public string? Label { get; set; }

    /// <summary>Replaces the node's icon size (pixels).</summary>
    public double? IconSize { get; set; }

    /// <summary>The node is hidden from its tree.</summary>
    public bool Hidden { get; set; }

    internal bool IsEmpty => Label is null && IconSize is null && !Hidden;
}

/// <summary>
///     The per-node author-mode customisations of the navigation flyout and the Explorer tree, kept in
///     <c>%APPDATA%\Silo\nodeoverrides.json</c>. Keys are <c>nav:&lt;path of node names&gt;</c> or
///     <c>explorer:&lt;node key&gt;</c>. Nodes read their overrides from here; the trees listen to <see cref="Changed" /> and
///     tell their nodes to re-read.
/// </summary>
public static class NodeOverrides
{
    public const string NavPrefix      = "nav:";
    public const string ExplorerPrefix = "explorer:";

    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "nodeoverrides.json");

    private static readonly Dictionary<string, NodeOverride> Map = Load();

    private static bool _saveQueued;

    /// <summary>Raised on the UI thread after any override changed.</summary>
    public static event EventHandler? Changed;

    public static NodeOverride? Get(string key) => Map.TryGetValue(key, out var o) ? o : null;

    /// <summary>Changes the override of a node (created if needed, removed again when it ends up empty), saves, and raises <see cref="Changed" />.</summary>
    public static void Update(string key, Action<NodeOverride> change)
    {
        if (!Map.TryGetValue(key, out var o)) o = new NodeOverride();
        change(o);

        if (o.IsEmpty) Map.Remove(key);
        else Map[key] = o;

        QueueSave();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>How many nodes under <paramref name="prefix" /> are hidden.</summary>
    public static int HiddenCount(string prefix) => Map.Count(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Value.Hidden);

    /// <summary>Shows every hidden node under <paramref name="prefix" /> again.</summary>
    public static void ShowHidden(string prefix)
    {
        foreach (var key in Map.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Value.Hidden).Select(kv => kv.Key).ToList())
        {
            var o = Map[key];
            o.Hidden = false;
            if (o.IsEmpty) Map.Remove(key);
        }

        QueueSave();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static Dictionary<string, NodeOverride> Load()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<Dictionary<string, NodeOverride>>(File.ReadAllText(SettingsFile)) is { } map)
                return new Dictionary<string, NodeOverride>(map, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Reading the node overrides failed; starting empty");
        }

        return new Dictionary<string, NodeOverride>(StringComparer.Ordinal);
    }

    // a slider drag changes the value many times a second: write the file once things settle
    private static void QueueSave()
    {
        if (_saveQueued) return;

        _saveQueued = true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Save();
            return;
        }

        dispatcher.BeginInvoke(new Action(Save), DispatcherPriority.Background);
    }

    private static void Save()
    {
        _saveQueued = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Map));
        }
        catch
        {
            /* best effort, like the other settings files */
        }
    }
}
