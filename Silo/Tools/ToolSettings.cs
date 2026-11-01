using System.Text.Json;
using Serilog;

namespace Silo.Tools;

/// <summary>One tool in the person's arrangement: which tool (<see cref="ITool.Name" />) and whether it is switched on.</summary>
public sealed class ToolEntry
{
    public string Name    { get; set; } = string.Empty;
    public bool   Enabled { get; set; } = true;
}

/// <summary>
///     Which tools are switched on and the order they come in (the Tools menu, and the toolbar's tool buttons and
///     drop-down), kept in <c>%APPDATA%\Silo\tools.json</c>. Tools are matched by name, so this also holds for tools that are
///     made anew each time they are asked for. A tool that is not listed (a new one) is on, and comes after the listed ones.
/// </summary>
public static class ToolSettings
{
    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "tools.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static List<ToolEntry>? _entries;

    /// <summary>The saved arrangement (do not change it in place: pass a changed copy to <see cref="Apply" />).</summary>
    public static IReadOnlyList<ToolEntry> Entries => _entries ??= Load();

    /// <summary>Raised on the calling thread after the arrangement changed.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    ///     The tools in the person's order. Each tool is also told its place (<see cref="ITool.Priority" />) and whether it is on
    ///     (<see cref="ToolBase.IsVisible" />), so the Tools menu follows; the disabled ones are still returned: callers that
    ///     show tools leave out those that are not <see cref="ITool.IsVisible" />.
    /// </summary>
    public static IReadOnlyList<ITool> Ordered(IEnumerable<ITool> tools)
    {
        var entries = Entries;
        int IndexOf(ITool t) => entries.ToList().FindIndex(e => string.Equals(e.Name, t.Name, StringComparison.Ordinal));

        var ordered = tools.Select(t => (Tool: t, Index: IndexOf(t)))
                           .OrderBy(x => x.Index < 0 ? int.MaxValue : x.Index) // OrderBy is stable: unlisted tools keep their order
                           .Select(x => x.Tool)
                           .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Priority = i;
            if (ordered[i] is ToolBase tb) tb.IsVisible = IsEnabled(ordered[i].Name);
        }

        return ordered;
    }

    public static bool IsEnabled(string name)
    {
        return Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal))?.Enabled ?? true;
    }

    /// <summary>Makes <paramref name="entries" /> the arrangement: remembered, saved and shown.</summary>
    public static void Apply(IEnumerable<ToolEntry> entries)
    {
        _entries = entries.Select(e => new ToolEntry { Name = e.Name, Enabled = e.Enabled }).ToList();
        Save();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Every tool on, in the order they come in.</summary>
    public static void Reset() => Apply([]);

    private static List<ToolEntry> Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<List<ToolEntry>>(File.ReadAllText(SettingsFile), Options)?
                                     .Where(e => !string.IsNullOrEmpty(e.Name)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading the tool arrangement {file} failed; using the default", SettingsFile);
        }

        return [];
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_entries, Options));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Saving the tool arrangement to {file} failed", SettingsFile);
        }
    }
}
