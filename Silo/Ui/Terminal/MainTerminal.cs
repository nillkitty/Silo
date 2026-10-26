using System.Text;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using Silo.Commanding;
using Silo.Contracts.Core;
using Silo.Labs;
using Silo.Model;
using Silo.Toolbar;
using Silo.Ui.Panes;

namespace Silo.Ui;

/// <summary>
///     The terminal in the main window's status bar, the counterpart of a Lab's (<see cref="LabTerminal" />). A line is, in turn:
///     one of the window's own commands (<c>help</c>, <c>show</c>, <c>nav</c>, <c>open</c>, <c>macro</c>...), a menu or toolbar
///     command by id or name (<c>file.save</c>, <c>save</c>, <c>newconnection</c>), a command of the node selected in the Explorer,
///     a tool's name (opens or closes it), or -- when it reads as one -- a Silo file or address to open.
/// </summary>
internal sealed class MainTerminal
{
    /// <summary>Where the main window's macros are kept in the profile (<see cref="LabProfile" />), beside the Labs' own.</summary>
    public const string MacroScope = "$main";

    private static readonly string[] BuiltIns =
    [
        "help", "commands", "node", "menu", "tools", "tool", "panes", "show", "hide", "layout", "nav", "open", "silos", "active", "log", "clear", "macro"
    ];

    private readonly List<string> _recorded = [];
    private readonly MainWindow   _window;
    private          bool         _replaying;

    public MainTerminal(MainWindow window)
    {
        _window = window;
    }

    /// <summary>The name of the macro being recorded, or null.</summary>
    public string? RecordingName { get; private set; }

    /// <summary>How many steps the macro being recorded has so far.</summary>
    public int RecordedCount => _recorded.Count;

    /// <summary>Raised when recording starts, stops or gains a step.</summary>
    public event EventHandler? RecordingChanged;

