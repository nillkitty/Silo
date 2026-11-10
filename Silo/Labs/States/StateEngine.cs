using System.Globalization;
using System.Text;

namespace Silo.Labs.States;

/// <summary>What the machine says about one transition from where it is: whether it can be taken, and if not, why.</summary>
public sealed record StateTransitionStatus(StateTransitionDef Transition, bool Enabled, string? Reason);

/// <summary>What taking a transition (or updating the machine) came to.</summary>
public sealed record StateStep(bool Done, string Message, StateTransitionDef? Transition = null);

/// <summary>Reading and showing the values of fields.</summary>
public static class StateValues
{
    public static object DefaultOf(StateFieldKind kind) => kind switch { StateFieldKind.Number => 0d, StateFieldKind.Bool => false, _ => string.Empty };

    public static bool TryParse(StateFieldKind kind, string? text, out object value, out string? error)
    {
        text = text?.Trim() ?? string.Empty;
        error = null;
        switch (kind)
        {
            case StateFieldKind.Number when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d):
                value = d;
                return true;
            case StateFieldKind.Number:
                error = $"'{text}' is not a number";
                break;
            case StateFieldKind.Bool when bool.TryParse(text, out bool b):
                value = b;
                return true;
            case StateFieldKind.Bool:
                error = $"'{text}' is not true or false";
                break;
            default:
                value = text;
                return true;
        }

        value = DefaultOf(kind);
        return false;
    }

    public static string Show(object? value) => value switch
    {
        null      => string.Empty,
        double d  => d.ToString("0.######", CultureInfo.InvariantCulture),
        bool b    => b ? "true" : "false",
        _         => value.ToString() ?? string.Empty
    };
}

/// <summary>
///     One run of a state machine: where it is, what its fields hold, and what has happened. A run follows Telefrag.State's rules: a transition can
///     be taken from its From state (or from any, when it says so) when all its constraints hold; of those that can, the one with the highest priority
///     (then the one that leads to the state with the higher priority, then the first drawn) is what an update takes; taking it runs its effects in order.
/// </summary>
public sealed class StateRun
{
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string>               _log    = [];

    public StateRun(StateDoc doc)
    {
        Doc = doc;
        Reset();
    }

    public StateDoc Doc { get; private set; }

    /// <summary>Where the machine is; null when the machine has no initial state.</summary>
    public string? Current { get; private set; }

    public IReadOnlyDictionary<string, object> Values => _values;

    public IReadOnlyList<string> Log => _log;

    /// <summary>How many transitions have been taken.</summary>
    public int Steps { get; private set; }

    /// <summary>The state that was left by the last transition, for showing it.</summary>
    public string? Previous { get; private set; }

    /// <summary>The transition that was taken last.</summary>
    public StateTransitionDef? LastTransition { get; private set; }

    /// <summary>Starts again from the initial state with the default values.</summary>
    public void Reset()
    {
        _values.Clear();
        _log.Clear();
        foreach (var field in Doc.Fields) _values[field.Name] = DefaultValue(field);
        Steps          = 0;
        Previous       = null;
        LastTransition = null;
        Current        = Doc.Find(Doc.Initial)?.Name;
        _log.Add(Current is null ? "No initial state: choose one" : $"Started in {Current}");
    }

    /// <summary>Looks at the machine again after it was edited, keeping the run (where it is and the values) as far as it still makes sense.</summary>
    public void Sync(StateDoc doc)
    {
        Doc = doc;
        foreach (string gone in _values.Keys.Where(k => doc.FindField(k) is null).ToList()) _values.Remove(gone);
        foreach (var field in doc.Fields)
        {
            bool fits = _values.TryGetValue(field.Name, out var v) && Fits(field.Kind, v);
            if (!fits) _values[field.Name] = DefaultValue(field);
        }

        if (Current is not null) Current = doc.Find(Current)?.Name;
        LastTransition = LastTransition is null ? null : doc.Transitions.FirstOrDefault(t => t.Id == LastTransition.Id);
    }

    private static bool Fits(StateFieldKind kind, object value) => kind switch { StateFieldKind.Number => value is double, StateFieldKind.Bool => value is bool, _ => value is string };

