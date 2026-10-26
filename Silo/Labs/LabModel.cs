using System.Text.RegularExpressions;
using Silo.Contracts.Core;

namespace Silo.Labs;

// ---- the model every Lab is described by ----------------------------------------------------------------------------------
//
//   Lab        the Lab as a kind (all windows of it)             Instance   one window of the Lab, whatever it is studying
//   Subject    what the instance is studying (a regex record)    Part       a piece of the subject (a group, a rule...); parts nest
//   Aspect     a way of looking at a subject or a part ("DOM")   Tool       something the person picks up to work with (the active tool)
//   Command    a named action; the target of menu items, buttons, toolbar buttons, linked actions in parameter lists and macros
//   Parameter  a setting; Preset  a stored combination of parameter values
//   Prefab     a ready-made subject or part that can be added      Template  a prefab with placeholders and/or bindings made when it is added
//
// Subject types and part types are what prefabs and templates are of; aspects and tools can belong to a Lab, an instance, a subject
// (type), a part (type), an aspect or (aspects only) a tool. Anything that belongs to something is only "active" while that something is
// the case: an aspect of a part type is active while the active part is of that type.

/// <summary>What something of the model belongs to.</summary>
public enum LabOwnerKind
{
    /// <summary>The Lab (every window).</summary>
    Lab,

    /// <summary>The window (the instance), whatever its subject.</summary>
    Instance,

    /// <summary>The subject being studied, whatever its type.</summary>
    Subject,

    /// <summary>Subjects of one type (<see cref="LabOwner.Id" />).</summary>
    SubjectType,

    /// <summary>The active part, whatever its type.</summary>
    Part,

    /// <summary>Parts of one type (<see cref="LabOwner.Id" />).</summary>
    PartType,

    /// <summary>One aspect (<see cref="LabOwner.Id" />): active while the aspect is.</summary>
    Aspect,

    /// <summary>One tool (<see cref="LabOwner.Id" />): active while it is the selected tool.</summary>
    Tool
}

/// <summary>Who has an aspect, tool, parameter set, command or pane: decides when it applies (see <see cref="LabModelRules.IsActive" />).</summary>
public readonly record struct LabOwner(LabOwnerKind Kind, string Id = "")
{
    public static LabOwner Lab      => new(LabOwnerKind.Lab);
    public static LabOwner Instance => new(LabOwnerKind.Instance);
    public static LabOwner Subject  => new(LabOwnerKind.Subject);
    public static LabOwner Part     => new(LabOwnerKind.Part);

    public static LabOwner OfSubjectType(string id) => new(LabOwnerKind.SubjectType, id);
    public static LabOwner OfPartType(string id)    => new(LabOwnerKind.PartType, id);
    public static LabOwner OfAspect(string id)      => new(LabOwnerKind.Aspect, id);
    public static LabOwner OfTool(string id)        => new(LabOwnerKind.Tool, id);

    /// <summary>A short stable text for it ("part-type:group"); how presets and panes are filed.</summary>
    public string Key => Id.Length == 0 ? Kind.ToString().ToLowerInvariant() : $"{Kind.ToString().ToLowerInvariant()}:{Id}";

    public override string ToString() => Key;
}

/// <summary>Whether a pane shows what is always there (static) or what belongs to the subject, part or tool at hand (instance).</summary>
public enum LabPaneType
{
    /// <summary>
    ///     Shows static parameters and commands (always available) and instance ones (hidden while the active subject, part or tool is not
    ///     of the owning type). The pane itself is never hidden for lack of an owner.
    /// </summary>
    Static,

    /// <summary>
    ///     Shows the instance parameters and commands of the current subject, active part or selected tool. The pane is hidden while its
    ///     owner is an aspect of a tool that is not the active tool, of a part type the active part is not of, or of a subject type the
    ///     subject is not of.
    /// </summary>
    Instance
}

// ---- prefabs and templates ------------------------------------------------------------------------------------------------

/// <summary>A ready-made subject or part, in full, that can be added as it is.</summary>
/// <param name="Id">A short stable key.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Content">Its content as the Lab reads it (the text of a regex, the JSON of a menu...).</param>
/// <param name="Description">One sentence about it.</param>
/// <param name="IconKey">Its icon.</param>
/// <param name="Group">A heading to list it under.</param>
public sealed record LabPrefab(string Id, string Name, string Content, string? Description = null, string? IconKey = null, string? Group = null);

