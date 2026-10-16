using System.Windows.Input;
using Silo.Commanding;
using Silo.Model;
using Silo.Tabular;

namespace Silo.Toolbar;

/// <summary>What a catalog item turns into on the toolbar.</summary>
public enum ToolbarItemKind
{
    /// <summary>One button that runs a command.</summary>
    Command,

    /// <summary>The Tools drop-down: every tool that has no buttons of its own, each opening or closing the tool as the Tools menu does.</summary>
    ToolsDropdown,

    /// <summary>One button per toolbar command of the tools that implement <see cref="ICommandProvider" /> (those not placed on their own).</summary>
    ToolCommands,

    /// <summary>One button per command of the node selected in the Explorer (see <see cref="NodeCommandHub" />).</summary>
    NodeCommands,

    /// <summary>The button that opens the Customize Toolbar dialog.</summary>
    Customize
}

/// <summary>
///     A thing the toolbar can show: what the Customize dialog lists under its category and what a <see cref="ToolbarSlot" />
///     refers to by <see cref="Id" />.
/// </summary>
/// <param name="Id">Stable across runs; the saved layout is a list of these.</param>
/// <param name="Category">The group the Customize dialog files it under: File, Edit, Silo, Help, Tools, Node, Toolbar.</param>
/// <param name="Header">The label (menu-style: an underscore marks an access key, which the toolbar drops).</param>
/// <param name="ToolTip">Explanation shown when hovering.</param>
/// <param name="IconKey">The default icon (a geometry resource key); null shows the label as text.</param>
/// <param name="Kind">What it becomes on the toolbar.</param>
/// <param name="Command">
///     For a routed command (Cut, Copy...): the command itself, so it acts on whatever has the keyboard focus. Otherwise
///     null, and <see cref="Resolve" /> is used.
/// </param>
/// <param name="Resolve">Finds the command when the button is used (so Silo commands act on the Silo that is active then).</param>
/// <param name="Parameter">Passed to the command.</param>
public sealed record ToolbarItemDef(
    string          Id,
    string          Category,
    string          Header,
    string?         ToolTip,
    string?         IconKey,
    ToolbarItemKind Kind      = ToolbarItemKind.Command,
    ICommand?       Command   = null,
    Func<ICommand?>? Resolve  = null,
    object?         Parameter = null)
{
    /// <summary>The label without its access-key underscore or trailing dots.</summary>
    public string PlainHeader => ToolbarText.Plain(Header);

    /// <summary>The command a button for this item runs: <see cref="Command" /> as is, else one that looks up <see cref="Resolve" /> on use.</summary>
    public ICommand? CreateCommand()
    {
        if (Command is not null) return Command;
        return Resolve is null ? null : new LateBoundCommand(Resolve);
    }
}

/// <summary>Label helpers for the toolbar.</summary>
public static class ToolbarText
{
    /// <summary>"_Open Silo..." becomes "Open Silo": no access-key underscore, no trailing dots.</summary>
    public static string Plain(string? header)
    {
        if (string.IsNullOrEmpty(header)) return string.Empty;

        // "__" is a literal underscore; a single "_" marks an access key
        string text = header.Replace("__", "\u0001").Replace("_", string.Empty).Replace("\u0001", "_");
        return text.TrimEnd('.', '…', ' ');
    }
}

/// <summary>
///     Everything that can go on the toolbar: the File, Edit, Help and Silo menu commands, the structural items (node
///     commands, tool commands, the Tools drop-down, Customize), and the commands tools offer through
///     <see cref="ICommandProvider" /> (found when asked, as tools come and go).
/// </summary>
public static class ToolbarCatalog
{
    public const string ToolCommandPrefix = "tool:";

    private static readonly Lazy<IReadOnlyList<ToolbarItemDef>> StaticItems = new(BuildStatic);

    /// <summary>The built-in items, in the order the Customize dialog lists them.</summary>
    public static IReadOnlyList<ToolbarItemDef> BuiltIn => StaticItems.Value;

    private static OpenSilo? Active => App.Instance?.ActiveSilo;

    /// <summary>Built-in items plus the commands the tools offer.</summary>
    public static IEnumerable<ToolbarItemDef> All() => BuiltIn.Concat(ToolCommands());

