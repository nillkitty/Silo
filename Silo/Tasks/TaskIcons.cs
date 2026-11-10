namespace Silo.Tasks;

/// <summary>The icon of a task, by what asked for it (<see cref="ITrackedTask.Source" />): each kind of work has its own, so the pane can be read at a glance.</summary>
public static class TaskIcons
{
    private static readonly Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase)
                                                              {
                                                                  ["Silo file"]    = "TaskSiloFile",
                                                                  ["Silo import"]  = "TaskImport",
                                                                  ["Text editor"]  = "TaskTextEditor",
                                                                  ["Table"]        = "TaskTable",
                                                                  ["File system"]  = "TaskFileSystem",
                                                                  ["Certificates"] = "CertStore",
                                                                  ["Link Crawler"] = "Crawl",
                                                                  ["Web Lab"]      = "Crawl",
                                                                  ["Explorer"]     = "TaskExplorer",
                                                                  ["Sync"]         = "TaskSync"
                                                              };

    /// <summary>The key of the icon for work from this source; the generic task icon when the source has none of its own.</summary>
    public static string KeyFor(string? source)
    {
        return source is not null && Keys.TryGetValue(source, out var key) ? key : "Tasks";
    }

    public static IconRef For(string? source)
    {
        return IconRef.FromKey(KeyFor(source));
    }
}
