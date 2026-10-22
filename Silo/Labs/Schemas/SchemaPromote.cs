using Silo.DbModel.Meta;
using Silo.DbModel.Worlds;
using Silo.Providers.Data;
using Silo.Providers.Definitions;

namespace Silo.Labs.Schemas;

/// <summary>A class has an attribute (by name; the store resolves both).</summary>
public sealed record ClassMembership(string Class, string Attribute);

/// <summary>
///     What a promotion writes besides enums and syntaxes. Everything refers to everything else by <b>name</b> (a stub
///     <see cref="MvType" /> with only its name stands for a class the Silo already has); an <c>Id</c> of 0 means new, and the
///     store gives it one.
/// </summary>
public sealed record SchemaChanges(
    PromoteTarget                      Target,
    IReadOnlyList<MvType>              Types,
    IReadOnlyList<MvAttribute>         Attributes,
    IReadOnlyList<MvTypeHierarchy>     Hierarchy,
    IReadOnlyList<ClassMembership>     Members,
    IReadOnlyList<WorldType>           WorldTypes)
{
    public bool IsEmpty => Types.Count == 0 && Attributes.Count == 0 && Hierarchy.Count == 0 && WorldTypes.Count == 0;
}

/// <summary>
///     What a Silo's database offers for classes, attributes and type relations. <b>Nothing implements this yet:</b> the Silo's
///     SQLite provider only has <c>AddItem</c>, which upserts by an <c>Id</c> the caller has to choose, and no way to list what is
///     there, so the Lab cannot safely allocate ids itself. A provider that implements this interface (the generalized constructs
///     of <c>entity-tiers-and-universal-packages.md</c> need the same thing) turns the Lab's class, attribute and relation
///     promotion on; until then those steps are shown as blocked and the draft stays as it is.
/// </summary>
public interface ISchemaStore
{
    /// <summary>The classes, attributes, type relations and local-world types the Silo has (the Lab fills in enums and syntaxes itself).</summary>
    SchemaSnapshot GetSnapshot();

    /// <summary>
    ///     Writes the changes in one transaction: classes and attributes are added, or (same name) updated; type relations are added
    ///     when not there; for the world target the local world's types are rewritten with their fields. Throws
    ///     <see cref="InvalidOperationException" /> with something to tell the person when it cannot.
    /// </summary>
    void Apply(SchemaChanges changes);
}

public enum StepAction
{
    /// <summary>New in the target.</summary>
    Add,

    /// <summary>Replaces what has the name.</summary>
    Replace,

    /// <summary>Added under a new name, because the name is taken.</summary>
    Copy,

    /// <summary>Nothing is written for it (and why).</summary>
    Skip,

    /// <summary>Cannot be promoted to this target (and why).</summary>
    Blocked
}

/// <summary>One line of the plan.</summary>
public sealed record PromoteStep(ConstructKind Kind, string Name, string StoredAs, StepAction Action, string Why)
{
    public bool Writes => Action is StepAction.Add or StepAction.Replace or StepAction.Copy;
}

public sealed class PromoteOptions
{
    /// <summary>What to do with a name the target already has: replace it, or keep both (the new one named <c>Copy_of_...</c>). When neither, it is skipped.</summary>
    public bool Replace { get; init; }

    public bool Copy { get; init; }
}

/// <summary>Which stores the Silo has, because a Silo without one cannot take that kind of construct.</summary>
public sealed record SchemaSupport(bool Enums, bool Syntaxes, bool Schema)
{
    public static SchemaSupport All  { get; } = new(true, true, true);
    public static SchemaSupport None { get; } = new(false, false, false);
}

public sealed record PromotionPlan(PromoteTarget Target, IReadOnlyList<PromoteStep> Steps, IReadOnlyList<SchemaProblem> Problems)
{
    /// <summary>Errors in the draft stop the whole promotion: nothing is written from a draft that is wrong.</summary>
    public bool HasErrors => Problems.Any(p => p.Severity == SchemaSeverity.Error);