    private static object DefaultValue(StateFieldDef field) => StateValues.TryParse(field.Kind, field.Default, out var v, out _) ? v : StateValues.DefaultOf(field.Kind);

    /// <summary>Puts a value in a field; returns what is wrong with it, or null.</summary>
    public string? SetField(string name, string text)
    {
        if (Doc.FindField(name) is not { } field) return $"There is no field '{name}'";
        if (!StateValues.TryParse(field.Kind, text, out var value, out string? error)) return error;

        _values[field.Name] = value;
        return null;
    }

    public void SetState(string name)
    {
        if (Doc.Find(name) is not { } state) return;

        Previous = Current;
        Current  = state.Name;
        _log.Add($"Moved to {state.Name} by hand");
    }

    /// <summary>Every transition that leaves the state the machine is in, in the order an update would try them, with whether each can be taken.</summary>
    public IReadOnlyList<StateTransitionStatus> Candidates()
    {
        if (Current is null) return [];

        return Doc.Transitions.Where(t => t.FromAny || string.Equals(t.From, Current, StringComparison.OrdinalIgnoreCase))
                  .Select((t, i) => (t, i))
                  .OrderByDescending(x => x.t.Priority)
                  .ThenByDescending(x => Doc.Find(x.t.To)?.Priority ?? 0)
                  .ThenBy(x => x.i)
                  .Select(x => Status(x.t))
                  .ToList();
    }

    private StateTransitionStatus Status(StateTransitionDef transition)
    {
        if (Doc.Find(transition.To) is null) return new StateTransitionStatus(transition, false, transition.To.Length == 0 ? "leads nowhere" : $"leads to '{transition.To}', which does not exist");

        foreach (var constraint in transition.Constraints)
        {
            var (holds, why) = Check(constraint);
            if (!holds) return new StateTransitionStatus(transition, false, why);
        }

        return new StateTransitionStatus(transition, true, null);
    }

    /// <summary>Whether a constraint holds now, and if it does not, in words why.</summary>
    public (bool Holds, string? Why) Check(StateConstraintDef constraint)
    {
        string text = $"{constraint.Field} {constraint.Op} {constraint.Value}";
        if (Doc.FindField(constraint.Field) is not { } field || !_values.TryGetValue(field.Name, out var actual)) return (false, $"needs {text}: there is no field '{constraint.Field}'");
        if (!StateValues.TryParse(field.Kind, constraint.Value, out var expected, out string? error)) return (false, $"needs {text}: {error}");

        bool holds = Compare(actual, expected, constraint.Op, out string? problem);
        return holds ? (true, null) : (false, problem ?? $"needs {text} ({field.Name} is {StateValues.Show(actual)})");
    }

    private static bool Compare(object actual, object expected, string op, out string? problem)
    {
        problem = null;
        if (op == "contains") return StateValues.Show(actual).Contains(StateValues.Show(expected), StringComparison.OrdinalIgnoreCase);

        int order;
        switch (actual)
        {
            case double a when expected is double e:
                order = a.CompareTo(e);
                break;
            case bool a when expected is bool e:
                order = a.CompareTo(e);
                if (op is not ("==" or "!="))
                {
                    problem = "a bool can only be compared with == and !=";
                    return false;
                }

                break;
            default:
                order = string.Compare(StateValues.Show(actual), StateValues.Show(expected), StringComparison.Ordinal);
                break;
        }

        return op switch
        {
            "=="  => order == 0,
            "!="  => order != 0,
            "<"   => order < 0,
            "<="  => order <= 0,
            ">"   => order > 0,
            ">="  => order >= 0,
            _     => Unknown(op, out problem)
        };
    }

    private static bool Unknown(string op, out string? problem)
    {
        problem = $"'{op}' is not a comparison";
        return false;
    }

    /// <summary>Takes a transition if it can be taken from where the machine is.</summary>
    public StateStep Fire(StateTransitionDef transition)
    {
        if (Current is null) return new StateStep(false, "The machine is not in a state: it has no initial state");
        if (!transition.FromAny && !string.Equals(transition.From, Current, StringComparison.OrdinalIgnoreCase)) return new StateStep(false, $"{transition} cannot be taken from {Current}", transition);

        var status = Status(transition);
        if (!status.Enabled) return new StateStep(false, $"{transition} is blocked: {status.Reason}", transition);

        string from = Current;
        foreach (var effect in transition.Effects)
            if (RunEffect(effect) is { } failed)
                _log.Add("  ! " + failed);

        Previous       = from;
        Current        = Doc.Find(transition.To)!.Name;
        LastTransition = transition;
        Steps++;
        string message = $"{from} → {Current}";
        _log.Add(message);
        return new StateStep(true, message, transition);
    }