/// <summary>A hole in a <see cref="LabTemplate" />: <c>{{Name}}</c> in the body is replaced by what the person gives, or by the binding.</summary>
/// <param name="Name">What it is called in the body.</param>
/// <param name="Prompt">What the person is asked.</param>
/// <param name="Default">The value when nothing else is given.</param>
/// <param name="Choices">When given, the value is one of these.</param>
/// <param name="Bind">
///     A binding made when the template is used: reads a value off the instance (the active part's name, the subject's text). When it gives
///     one, the person is not asked.
/// </param>
public sealed record LabPlaceholder(string Name, string Prompt, string Default = "", IReadOnlyList<string>? Choices = null, Func<ILabModel, string?>? Bind = null);

/// <summary>A prefab with placeholders and/or creation-time bindings.</summary>
public sealed record LabTemplate(string Id, string Name, string Body, IReadOnlyList<LabPlaceholder> Placeholders, string? Description = null, string? IconKey = null, string? Group = null)
{
    private static readonly Regex Hole = new(@"\{\{\s*([A-Za-z0-9_.\-]+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>The body with each <c>{{name}}</c> replaced by its value (a hole nobody knows stays as it is).</summary>
    public string Render(IReadOnlyDictionary<string, string> values)
    {
        return Hole.Replace(Body, m => values.TryGetValue(m.Groups[1].Value, out string? value) ? value : m.Value);
    }

    /// <summary>
    ///     The values the placeholders have at creation: each from its binding, else its default. <paramref name="bound" /> lists the ones that
    ///     a binding gave (the person need not be asked about those).
    /// </summary>
    public Dictionary<string, string> Initial(ILabModel? model, out HashSet<string> bound)
    {
        bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Placeholders)
        {
            string? fromBinding = null;
            if (p.Bind is not null && model is not null)
            {
                try
                {
                    fromBinding = p.Bind(model);
                }
                catch (Exception)
                {
                    fromBinding = null;
                }
            }

            if (fromBinding is not null) bound.Add(p.Name);
            values[p.Name] = fromBinding ?? p.Default;
        }

        return values;
    }

    /// <summary>Whether the person has to be asked anything (some placeholder has no binding).</summary>
    public bool NeedsAnswers(ILabModel? model)
    {
        Initial(model, out var bound);
        return Placeholders.Any(p => !bound.Contains(p.Name));
    }
}

/// <summary>A kind of subject: what a Lab studies, with the prefabs and templates a new one can start from.</summary>
public sealed record LabSubjectType(string Id, string Name, string? IconKey = null, string? Description = null, IReadOnlyList<LabPrefab>? Prefabs = null, IReadOnlyList<LabTemplate>? Templates = null)
{
    public IReadOnlyList<LabPrefab>   PrefabList   => Prefabs   ?? [];
    public IReadOnlyList<LabTemplate> TemplateList => Templates ?? [];
}

/// <summary>A kind of part, with the prefabs and templates a new one can start from.</summary>
/// <param name="ParentTypeId">When given, a part of this type goes inside a part of that type (a member inside a class); null: anywhere.</param>
public sealed record LabPartType(string Id, string Name, string? IconKey = null, string? Description = null, IReadOnlyList<LabPrefab>? Prefabs = null, IReadOnlyList<LabTemplate>? Templates = null, string? ParentTypeId = null)
{
    public IReadOnlyList<LabPrefab>   PrefabList   => Prefabs   ?? [];
    public IReadOnlyList<LabTemplate> TemplateList => Templates ?? [];
}

// ---- parts ------------------------------------------------------------------------------------------------------------------

/// <summary>One piece of the subject as it is now (a regex group, a parser rule, a menu item): parts nest, and one at a time is the active part.</summary>
public sealed class LabPart
{
    private readonly List<LabPart> _children = [];

    /// <param name="type">What kind of part it is.</param>
    /// <param name="key">Identifies it in the subject, stable while the subject is edited ("group/2", "rule:Expr"): the active part is remembered by it.</param>
    /// <param name="name">What it is called to a person.</param>
    /// <param name="tag">The Lab's own object for it.</param>
    /// <param name="detail">More, after the name (the rule's kind, the group's pattern).</param>
    public LabPart(LabPartType type, string key, string name, object? tag = null, string? detail = null)
    {
        Type   = type ?? throw new ArgumentNullException(nameof(type));
        Key    = key  ?? throw new ArgumentNullException(nameof(key));
        Name   = name ?? string.Empty;
        Tag    = tag;
        Detail = detail;
    }

