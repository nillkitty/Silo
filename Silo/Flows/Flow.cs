using Silo.Actions;
using Silo.Contracts.Adapters;

namespace Silo.Flows;

/// <summary>
///     What a Flow's steps share for one run: the value moving through (<see cref="Current" />), named
///     <see cref="Variables" />, the <see cref="Actions" /> a step may invoke, and the <see cref="Trace" /> of steps taken.
/// </summary>
public sealed class FlowContext(ActionRegistry? actions = null, CancellationToken ct = default)
{
    /// <summary>The value being passed from step to step: a source sets it, an adapter converts it, and so on.</summary>
    public object? Current { get; set; }

    public Dictionary<string, object?> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ActionRegistry Actions { get; } = actions ?? new ActionRegistry();

    public CancellationToken CancellationToken { get; } = ct;

    public List<string> Trace { get; } = [];
}

/// <summary>What a step tells the Flow to do next.</summary>
public readonly record struct StepOutcome(StepFlow Flow, string? Label = null)
{
    public static readonly StepOutcome Next = new(StepFlow.Next);
    public static readonly StepOutcome Stop = new(StepFlow.Stop);

    public static StepOutcome JumpTo(string label)
    {
        return new StepOutcome(StepFlow.Jump, label);
    }
}

public enum StepFlow
{
    /// <summary>Go on to the following step.</summary>
    Next,

    /// <summary>Go to the step with <see cref="StepOutcome.Label" />.</summary>
    Jump,

    /// <summary>End the run; the current value is the result.</summary>
    Stop
}

/// <summary>One thing a Flow does. A step may carry a <see cref="Label" /> so others can jump to it.</summary>
public abstract class FlowStep
{
    public string? Label { get; init; }

    public abstract string Describe();

    public abstract Task<StepOutcome> RunAsync(FlowContext context);

    /// <summary>Called once when the Flow is first run, before any item (adapters are set up once, not per item).</summary>
    public virtual void Attach(FlowContext context)
    {
    }
}

/// <summary>Entry point for data: sets the current value.</summary>
public sealed class SourceStep(Func<FlowContext, Task<object?>> get, string description = "Source") : FlowStep
{
    public SourceStep(Func<FlowContext, object?> get, string description = "Source") : this(c => Task.FromResult(get(c)), description)
    {
    }

    public override string Describe() => description;

    public override async Task<StepOutcome> RunAsync(FlowContext context)
    {
        context.Current = await get(context);
        return StepOutcome.Next;
    }
}

/// <summary>
///     Converts the current value with an <see cref="IAdapter" />. A <see cref="IAdapter.Pure" /> adapter's results are
///     remembered, so a Flow that sees the same input again doesn't convert it again.
/// </summary>
public sealed class AdapterStep(IAdapter adapter, Type? target = null) : FlowStep
{
    private readonly Dictionary<object, object?> _cache = [];

    public override string Describe() => $"Adapter {adapter.GetType().Name}" + (target is null ? "" : $" → {target.Name}");

    public override void Attach(FlowContext context)
    {
        if (adapter is IFlowAdapter f) f.AttachToFlow(context);
    }

    public override async Task<StepOutcome> RunAsync(FlowContext context)
    {
        var input = context.Current ?? throw new InvalidOperationException("There is no value to adapt.");
        if (target is not null && adapter is Providers.Adapter.AdapterBase b) b.Target = target;
        if (adapter.Pure && _cache.TryGetValue(input, out var hit))
        {
            context.Current = hit;
            return StepOutcome.Next;
        }

        var result = await adapter.ConvertAsync(input);
        if (adapter.Pure) _cache[input] = result;
        context.Current = result;
        return StepOutcome.Next;
    }
}

/// <summary>Evaluates a condition on the context and jumps to one label when true and optionally another when false.</summary>
public sealed class ConditionStep(Func<FlowContext, bool> test, string? whenTrue, string? whenFalse = null, string description = "If")
    : FlowStep
{
    public override string Describe() => description;

    public override Task<StepOutcome> RunAsync(FlowContext context)
    {
        var label = test(context) ? whenTrue : whenFalse;
        return Task.FromResult(label is null ? StepOutcome.Next : StepOutcome.JumpTo(label));
    }
}

/// <summary>Jumps to a label unconditionally (loops are possible; the Flow's step limit ends a runaway one).</summary>
public sealed class JumpStep(string label) : FlowStep
{
    public override string Describe() => $"Jump {label}";

    public override Task<StepOutcome> RunAsync(FlowContext context) => Task.FromResult(StepOutcome.JumpTo(label));
}

/// <summary>Ends the run; the current value is the result.</summary>
public sealed class StopStep : FlowStep
{
    public override string Describe() => "Stop";

    public override Task<StepOutcome> RunAsync(FlowContext context) => Task.FromResult(StepOutcome.Stop);
}