    /// <summary>Takes the transition an update would: the first that can be taken.</summary>
    public StateStep Update()
    {
        if (Current is null) return new StateStep(false, "The machine is not in a state: it has no initial state");
        if (Candidates().FirstOrDefault(c => c.Enabled) is not { } next)
        {
            string message = $"Nothing leaves {Current} right now";
            _log.Add(message);
            return new StateStep(false, message);
        }

        return Fire(next.Transition);
    }

    /// <summary>Updates until nothing can be taken or <paramref name="limit" /> steps were taken (a machine can loop for ever).</summary>
    public IReadOnlyList<StateStep> RunToRest(int limit = 50)
    {
        var steps = new List<StateStep>();
        while (steps.Count < limit && Update() is { Done: true } step) steps.Add(step);
        if (steps.Count == limit) _log.Add($"  ! stopped after {limit} steps");
        return steps;
    }

    private string? RunEffect(StateEffectDef effect)
    {
        if (effect.Kind == StateEffectKind.Log)
        {
            _log.Add("  " + Interpolate(effect.Value));
            return null;
        }

        if (Doc.FindField(effect.Field) is not { } field) return $"{effect.Kind} {effect.Field}: there is no such field";

        switch (effect.Kind)
        {
            case StateEffectKind.Set:
                if (!StateValues.TryParse(field.Kind, Interpolate(effect.Value), out var value, out string? error)) return $"Set {field.Name}: {error}";

                _values[field.Name] = value;
                _log.Add($"  {field.Name} = {StateValues.Show(value)}");
                return null;
            case StateEffectKind.Add:
                if (field.Kind != StateFieldKind.Number) return $"Add {field.Name}: it is not a number";
                if (!StateValues.TryParse(StateFieldKind.Number, Interpolate(effect.Value), out var amount, out error)) return $"Add {field.Name}: {error}";

                _values[field.Name] = (double)_values[field.Name] + (double)amount;
                _log.Add($"  {field.Name} = {StateValues.Show(_values[field.Name])}");
                return null;
            case StateEffectKind.Toggle:
                if (field.Kind != StateFieldKind.Bool) return $"Toggle {field.Name}: it is not a bool";

                _values[field.Name] = !(bool)_values[field.Name];
                _log.Add($"  {field.Name} = {StateValues.Show(_values[field.Name])}");
                return null;
            default:
                return null;
        }
    }

    /// <summary>Text with each {Field} replaced by what the field holds.</summary>
    private string Interpolate(string text)
    {
        if (!text.Contains('{')) return text;

        var result = new StringBuilder(text);
        foreach (var (name, value) in _values) result.Replace("{" + name + "}", StateValues.Show(value));
        return result.ToString();
    }
}

/// <summary>What is wrong, or worth a look, in a state machine.</summary>
public sealed record StateIssue(LabProblemSeverity Severity, string Message, string? Where = null);

