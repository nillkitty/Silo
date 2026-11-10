using Newtonsoft.Json;

namespace Silo.Labs.States;

/// <summary>The kind of value a field of a state machine holds.</summary>
public enum StateFieldKind
{
    Number,
    Text,
    Bool
}

/// <summary>What an effect does when its transition is taken.</summary>
public enum StateEffectKind
{
    /// <summary>Puts a value in a field.</summary>
    Set,

    /// <summary>Adds a number to a numeric field.</summary>
    Add,

    /// <summary>Turns a bool field around.</summary>
    Toggle,

    /// <summary>Writes a line to the log; {Field} in the text is the field's value.</summary>
    Log
}

/// <summary>A state of the machine, drawn at a place on the canvas.</summary>
public sealed class StateDef
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Which state wins when more than one is viable (Telefrag's <c>IState.Priority</c>).</summary>
    public int Priority { get; set; }

    public string? Description { get; set; }

    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>A value the machine keeps along with its state: what constraints look at and effects change.</summary>
public sealed class StateFieldDef
{
    public string Name { get; set; } = string.Empty;

    public StateFieldKind Kind { get; set; }

    /// <summary>The value a run starts with, as text.</summary>
    public string Default { get; set; } = string.Empty;
}

/// <summary>A condition on a field that has to hold for a transition to be taken.</summary>
public sealed class StateConstraintDef
{
    public string Field { get; set; } = string.Empty;

    /// <summary>One of <see cref="StateOps.All" />.</summary>
    public string Op { get; set; } = "==";

    public string Value { get; set; } = string.Empty;
}

/// <summary>Something that happens when a transition is taken, in the order of the effects.</summary>
public sealed class StateEffectDef
{
    public StateEffectKind Kind { get; set; }

    /// <summary>The field, for Set, Add and Toggle.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>The value to set or add, or the text to log.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>A way from one state (or any) to another, taken when its constraints hold.</summary>
public sealed class StateTransitionDef
{
    /// <summary>What <see cref="From" /> says for a transition that can be taken from every state (Telefrag's <c>FromAny</c>).</summary>
    public const string Any = "*";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;

    /// <summary>The higher goes first when several can be taken.</summary>
    public int Priority { get; set; }

    public List<StateConstraintDef> Constraints { get; set; } = [];

    public List<StateEffectDef> Effects { get; set; } = [];

    [JsonIgnore]
    public bool FromAny => From == Any;

    public override string ToString() => $"{(FromAny ? "any" : From)} → {To}";
}

/// <summary>A state machine as it is designed in the State Lab: its states, fields and transitions with their constraints and effects.</summary>
public sealed class StateDoc
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The state a run starts in.</summary>
    public string Initial { get; set; } = string.Empty;

    public List<StateDef> States { get; set; } = [];

    public List<StateFieldDef> Fields { get; set; } = [];

    public List<StateTransitionDef> Transitions { get; set; } = [];

    public StateDef? Find(string name) => States.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public StateFieldDef? FindField(string name) => Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public StateDoc Clone() => JsonConvert.DeserializeObject<StateDoc>(JsonConvert.SerializeObject(this)) ?? new StateDoc();

    public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);

    /// <summary>A name for a new state that no state has yet ("State 3").</summary>
    public string NewStateName()
    {
        for (int i = States.Count + 1;; i++)
            if (Find($"State {i}") is null)
                return $"State {i}";
    }
}

/// <summary>The comparisons a constraint can make.</summary>
public static class StateOps
{
    public static readonly string[] All = ["==", "!=", "<", "<=", ">", ">=", "contains"];
}