    /// <summary>The item with this id, or null (a tool's command whose tool is not there, for instance).</summary>
    public static ToolbarItemDef? Find(string id)
    {
        foreach (var item in BuiltIn)
            if (item.Id == id)
                return item;

        return id.StartsWith(ToolCommandPrefix, StringComparison.Ordinal) ? ToolCommands().FirstOrDefault(d => d.Id == id) : null;
    }

    /// <summary>One item per toolbar command of each tool that implements <see cref="ICommandProvider" />.</summary>
    public static IEnumerable<ToolbarItemDef> ToolCommands()
    {
        foreach (var tool in Tools())
        {
            if (tool is not ICommandProvider provider) continue;

            IEnumerable<ProvidedCommand> commands;
            try
            {
                commands = provider.GetCommands().Where(c => c.Placement.HasFlag(CommandPlacement.Toolbar)).ToList();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Getting the commands of tool {tool} failed", tool.Name);
                continue;
            }

            foreach (var c in commands)
                yield return FromProvided(ToolCommandPrefix + tool.Name + ":" + c.Id, "Tools", c);
        }
    }

    /// <summary>The tools that have no button of their own, for the Tools drop-down.</summary>
    public static IEnumerable<ITool> DropdownTools()
    {
        return Tools().Where(t => t.IsVisible && !HasToolbarCommands(t));
    }

