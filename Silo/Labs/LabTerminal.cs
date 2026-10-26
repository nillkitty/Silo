using System.Text;
using Silo.Commanding;

namespace Silo.Labs;

/// <summary>
///     The little terminal in a Lab window's status bar. A line is, in turn: a command the Lab added, one of the window's own commands
///     (<c>help</c>, <c>params</c>, <c>set</c>, <c>show</c>...), the id or name of one of the Lab's actions (a verb: <c>refresh</c>,
///     <c>back</c>), or -- when it reads as one -- a value to open.
/// </summary>
internal sealed class LabTerminal
{
    private static readonly string[] BuiltIns =
    [
        "help", "actions", "value", "open", "go", "params", "get", "set", "add", "remove", "clear", "reset", "preset", "inspectors", "show", "hide", "layout", "log", "close",
        "macro", "parts", "part", "tools", "tool", "aspects", "prefabs"
    ];

    private readonly ILabSession _session;
    private readonly LabWindow   _window;

    public LabTerminal(LabWindow window, ILabSession session)
    {
        _window  = window;
        _session = session;
    }

    private LabParameterSet Parameters => _session.Lab.Parameters;

    private ILabModel? Model => _session as ILabModel;

    /// <summary>Splits a line into words; "double quotes" keep words together.</summary>
    public static List<string> Tokenize(string line)
    {
        var words   = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, started = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                quoted  = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started) words.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started) words.Add(current.ToString());
        return words;
    }

    /// <summary>The command words that start with <paramref name="prefix" />, for Tab.</summary>
    public IReadOnlyList<string> Completions(string prefix)
    {
        return _session.TerminalCommands.SelectMany(c => (c.Aliases ?? []).Prepend(c.Name))
                       .Concat(BuiltIns)
                       .Concat(AllActions().Select(a => a.Id))
                       .Where(w => w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
                       .ToList();
    }

    /// <summary>Runs a line; returns what to show (null for just "ok"). Throws with a message a person can read when it cannot be done.</summary>
    public async Task<string?> ExecuteAsync(string line)
    {
        line = line.Trim();

        // a line that starts with # is a comment (a macro keeps what it did that way: # prefab x)
        if (line.Length == 0 || line[0] == '#') return null;

        int    space = line.IndexOfAny([' ', '\t']);
        string word  = space < 0 ? line : line[..space];
        string rest  = space < 0 ? string.Empty : line[space..].Trim();
        var    args  = Tokenize(rest);

        var call = new LabTerminalCall(_session, line, rest, args);
        foreach (var command in _session.TerminalCommands)
            if (command.Matches(word))
                return await command.Run(call);

        switch (word.ToLowerInvariant())
        {
            case "help" or "?":
                return Help();
            case "actions":
                return string.Join(Environment.NewLine, AllActions().Select(Describe));
            case "value":
                return _session.ValueText;
            case "open" or "go":
                return await OpenAsync(rest);
            case "params":
                return ParameterList();
            case "macro":
                return await MacroAsync(rest, args);
            case "parts":
                return Parts();
            case "part":
                return Part(rest);
            case "tools":
                return Tools();
            case "tool":
                return Tool(rest);
            case "aspects":
                return Aspects();
            case "prefabs":
                return Prefabs();
            case "get":
                return Get(args);
            case "set":
                return Set(rest, args);
            case "add" or "remove":
                return AddOrRemove(word.ToLowerInvariant() == "add", rest, args);
            case "clear" when args.Count > 0:
                RequireParameter(args[0]).Value = null;
                return null;
            case "clear":
                _window.ClearLog();
                return null;
            case "reset":
                return Reset(args);
            case "preset":
                return Preset(args);
            case "inspectors":
                return string.Join(Environment.NewLine, _window.InspectorIds.Select(id => $"{id}{(_window.IsInspectorOpen(id) ? "  (open)" : string.Empty)}"));
            case "show" or "hide":
                return ShowOrHide(word.Equals("show", StringComparison.OrdinalIgnoreCase), args);
            case "layout" when args.Count > 0 && args[0].Equals("reset", StringComparison.OrdinalIgnoreCase):
                _window.ResetLayout();
                return null;
            case "log":
                _window.ShowLog();
                return null;
            case "close":
                _window.Close();
                return null;
        }

        if (FindAction(word) is { } action)
        {
            if (!action.Command.CanExecute(action.Parameter))
                throw new InvalidOperationException($"{Strip(action.Header)} is not available right now.");

            action.Command.Execute(action.Parameter);
            return null;
        }

        if (_session.TryParseValue(line, out _))
            return await OpenAsync(line);

        throw new InvalidOperationException($"Unknown command '{word}'. Type help.");
    }

    private async Task<string?> OpenAsync(string text)
    {
        if (text.Length == 0)
        {
            await _session.OpenAsync(null);
            return null;
        }

        if (!_session.TryParseValue(text, out var value))
            throw new InvalidOperationException($"'{text}' is not something {_session.Lab.Name} can open.");

        await _session.OpenAsync(value);
        return null;
    }

    private string Help()
    {
        var text = new StringBuilder();

        if (_session.TerminalCommands.Count > 0)
        {
            text.AppendLine($"{_session.Lab.Name} commands:");
            foreach (var c in _session.TerminalCommands) text.AppendLine($"  {c.Help}");
        }

        text.AppendLine("Window commands:");
        text.AppendLine("  open <value> | go <value>   study a value (a bare value works too)");
        text.AppendLine("  value                       show the value being studied");
        text.AppendLine("  params | get <p> | set <p> <value> | add <p> <line> | remove <p> <line> | clear <p> | reset [p]");
        text.AppendLine("  preset list | save <name> | load <name> | delete <name>");
        text.AppendLine("  parts | part [<name> | parent | child | next | previous | none]   the parts of the subject, and the active one");
        text.AppendLine("  tools | tool [<name> | none] | aspects | prefabs");
        text.AppendLine("  macro list | record <name> | stop | run <name> | show <name> | save <name> <line>; <line> | delete <name>");
        text.AppendLine("  inspectors | show <id> | hide <id> | layout reset");
        text.AppendLine("  actions                     list the actions; type an id to run it");
        text.AppendLine("  log | clear | close");

        var actions = AllActions().ToList();
        if (actions.Count > 0) text.AppendLine("Actions: " + string.Join(", ", actions.Select(a => a.Id)));
        return text.ToString().TrimEnd();
    }

    private string Get(IReadOnlyList<string> args)
    {
        if (args.Count == 0) throw new InvalidOperationException("get <parameter>");

        var p = RequireParameter(args[0]);
        return $"{p.Name} = {p.Display()}";
    }

    private string? Set(string rest, IReadOnlyList<string> args)
    {
        if (args.Count == 0) throw new InvalidOperationException("set <parameter> <value>");

        var p = RequireParameter(args[0]);
        p.SetFromText(Unquote(After(rest)));
        return $"{p.Name} = {p.Display()}";
    }

    private string? AddOrRemove(bool add, string rest, IReadOnlyList<string> args)
    {
        if (args.Count < 2) throw new InvalidOperationException($"{(add ? "add" : "remove")} <parameter> <line>");

        var p = RequireParameter(args[0]);
        if (p.Kind is not (LabParameterKind.Lines or LabParameterKind.Flags))
            throw new InvalidOperationException($"{p.Name} is one value; use set.");

        string line    = Unquote(After(rest));
        var    current = p.Lines.ToList();
        if (add)
        {
            if (p.Kind == LabParameterKind.Flags)
                line = p.Choices.FirstOrDefault(c => c.Equals(line, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{line}' is not one of: {string.Join(", ", p.Choices)}.");

            current.Add(line);
        }
        else if (current.RemoveAll(l => l.Equals(line, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            throw new InvalidOperationException($"'{line}' is not in {p.Name}.");
        }

        p.Value = current;
        return $"{p.Name} = {Shorten(p.Display())}";
    }

    private string? Reset(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            Parameters.ResetToDefaults();
            return "All parameters are back to their defaults.";
        }

        var p = RequireParameter(args[0]);
        p.Value = p.Default;
        return $"{p.Name} = {Shorten(p.Display())}";
    }

    private string? Preset(IReadOnlyList<string> args)
    {
        string verb = args.Count > 0 ? args[0].ToLowerInvariant() : "list";
        string name = args.Count > 1 ? string.Join(' ', args.Skip(1)) : string.Empty;

        switch (verb)
        {
            case "list":
                var names = Parameters.PresetNames();
                return names.Count == 0 ? "No presets yet. preset save <name> keeps the parameters as they are." : string.Join(Environment.NewLine, names);
            case "save" when name.Length > 0:
                Parameters.SavePreset(name);
                return $"Saved preset '{name}'.";
            case "load" when name.Length > 0:
                return Parameters.LoadPreset(name) ? $"Loaded preset '{name}'." : throw new InvalidOperationException($"There is no preset '{name}'.");
            case "delete" when name.Length > 0:
                return Parameters.DeletePreset(name) ? $"Deleted preset '{name}'." : throw new InvalidOperationException($"There is no preset '{name}'.");
            default:
                throw new InvalidOperationException("preset list | save <name> | load <name> | delete <name>");
        }
    }

    private string? ShowOrHide(bool show, IReadOnlyList<string> args)
    {
        if (args.Count == 0) throw new InvalidOperationException($"{(show ? "show" : "hide")} <inspector>  (see inspectors)");

        string id = _window.InspectorIds.FirstOrDefault(i => i.Equals(args[0], StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"There is no inspector '{args[0]}'. See inspectors.");

        if (show) _window.ShowInspector(id);
        else _window.HideInspector(id);
        return null;
    }

    private LabParameter RequireParameter(string key)
    {
        // "dom.collapse" names a parameter of another list (an aspect's, a tool's...) by the list's title
        int dot = key.IndexOf('.');
        if (dot > 0)
        {
            string wanted = Squeeze(key[..dot]);
            foreach (var (title, set) in ParameterSets())
                if (Squeeze(title) == wanted && set.Find(key[(dot + 1)..]) is { } named)
                    return named;
        }

        foreach (var (_, set) in ParameterSets())
            if (set.Find(key) is { } found)
                return found;

        throw new InvalidOperationException($"There is no parameter '{key}'. See params.");
    }

    private static string Squeeze(string text) => new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>The Lab's parameters, then the other lists that apply now (an aspect's, a tool's, the active part's).</summary>
    private IEnumerable<(string Title, LabParameterSet Set)> ParameterSets()
    {
        yield return ("Lab", Parameters);
        if (Model is not { } model) yield break;

        foreach (var source in LabModelRules.SourcesFor(model, null, LabPaneType.Static))
            if (!ReferenceEquals(source.Set, Parameters))
                yield return (source.Title, source.Set);
    }

    private string ParameterList()
    {
        var text = new StringBuilder();
        foreach (var (title, set) in ParameterSets())
        {
            bool lab = ReferenceEquals(set, Parameters);
            foreach (var p in set)
            {
                if (text.Length > 0) text.AppendLine();
                string name = lab ? p.Name : $"{Squeeze(title)}.{p.Name}";
                text.Append($"{name} = {Shorten(p.Display())}{(p.IsDefault ? string.Empty : "  *")}");
            }
        }

        return text.Length == 0 ? "This Lab has no parameters." : text.ToString();
    }

    // ---- the model: parts, tools, aspects, prefabs and macros

    private ILabModel RequireModel() => Model ?? throw new InvalidOperationException($"{_session.Lab.Name} has no parts or tools.");

    private string Parts()
    {
        var model = RequireModel();
        var all   = LabModelRules.AllParts(model).ToList();
        if (all.Count == 0) return "No parts: the subject has none right now.";

        var active = model.ActivePart;
        return string.Join(Environment.NewLine, all.Select(p => $"{(active is not null && p.Key == active.Key ? "* " : "  ")}{new string(' ', Math.Min(p.Depth, 8) * 2)}{p.Name}  [{p.Type.Name}]{(string.IsNullOrEmpty(p.Detail) ? string.Empty : "  " + Shorten(p.Detail))}"));
    }

    private string? Part(string rest)
    {
        var    model = RequireModel();
        string word  = Unquote(rest.Trim());
        var    all   = LabModelRules.AllParts(model).ToList();
        var    now   = model.ActivePart;
        int    at    = now is null ? -1 : all.FindIndex(p => p.Key == now.Key);

        switch (word.ToLowerInvariant())
        {
            case "":
                return now is null ? "(no part)" : $"{now.Path}  [{now.Type.Name}]";
            case "none":
                model.ActivatePart(null);
                return null;
            case "parent":
                model.ActivatePart(now?.Parent ?? throw new InvalidOperationException("The active part has no parent."));
                return null;
            case "child":
                model.ActivatePart(now is { Children.Count: > 0 } ? now.Children[0] : throw new InvalidOperationException("The active part has no children."));
                return null;
            case "next":
                model.ActivatePart(at >= 0 && at < all.Count - 1 ? all[at + 1] : throw new InvalidOperationException("There is no next part."));
                return null;
            case "previous":
                model.ActivatePart(at > 0 ? all[at - 1] : throw new InvalidOperationException("There is no previous part."));
                return null;
        }

        var part = LabModelRules.FindPart(model, word) ?? throw new InvalidOperationException($"There is no part '{word}'. See parts.");
        model.ActivatePart(part);
        return null;
    }

    private string Tools()
    {
        var model     = RequireModel();
        var available = LabModelRules.AvailableTools(model).ToList();
        if (model.Tools.Count == 0) return "No tools.";

        return string.Join(Environment.NewLine, model.Tools.Select(t => $"{(ReferenceEquals(t, model.ActiveTool) ? "* " : "  ")}{t.Id,-16} {Strip(t.Name)}{(available.Contains(t) ? string.Empty : "  (not now)")}"));
    }

    private string? Tool(string rest)
    {
        var    model = RequireModel();
        string word  = Unquote(rest.Trim());
        if (word.Length == 0) return model.ActiveTool is { } active ? Strip(active.Name) : "(no tool)";

        if (word.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            model.ActivateTool(null);
            return null;
        }

        var tool = LabModelRules.FindTool(model, word) ?? throw new InvalidOperationException($"There is no tool '{word}'. See tools.");
        if (!LabModelRules.AvailableTools(model).Contains(tool)) throw new InvalidOperationException($"{Strip(tool.Name)} is not available right now.");

        model.ActivateTool(tool);
        return null;
    }

    private string Aspects()
    {
        var model = RequireModel();
        if (model.Aspects.Count == 0) return "No aspects.";

        return string.Join(Environment.NewLine, model.Aspects.Select(a => $"{a.Id,-16} {a.Title}  ({a.Owner}){(LabModelRules.IsActive(model, a.Owner) ? "  active" : string.Empty)}"));
    }

    private string Prefabs()
    {
        var model = RequireModel();
        var lines = new List<string>();
        foreach (var type in model.SubjectTypes)
        {
            lines.AddRange(type.PrefabList.Select(p => $"{type.Name}: {p.Name}  (prefab)"));
            lines.AddRange(type.TemplateList.Select(t => $"{type.Name}: {t.Name}  (template: {string.Join(", ", t.Placeholders.Select(p => p.Name))})"));
        }

        foreach (var type in LabModelRules.AddableTypes(model, model.ActivePart))
        {
            lines.AddRange(type.PrefabList.Select(p => $"add {type.Name}: {p.Name}  (prefab)"));
            lines.AddRange(type.TemplateList.Select(t => $"add {type.Name}: {t.Name}  (template: {string.Join(", ", t.Placeholders.Select(p => p.Name))})"));
        }

        return lines.Count == 0 ? "No prefabs or templates." : string.Join(Environment.NewLine, lines);
    }

    private async Task<string?> MacroAsync(string rest, IReadOnlyList<string> args)
    {
        string verb = args.Count > 0 ? args[0].ToLowerInvariant() : "list";
        string name = args.Count > 1 ? args[1] : string.Empty;
        string id   = _session.Lab.Id;

        switch (verb)
        {
            case "list":
                var names = LabProfile.MacroNames(id);
                return names.Count == 0 ? "No macros yet. macro record <name>, do things, macro stop." : string.Join(Environment.NewLine, names.Select(n => $"{n}  ({LabProfile.GetMacro(id, n)?.Count ?? 0} steps)"));
            case "record" when name.Length > 0:
                _window.StartRecording(name);
                return $"Recording '{name}'. macro stop keeps it.";
            case "stop":
                string? recording = _window.RecordingName;
                int     steps     = _window.StopRecording();
                return $"Saved macro '{recording}' ({steps} step(s)).";
            case "run" when name.Length > 0:
                await _window.RunMacroAsync(name);
                return null;
            case "show" when name.Length > 0:
                return string.Join(Environment.NewLine, _window.MacroLines(name) ?? throw new InvalidOperationException($"There is no macro '{name}'."));
            case "save" when name.Length > 0:
                int    at    = rest.IndexOf(name, StringComparison.Ordinal);
                string after = at < 0 ? string.Empty : rest[(at + name.Length)..].Trim();
                var    lines = after.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length == 0) throw new InvalidOperationException("macro save <name> <line>; <line>");

                LabProfile.SaveMacro(id, name, lines);
                return $"Saved macro '{name}' ({lines.Length} step(s)).";
            case "delete" when name.Length > 0:
                return LabProfile.DeleteMacro(id, name) ? $"Deleted macro '{name}'." : throw new InvalidOperationException($"There is no macro '{name}'.");
            default:
                throw new InvalidOperationException("macro list | record <name> | stop | run <name> | show <name> | save <name> <line>; <line> | delete <name>");
        }
    }

    private IEnumerable<ProvidedCommand> AllActions()
    {
        return LabMenus.All(_session);
    }

    private ProvidedCommand? FindAction(string word)
    {
        string squeezed = new string(word.Where(char.IsLetterOrDigit).ToArray());
        return AllActions().FirstOrDefault(a => a.Id.Equals(word, StringComparison.OrdinalIgnoreCase)
                                                || new string(Strip(a.Header).Where(char.IsLetterOrDigit).ToArray()).Equals(squeezed, StringComparison.OrdinalIgnoreCase));
    }

    private static string Describe(ProvidedCommand a)
    {
        return $"{a.Id,-16} {Strip(a.Header)}{(a.Command.CanExecute(a.Parameter) ? string.Empty : "  (not now)")}";
    }

    internal static string Strip(string header) => header.Replace("_", string.Empty);

    private static string After(string rest)
    {
        int space = rest.IndexOfAny([' ', '\t']);
        return space < 0 ? string.Empty : rest[space..].Trim();
    }

    private static string Unquote(string text)
    {
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
    }

    private static string Shorten(string text)
    {
        return text.Length <= 160 ? text : text[..160] + "...";
    }
}