    public LabPartType Type { get; }

    public string Key { get; }

    public string Name { get; }

    public object? Tag { get; }

    public string? Detail { get; }

    /// <summary>The settings of this part (kept while the subject lasts); null for none.</summary>
    public LabParameterSet? Parameters { get; set; }

    public LabPart? Parent { get; private set; }

    public IReadOnlyList<LabPart> Children => _children;

    /// <summary>How far down the tree it is (0 for a top-level part).</summary>
    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    /// <summary>Its name preceded by its ancestors' ("Expr / Term / Factor").</summary>
    public string Path => Parent is null ? Name : Parent.Path + " / " + Name;

    public LabPart Add(LabPart child)
    {
        child.Parent = this;
        _children.Add(child);
        return child;
    }

    /// <summary>This part and everything below it, depth first.</summary>
    public IEnumerable<LabPart> Self()
    {
        yield return this;
        foreach (var child in _children)
        foreach (var inner in child.Self())
            yield return inner;
    }

    public override string ToString() => Name;
}

// ---- aspects and tools ------------------------------------------------------------------------------------------------------

/// <summary>A way of looking at a subject or a part ("DOM", "Matches"): with its own commands, parameters and tools, active while its owner is.</summary>
public sealed class LabAspect
{
    public LabAspect(string id, string title, LabOwner owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id    = id;
        Title = title;
        Owner = owner;
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>What has the aspect: it is active while that is the case.</summary>
    public LabOwner Owner { get; }

    public string? IconKey { get; init; }

    public string? Description { get; init; }

    /// <summary>The aspect's settings; null for none.</summary>
    public LabParameterSet? Parameters { get; init; }

    /// <summary>The aspect's commands (a menu of its own while the aspect is active).</summary>
    public IReadOnlyList<ProvidedCommand> Commands { get; init; } = [];

    /// <summary>Tools that come with the aspect: selectable while it is active.</summary>
    public IReadOnlyList<LabTool> Tools { get; init; } = [];

    public override string ToString() => Title;
}

/// <summary>Something the person picks up to work with (select, measure, edit...): one tool at a time is the active one.</summary>
public sealed class LabTool
{
    public LabTool(string id, string name, LabOwner owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id    = id;
        Name  = name;
        Owner = owner;
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>What has the tool: it can be selected while that is the case.</summary>
    public LabOwner Owner { get; }

    public string? IconKey { get; init; }

    public string? Description { get; init; }

    /// <summary>The tool's settings; null for none.</summary>
    public LabParameterSet? Parameters { get; init; }

    /// <summary>The tool's commands (offered while it is the active tool).</summary>
    public IReadOnlyList<ProvidedCommand> Commands { get; init; } = [];

    /// <summary>Aspects of the tool (active while it is the active tool).</summary>
    public IReadOnlyList<LabAspect> Aspects { get; init; } = [];

    /// <summary>Called when the tool becomes the active tool, and when it stops being it.</summary>
    public Action<bool>? Activated { get; init; }

    public override string ToString() => Name;
}

/// <summary>A command with the owner that decides when it is offered.</summary>
/// <param name="Owner">Who has it.</param>
/// <param name="Command">The command: name, icon, tooltip, checked and enabled state, and what it does.</param>
public sealed record LabCommandEntry(LabOwner Owner, ProvidedCommand Command);

/// <summary>One list of parameters as the Parameters panes show it.</summary>
/// <param name="Owner">Who has the parameters.</param>
/// <param name="Title">The heading ("Lab", "DOM", "Group 2").</param>
/// <param name="Set">The parameters.</param>
/// <param name="IsStatic">True for settings that are always there (the Lab's, an aspect's, a tool's); false for those of the subject or part at hand.</param>
public sealed record LabParameterSource(LabOwner Owner, string Title, LabParameterSet Set, bool IsStatic);

// ---- what a session says about the model ----------------------------------------------------------------------------------

/// <summary>
///     What a session says about its model: the subject types and part types, the parts of the subject, the aspects and tools, the
///     commands and parameters of each scope. <see cref="LabSession{TValue}" /> implements it with nothing to say; a Lab overrides what it has.
/// </summary>
public interface ILabModel
{
    /// <summary>The kinds of subject the Lab studies.</summary>
    IReadOnlyList<LabSubjectType> SubjectTypes { get; }

    /// <summary>The type of the subject being studied now; null when there is none.</summary>
    LabSubjectType? SubjectType { get; }

