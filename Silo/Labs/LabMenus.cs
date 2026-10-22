using System.Windows.Controls;
using Serilog;
using Silo.Toolbar;

namespace Silo.Labs;

/// <summary>Which of the window's menus an action belongs in when the Lab does not make its own menus.</summary>
internal enum LabMenuPlace
{
    Lab,
    File,
    Edit
}

/// <summary>Sorts the actions of a Lab into File and Edit by what they are, and builds menu entries with separators between the kinds.</summary>
internal static class LabMenus
{
    private static readonly string[] OpenWhat = ["file", "reel", "spec", "menu", "regex", "syntax", "draft", "code", "saved", "silo"];

    /// <summary>
    ///     Where the action goes, and how it ranks inside that menu (a separator goes wherever the rank changes). It goes by what the
    ///     header says (New..., Open File, Save..., Export..., Undo, Copy..., Paste, Delete...); an icon says nothing (a "stop" is drawn
    ///     with the delete icon).
    /// </summary>
    public static (LabMenuPlace Place, int Rank) Classify(ProvidedCommand c)
    {
        string h     = LabTerminal.Strip(c.Header).Trim().TrimEnd('.', '\u2026').ToLowerInvariant();
        string first = h.Split(' ', 2)[0];
        string rest  = h.Length > first.Length ? h[(first.Length + 1)..] : string.Empty;

        switch (first)
        {
            case "undo":
            case "redo":
                return (LabMenuPlace.Edit, 0);
            case "cut":
            case "copy":
            case "paste":
            case "duplicate":
                return (LabMenuPlace.Edit, 1);
            case "delete":
                return (LabMenuPlace.Edit, 2);
            case "select" when rest == "all":
                return (LabMenuPlace.Edit, 2);
            case "new":
                return (LabMenuPlace.File, 0);
            case "open" when rest.Length == 0 || OpenWhat.Any(w => rest.StartsWith(w, StringComparison.Ordinal)):
                return (LabMenuPlace.File, 0);
            case "save":
                return (LabMenuPlace.File, 1);
            case "keep" when rest is "draft" or "kept draft":
                return (LabMenuPlace.File, 1);
            case "import":
            case "export":
                return (LabMenuPlace.File, 2);
        }

        return (LabMenuPlace.Lab, 0);
    }

    /// <summary>Every action the Lab has: the global, context and value ones and those in its own menus (once each, by id).</summary>
    public static List<ProvidedCommand> All(ILabSession session)
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ProvidedCommand>();

        void Add(IEnumerable<ProvidedCommand?> commands)
        {
            foreach (var c in commands)
            {
                if (c is null) continue;

                if (c.Children is { Count: > 0 } children) Add(children);
                else if (seen.Add(c.Id)) result.Add(c);
            }
        }

        Add(Safe(() => session.GlobalActions));
        Add(Safe(() => session.ContextActions));
        Add(Safe(() => session.ValueActions));
        foreach (var menu in SafeMenus(session)) Add(menu.Items);
        return result;
    }

    public static List<ProvidedCommand?> Safe(Func<IEnumerable<ProvidedCommand>> get)
    {
        try
        {
            return get().Cast<ProvidedCommand?>().ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Asking a Lab for its actions failed");
            return [];
        }
    }

    public static List<LabMenu> SafeMenus(ILabSession session)
    {
        try
        {
            return session.Menus.ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Asking a Lab for its menus failed");
            return [];
        }
    }

    /// <summary>The entries of a Lab's own menu: its commands, separators where it has a null.</summary>
    public static void Fill(MenuItem menu, IEnumerable<ProvidedCommand?> items)
    {
        bool lastWasSeparator = true;
        foreach (var c in items)
        {
            if (c is null)
            {
                if (!lastWasSeparator) menu.Items.Add(new Separator());
                lastWasSeparator = true;
                continue;
            }

            menu.Items.Add(CommandMenu.Item(NodeCommand.From(c)));
            lastWasSeparator = false;
        }

        while (menu.Items.Count > 0 && menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
    }

    /// <summary>The actions sorted into rank order with a separator where the rank changes (stable inside a rank).</summary>
    public static List<ProvidedCommand?> Ranked(IEnumerable<(ProvidedCommand Command, int Rank)> items)
    {
        var result = new List<ProvidedCommand?>();
        int? rank  = null;
        foreach (var (command, r) in items.Select((x, i) => (x, i)).OrderBy(t => t.x.Rank).ThenBy(t => t.i).Select(t => t.x))
        {
            if (rank is { } last && last != r) result.Add(null);
            rank = r;
            result.Add(command);
        }

        return result;
    }

    /// <summary>The icon a toolbar button shows with its text.</summary>
    public static string IconFor(ProvidedCommand c) => !string.IsNullOrEmpty(c.IconKey) && ToolbarIcons.Find(c.IconKey) is not null ? c.IconKey : LabIcons.For(c.Header);
}