/// <summary>Checks a machine the way Telefrag's Switch validates itself, and a bit more.</summary>
public static class StateCheck
{
    public static IReadOnlyList<StateIssue> Problems(StateDoc doc)
    {
        var found = new List<StateIssue>();

        foreach (var group in doc.States.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0 && g.Count() > 1))
            found.Add(new StateIssue(LabProblemSeverity.Error, $"Two states are called '{group.Key}'", group.Key));
        foreach (var s in doc.States.Where(s => string.IsNullOrWhiteSpace(s.Name))) found.Add(new StateIssue(LabProblemSeverity.Error, "A state has no name"));
        foreach (var group in doc.Fields.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0 && g.Count() > 1))
            found.Add(new StateIssue(LabProblemSeverity.Error, $"Two fields are called '{group.Key}'", group.Key));
        foreach (var f in doc.Fields)
        {
            if (string.IsNullOrWhiteSpace(f.Name)) found.Add(new StateIssue(LabProblemSeverity.Error, "A field has no name"));
            else if (!StateValues.TryParse(f.Kind, f.Default, out _, out string? error)) found.Add(new StateIssue(LabProblemSeverity.Error, $"The default of {f.Name}: {error}", f.Name));
        }

        if (doc.States.Count == 0) found.Add(new StateIssue(LabProblemSeverity.Warning, "The machine has no states yet"));
        else if (doc.Find(doc.Initial) is null) found.Add(new StateIssue(LabProblemSeverity.Error, doc.Initial.Length == 0 ? "The machine has no initial state" : $"The initial state '{doc.Initial}' does not exist"));

        foreach (var t in doc.Transitions)
        {
            if (!t.FromAny && doc.Find(t.From) is null) found.Add(new StateIssue(LabProblemSeverity.Error, $"{t}: the From state '{t.From}' does not exist", t.Id));
            if (doc.Find(t.To) is null) found.Add(new StateIssue(LabProblemSeverity.Error, $"{t}: the To state '{t.To}' does not exist", t.Id));

            foreach (var c in t.Constraints)
            {
                if (doc.FindField(c.Field) is not { } field) found.Add(new StateIssue(LabProblemSeverity.Error, $"{t}: the constraint {c.Field} {c.Op} {c.Value} uses a field that does not exist", t.Id));
                else if (!StateValues.TryParse(field.Kind, c.Value, out _, out string? error)) found.Add(new StateIssue(LabProblemSeverity.Error, $"{t}: the constraint on {field.Name}: {error}", t.Id));
                else if (field.Kind == StateFieldKind.Bool && c.Op is not ("==" or "!=")) found.Add(new StateIssue(LabProblemSeverity.Error, $"{t}: {field.Name} is a bool, so it can only be compared with == and !=", t.Id));
            }

            foreach (var e in t.Effects.Where(e => e.Kind != StateEffectKind.Log))
            {
                if (doc.FindField(e.Field) is not { } field) found.Add(new StateIssue(LabProblemSeverity.Error, $"{t}: the {e.Kind} effect uses field '{e.Field}', which does not exist", t.Id));
                else if (e.Kind == StateEffectKind.Add && field.Kind != StateFieldKind.Number) found.Add(new StateIssue(LabProblemSeverity.Error, $"{t}: Add needs a number field, and {field.Name} is not one", t.Id));
                else if (e.Kind == StateEffectKind.Toggle && field.Kind != StateFieldKind.Bool) found.Add(new StateIssue(LabProblemSeverity.Error, $"{t}: Toggle needs a bool field, and {field.Name} is not one", t.Id));
            }
        }

        // the same way twice: only the order of the priorities says which one an update takes
        foreach (var group in doc.Transitions.Where(t => doc.Find(t.To) is not null).GroupBy(t => (t.From.ToLowerInvariant(), t.To.ToLowerInvariant())).Where(g => g.Count() > 1))
            found.Add(new StateIssue(LabProblemSeverity.Info, $"{group.First()} is drawn {group.Count()} times", group.First().Id));

        // states nothing leads to, and states nothing leaves (Telefrag's AllStateTransitionsMustBeDefined)
        if (doc.Find(doc.Initial) is not null)
        {
            var reached = Reachable(doc);
            foreach (var s in doc.States.Where(s => s.Name.Length > 0 && !reached.Contains(s.Name)))
                found.Add(new StateIssue(LabProblemSeverity.Warning, $"{s.Name} cannot be reached from {doc.Initial}", s.Name));
        }

        foreach (var s in doc.States.Where(s => s.Name.Length > 0 && !doc.Transitions.Any(t => t.FromAny || string.Equals(t.From, s.Name, StringComparison.OrdinalIgnoreCase))))
            found.Add(new StateIssue(LabProblemSeverity.Info, $"Nothing leaves {s.Name}: it is an end state", s.Name));

        return found;
    }

    /// <summary>The names of the states that can be reached from the initial state.</summary>
    public static ISet<string> Reachable(StateDoc doc)
    {
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        if (doc.Find(doc.Initial) is { } start)
        {
            seen.Add(start.Name);
            queue.Enqueue(start.Name);
        }

        // a transition from any state can be taken wherever the machine is
        var anyTargets = doc.Transitions.Where(t => t.FromAny && doc.Find(t.To) is not null).Select(t => t.To).ToList();
        while (queue.Count > 0)
        {
            string at = queue.Dequeue();
            foreach (string to in doc.Transitions.Where(t => string.Equals(t.From, at, StringComparison.OrdinalIgnoreCase)).Select(t => t.To).Concat(anyTargets))
                if (doc.Find(to) is { } state && seen.Add(state.Name))
                    queue.Enqueue(state.Name);
        }

        return seen;
    }
}

