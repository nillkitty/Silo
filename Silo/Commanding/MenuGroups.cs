using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls;
using Serilog;
using Silo.Explorer;
using Silo.Toolbar;
using Silo.Ui;

namespace Silo.Commanding;

/// <summary>
///     The ids of the menu groups: the places in the menu bar a command can say it belongs in (<see cref="ProvidedCommand.MenuGroup" />).
///     The prefix is only a namespace. A menu's own id ("Tools") names that menu's default group (the <c>*</c> row of the layout), for a
///     command that knows the menu but not the group.
/// </summary>
public static class MenuGroups
{
    // menus
    public const string FileMenu = "File", EditMenu = "Edit", ViewMenu = "View", SiloMenu = "Silo", ConnectionMenu = "Connection", NodeMenu = "Node", DiagramMenu = "Diagram",
                        LabMenu = "Lab", ToolsMenu = "Tools", HelpMenu = "Help";

    /// <summary>The group of a menu's own default entries (the <c>*</c> row).</summary>
    public const string Default = "*";

    // File
    public const string DocMgmt = "App.DocMgmt", DocMru = "App.DocMru", FileMgmt = "App.FileMgmt", ExitCmds = "App.ExitCmds";

    // Edit
    public const string UndoRedo = "Edit.UndoRedo", ClipboardCmds = "Edit.ClipboardCmds";

    // Silo
    public const string ConnectionMgmt = "Silo.ConnectionMgmt", CreationList = "Silo.CreationList", FormatList = "Silo.FormatList", SiloOtherCmds = "Silo.OtherCmds",
                        AddDataCmds = "Silo.AddDataCmds", ExportList = "Silo.ExportList", ImportList = "Silo.ImportList";

    // Connection and Node: the commands of a node (the selected one; the connection that contains it)
    public const string ConnectionNodeCmds = "Connection.NodeCmds", OtherNodeCmds = "Node.OtherNodeCmds", NodeLabCmds = "Node.NodeLabCmds", NodeDiagramGenCmds = "Node.DiagramGenCmds";

    // Diagram
    public const string NewDiagramCmds = "Silo.NewDiagramCmds", DiagramList = "Silo.DiagramList", SiloDiagramGenCmds = "Silo.DiagramGenCmds";

    // Lab
    public const string LabPreferences = "App.LabPreferences", LabsList = "App.LabsList";

    // Tools
    public const string ToolsMgmt = "Silo.ToolsMgmt", ToolsList = "Silo.ToolsList";

    // Help
    public const string HelpCmds = "Silo.HelpCmds", AboutCmds = "App.AboutCmds";
}

/// <summary>One block of a menu: a group's commands, or -- when <see cref="Submenu" /> is given -- a submenu that holds them.</summary>
public sealed class MenuSectionDef
{
    public MenuSectionDef() { }

    public MenuSectionDef(string group, string? submenu = null)
    {
        Group   = group;
        Submenu = submenu;
    }

    /// <summary>A group id (<see cref="MenuGroups" />), or "*" for the menu's default group.</summary>
    public string Group { get; set; } = MenuGroups.Default;

    /// <summary>The header of the submenu the group's commands go in ("_Export"); null puts them in the menu itself.</summary>
    public string? Submenu { get; set; }
}

/// <summary>One menu of the menu bar: its blocks, top to bottom, with a separator between those that have commands.</summary>
public sealed class MenuDef
{
    /// <summary>"File": what a command's <see cref="ProvidedCommand.MenuGroup" /> names to mean this menu's default group.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The menu's text; an underscore marks its access key.</summary>
    public string Header { get; set; } = string.Empty;

    /// <summary>True for a menu made in XAML (View): the layout only says where it sits.</summary>
    public bool Native { get; set; }

    /// <summary>What the menu says when it has no commands.</summary>
    public string? EmptyText { get; set; }

    public List<MenuSectionDef> Sections { get; set; } = [];
}