    public bool CanApply => !HasErrors && Steps.Any(s => s.Writes);

    public int Writes => Steps.Count(s => s.Writes);
}

public sealed record PromoteOutcome(IReadOnlyList<string> Done, IReadOnlyList<string> Failed)
{
    public bool Ok => Failed.Count == 0;
}

/// <summary>Plans a promotion, builds what it writes, and writes it through the Silo's stores.</summary>
public static class SchemaPromote
{
    private static readonly ConstructKind[] Order =
    [
        ConstructKind.Syntax, ConstructKind.Enum, ConstructKind.Attribute, ConstructKind.Class, ConstructKind.Relation, ConstructKind.Unit, ConstructKind.UnitRelation
    ];

    public static PromotionPlan Plan(SchemaDraft draft, PromoteTarget target, SchemaSnapshot silo, SchemaSupport support, PromoteOptions? options = null)
    {
        options ??= new PromoteOptions();
        var problems = SchemaCheck.Validate(draft, silo).ToList();
        var steps    = new List<PromoteStep>();
        var stored   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // "Kind:Name" -> the name it is stored under
        var taken    = new Dictionary<ConstructKind, HashSet<string>>();

        HashSet<string> Taken(ConstructKind k)
        {
            if (taken.TryGetValue(k, out var set)) return set;
            var names = k switch
                        {
                            ConstructKind.Syntax    => silo.Syntaxes.SelectMany(s => s.Alias is null ? [s.Name] : new[] { s.Name, s.Alias }),
                            ConstructKind.Enum      => silo.Enums.Select(e => e.Name),
                            ConstructKind.Attribute => silo.Attributes,
                            ConstructKind.Class     => target == PromoteTarget.World ? silo.LocalWorldTypes : silo.Classes,
                            _                       => []
                        };
            return taken[k] = new HashSet<string>(names.Concat(draft.Of(k).Select(c => c.Name)), StringComparer.OrdinalIgnoreCase);
        }

        bool InSilo(ConstructKind k, string name) => k switch
                                                      {
                                                          ConstructKind.Syntax    => silo.HasSyntax(name),
                                                          ConstructKind.Enum      => silo.HasEnum(name),
                                                          ConstructKind.Attribute => silo.HasAttribute(name),
                                                          ConstructKind.Class     => target == PromoteTarget.World ? silo.LocalWorldTypes.Contains(name, StringComparer.OrdinalIgnoreCase) : silo.HasClass(name),
                                                          _                       => false
                                                      };

        void Add(SchemaConstruct c, string storedAs, StepAction action, string why)
        {
            steps.Add(new PromoteStep(c.Kind, c.Name, storedAs, action, why));
            if (action is StepAction.Add or StepAction.Replace or StepAction.Copy or StepAction.Skip) stored[c.Ref] = storedAs;
        }

        // a name the target has: replace, keep both, or leave it
        void Conflict(SchemaConstruct c, string what)
        {
            if (options.Replace) Add(c, c.Name, StepAction.Replace, $"The {what} \"{c.Name}\" is replaced.");
            else if (options.Copy)
            {
                string copy = CopyName(c.Name, Taken(c.Kind));
                Taken(c.Kind).Add(copy);
                Add(c, copy, StepAction.Copy, $"The {what} \"{c.Name}\" exists, so this is added as \"{copy}\".");
            }
            else Add(c, c.Name, StepAction.Skip, $"The target already has a {what} named \"{c.Name}\" (choose Replace or Keep both to write it).");
        }

        string Block(SchemaConstruct c, string why)
        {
            steps.Add(new PromoteStep(c.Kind, c.Name, c.Name, StepAction.Blocked, why));
            return why;
        }

        bool Have(ConstructKind k, string name) => stored.ContainsKey($"{k}:{name}") || (InSilo(k, name) && target == PromoteTarget.Meta) ||
                                                    (k == ConstructKind.Class && InSilo(k, name));

        foreach (var kind in Order)
        foreach (var c in draft.Of(kind))
        {
            switch (kind)
            {
                case ConstructKind.Syntax when target == PromoteTarget.World:
                case ConstructKind.Enum when target == PromoteTarget.World:
                    Block(c, $"A {kind.Title().ToLowerInvariant()} lives in the metaverse and in scratch, not in a world; promote it to the metaverse.");
                    break;
                case ConstructKind.Syntax:
                    if (!support.Syntaxes) Block(c, "This Silo keeps no syntaxes.");
                    else if (InSilo(kind, c.Name)) Conflict(c, "syntax");
                    else Add(c, c.Name, StepAction.Add, "New syntax.");
                    break;
                case ConstructKind.Enum:
                    if (!support.Enums) Block(c, "This Silo keeps no enums.");
                    else if (InSilo(kind, c.Name)) Conflict(c, "enum");
                    else Add(c, c.Name, StepAction.Add, "New enum.");
                    break;
                case ConstructKind.Attribute when target == PromoteTarget.World:
                    Add(c, c.Name, StepAction.Skip,
                        draft.Of(ConstructKind.Class).Any(k => k.Members.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
                            ? "Becomes a field of each world type whose class has it."
                            : "No class has it, so no world type gets it.");
                    break;
                case ConstructKind.Attribute:
                    if (!support.Schema) Block(c, NoSchemaStore);
                    else if (SchemaTypes.EnumOf(c.Get("type")) is { } en && !Have(ConstructKind.Enum, en)) Block(c, $"Its enum \"{en}\" is not promoted with it and the Silo does not have it.");
                    else if (c.Has("syntax") && !Have(ConstructKind.Syntax, c.Get("syntax"))) Block(c, $"Its syntax \"{c.Get("syntax")}\" is not promoted with it and the Silo does not have it.");
                    else if (InSilo(kind, c.Name)) Conflict(c, "attribute");
                    else Add(c, c.Name, StepAction.Add, "New attribute.");
                    break;
                case ConstructKind.Class:
                    if (!support.Schema) Block(c, target == PromoteTarget.World ? NoSchemaStore.Replace("class", "world type") : NoSchemaStore);
                    else if (c.Members.FirstOrDefault(m => !Have(ConstructKind.Attribute, m) && !(target == PromoteTarget.World && draft.Find(ConstructKind.Attribute, m) is not null)) is { } lost)
                        Block(c, $"Its attribute \"{lost}\" is not promoted with it and the Silo does not have it.");
                    else if (InSilo(kind, c.Name)) Conflict(c, target == PromoteTarget.World ? "world type" : "class");
                    else Add(c, c.Name, StepAction.Add, target == PromoteTarget.World ? "New type in the local world." : "New class.");
                    break;
                case ConstructKind.Relation:
                    string subject = c.Get("subject"), tgt = c.Get("target");
                    if (!support.Schema) Block(c, NoSchemaStore);
                    else if (!Have(ConstructKind.Class, subject) || !Have(ConstructKind.Class, tgt))
                        Block(c, $"Its class \"{(!Have(ConstructKind.Class, subject) ? subject : tgt)}\" is not promoted with it and the target does not have it.");
                    else if (target == PromoteTarget.Meta && silo.Hierarchy.Any(h => string.Equals(h.Subject, subject, StringComparison.OrdinalIgnoreCase) &&
                                                                                      string.Equals(h.Target,  tgt,     StringComparison.OrdinalIgnoreCase) &&
                                                                                      string.Equals(h.Relation.ToString(), c.Get("relation", "Inheritance"), StringComparison.OrdinalIgnoreCase)))
                        Add(c, c.Name, StepAction.Skip, "The Silo already has this relation.");
                    else if (target == PromoteTarget.World) Add(c, c.Name, StepAction.Skip, "Becomes the base type or an included type of the subject's world type.");
                    else Add(c, c.Name, StepAction.Add, "New type relation.");
                    break;
                case ConstructKind.Unit:
                case ConstructKind.UnitRelation:
                    Block(c, target == PromoteTarget.World
                                 ? "Units are not stored in a world."
                                 : "Units are only stored in the Universal database today, which is shipped by packages and read-only here. The placement sheet adds scratch and the metaverse for them; nothing implements that yet. Export the draft instead.");
                    break;
            }
        }

        // a world type needs its base type to be in the local world
        if (target == PromoteTarget.World)
            foreach (var r in draft.Of(ConstructKind.Relation))
            {
                string sub = r.Get("subject"), tgt = r.Get("target");
                var    st  = steps.FirstOrDefault(s => s.Kind == ConstructKind.Class && string.Equals(s.Name, sub, StringComparison.OrdinalIgnoreCase));
                if (st is null || !st.Writes) continue;

                bool targetThere = steps.Any(s => s.Kind == ConstructKind.Class && s.Writes && string.Equals(s.Name, tgt, StringComparison.OrdinalIgnoreCase)) || silo.LocalWorldTypes.Contains(tgt, StringComparer.OrdinalIgnoreCase);
                if (!targetThere) problems.Add(new SchemaProblem(SchemaSeverity.Error, ConstructKind.Class, sub, $"\"{sub}\" needs \"{tgt}\" in the local world, but it is not promoted with it and is not there."));
            }

        foreach (var s in steps.Where(s => s.Action == StepAction.Blocked))
            problems.Add(new SchemaProblem(SchemaSeverity.Info, s.Kind, s.Name, "Not promoted: " + s.Why));

        return new PromotionPlan(target, steps, problems);
    }

    private const string NoSchemaStore = "This Silo's database cannot yet store classes, attributes and type relations (it has no schema store); the draft keeps them.";

    /// <summary><c>Copy_of_Name</c>, with a number when that is taken too (a name cannot have spaces).</summary>
    public static string CopyName(string name, ISet<string> taken)
    {
        string first = "Copy_of_" + name;
        if (!taken.Contains(first)) return first;
        for (int i = 2;; i++)
        {
            string next = $"{first}_{i}";
            if (!taken.Contains(next)) return next;
        }
    }

    /// <summary>What the steps that write give as the stored name, for references to follow (by kind and draft name).</summary>
    private static Func<ConstructKind, string, string> NamesOf(PromotionPlan plan)
    {
        var map = plan.Steps.Where(s => s.Writes || s.Action == StepAction.Skip).ToDictionary(s => $"{s.Kind}:{s.Name}", s => s.StoredAs, StringComparer.OrdinalIgnoreCase);
        return (k, n) => map.TryGetValue($"{k}:{n}", out string? v) ? v : n;
    }

    /// <summary>The syntaxes and enums the plan writes, as the Silo's own records (a replaced one carries the id of what it replaces).</summary>
    public static (IReadOnlyList<SyntaxRecord> Syntaxes, IReadOnlyList<EnumRecord> Enums) BuildDefinitions(SchemaDraft draft, PromotionPlan plan, SchemaSnapshot silo)
    {
        var syntaxes = new List<SyntaxRecord>();
        var enums    = new List<EnumRecord>();
        foreach (var step in plan.Steps.Where(s => s.Writes))
            switch (step.Kind)
            {
                case ConstructKind.Syntax when draft.Find(ConstructKind.Syntax, step.Name) is { } s:
                    var syn = SchemaRecords.ToSyntax(s) with { Name = step.StoredAs };
                    if (step.Action == StepAction.Copy) syn = syn with { Alias = null }; // the alias belongs to the original
                    if (step.Action == StepAction.Replace && silo.Syntaxes.FirstOrDefault(x => string.Equals(x.Name, step.Name, StringComparison.OrdinalIgnoreCase) || (x.Alias is not null && string.Equals(x.Alias, step.Name, StringComparison.OrdinalIgnoreCase))) is { } old)
                        syn = syn with { Id = old.Id, Name = old.Name };
                    syntaxes.Add(syn);
                    break;
                case ConstructKind.Enum when draft.Find(ConstructKind.Enum, step.Name) is { } e:
                    var en = SchemaRecords.ToEnum(e) with { Name = step.StoredAs };
                    if (step.Action == StepAction.Replace && silo.Enums.FirstOrDefault(x => string.Equals(x.Name, step.Name, StringComparison.OrdinalIgnoreCase)) is { } oldEnum)
                        en = en with { Id = oldEnum.Id };
                    enums.Add(en);
                    break;
            }

        return (syntaxes, enums);
    }

    /// <summary>
    ///     The classes, attributes and relations (or, for a world, the world types) the plan writes. <paramref name="syntaxId" /> says
    ///     the id of a syntax by its stored name (the syntaxes are saved first); one it cannot give leaves the attribute without it.
    /// </summary>
    public static SchemaChanges BuildSchema(SchemaDraft draft, PromotionPlan plan, Func<string, int?> syntaxId)
    {
        var name = NamesOf(plan);
        var done = new HashSet<string>(plan.Steps.Where(s => s.Writes).Select(s => $"{s.Kind}:{s.Name}"), StringComparer.OrdinalIgnoreCase);

        if (plan.Target == PromoteTarget.World) return new SchemaChanges(plan.Target, [], [], [], [], BuildWorldTypes(draft, plan, name, done));

        var types = new Dictionary<string, MvType>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in draft.Of(ConstructKind.Class).Where(c => done.Contains(c.Ref)))
        {
            string stored = name(ConstructKind.Class, c.Name);
            types[c.Name] = new MvType { Name = stored, DisplayName = c.Get("displayName", stored), Flags = c.Flag("auxiliary") ? MvTypeFlags.Auxiliary : MvTypeFlags.None };
        }

        var attributes = new List<MvAttribute>();
        foreach (var a in draft.Of(ConstructKind.Attribute).Where(a => done.Contains(a.Ref)))
        {
            string  stored = name(ConstructKind.Attribute, a.Name);
            string  type   = a.Get("type", "String");
            if (SchemaTypes.EnumOf(type) is { } en) type = "enum:" + name(ConstructKind.Enum, en);
            int? syntax = a.Has("syntax") ? syntaxId(name(ConstructKind.Syntax, a.Get("syntax"))) : null;
            attributes.Add(new MvAttribute
                           {
                               Name           = stored,
                               DisplayName    = a.Get("displayName", stored),
                               Type           = type,
                               Syntax         = syntax,
                               DefaultValue   = a.Has("default") ? a.Get("default") : null,
                               CombinatorType = a.Get("combinator"),
                               Flags          = SchemaRecords.AttributeFlags(a)
                           });
        }

        var hierarchy = new List<MvTypeHierarchy>();
        foreach (var r in draft.Of(ConstructKind.Relation).Where(r => done.Contains(r.Ref)))
        {
            string subject = name(ConstructKind.Class, r.Get("subject")), target = name(ConstructKind.Class, r.Get("target"));
            hierarchy.Add(new MvTypeHierarchy
                          {
                              Subject  = types.GetValueOrDefault(r.Get("subject")) ?? new MvType { Name = subject },
                              Target   = types.GetValueOrDefault(r.Get("target")) ?? new MvType { Name = target },
                              Relation = Enum.TryParse(r.Get("relation", "Inheritance"), true, out TypeRelation rel) ? rel : TypeRelation.Inheritance
                          });
        }

        var members = draft.Of(ConstructKind.Class).Where(c => done.Contains(c.Ref))
                           .SelectMany(c => c.Members.Where(m => draft.Find(ConstructKind.Attribute, m) is null || done.Contains($"Attribute:{draft.Find(ConstructKind.Attribute, m)!.Name}"))
                                             .Select(m => new ClassMembership(name(ConstructKind.Class, c.Name), name(ConstructKind.Attribute, m))))
                           .ToList();
        return new SchemaChanges(plan.Target, types.Values.ToList(), attributes, hierarchy, members, []);
    }