    /// <summary>The kinds of part the subject has.</summary>
    IReadOnlyList<LabPartType> PartTypes { get; }

    /// <summary>The top-level parts of the subject now (each holds its own children).</summary>
    IReadOnlyList<LabPart> Parts { get; }

    /// <summary>The part the person is on, or null.</summary>
    LabPart? ActivePart { get; }

    /// <summary>Makes <paramref name="part" /> the active part (null: none), and lets the Lab show it.</summary>
    void ActivatePart(LabPart? part);

    /// <summary>Every aspect, whether active or not (those of tools included).</summary>
    IReadOnlyList<LabAspect> Aspects { get; }

    /// <summary>Every tool, whether it can be selected now or not (those of aspects included).</summary>
    IReadOnlyList<LabTool> Tools { get; }

    /// <summary>The selected tool, or null.</summary>
    LabTool? ActiveTool { get; }

    /// <summary>Selects a tool (null: none).</summary>
    void ActivateTool(LabTool? tool);

    /// <summary>
    ///     The commands of the Lab, the instance, the subject and its type, the active part and its type (those of aspects and tools are
    ///     theirs). All of them, whether they apply now or not.
    /// </summary>
    IReadOnlyList<LabCommandEntry> ModelCommands { get; }

    /// <summary>Every list of parameters there is (the Lab's, the instance's, the subject's, the active part's, those of aspects and tools).</summary>
    IReadOnlyList<LabParameterSource> ParameterSources { get; }

    /// <summary>Starts a new subject of this type from content (a prefab's, or a template's rendering).</summary>
    Task NewSubjectAsync(LabSubjectType type, string content);

    /// <summary>Adds a part of this type from content, inside <paramref name="parent" /> (null: at the top).</summary>
    Task AddPartAsync(LabPartType type, LabPart? parent, string content);

    /// <summary>Raised when the parts, the active part, the active tool or what applies may have changed.</summary>
    event EventHandler? ModelChanged;
}

/// <summary>The rules that tie the model together, apart from any window (so they can be tested).</summary>
public static class LabModelRules
{
    /// <summary>Whether what <paramref name="owner" /> has applies right now.</summary>
    public static bool IsActive(ILabModel model, LabOwner owner) => IsActive(model, owner, 0);

    private static bool IsActive(ILabModel model, LabOwner owner, int depth)
    {
        if (depth > 8) return false;

        switch (owner.Kind)
        {
            case LabOwnerKind.Lab:
            case LabOwnerKind.Instance:
                return true;
            case LabOwnerKind.Subject:
                return true;
            case LabOwnerKind.SubjectType:
                return string.Equals(model.SubjectType?.Id, owner.Id, StringComparison.OrdinalIgnoreCase);
            case LabOwnerKind.Part:
                return model.ActivePart is not null;
            case LabOwnerKind.PartType:
                return model.ActivePart is { } part && string.Equals(part.Type.Id, owner.Id, StringComparison.OrdinalIgnoreCase);
            case LabOwnerKind.Tool:
                return model.ActiveTool is { } tool && string.Equals(tool.Id, owner.Id, StringComparison.OrdinalIgnoreCase);
            case LabOwnerKind.Aspect:
                var aspect = model.Aspects.FirstOrDefault(a => string.Equals(a.Id, owner.Id, StringComparison.OrdinalIgnoreCase));
                return aspect is not null && IsActive(model, aspect.Owner, depth + 1);
            default:
                return false;
        }
    }

    /// <summary>Whether a pane is to be shown right now: an instance pane needs its owner to be active, a static pane is always there.</summary>
    public static bool Applies(ILabModel model, LabInspector pane)
    {
        if (pane.PaneType == LabPaneType.Static) return true;

        return pane.Scope is not { } scope || IsActive(model, scope);
    }

    /// <summary>The aspects active now.</summary>
    public static IEnumerable<LabAspect> ActiveAspects(ILabModel model) => model.Aspects.Where(a => IsActive(model, a.Owner));

    /// <summary>The tools that can be selected now.</summary>
    public static IEnumerable<LabTool> AvailableTools(ILabModel model) => model.Tools.Where(t => IsActive(model, t.Owner));

    /// <summary>Every part of the subject, depth first.</summary>
    public static IEnumerable<LabPart> AllParts(ILabModel model) => model.Parts.SelectMany(p => p.Self());