/// <summary>The menu bar's layout: its menus, left to right, and the groups in each. The built-in one is <see cref="Default" />; <see cref="MenuSettings" /> keeps the person's.</summary>
public sealed class MenuLayoutDef
{
    public List<MenuDef> Menus { get; set; } = [];

    public MenuLayoutDef Clone() => MenuSettings.FromJson(ToJson()) ?? Default();

    public string ToJson() => JsonSerializer.Serialize(this, MenuSettings.Options);

    private static MenuSectionDef S(string group, string? submenu = null) => new(group, submenu);

    private static MenuDef M(string id, string header, params MenuSectionDef[] sections) => new() { Id = id, Header = header, Sections = [S(MenuGroups.Default), .. sections] };

    /// <summary>The layout a new install starts with.</summary>
    public static MenuLayoutDef Default() => new()
    {
        Menus =
        [
            M(MenuGroups.FileMenu, "_File", S(MenuGroups.DocMgmt), S(MenuGroups.DocMru), S(MenuGroups.FileMgmt), S(MenuGroups.ExitCmds)),
            M(MenuGroups.EditMenu, "_Edit", S(MenuGroups.UndoRedo), S(MenuGroups.ClipboardCmds)),
            new MenuDef { Id = MenuGroups.ViewMenu, Header = "_View", Native = true },
            M(MenuGroups.SiloMenu, "_Silo", S(MenuGroups.ConnectionMgmt), S(MenuGroups.CreationList), S(MenuGroups.FormatList, "Silo _Format"), S(MenuGroups.SiloOtherCmds),
              S(MenuGroups.AddDataCmds), S(MenuGroups.ExportList, "_Export"), S(MenuGroups.ImportList, "_Import")),
            new MenuDef { Id = MenuGroups.ConnectionMenu, Header = "_Connection", EmptyText = "(select a node in a connection)", Sections = [S(MenuGroups.Default), S(MenuGroups.ConnectionNodeCmds)] },
            new MenuDef { Id = MenuGroups.NodeMenu, Header = "_Node", EmptyText = "(select a node in the Explorer)", Sections = [S(MenuGroups.Default), S(MenuGroups.OtherNodeCmds)] },
            M(MenuGroups.DiagramMenu, "_Diagram", S(MenuGroups.NewDiagramCmds), S(MenuGroups.DiagramList, "_Open Diagram"), S(MenuGroups.NodeDiagramGenCmds), S(MenuGroups.SiloDiagramGenCmds)),
            M(MenuGroups.LabMenu, "_Lab", S(MenuGroups.LabPreferences), S(MenuGroups.NodeLabCmds), S(MenuGroups.LabsList)),
            M(MenuGroups.ToolsMenu, "_Tools", S(MenuGroups.ToolsMgmt), S(MenuGroups.ToolsList)),
            M(MenuGroups.HelpMenu, "_Help", S(MenuGroups.HelpCmds), S(MenuGroups.AboutCmds))
        ]
    };

    /// <summary>The ids of every group the layout places somewhere (what a toolbar group slot can name).</summary>
    public IEnumerable<string> Groups() => Menus.SelectMany(m => m.Sections).Select(s => s.Group).Where(g => g != MenuGroups.Default).Distinct();
}