    /// <summary>The tools that have their own buttons (they implement <see cref="ICommandProvider" /> and offer a toolbar command).</summary>
    public static bool HasToolbarCommands(ITool tool)
    {
        if (tool is not ICommandProvider provider) return false;

        try
        {
            return provider.GetCommands().Any(c => c.Placement.HasFlag(CommandPlacement.Toolbar));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A catalog item for a command a node or tool provided.</summary>
    public static ToolbarItemDef FromProvided(string id, string category, ProvidedCommand c)
    {
        return new ToolbarItemDef(id, category, c.Header, c.ToolTip ?? ToolbarText.Plain(c.Header), c.IconKey, Command: c.Command, Parameter: c.Parameter);
    }

    private static IEnumerable<ITool> Tools()
    {
        try
        {
            return App.Instance?.Tools.Tools.ToList() ?? [];
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Listing the tools for the toolbar failed");
            return [];
        }
    }

    private static List<ToolbarItemDef> BuildStatic()
    {
        var items = new List<ToolbarItemDef>();

        void Add(string id, string category, string header, string? tip, string? icon, Func<ICommand?> resolve, object? parameter = null)
        {
            items.Add(new ToolbarItemDef(id, category, header, tip, icon, Resolve: resolve, Parameter: parameter));
        }

        void Routed(string id, string header, string tip, string icon, ICommand command)
        {
            items.Add(new ToolbarItemDef(id, "Edit", header, tip, icon, Command: command));
        }

        // ---- File ---------------------------------------------------------------------------
        Add("file.new",     "File", "_New Silo...",       "New Silo (Ctrl+N)",                 "TbNew",     () => App.NewSiloCommand);
        Add("file.open",    "File", "_Open Silo...",      "Open Silo (Ctrl+O)",                "TbOpen",    () => App.OpenSiloCommand);
        Add("file.openurl", "File", "Open from _URL...",  "Open Silo from URL (Ctrl+Shift+O)", "Link",      () => App.OpenSiloFromUrlCommand);
        Add("file.save",    "File", "_Save Silo",         "Save Silo (Ctrl+S)",                "TbSave",    () => Active?.Commands.SaveSilo);
        Add("file.saveas",  "File", "Save Silo _As...",   "Save Silo As (Ctrl+Shift+S)",       "TbSaveAs",  () => Active?.Commands.SaveSiloAs);
        Add("file.close",   "File", "_Close Silo",        "Close Silo",                        "TbCloseSilo", () => Active?.Commands.CloseSilo);
        Add("file.exit",    "File", "E_xit",              "Exit (Alt+F4)",                     "TbExit",    () => App.ExitCommand);

        // ---- Edit: routed commands, so they act on whatever has the keyboard focus --------------
        Routed("edit.undo",      "_Undo",      "Undo (Ctrl+Z)",  "Undo",     ApplicationCommands.Undo);
        Routed("edit.redo",      "_Redo",      "Redo (Ctrl+Y)",  "TbRedo",   ApplicationCommands.Redo);
        Routed("edit.cut",       "Cu_t",       "Cut (Ctrl+X)",   "TbCut",    ApplicationCommands.Cut);
        Routed("edit.copy",      "_Copy",      "Copy (Ctrl+C)",  "TbCopy",   ApplicationCommands.Copy);
        Routed("edit.paste",     "_Paste",     "Paste (Ctrl+V)", "TbPaste",  ApplicationCommands.Paste);
        items.Add(new ToolbarItemDef("edit.pastetable", "Edit", "Paste as _New Table...",
                                     "Make a new table in the Silo from the table or text on the clipboard", "TbPasteTable",
                                     Command: new DelegateCommand(_ => TablePaste.AsNewTable(Active), _ => Active is not null)));
        Routed("edit.delete",    "_Delete",    "Delete (Del)",       "TbDelete",    ApplicationCommands.Delete);
        Routed("edit.selectall", "Select _All", "Select All (Ctrl+A)", "TbSelectAll", ApplicationCommands.SelectAll);

        // ---- Help ---------------------------------------------------------------------------
        Add("help.about", "Help", "_About Silo...", "About Silo", "TbHelp", () => App.About);

        // ---- Silo (act on the active Silo) -------------------------------------------------------
        Add("silo.newconnection", "Silo", "_New Connection...", "New Connection (Ctrl+Shift+N)", "Plug",  () => Active?.Commands.NewConnection);
        Add("silo.newcredential", "Silo", "New _Credential...", "New Credential",                "Key",   () => Active?.Commands.NewCredential);
        Add("silo.archive",       "Silo", "Convert to _Archive Format", "Convert the Silo to its archive format", "Layers", () => Active?.Commands.ConvertToArchive);
        Add("silo.table.rich",      "Silo", "New Table: _Rich Text",    "A table whose every column is rich text",         "TbTable", () => Active?.Commands.NewTable, "rich");
        Add("silo.table.plain",     "Silo", "New Table: _Plain Text",   "A table whose every column is literal text",      "TbTable", () => Active?.Commands.NewTable, "plain");
        Add("silo.table.typed",     "Silo", "New Table: T_yped Columns", "A table that starts with a text, a number and a date column", "TbTable", () => Active?.Commands.NewTable, "typed");
        Add("silo.table.clipboard", "Silo", "New Table From _Clipboard...", "A table made of the table or text on the clipboard", "TbPasteTable", () => Active?.Commands.NewTable, "clipboard");
        Add("silo.export.logs",  "Silo", "Export _Logs...",  "Export the Silo's logs",  "TbExport", () => Active?.Commands.ExportLogs);
        Add("silo.export.state", "Silo", "Export _State...", "Export the Silo's state", "TbExport", () => Active?.Commands.ExportState);
        Add("silo.export.all",   "Silo", "Export _All...",   "Export everything in the Silo", "TbExport", () => Active?.Commands.ExportAll);
        Add("silo.import",       "Silo", "_Import Data...",  "Import data files into the Silo", "TbImport", () => Active?.Commands.ImportData);

        // ---- structure -------------------------------------------------------------------------
        items.Add(new ToolbarItemDef(ToolbarLayout.NodeCommandsId, "Node", "Node commands",
                                     "The commands of the node selected in the Explorer (they change with the selection)", "Nodes", ToolbarItemKind.NodeCommands));
        items.Add(new ToolbarItemDef(ToolbarLayout.ToolCommandsId, "Tools", "Tool commands",
                                     "The commands that tools offer, each tool's own buttons", "Puzzle", ToolbarItemKind.ToolCommands));
        items.Add(new ToolbarItemDef(ToolbarLayout.ToolsDropdownId, "Tools", "Tools",
                                     "Open or close a tool (the tools that have no buttons of their own)", "Puzzle", ToolbarItemKind.ToolsDropdown));
        items.Add(new ToolbarItemDef(ToolbarLayout.CustomizeId, "Toolbar", "Customize Toolbar...", "Add, remove, reorder and rename the toolbar's buttons",
                                     "TbCustomize", ToolbarItemKind.Customize));

        return items;
    }
}