/// <summary>The machine as C# that builds it with Telefrag.State.</summary>
public static class StateCode
{
    public static string ToCSharp(StateDoc doc)
    {
        var names  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used   = new HashSet<string>();
        foreach (var s in doc.States.Where(s => s.Name.Length > 0 && !names.ContainsKey(s.Name)))
        {
            string id = Identifier(s.Name);
            for (int n = 2; !used.Add(id); n++) id = Identifier(s.Name) + n;
            names[s.Name] = id;
        }

        string type = Identifier(doc.Name.Length == 0 ? "Machine" : doc.Name) + "State";
        string Of(string state) => names.TryGetValue(state, out string? id) ? $"{type}.{id}" : $"default({type}) /* '{state}' does not exist */";

        var code = new StringBuilder();
        code.AppendLine("using Telefrag.State;");
        code.AppendLine();
        if (!string.IsNullOrWhiteSpace(doc.Description)) code.AppendLine($"// {doc.Description.ReplaceLineEndings(" ")}");
        code.AppendLine($"public enum {type}");
        code.AppendLine("{");
        foreach (string id in names.Values) code.AppendLine($"    {id},");
        code.AppendLine("}");
        code.AppendLine();
        code.AppendLine($"var machine = new Switch<{type}>(new NoScoping<{type}>(), new CachedStorage<{type}>());");
        code.AppendLine();

        foreach (var s in doc.States.Where(s => names.ContainsKey(s.Name)))
            code.AppendLine($"machine.AddState(new State<{type}> {{ StateValue = {Of(s.Name)}, Name = {Quote(s.Name)}, Priority = {s.Priority} }});");

        if (doc.Fields.Count > 0) code.AppendLine();
        foreach (var f in doc.Fields.Where(f => f.Name.Length > 0))
        {
            string value = StateValues.TryParse(f.Kind, f.Default, out var v, out _) ? v switch { string s => Quote(s), bool b => b ? "true" : "false", _ => StateValues.Show(v) } : "default";
            code.AppendLine($"machine.AddField(new Field<{type}>({Quote(f.Name)}, {value}));");
        }

        if (doc.Find(doc.Initial) is not null)
        {
            code.AppendLine();
            code.AppendLine($"machine.Transitions.Initial({Of(doc.Find(doc.Initial)!.Name)});");
        }

        foreach (var t in doc.Transitions)
        {
            code.AppendLine();
            foreach (var c in t.Constraints) code.AppendLine($"// constraint: {c.Field} {c.Op} {c.Value}");
            foreach (var e in t.Effects) code.AppendLine($"// effect: {Describe(e)}");
            string from = t.FromAny ? "FromAny = true" : $"FromStates = [{Of(t.From)}]";
            code.AppendLine($"machine.AddTransition(new Transition<{type}> {{ {from}, ToStates = [{Of(t.To)}], Priority = {t.Priority} }});");
        }

        code.AppendLine();
        code.AppendLine("machine.Commit();");
        return code.ToString();
    }

    private static string Describe(StateEffectDef e) => e.Kind switch
    {
        StateEffectKind.Set    => $"{e.Field} = {e.Value}",
        StateEffectKind.Add    => $"{e.Field} += {e.Value}",
        StateEffectKind.Toggle => $"toggle {e.Field}",
        _                      => $"log \"{e.Value}\""
    };

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Identifier(string text)
    {
        var id = new StringBuilder();
        bool upper = true;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                id.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else
            {
                upper = true;
            }
        }

        if (id.Length == 0) return "Item";

        return char.IsDigit(id[0]) ? "_" + id : id.ToString();
    }
}