/// <summary>
///     Invokes a named action. A string argument that is exactly <c>$current</c> passes the current value, and
///     <c>$name</c> a variable. The action's result becomes the current value unless <paramref name="keepCurrent" />.
/// </summary>
public sealed class ActionStep(string action, IReadOnlyDictionary<string, object?>? args = null, bool keepCurrent = false) : FlowStep
{
    public override string Describe() => $"Action {action}";

    public override async Task<StepOutcome> RunAsync(FlowContext context)
    {
        var bound = (args ?? new Dictionary<string, object?>())
           .ToDictionary(kv => kv.Key, kv => Resolve(kv.Value, context), StringComparer.OrdinalIgnoreCase);
        var result = await context.Actions.InvokeAsync(action, bound, context.CancellationToken);
        if (!keepCurrent) context.Current = result;
        return StepOutcome.Next;
    }

    private static object? Resolve(object? value, FlowContext context)
    {
        if (value is not string s || s.Length < 2 || s[0] != '$') return value;
        if (s.Equals("$current", StringComparison.OrdinalIgnoreCase)) return context.Current;
        return context.Variables.TryGetValue(s[1..], out var v) ? v : value;
    }
}

/// <summary>
///     Runs a script in a fresh in-process context (see <see cref="IInProcScriptHost" />). The script sees the current value
///     as <c>input</c>; what it evaluates to becomes the current value.
/// </summary>
public sealed class ScriptStep(IInProcScriptHost host, string script, string language = "script") : FlowStep
{
    public override string Describe() => $"Script ({language})";

    public override async Task<StepOutcome> RunAsync(FlowContext context)
    {
        var scriptContext = host.CreateContext();
        scriptContext.AddVariable("input", context.Current!);
        var result = await scriptContext.RunAsync(script);
        if (result.IsFailed)
            throw new InvalidOperationException(result.IsTimeout
                                                    ? "The script timed out."
                                                    : (result as Providers.Script.ScriptResult)?.Errors.FirstOrDefault() ?? "The script failed.");
        if (result is Providers.Script.ScriptResult { Value: { } value }) context.Current = value;
        return StepOutcome.Next;
    }
}

public enum FlowStatus
{
    Completed,
    Stopped,
    Failed
}

/// <summary>How one run of a Flow ended.</summary>
public sealed record FlowResult(FlowStatus Status, object? Output, Exception? Error, IReadOnlyList<string> Trace)
{
    public bool Succeeded => Status != FlowStatus.Failed;
}

/// <summary>
///     A general-purpose workflow: steps run in order on a value, and conditions and jumps change the order. Create a
///     Flow once and <see cref="RunAsync" /> it on as many inputs as you like -- adapters are attached once and keep any
///     caching between runs.
/// </summary>
public sealed class Flow(string name, IEnumerable<FlowStep> steps)
{
    private bool _attached;

    public string Name { get; } = name;

    public IReadOnlyList<FlowStep> Steps { get; } = steps.ToList();

    /// <summary>Most steps one run may take; a Flow that goes on longer fails (a loop that never ends, say).</summary>
    public int MaxSteps { get; init; } = 10_000;

    public async Task<FlowResult> RunAsync(object? input = null, ActionRegistry? actions = null, CancellationToken ct = default)
    {
        var context = new FlowContext(actions, ct) { Current = input };
        try
        {
            if (!_attached)
            {
                foreach (var step in Steps) step.Attach(context);
                _attached = true;
            }

            var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < Steps.Count; i++)
                if (Steps[i].Label is { } l && !labels.TryAdd(l, i))
                    throw new InvalidOperationException($"Two steps in '{Name}' are labelled '{l}'.");

            var taken = 0;
            var at    = 0;
            while (at < Steps.Count)
            {
                ct.ThrowIfCancellationRequested();
                if (++taken > MaxSteps) throw new InvalidOperationException($"'{Name}' took more than {MaxSteps} steps.");
                var step = Steps[at];
                context.Trace.Add(step.Describe());
                var outcome = await step.RunAsync(context);
                switch (outcome.Flow)
                {
                    case StepFlow.Stop:
                        return new FlowResult(FlowStatus.Stopped, context.Current, null, context.Trace);
                    case StepFlow.Jump:
                        at = labels.TryGetValue(outcome.Label!, out var to)
                                 ? to
                                 : throw new InvalidOperationException($"'{Name}' has no step labelled '{outcome.Label}'.");
                        break;
                    default:
                        at++;
                        break;
                }
            }

            return new FlowResult(FlowStatus.Completed, context.Current, null, context.Trace);
        }
        catch (Exception ex)
        {
            return new FlowResult(FlowStatus.Failed, context.Current, ex, context.Trace);
        }
    }

    /// <summary>Runs the Flow on each input in turn, one run per item.</summary>
    public async IAsyncEnumerable<FlowResult> RunAllAsync(IEnumerable<object?> inputs, ActionRegistry? actions = null,
                                                          [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var input in inputs)
            yield return await RunAsync(input, actions, ct);
    }
}
