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
        "help", "actions", "value", "open", "go", "params", "get", "set", "add", "remove", "clear", "reset", "preset", "inspectors", "show", "hide", "layout", "log", "close"
    ];

    private readonly ILabSession _session;
    private readonly LabWindow   _window;

    public LabTerminal(LabWindow window, ILabSession session)
    {
        _window  = window;
        _session = session;
    }

    private LabParameterSet Parameters => _session.Lab.Parameters;

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
        if (line.Length == 0) return null;

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
                return Parameters.Count == 0 ? "This Lab has no parameters." : string.Join(Environment.NewLine, Parameters.Select(p => $"{p.Name} = {Shorten(p.Display())}{(p.IsDefault ? string.Empty : "  *")}"));
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
        return Parameters.Find(key) ?? throw new InvalidOperationException($"There is no parameter '{key}'. See params.");
    }

    private IEnumerable<ProvidedCommand> AllActions()
    {
        return _session.GlobalActions.Concat(_session.ContextActions).Concat(_session.ValueActions);
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