/// <summary>The person's menu layout, kept in <c>%APPDATA%\Silo\menus.json</c> when they changed it. Menus rebuild themselves each time they open, so a change shows at once.</summary>
public static class MenuSettings
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "menus.json");

    private static MenuLayoutDef? _layout;

    public static MenuLayoutDef Layout => _layout ??= Load();

    /// <summary>Raised after the layout changed.</summary>
    public static event EventHandler? Changed;

    public static MenuLayoutDef? FromJson(string json)
    {
        try
        {
            var layout = JsonSerializer.Deserialize<MenuLayoutDef>(json, Options);
            if (layout is not { Menus.Count: > 0 }) return null;

            layout.Menus.RemoveAll(m => m is null || string.IsNullOrWhiteSpace(m.Id));
            foreach (var m in layout.Menus)
            {
                m.Sections ??= [];
                m.Sections.RemoveAll(s => s is null || string.IsNullOrWhiteSpace(s.Group));
            }

            return layout.Menus.Count > 0 ? layout : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Apply(MenuLayoutDef layout)
    {
        _layout = layout;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, layout.ToJson());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Saving the menu layout to {file} failed", SettingsFile);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Back to the built-in layout (the file is deleted).</summary>
    public static void Reset()
    {
        _layout = MenuLayoutDef.Default();
        try
        {
            if (File.Exists(SettingsFile)) File.Delete(SettingsFile);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Deleting {file} failed", SettingsFile);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static MenuLayoutDef Load()
    {
        try
        {
            if (File.Exists(SettingsFile) && FromJson(File.ReadAllText(SettingsFile)) is { } layout) return layout;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading the menu layout {file} failed; using the default", SettingsFile);
        }

        return MenuLayoutDef.Default();
    }
}

/// <summary>What a menu is being built for: the main window, and the node selected in the Explorer.</summary>
public sealed record MenuContext(MainWindow? Main, ExplorerNode? Node);

/// <summary>
///     Where the commands of the menu bar come from, by group. The stock commands (<see cref="StockMenus" />, and the toolbar catalog's
///     <see cref="ToolbarItemDef.MenuGroup" />) are there; so are the commands of tools (<see cref="ICommandProvider" />) and of the selected node
///     (<see cref="NodeCommand" />) that name a group with <see cref="ProvidedCommand.MenuGroup" />, and anything a component adds with
///     <see cref="Add(string, Func{IEnumerable{ProvidedCommand}})" />. That is how a custom component's commands sit among the stock ones in the right menu.
/// </summary>
public static class MenuCommands
{
    private static readonly List<(string Group, Func<IEnumerable<ProvidedCommand>> Source)> Sources  = [];
    private static readonly List<(string Group, Func<MenuContext, IEnumerable<Control>> Source)> Dynamic = [];

    /// <summary>Adds commands to a group (or, with a menu id, to a menu's default group). The function is asked each time a menu opens.</summary>
    public static void Add(string group, Func<IEnumerable<ProvidedCommand>> source)
    {
        lock (Sources) Sources.Add((group, source));
    }

    public static void Add(string group, params ProvidedCommand[] commands) => Add(group, () => commands);

    /// <summary>Adds menu entries built by the caller (a list that is not made of plain commands, such as the tools); asked each time a menu opens.</summary>
    internal static void AddEntries(string group, Func<MenuContext, IEnumerable<Control>> source)
    {
        lock (Dynamic) Dynamic.Add((group, source));
    }

    /// <summary>The commands (not their entries) of a group that come from the stock list, the catalog, registrations and tools; the selected node's are not included.</summary>
    public static IReadOnlyList<ProvidedCommand> Commands(string group)
    {
        var result = new List<ProvidedCommand>();

        // the catalog first (File: New, Open...), then the rest of the stock commands, then what components added
        foreach (var def in ToolbarCatalog.BuiltIn)
            if (def.MenuGroup is not null && Is(def.MenuGroup, group) && def.Kind == ToolbarItemKind.Command)
                result.Add(def.ToProvided());

        foreach (var c in StockMenus.Commands)
            if (Is(c.MenuGroup, group))
                result.Add(c);

        (string Group, Func<IEnumerable<ProvidedCommand>> Source)[] sources;
        lock (Sources) sources = Sources.ToArray();
        foreach (var (g, source) in sources)
            if (Is(g, group))
                result.AddRange(Safe(source, g));

        foreach (var tool in ToolbarCatalog.AllTools().Where(t => t.IsVisible).OfType<ICommandProvider>())
            result.AddRange(Safe(() => tool.GetCommands().Where(c => c.MenuGroup is not null && Is(c.MenuGroup, group) && c.Placement.HasFlag(CommandPlacement.Menu)), group));

        return result;
    }

    /// <summary>The menu entries of a group, in the context of the main window and selected node.</summary>
    public static IReadOnlyList<Control> Entries(string group, MenuContext ctx)
    {
        var entries = new List<Control>();

        foreach (var c in Commands(group))
            entries.Add(CommandMenu.Item(NodeCommand.From(c)));

        (string Group, Func<MenuContext, IEnumerable<Control>> Source)[] dynamic;
        lock (Dynamic) dynamic = Dynamic.ToArray();
        foreach (var (g, source) in dynamic)
            if (Is(g, group))
                entries.AddRange(Safe(() => source(ctx), g));

        entries.AddRange(NodeEntries(group, ctx));
        return entries;
    }

    private static IEnumerable<Control> NodeEntries(string group, MenuContext ctx)
    {
        if (ctx.Node is null) return [];

        // the connection menu: what the Node menu would hold for the connection that contains the selected node
        if (Is(group, MenuGroups.ConnectionNodeCmds))
        {
            var connection = ConnectionOf(ctx.Node);
            return connection is null ? [] : CommandMenu.Items(NodeCommandHub.CommandsFor(connection).Where(IsPlainNodeCommand)).ToList();
        }

        var commands = NodeCommandHub.CommandsFor(ctx.Node);
        if (Is(group, MenuGroups.OtherNodeCmds) || Is(group, MenuGroups.NodeMenu))
            return NodeMenuEntries(ctx, commands);

        return CommandMenu.Items(commands.Where(c => c.MenuGroup is not null && Is(c.MenuGroup, group))).ToList();
    }

    /// <summary>Opening the node (what a click and a double-click do), its commands -- exactly what its right-click menu lists -- and Cut / Copy / Paste.</summary>
    private static IEnumerable<Control> NodeMenuEntries(MenuContext ctx, IReadOnlyList<NodeCommand> commands)
    {
        var list = new List<Control>();
        try
        {
            if (ctx.Node is { Opener: { } open, DocKey: not null })
            {
                var preview = new MenuItem { Header = "_Open (preview tab)" };
                preview.Click += (_, _) => open(false);
                var keep = new MenuItem { Header = "Open (_keep as a tab)" };
                keep.Click += (_, _) => open(true);
                list.Add(preview);
                list.Add(keep);
                list.Add(new Separator());
            }

            list.AddRange(CommandMenu.Items(commands.Where(IsPlainNodeCommand)));

            if (ctx.Main?.ExplorerControl is { } explorer)
            {
                var clipboard = Silo.Clipboarding.ClipboardCommands.Items(explorer.GetClipboardContext()).ToList();
                if (clipboard.Count > 0)
                {
                    if (list.Count > 0 && list[^1] is not Separator) list.Add(new Separator());
                    list.AddRange(clipboard);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Building the Node menu failed");
        }

        return list;
    }

    /// <summary>A node command with no menu group of its own (or one that names the Node menu) is the Node menu's.</summary>
    public static bool IsPlainNodeCommand(NodeCommand c)
    {
        return string.IsNullOrEmpty(c.MenuGroup) || Is(c.MenuGroup, MenuGroups.OtherNodeCmds) || Is(c.MenuGroup, MenuGroups.NodeMenu);
    }

    /// <summary>The node itself or the nearest node above it that stands for a connection; null when it is in none.</summary>
    public static ExplorerNode? ConnectionOf(ExplorerNode node)
    {
        for (var n = node; n is not null; n = n.Parent)
            if (n.Model is Silo.Contracts.Connectors.IConnection)
                return n;

        return null;
    }

    private static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<T> Safe<T>(Func<IEnumerable<T>> source, string group)
    {
        try
        {
            return source().ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Getting the commands of menu group {group} failed", group);
            return [];
        }
    }
}