    /// <summary>The part with this key, or whose name or path is this text (any case).</summary>
    public static LabPart? FindPart(ILabModel model, string text)
    {
        var all = AllParts(model).ToList();
        return all.FirstOrDefault(p => string.Equals(p.Key, text, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(p => string.Equals(p.Path, text, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(p => string.Equals(p.Name, text, StringComparison.OrdinalIgnoreCase));
    }

    public static LabTool? FindTool(ILabModel model, string text)
    {
        return model.Tools.FirstOrDefault(t => string.Equals(t.Id, text, StringComparison.OrdinalIgnoreCase))
               ?? model.Tools.FirstOrDefault(t => string.Equals(t.Name, text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The part types a new part can be added as, inside <paramref name="parent" /> (null: at the top).</summary>
    public static IEnumerable<LabPartType> AddableTypes(ILabModel model, LabPart? parent)
    {
        return model.PartTypes.Where(t => t.ParentTypeId is null
                                          ? true
                                          : parent is not null && string.Equals(parent.Type.Id, t.ParentTypeId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The commands that apply now: the model's own whose owner is active, then the active aspects', then the active tool's.</summary>
    public static IEnumerable<LabCommandEntry> ActiveCommands(ILabModel model)
    {
        foreach (var entry in model.ModelCommands)
            if (IsActive(model, entry.Owner))
                yield return entry;

        foreach (var aspect in ActiveAspects(model))
        foreach (var command in aspect.Commands)
            yield return new LabCommandEntry(LabOwner.OfAspect(aspect.Id), command);

        if (model.ActiveTool is { } tool)
            foreach (var command in tool.Commands)
                yield return new LabCommandEntry(LabOwner.OfTool(tool.Id), command);
    }

    /// <summary>Every command whether it applies now or not (for finding one by id, e.g. in a macro).</summary>
    public static IEnumerable<LabCommandEntry> EveryCommand(ILabModel model)
    {
        foreach (var entry in model.ModelCommands)
            yield return entry;

        foreach (var aspect in model.Aspects)
        foreach (var command in aspect.Commands)
            yield return new LabCommandEntry(LabOwner.OfAspect(aspect.Id), command);

        foreach (var tool in model.Tools)
        foreach (var command in tool.Commands)
            yield return new LabCommandEntry(LabOwner.OfTool(tool.Id), command);
    }

    /// <summary>
    ///     The parameter lists a pane shows. A pane with a <paramref name="scope" /> (an aspect's or a tool's) shows its owner's lists and the
    ///     Part, Subject and Lab ones; the general pane (no scope) shows them all. A static pane shows static lists always and instance lists
    ///     while their owner is active; an instance pane shows only lists whose owner is active.
    /// </summary>
    public static IReadOnlyList<LabParameterSource> SourcesFor(ILabModel model, LabOwner? scope, LabPaneType type)
    {
        return model.ParameterSources.Where(s => InScope(s.Owner, scope) && Shown(model, s.Owner, s.IsStatic, type)).ToList();
    }

    private static bool Shown(ILabModel model, LabOwner owner, bool isStatic, LabPaneType type)
    {
        return (type == LabPaneType.Static && isStatic) || IsActive(model, owner);
    }

    private static bool InScope(LabOwner owner, LabOwner? scope)
    {
        if (scope is not { } s) return true;

        return owner.Kind switch
               {
                   LabOwnerKind.Aspect => s.Kind == LabOwnerKind.Aspect && string.Equals(s.Id, owner.Id, StringComparison.OrdinalIgnoreCase),
                   LabOwnerKind.Tool   => s.Kind == LabOwnerKind.Tool   && string.Equals(s.Id, owner.Id, StringComparison.OrdinalIgnoreCase),
                   _                   => true
               };
    }

    /// <summary>The commands a pane offers next to its parameters, by the same rule as <see cref="SourcesFor" /> (the Lab's, an aspect's and a tool's are static).</summary>
    public static IReadOnlyList<ProvidedCommand> CommandsFor(ILabModel model, LabOwner? scope, LabPaneType type)
    {
        var result = new List<ProvidedCommand>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in EveryCommand(model))
        {
            if (!InScope(entry.Owner, scope)) continue;

            bool isStatic = entry.Owner.Kind is LabOwnerKind.Lab or LabOwnerKind.Instance or LabOwnerKind.Aspect or LabOwnerKind.Tool;
            if (Shown(model, entry.Owner, isStatic, type) && entry.Command.Children is not { Count: > 0 } && seen.Add(entry.Command.Id))
                result.Add(entry.Command);
        }

        return result;
    }
}