    private static IReadOnlyList<WorldType> BuildWorldTypes(SchemaDraft draft, PromotionPlan plan, Func<ConstructKind, string, string> name, HashSet<string> done)
    {
        var world = LocalWorld.Create();
        var types = new List<WorldType>();
        var byName = new Dictionary<string, WorldType>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in draft.Of(ConstructKind.Class).Where(c => done.Contains(c.Ref)))
        {
            string stored = name(ConstructKind.Class, c.Name);
            var type = new WorldType
                       {
                           Name        = stored,
                           DisplayName = c.Get("displayName", stored),
                           NativeName  = stored,
                           Provider    = LocalWorld.Provider,
                           World       = world
                       };
            int index = 0;
            foreach (string m in c.Members)
            {
                var attr = draft.Find(ConstructKind.Attribute, m);
                var flags = attr is null ? MvAttributeFlags.None : SchemaRecords.AttributeFlags(attr);
                string attrType = attr?.Get("type", "String") ?? "String";
                EnumType? enumType = SchemaTypes.EnumOf(attrType) is { } en && draft.Find(ConstructKind.Enum, en) is { } enumDef ? SchemaRecords.ToEnum(enumDef).Type : null;
                string clr = SchemaTypes.ClrFor(attrType, enumType);
                type.Items.Add(new WorldField
                               {
                                   Name       = m,
                                   DataType   = clr,
                                   NativeType = clr,
                                   Index      = index++,
                                   World      = world,
                                   WorldType  = type,
                                   Flags      = FieldFlags(flags, attr?.Get("default"))
                               });
            }

            types.Add(type);
            byName[c.Name] = type;
        }