    /// <summary>The command words that start with <paramref name="prefix" />, for Tab.</summary>
    public IReadOnlyList<string> Completions(string prefix)
    {
        return BuiltIns.Concat(ToolbarCatalog.All().Where(d => d.Kind == ToolbarItemKind.Command).Select(d => d.Id))
                       .Concat(NodeCommands().Select(c => c.Id))
                       .Concat(Tools().Select(t => t.Name))
                       .Where(w => w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
                       .ToList();
    }

    /// <summary>Runs a line; returns what to show (null for just "ok"). Throws with a message a person can read when it cannot be done.</summary>
    public async Task<string?> ExecuteAsync(string line)
    {
        line = line.Trim();

        // a line that starts with # is a comment
        if (line.Length == 0 || line[0] == '#') return null;

        int    space = line.IndexOfAny([' ', '\t']);
        string word  = space < 0 ? line : line[..space];
        string rest  = space < 0 ? string.Empty : line[space..].Trim();
        var    args  = LabTerminal.Tokenize(rest);

        // the macro commands themselves are not steps of a macro
        if (!word.Equals("macro", StringComparison.OrdinalIgnoreCase)) Note(line);

        switch (word.ToLowerInvariant())
        {
            case "help" or "?":
                return Help();
            case "commands":
                return CommandList(rest);
            case "node":
                return rest.Length == 0 ? NodeList() : RunNodeCommand(rest);
            case "menu":
                return rest.Length == 0 || rest.EndsWith('/') ? MenuList(rest.TrimEnd('/')) : RunMenu(rest);
            case "tools":
                return ToolList();
            case "tool":
                return ToggleTool(Unquote(rest));
            case "panes":
                return PaneList();
            case "show" or "hide":
                return ShowOrHide(word.Equals("show", StringComparison.OrdinalIgnoreCase), args);
            case "layout" when args.Count > 0 && args[0].Equals("reset", StringComparison.OrdinalIgnoreCase):
                _window.ResetWindowLayout();
                return null;
            case "nav":
                return rest.Length == 0 ? string.Join(Environment.NewLine, _window.NavPaths()) : Navigate(Unquote(rest));
            case "open":
                return await OpenAsync(Unquote(rest));
            case "silos":
                return SiloList();
            case "active":
                return Activate(Unquote(rest));
            case "log":
                _window.ShowTerminalLog();
                return null;
            case "clear":
                _window.ClearTerminalLog();
                return null;
            case "macro":
                return await MacroAsync(rest, args);
        }

        // a command of the menus or the toolbar, then one of the selected node, then a tool
        if (FindCatalog(line) is { } item) return Run(item.CreateCommand(), item.Parameter, item.PlainHeader);
        if (FindNodeCommand(line) is { } nodeCommand) return Run(nodeCommand.Command, nodeCommand.Parameter, ToolbarText.Plain(nodeCommand.Header));
        if (FindTool(line) is { } tool) return ToggleTool(tool);

        if (LooksOpenable(line)) return await OpenAsync(line);

        throw new InvalidOperationException($"Unknown command '{word}'. Type help.");
    }

    private static string Help()
    {
        var text = new StringBuilder();
        text.AppendLine("Window commands:");
        text.AppendLine("  commands [filter]           the menu and toolbar commands; type an id (file.save) or a name (save) to run one");
        text.AppendLine("  node [<command>]            the commands of the node selected in the Explorer, or run one");
        text.AppendLine("  menu [<path>/] | menu <path>   list the menu bar (View/), or click an entry: menu \"View/Reset Toolbar\"");
        text.AppendLine("  tools | tool <name>         list the tools, or open/close one (a tool's name alone works too)");
        text.AppendLine("  panes | show <pane> | hide <pane> | layout reset");
        text.AppendLine("  nav [<path>]                list the navigation, or go to a node: nav \"Settings/Silo Settings/Snapshots\"");
        text.AppendLine("  open <file or URL>          open a Silo (a bare path or address works too)");
        text.AppendLine("  silos | active <name or number>");
        text.AppendLine("  macro list | record <name> | stop | run <name> | show <name> | save <name> <line>; <line> | delete <name>");
        text.AppendLine("  log | clear");
        text.Append("Up and Down recall, Tab completes, Ctrl+` comes here, lines starting with # are comments.");
        return text.ToString();
    }

    // ---- the menu and toolbar commands (the toolbar catalog) ---------------------------------

    private static string CommandList(string filter)
    {
        var lines = ToolbarCatalog.All()
                                  .Where(d => d.Kind == ToolbarItemKind.Command)
                                  .Where(d => filter.Length == 0 || d.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                              d.PlainHeader.Contains(filter, StringComparison.OrdinalIgnoreCase))
                                  .Select(d => $"{d.Id,-22} {d.PlainHeader}{(IsAvailable(d.CreateCommand(), d.Parameter) ? string.Empty : "  (not now)")}")
                                  .ToList();
        return lines.Count == 0 ? $"No command matches '{filter}'." : string.Join(Environment.NewLine, lines);
    }

    private static ToolbarItemDef? FindCatalog(string text)
    {
        string squeezed = Squeeze(text);
        var    commands = ToolbarCatalog.All().Where(d => d.Kind == ToolbarItemKind.Command).ToList();
        return commands.FirstOrDefault(d => d.Id.Equals(text, StringComparison.OrdinalIgnoreCase))
               ?? commands.FirstOrDefault(d => Squeeze(d.PlainHeader) == squeezed)
               // "save" for "Save Silo", "newconnection" for "New Connection...": the name without the word Silo
               ?? commands.FirstOrDefault(d => Squeeze(d.PlainHeader.Replace("Silo", string.Empty, StringComparison.OrdinalIgnoreCase)) == squeezed);
    }

    // ---- the selected node's commands -------------------------------------------------------

    private static IEnumerable<NodeCommand> NodeCommands()
    {
        return NodeCommandHub.CommandsFor(NodeCommandHub.Current).SelectMany(c => c.SelfAndDescendants()).Where(c => c.Children is not { Count: > 0 });
    }

    private static string NodeList()
    {
        if (NodeCommandHub.Current is not { } node) return "Nothing is selected in the Explorer.";

        var commands = NodeCommands().ToList();
        if (commands.Count == 0) return $"{node.Display} has no commands.";

        return $"{node.Display}:{Environment.NewLine}" +
               string.Join(Environment.NewLine, commands.Select(c => $"  {c.Id,-22} {ToolbarText.Plain(c.Header)}{(IsAvailable(c.Command, c.Parameter) ? string.Empty : "  (not now)")}"));
    }

    private static NodeCommand? FindNodeCommand(string text)
    {
        string squeezed = Squeeze(text);
        var    commands = NodeCommands().ToList();
        return commands.FirstOrDefault(c => c.Id.Equals(text, StringComparison.OrdinalIgnoreCase))
               ?? commands.FirstOrDefault(c => Squeeze(ToolbarText.Plain(c.Header)) == squeezed);
    }

    private static string? RunNodeCommand(string text)
    {
        if (NodeCommandHub.Current is null) throw new InvalidOperationException("Nothing is selected in the Explorer.");

        var command = FindNodeCommand(Unquote(text)) ?? throw new InvalidOperationException($"The selected node has no command '{text}'. See node.");
        return Run(command.Command, command.Parameter, ToolbarText.Plain(command.Header));
    }

    // ---- the menu bar -----------------------------------------------------------------------

    /// <summary>Every entry of the menu bar with its path ("View/Reset Window Layout"), depth first. Menus filled when they open list what they hold now.</summary>
    private IEnumerable<(string Path, MenuItem Item)> MenuEntries()
    {
        return _window.MainMenu is { } menu ? Walk(menu.Items, string.Empty) : Enumerable.Empty<(string, MenuItem)>();

        static IEnumerable<(string, MenuItem)> Walk(ItemCollection items, string above)
        {
            foreach (object o in items)
            {
                if (o is not MenuItem item || item.Visibility != Visibility.Visible) continue;

                string name = ToolbarText.Plain(item.Header?.ToString());
                if (name.Length == 0) continue;

                string path = above.Length == 0 ? name : above + "/" + name;
                yield return (path, item);
                foreach (var inner in Walk(item.Items, path)) yield return inner;
            }
        }
    }

    private string MenuList(string under)
    {
        var lines = MenuEntries().Where(e => under.Length == 0 || e.Path.StartsWith(under + "/", StringComparison.OrdinalIgnoreCase))
                                 .Select(e => $"{e.Path}{(e.Item.HasItems ? "/" : string.Empty)}{(e.Item.IsEnabled ? string.Empty : "  (not now)")}")
                                 .ToList();
        return lines.Count == 0 ? $"There is no menu '{under}'." : string.Join(Environment.NewLine, lines);
    }

    private string? RunMenu(string path)
    {
        path = Unquote(path).Replace('>', '/').Replace(" / ", "/").Trim('/');
        var wanted = path.Split('/').Select(Squeeze).ToArray();
        var entry  = MenuEntries().FirstOrDefault(e => e.Path.Split('/').Select(Squeeze).SequenceEqual(wanted));
        if (entry.Item is not { } item) throw new InvalidOperationException($"There is no menu entry '{path}'. See menu.");
        if (item.HasItems) throw new InvalidOperationException($"{entry.Path} is a menu; menu \"{entry.Path}/\" lists it.");
        if (!item.IsEnabled) throw new InvalidOperationException($"{entry.Path} is not available right now.");

        // clicked the way a screen reader clicks it: the item's Click, its command and its check mark all happen as for the mouse
        if (new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException($"{entry.Path} cannot be clicked from here.");

        invoke.Invoke();
        return null;
    }

    // ---- tools and panes -------------------------------------------------------------------

    private static IReadOnlyList<ITool> Tools()
    {
        try
        {
            return App.Instance?.Tools.Tools.Where(t => t.IsVisible).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static ITool? FindTool(string text)
    {
        string squeezed = Squeeze(text);
        return Tools().FirstOrDefault(t => t.Name.Equals(text, StringComparison.OrdinalIgnoreCase) || Squeeze(t.Header) == squeezed);
    }

    private static string ToolList()
    {
        var tools = Tools();
        return tools.Count == 0
                   ? "No tools."
                   : string.Join(Environment.NewLine, tools.Select(t => $"{(t.IsChecked ? "* " : "  ")}{t.Name,-24} {t.Header}{(t.IsEnabled ? string.Empty : "  (not now)")}"));
    }

    private static string? ToggleTool(string name)
    {
        if (name.Length == 0) throw new InvalidOperationException("tool <name>  (see tools)");

        return ToggleTool(FindTool(name) ?? throw new InvalidOperationException($"There is no tool '{name}'. See tools."));
    }

    private static string? ToggleTool(ITool tool)
    {
        if (!tool.IsEnabled) throw new InvalidOperationException($"{tool.Header} is not available right now.");

        tool.Toggle();
        return tool.IsChecked ? $"{tool.Header} is open." : $"{tool.Header} is closed.";
    }

    private static readonly string[] Panes = ["explorer", "properties", "toolbox", "problems", "selected"];

    private string PaneList()
    {
        bool[] open = [_window.IsExplorerOpen, _window.IsPropertiesOpen, _window.IsToolboxOpen, ProblemsTool.Current?.IsChecked == true, _window.IsSelectedItemOpen];
        return string.Join(Environment.NewLine, Panes.Select((p, i) => $"{p}{(open[i] ? "  (open)" : string.Empty)}")) +
               Environment.NewLine + "(and every tool: see tools)";
    }

    private string? ShowOrHide(bool show, IReadOnlyList<string> args)
    {
        if (args.Count == 0) throw new InvalidOperationException($"{(show ? "show" : "hide")} <pane>  (see panes)");

        switch (args[0].ToLowerInvariant())
        {
            case "explorer":
                _window.SetExplorerOpen(show);
                return null;
            case "properties":
                _window.SetPropertiesOpen(show);
                return null;
            case "toolbox":
                _window.SetToolboxOpen(show);
                return null;
            case "selected" or "selecteditem" or "preview":
                _window.SetSelectedItemOpen(show);
                return null;
            case "problems":
                if (ProblemsTool.Current is not { } problems) throw new InvalidOperationException("There is no Problems pane.");
                if (show) problems.Show();
                else if (problems.IsChecked) problems.Toggle();
                return null;
        }

        // a tool's pane or window
        var tool = FindTool(string.Join(' ', args)) ?? throw new InvalidOperationException($"There is no pane or tool '{args[0]}'. See panes.");
        if (tool.IsChecked != show) tool.Toggle();
        return null;
    }

    // ---- navigation and Silos ---------------------------------------------------------------

    private string? Navigate(string path)
    {
        var found = _window.Navigate(path) ?? throw new InvalidOperationException($"There is no navigation node '{path}'. nav lists them.");
        return found;
    }

    private static bool LooksOpenable(string text)
    {
        text = Unquote(text);
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return true;

        try
        {
            return text.IndexOfAny(global::System.IO.Path.GetInvalidPathChars()) < 0 && global::System.IO.File.Exists(text);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> OpenAsync(string target)
    {
        if (target.Length == 0)
        {
            App.OpenSiloCommand.Execute(null); // the Open dialog
            return null;
        }

        await OpenSiloCommand.OpenPathAsync(target);
        return null;
    }

    private static string SiloList()
    {
        var silos  = OpenSilo.OpenSilos.ToList();
        var active = App.Instance?.ActiveSilo;
        if (silos.Count == 0) return "No Silos are open. open <file or URL>";

        return string.Join(Environment.NewLine,
                           silos.Select((s, i) => $"{(ReferenceEquals(s, active) ? "* " : "  ")}{i + 1}  {s.Name ?? "(untitled)"}{(s.IsDirty ? "*" : string.Empty)}  {s.FilePath}"));
    }

    private static string? Activate(string which)
    {
        if (App.Instance is not { } app) return null;

        var silos = OpenSilo.OpenSilos.ToList();
        if (which.Length == 0) return app.ActiveSilo?.Name ?? "(no active Silo)";

        var silo = int.TryParse(which, out int n) && n >= 1 && n <= silos.Count
                       ? silos[n - 1]
                       : silos.FirstOrDefault(s => string.Equals(s.Name, which, StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidOperationException($"No open Silo is called '{which}'. See silos.");

        app.ActiveSilo = silo;
        return $"{silo.Name ?? "(untitled)"} is the active Silo.";
    }

    // ---- macros (kept in the profile, as the Labs' are) -------------------------------------

    private void Note(string line)
    {
        if (RecordingName is null || _replaying) return;

        _recorded.Add(line);
        RecordingChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string?> MacroAsync(string rest, IReadOnlyList<string> args)
    {
        string verb = args.Count > 0 ? args[0].ToLowerInvariant() : "list";
        string name = args.Count > 1 ? args[1] : string.Empty;

        switch (verb)
        {
            case "list":
                var names = LabProfile.MacroNames(MacroScope);
                return names.Count == 0
                           ? "No macros yet. macro record <name>, type the lines, macro stop."
                           : string.Join(Environment.NewLine, names.Select(n => $"{n}  ({LabProfile.GetMacro(MacroScope, n)?.Count ?? 0} steps)"));
            case "record" when name.Length > 0:
                if (RecordingName is not null) throw new InvalidOperationException($"Already recording '{RecordingName}'. macro stop keeps it.");

                _recorded.Clear();
                RecordingName = name;
                RecordingChanged?.Invoke(this, EventArgs.Empty);
                return $"Recording '{name}': the lines typed here are kept. macro stop keeps it.";
            case "stop":
                return $"Saved macro '{RecordingName}' ({StopRecording()} step(s)).";
            case "run" when name.Length > 0:
                await RunMacroAsync(name);
                return $"Ran macro '{name}'.";
            case "show" when name.Length > 0:
                return string.Join(Environment.NewLine, LabProfile.GetMacro(MacroScope, name) ?? throw new InvalidOperationException($"There is no macro '{name}'."));
            case "save" when name.Length > 0:
                int    at    = rest.IndexOf(name, StringComparison.Ordinal);
                string after = at < 0 ? string.Empty : rest[(at + name.Length)..].Trim();
                var    lines = after.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length == 0) throw new InvalidOperationException("macro save <name> <line>; <line>");

                LabProfile.SaveMacro(MacroScope, name, lines);
                return $"Saved macro '{name}' ({lines.Length} step(s)).";
            case "delete" when name.Length > 0:
                return LabProfile.DeleteMacro(MacroScope, name) ? $"Deleted macro '{name}'." : throw new InvalidOperationException($"There is no macro '{name}'.");
            default:
                throw new InvalidOperationException("macro list | record <name> | stop | run <name> | show <name> | save <name> <line>; <line> | delete <name>");
        }
    }

    /// <summary>Stops recording and keeps the steps; returns how many there were.</summary>
    public int StopRecording()
    {
        if (RecordingName is not { } name) throw new InvalidOperationException("Not recording.");

        int count = _recorded.Count;
        LabProfile.SaveMacro(MacroScope, name, _recorded);
        _recorded.Clear();
        RecordingName = null;
        RecordingChanged?.Invoke(this, EventArgs.Empty);
        return count;
    }

    private async Task RunMacroAsync(string name)
    {
        var lines = LabProfile.GetMacro(MacroScope, name) ?? throw new InvalidOperationException($"There is no macro '{name}'.");
        if (RecordingName is not null) throw new InvalidOperationException("Stop recording first.");

        _replaying = true;
        try
        {
            foreach (string line in lines)
                await ExecuteAsync(line);
        }
        finally
        {
            _replaying = false;
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static bool IsAvailable(ICommand? command, object? parameter)
    {
        try
        {
            return command?.CanExecute(parameter) == true;
        }
        catch
        {
            return false;
        }
    }

    private static string? Run(ICommand? command, object? parameter, string name)
    {
        if (command is null || !IsAvailable(command, parameter)) throw new InvalidOperationException($"{name} is not available right now.");

        command.Execute(parameter);
        return null;
    }

    private static string Squeeze(string? text) => new string((text ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string Unquote(string text)
    {
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
    }
}
