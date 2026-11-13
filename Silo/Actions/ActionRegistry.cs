using Silo.Providers.Adapter;

namespace Silo.Actions;

/// <summary>One named argument of a <see cref="ISiloAction" />.</summary>
public sealed record ActionParameter(string Name, Type Type, bool Required = true, object? Default = null);

/// <summary>
///     A command that can be invoked by name, from a script, a Flow, an event handler or outside Silo (<c>silo.exe -action</c>,
///     an IPC <c>RunAction</c>). Not the same as a menu command (<c>ProvidedCommand</c>): an action takes named arguments and
///     returns a value.
/// </summary>
public interface ISiloAction
{
    /// <summary>The dotted name, such as <c>App.SaveAll</c>.</summary>
    string Name { get; }

    string? Description { get; }

    IReadOnlyList<ActionParameter> Parameters { get; }

    /// <summary>True when the action can be invoked from outside Silo.</summary>
    bool IsRegistered { get; }

    /// <summary>Runs the action. <paramref name="args" /> have already been checked and converted by <see cref="ActionRegistry" />.</summary>
    Task<object?> InvokeAsync(IReadOnlyDictionary<string, object?> args, CancellationToken ct = default);
}

/// <summary>An <see cref="ISiloAction" /> that runs a delegate.</summary>
public sealed class DelegateAction(string                                                                         name,
                                   Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<object?>> run,
                                   IEnumerable<ActionParameter>?                                                  parameters   = null,
                                   string?                                                                        description  = null,
                                   bool                                                                           isRegistered = false)
    : ISiloAction
{
    public string                        Name         { get; } = name;
    public string?                       Description  { get; } = description;
    public IReadOnlyList<ActionParameter> Parameters   { get; } = parameters?.ToList() ?? [];
    public bool                          IsRegistered { get; } = isRegistered;

    public Task<object?> InvokeAsync(IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        return run(args, ct);
    }

    /// <summary>A synchronous action.</summary>
    public static DelegateAction Of(string name, Func<IReadOnlyDictionary<string, object?>, object?> run,
                                    IEnumerable<ActionParameter>? parameters = null, string? description = null, bool isRegistered = false)
    {
        return new DelegateAction(name, (a, _) => Task.FromResult(run(a)), parameters, description, isRegistered);
    }
}

public sealed class ActionNotFoundException(string name) : Exception($"There is no action named '{name}'.");

public sealed class ActionArgumentException(string message) : ArgumentException(message);

/// <summary>
///     The actions that can be invoked by name (names are case-insensitive). <see cref="InvokeAsync" /> checks the arguments
///     against the action's parameters first: missing required ones fail, defaults fill in, unknown names fail, and a string
///     given for a non-string parameter is converted (<c>"5"</c> for an <c>int</c>).
/// </summary>
public sealed class ActionRegistry
{
    private readonly Dictionary<string, ISiloAction> _actions = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<ISiloAction> All => _actions.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The actions that can be invoked from outside Silo.</summary>
    public IEnumerable<ISiloAction> Registered => All.Where(a => a.IsRegistered);

    /// <summary>Adds or replaces the action with this name.</summary>
    public void Register(ISiloAction action)
    {
        _actions[action.Name] = action;
    }

    public bool TryGet(string name, out ISiloAction action)
    {
        return _actions.TryGetValue(name, out action!);
    }

    public Task<object?> InvokeAsync(string name, IReadOnlyDictionary<string, object?>? args = null, CancellationToken ct = default)
    {
        if (!TryGet(name, out var action)) throw new ActionNotFoundException(name);
        return action.InvokeAsync(Bind(action, args ?? new Dictionary<string, object?>()), ct);
    }

    /// <summary>Like <see cref="InvokeAsync" /> but only for an action marked as registered (what a caller outside Silo may run).</summary>
    public Task<object?> InvokeExternalAsync(string name, IReadOnlyDictionary<string, object?>? args = null, CancellationToken ct = default)
    {
        if (!TryGet(name, out var action) || !action.IsRegistered) throw new ActionNotFoundException(name);
        return InvokeAsync(name, args, ct);
    }

    private static IReadOnlyDictionary<string, object?> Bind(ISiloAction action, IReadOnlyDictionary<string, object?> args)
    {
        var bound = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in args.Keys)
            if (action.Parameters.All(p => !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ActionArgumentException($"{action.Name} has no parameter '{name}'.");

        foreach (var p in action.Parameters)
        {
            var key = args.Keys.FirstOrDefault(k => k.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (key is null)
            {
                if (p.Required && p.Default is null)
                    throw new ActionArgumentException($"{action.Name} needs '{p.Name}'.");
                bound[p.Name] = p.Default;
                continue;
            }

            bound[p.Name] = Coerce(action, p, args[key]);
        }

        return bound;
    }

    private static object? Coerce(ISiloAction action, ActionParameter p, object? value)
    {
        var target = Nullable.GetUnderlyingType(p.Type) ?? p.Type;
        if (value is null)
            return !p.Required || !target.IsValueType || Nullable.GetUnderlyingType(p.Type) is not null
                       ? null
                       : throw new ActionArgumentException($"{action.Name}: '{p.Name}' can't be null.");
        if (target.IsInstanceOfType(value)) return value;
        try
        {
            var basic = new BasicAdapter { Target = target, ThrowErrors = true };
            if (value is string s && basic.ConversionsFor(typeof(string)).Contains(target))
                return basic.ConvertAsync(s).GetAwaiter().GetResult();
            if (target.IsEnum && value is string name) return Enum.Parse(target, name, true);
            return Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new ActionArgumentException($"{action.Name}: '{p.Name}' must be a {target.Name}, not {value.GetType().Name}.");
        }
    }
}