        // bases and included types are references by name (RuntimeRef), bound by World.BindReferences once the types are in a world:
        // the name of one built in this run, or of one the local world already has
        foreach (var c in draft.Of(ConstructKind.Class).Where(c => byName.ContainsKey(c.Name)))
        {
            var type    = byName[c.Name];
            var include = new List<string>();
            foreach (var r in draft.Of(ConstructKind.Relation).Where(r => string.Equals(r.Get("subject"), c.Name, StringComparison.OrdinalIgnoreCase)))
            {
                string target = name(ConstructKind.Class, r.Get("target"));
                if (string.Equals(r.Get("relation"), nameof(TypeRelation.Auxiliary), StringComparison.OrdinalIgnoreCase)) include.Add(target);
                else type.Inherits ??= new RuntimeRef<WorldType> { Value = target };
            }

            if (include.Count > 0) type.Include = new RuntimeRef<WorldType[]> { Value = string.Join(",", include) };
        }

        return types;
    }

    private static WorldFieldFlags FieldFlags(MvAttributeFlags flags, string? _)
    {
        var f = WorldFieldFlags.None;
        if ((flags & MvAttributeFlags.Key) != 0) f |= WorldFieldFlags.Key;
        if ((flags & MvAttributeFlags.Indexed) != 0) f |= WorldFieldFlags.Index;
        if ((flags & MvAttributeFlags.Anr) != 0) f |= WorldFieldFlags.Anr;
        if ((flags & MvAttributeFlags.Nullable) != 0) f |= WorldFieldFlags.Nullable;
        if ((flags & MvAttributeFlags.Multivalue) != 0) f |= WorldFieldFlags.Multivalue;
        return f;
    }

    /// <summary>
    ///     Writes what the plan says through the Silo's stores: syntaxes, then enums, then (together) classes, attributes and
    ///     relations. The stores are separate, so a failure part-way leaves what was already written written; the outcome lists both.
    ///     Nothing is written when the plan has errors.
    /// </summary>
    public static PromoteOutcome Apply(SchemaDraft draft, PromotionPlan plan, SchemaSnapshot silo, ISyntaxStore? syntaxes, IEnumStore? enums, ISchemaStore? schema)
    {
        var done   = new List<string>();
        var failed = new List<string>();
        if (plan.HasErrors)
        {
            failed.Add("The draft has errors; nothing was written.");
            return new PromoteOutcome(done, failed);
        }

        var (syntaxRecords, enumRecords) = BuildDefinitions(draft, plan, silo);
        var syntaxIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in silo.Syntaxes) syntaxIds[s.Name] = s.Id;

        foreach (var rec in syntaxRecords)
        {
            if (syntaxes is null)
            {
                failed.Add($"Syntax {rec.Name}: this Silo keeps no syntaxes.");
                continue;
            }

            try
            {
                var saved = syntaxes.SaveSyntax(rec);
                syntaxIds[saved.Name] = saved.Id;
                done.Add($"Syntax {saved.Name}");
            }
            catch (DefinitionException ex)
            {
                failed.AddRange(ex.Problems.Select(p => $"Syntax {rec.Name}: {p}"));
            }
            catch (Exception ex)
            {
                failed.Add($"Syntax {rec.Name}: {ex.Message}");
            }
        }

        foreach (var rec in enumRecords)
        {
            if (enums is null)
            {
                failed.Add($"Enum {rec.Name}: this Silo keeps no enums.");
                continue;
            }

            try
            {
                var saved = enums.SaveEnum(rec);
                done.Add($"Enum {saved.Name} ({saved.Values.Count} value{(saved.Values.Count == 1 ? string.Empty : "s")})");
            }
            catch (DefinitionException ex)
            {
                failed.AddRange(ex.Problems.Select(p => $"Enum {rec.Name}: {p}"));
            }
            catch (Exception ex)
            {
                failed.Add($"Enum {rec.Name}: {ex.Message}");
            }
        }

        var changes = BuildSchema(draft, plan, n => syntaxIds.TryGetValue(n, out int id) ? id : null);

        // an attribute whose syntax could not be saved is not written (it would silently lose its rules)
        var unresolved = changes.Attributes.Where(a => a.Syntax is null && draft.Of(ConstructKind.Attribute).Any(d => d.Has("syntax") && string.Equals(d.Name, a.Name, StringComparison.OrdinalIgnoreCase))).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (unresolved.Count > 0)
        {
            foreach (string n in unresolved) failed.Add($"Attribute {n}: its syntax was not saved, so the attribute was not written.");
            changes = changes with
                      {
                          Attributes = changes.Attributes.Where(a => !unresolved.Contains(a.Name)).ToList(),
                          Members    = changes.Members.Where(m => !unresolved.Contains(m.Attribute)).ToList()
                      };
        }

        if (!changes.IsEmpty)
        {
            if (schema is null)
            {
                failed.Add("Classes and attributes: this Silo's database cannot yet store them (no schema store).");
            }
            else
            {
                try
                {
                    schema.Apply(changes);
                    if (plan.Target == PromoteTarget.World) done.AddRange(changes.WorldTypes.Select(t => $"World type {t.Name} ({t.Items.Count} field{(t.Items.Count == 1 ? string.Empty : "s")})"));
                    else
                    {
                        done.AddRange(changes.Attributes.Select(a => $"Attribute {a.Name}"));
                        done.AddRange(changes.Types.Select(t => $"Class {t.Name}"));
                        done.AddRange(changes.Hierarchy.Select(h => $"{h.Subject.Name} {(h.Relation == TypeRelation.Auxiliary ? "includes" : "inherits")} {h.Target.Name}"));
                    }
                }
                catch (Exception ex)
                {
                    failed.Add("Classes and attributes: " + ex.Message);
                }
            }
        }

        return new PromoteOutcome(done, failed);
    }
}
