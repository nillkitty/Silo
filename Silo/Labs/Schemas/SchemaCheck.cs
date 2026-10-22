using System.Globalization;
using Silo.DbModel.Meta;
using Silo.Providers.Data;
using Silo.Providers.Definitions;

namespace Silo.Labs.Schemas;

public enum SchemaSeverity
{
    Error,
    Warning,
    Info
}

/// <summary>One thing found wrong (or worth knowing) about the draft; <see cref="Kind" /> and <see cref="Name" /> say which construct, when one.</summary>
public sealed record SchemaProblem(SchemaSeverity Severity, ConstructKind? Kind, string? Name, string Text)
{
    public string Where => Kind is null ? "draft" : $"{Kind.Value.Title().ToLowerInvariant()} {Name}";
}

/// <summary>
///     What the Silo already has that a draft refers to or must not repeat. It is what the checks and promotion compare with; a
///     Silo that cannot list something (it has no class store) leaves that list empty.
/// </summary>
public sealed class SchemaSnapshot
{
    public static SchemaSnapshot Empty { get; } = new();

    public IReadOnlyList<EnumRecord>   Enums   { get; init; } = [];
    public IReadOnlyList<SyntaxRecord> Syntaxes { get; init; } = [];

    /// <summary>Names of the classes in the metaverse, and which of them are auxiliary.</summary>
    public IReadOnlyList<string> Classes { get; init; } = [];

    public IReadOnlySet<string> AuxiliaryClasses { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>The type relations the Silo has: subject, target, relation.</summary>
    public IReadOnlyList<(string Subject, string Target, TypeRelation Relation)> Hierarchy { get; init; } = [];

    /// <summary>Names of the types the local world already has (the target of a world promotion).</summary>
    public IReadOnlyList<string> LocalWorldTypes { get; init; } = [];

    public IReadOnlyList<string> Units         { get; init; } = [];
    public IReadOnlyList<string> UnitRelations { get; init; } = [];

    public bool HasEnum(string name)   => Enums.Any(e => Is(e.Name, name));
    public bool HasSyntax(string name) => Syntaxes.Any(s => Is(s.Name, name) || (s.Alias is not null && Is(s.Alias, name)));
    public bool HasClass(string name)  => Classes.Any(c => Is(c, name));
    public bool HasAttribute(string name) => Attributes.Any(a => Is(a, name));

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Draft constructs as the Silo's own definition records (so the Silo's rules can judge them).</summary>
public static class SchemaRecords
{
    public static EnumRecord ToEnum(SchemaConstruct c)
    {
        var type  = Enum.TryParse(c.Get("type", nameof(EnumType.Integral)), true, out EnumType t) ? t : EnumType.Integral;
        var flags = ParseFlags<EnumFlags>(c.List("flags"));
        var rec   = new EnumRecord(0, c.Name, c.Has("description") ? c.Get("description") : null, type, flags, []);

        // a value left blank gets the next sensible one, as the Enums window offers
        var values = new List<EnumValueRecord>();
        foreach (var v in c.Values)
        {
            string? value = v.Value;
            if (string.IsNullOrWhiteSpace(value)) value = EnumRules.SuggestNextValue(rec with { Values = values });
            values.Add(new EnumValueRecord(0, v.Name ?? string.Empty, value, v.Description));
        }

        return rec with { Values = values };
    }

    public static SyntaxRecord ToSyntax(SchemaConstruct c)
    {
        var kind = Enum.TryParse(c.Get("kind", nameof(SyntaxKind.Pattern)), true, out SyntaxKind k) ? k : SyntaxKind.Pattern;
        return new SyntaxRecord(0, c.Name, c.Has("alias") ? c.Get("alias") : null, c.Has("description") ? c.Get("description") : null, kind,
                                c.Has("parser") ? c.Get("parser") : null, c.Has("pattern") ? c.Get("pattern") : null, c.Has("example") ? c.Get("example") : null);
    }

    /// <summary>An enum of the Silo as a draft construct (to edit a copy of it).</summary>
    public static SchemaConstruct FromEnum(EnumRecord e)
    {
        var c = new SchemaConstruct { Kind = ConstructKind.Enum, Name = e.Name };
        c.Set("type", e.Type.ToString());
        c.Set("flags", string.Join(", ", SchemaProps.EnumFlagNames.Where(n => Enum.TryParse(n, out EnumFlags f) && (e.Flags & f) != 0)));
        c.Set("description", e.Description);
        c.Values = e.Values.Select(v => new SchemaValue { Name = v.Name, Value = v.Value, Description = v.Description }).ToList();
        return c;
    }

    /// <summary>A syntax of the Silo as a draft construct (to edit a copy of it).</summary>
    public static SchemaConstruct FromSyntax(SyntaxRecord s)
    {
        var c = new SchemaConstruct { Kind = ConstructKind.Syntax, Name = s.Name };
        c.Set("kind", s.Kind.ToString());
        c.Set("pattern", s.Pattern);
        c.Set("parser", s.Parser);
        c.Set("alias", s.Alias);
        c.Set("example", s.Example);
        c.Set("description", s.Description);
        return c;
    }

    public static MvAttributeFlags AttributeFlags(SchemaConstruct c) => ParseFlags<MvAttributeFlags>(c.List("flags"));

    public static TEnum ParseFlags<TEnum>(IEnumerable<string> names) where TEnum : struct, Enum
    {
        long bits = 0;
        foreach (string n in names)
            if (Enum.TryParse(n, true, out TEnum f))
                bits |= Convert.ToInt64(f, CultureInfo.InvariantCulture);
        return (TEnum)Enum.ToObject(typeof(TEnum), bits);
    }
}

/// <summary>Checks a draft with the Silo's own rules wherever it has them (enums, syntaxes, units) and the draft's own where it has not.</summary>
public static class SchemaCheck
{
    private const int MaxUnitName = 64;

    public static IReadOnlyList<SchemaProblem> Validate(SchemaDraft draft, SchemaSnapshot? silo = null)
    {
        silo ??= SchemaSnapshot.Empty;
        var problems = new List<SchemaProblem>();

        void Add(SchemaSeverity s, SchemaConstruct? c, string text) => problems.Add(new SchemaProblem(s, c?.Kind, c?.Name, text));

        if (draft.IsEmpty) Add(SchemaSeverity.Info, null, "There is nothing in the draft yet.");

        // names, and names used twice
        foreach (var group in draft.Items.GroupBy(i => i.Kind))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in group)
            {
                if (c.Kind != ConstructKind.Relation && NameProblem(c) is { } nameProblem) Add(SchemaSeverity.Error, c, nameProblem);
                if (!seen.Add(c.Name)) Add(SchemaSeverity.Error, c, $"There is more than one {c.Kind.Title().ToLowerInvariant()} called \"{c.Name}\".");
            }
        }

        foreach (var c in draft.Of(ConstructKind.Enum)) CheckEnum(draft, silo, c, Add);
        foreach (var c in draft.Of(ConstructKind.Syntax)) CheckSyntax(draft, silo, c, Add);
        foreach (var c in draft.Of(ConstructKind.Attribute)) CheckAttribute(draft, silo, c, Add);
        foreach (var c in draft.Of(ConstructKind.Class)) CheckClass(draft, silo, c, Add);
        foreach (var c in draft.Of(ConstructKind.Relation)) CheckRelation(draft, silo, c, Add);
        foreach (var c in draft.Of(ConstructKind.Unit)) CheckUnit(draft, silo, c, Add);
        foreach (var c in draft.Of(ConstructKind.UnitRelation)) CheckUnitRelation(draft, silo, c, Add);

        CheckCycles(draft, silo, Add);

        return problems.OrderBy(p => p.Severity).ThenBy(p => p.Kind).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? NameProblem(SchemaConstruct c)
    {
        if (c.Kind == ConstructKind.Unit || c.Kind == ConstructKind.UnitRelation)
        {
            // units are written the way people write them: B, s, $, %, °C
            if (string.IsNullOrWhiteSpace(c.Name)) return "The name is required.";
            if (c.Name.Length > MaxUnitName) return $"The name is longer than {MaxUnitName} characters.";
            if (c.Name.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch))) return "The name cannot contain spaces or control characters.";
            return null;
        }

        return DefinitionNames.Check("The name", c.Name);
    }

    private static void CheckEnum(SchemaDraft draft, SchemaSnapshot silo, SchemaConstruct c, Action<SchemaSeverity, SchemaConstruct?, string> add)
    {
        if (c.Has("type") && !Enum.TryParse<EnumType>(c.Get("type"), true, out _)) add(SchemaSeverity.Error, c, $"\"{c.Get("type")}\" is not a kind of enum value.");

        var others = draft.Of(ConstructKind.Enum).Where(o => !ReferenceEquals(o, c)).Select(o => o.Name);
        string? own = DefinitionNames.Check("The name", c.Name); // judged with the others, once
        foreach (string p in EnumRules.Validate(SchemaRecords.ToEnum(c), others))
            if (p != own && !p.StartsWith("There is already an enum", StringComparison.Ordinal))
                add(SchemaSeverity.Error, c, p);

        if (c.Values.Count == 0) add(SchemaSeverity.Info, c, "The enum has no values yet.");
        if (silo.HasEnum(c.Name)) add(SchemaSeverity.Warning, c, $"The Silo already has an enum named \"{c.Name}\"; promoting will not replace it unless you ask to.");
    }

    private static void CheckSyntax(SchemaDraft draft, SchemaSnapshot silo, SchemaConstruct c, Action<SchemaSeverity, SchemaConstruct?, string> add)
    {
        var others = draft.Of(ConstructKind.Syntax).Where(o => !ReferenceEquals(o, c)).Select(SchemaRecords.ToSyntax).ToList();
        string? own = DefinitionNames.Check("The name", c.Name);
        foreach (string p in SyntaxRules.Validate(SchemaRecords.ToSyntax(c), others))
            if (p != own && !p.StartsWith($"The name \"{c.Name.Trim()}\" is already used", StringComparison.Ordinal))
                add(SchemaSeverity.Error, c, p);

        if (c.Has("kind") && !Enum.TryParse<SyntaxKind>(c.Get("kind"), true, out _)) add(SchemaSeverity.Error, c, $"\"{c.Get("kind")}\" is not a kind of syntax.");
        if (silo.HasSyntax(c.Name)) add(SchemaSeverity.Warning, c, $"The Silo already has a syntax named \"{c.Name}\"; promoting will not replace it unless you ask to.");
    }

    private static void CheckAttribute(SchemaDraft draft, SchemaSnapshot silo, SchemaConstruct c, Action<SchemaSeverity, SchemaConstruct?, string> add)
    {
        string type = c.Get("type", "String");
        string? enumName = SchemaTypes.EnumOf(type);
        EnumRecord? enumRec = null;
        if (enumName is not null)
        {
            var own = draft.Find(ConstructKind.Enum, enumName);
            enumRec = own is not null ? SchemaRecords.ToEnum(own) : silo.Enums.FirstOrDefault(e => string.Equals(e.Name, enumName, StringComparison.OrdinalIgnoreCase));
            if (enumRec is null) add(SchemaSeverity.Error, c, $"The type refers to the enum \"{enumName}\", which is neither in the draft nor in the Silo.");
        }
        else if (!SchemaTypes.TryNormalize(type, out type))
        {
            add(SchemaSeverity.Error, c, $"\"{c.Get("type")}\" is not a type an attribute can have.");
            type = string.Empty;
        }

        if (c.Has("syntax"))
        {
            string syn = c.Get("syntax");
            if (draft.Find(ConstructKind.Syntax, syn) is null && !silo.HasSyntax(syn)) add(SchemaSeverity.Error, c, $"The syntax \"{syn}\" is neither in the draft nor in the Silo.");
            if (type.Length > 0 && enumName is null && type != "String") add(SchemaSeverity.Warning, c, "A syntax is rules for text, but the type is " + type + ".");
            if (enumName is not null) add(SchemaSeverity.Warning, c, "An enum-typed attribute does not need a syntax.");
        }

        if (c.Has("default") && type.Length > 0 || (c.Has("default") && enumRec is not null))
        {
            string def = c.Get("default");
            if (enumRec is not null)
            {
                if (EnumRules.ValueFor(enumRec, def) is null && !(EnumRules.TryNormalizeValue(enumRec.Type, def, out string canonical, out _) && EnumRules.LabelFor(enumRec, canonical) is not null))
                    add(SchemaSeverity.Warning, c, $"The default \"{def}\" is not a label or value of the enum \"{enumRec.Name}\".");
            }
            else if (!SchemaTypes.TryValue(type, def, out string? why))
            {
                add(SchemaSeverity.Error, c, $"The default \"{def}\" {why}.");
            }
        }

        var flags = c.List("flags");
        foreach (string f in flags)
            if (!SchemaProps.AttributeFlags.Contains(f, StringComparer.OrdinalIgnoreCase))
                add(SchemaSeverity.Error, c, $"\"{f}\" is not an attribute flag; the flags are {string.Join(", ", SchemaProps.AttributeFlags)}.");

        var set = SchemaRecords.AttributeFlags(c);
        if ((set & MvAttributeFlags.Key) != 0 && (set & (MvAttributeFlags.Multivalue | MvAttributeFlags.Nullable)) != 0)
            add(SchemaSeverity.Error, c, "A key attribute cannot be multivalued or nullable.");
        if (c.Has("combinator") && (set & MvAttributeFlags.Multivalue) == 0) add(SchemaSeverity.Warning, c, "A combinator only matters for a multivalued attribute.");
        if (!draft.Of(ConstructKind.Class).Any(k => k.Members.Contains(c.Name, StringComparer.OrdinalIgnoreCase)))
            add(SchemaSeverity.Info, c, "No class of the draft has this attribute.");
        if (silo.HasAttribute(c.Name)) add(SchemaSeverity.Warning, c, $"The Silo already has an attribute named \"{c.Name}\".");
    }

    private static void CheckClass(SchemaDraft draft, SchemaSnapshot silo, SchemaConstruct c, Action<SchemaSeverity, SchemaConstruct?, string> add)
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys    = new List<string>();
        foreach (string m in c.Members)
        {
            if (!members.Add(m))
            {
                add(SchemaSeverity.Warning, c, $"The attribute \"{m}\" is listed twice.");
                continue;
            }

            var attr = draft.Find(ConstructKind.Attribute, m);
            if (attr is null)
            {
                if (!silo.HasAttribute(m)) add(SchemaSeverity.Error, c, $"The attribute \"{m}\" is neither in the draft nor in the Silo.");
            }
            else if ((SchemaRecords.AttributeFlags(attr) & MvAttributeFlags.Key) != 0)
            {
                keys.Add(m);
            }
        }

        if (keys.Count > 1) add(SchemaSeverity.Error, c, $"A class has one primary key, but {string.Join(", ", keys)} are all keys.");
        if (c.Members.Count == 0 && !Bases(draft, c.Name).Any()) add(SchemaSeverity.Info, c, "The class has no attributes.");
        else if (keys.Count == 0 && !c.Flag("auxiliary") && !Bases(draft, c.Name).Any() && c.Members.Count > 0) add(SchemaSeverity.Info, c, "The class has no key attribute.");

        // an attribute the class gets again from a base class
        foreach (string baseName in Ancestors(draft, c.Name))
            if (draft.Find(ConstructKind.Class, baseName) is { } b)
                foreach (string dup in b.Members.Intersect(c.Members, StringComparer.OrdinalIgnoreCase))
                    add(SchemaSeverity.Warning, c, $"The attribute \"{dup}\" is already inherited from \"{b.Name}\".");

        if (silo.HasClass(c.Name)) add(SchemaSeverity.Warning, c, $"The Silo already has a class named \"{c.Name}\".");
    }

    private static void CheckRelation(SchemaDraft draft, SchemaSnapshot silo, SchemaConstruct c, Action<SchemaSeverity, SchemaConstruct?, string> add)
    {
        string subject = c.Get("subject"), target = c.Get("target");
        string relation = c.Get("relation", nameof(TypeRelation.Inheritance));
        if (!Enum.TryParse<TypeRelation>(relation, true, out var rel))
        {
            add(SchemaSeverity.Error, c, $"\"{relation}\" is not a kind of type relation.");
            return;
        }

        bool Known(string name) => draft.Find(ConstructKind.Class, name) is not null || silo.HasClass(name);
        if (subject.Length == 0) add(SchemaSeverity.Error, c, "The relation has no subject class.");
        else if (!Known(subject)) add(SchemaSeverity.Error, c, $"The subject \"{subject}\" is neither a class of the draft nor of the Silo.");
        if (target.Length == 0) add(SchemaSeverity.Error, c, "The relation has no target class.");
        else if (!Known(target)) add(SchemaSeverity.Error, c, $"The target \"{target}\" is neither a class of the draft nor of the Silo.");
        if (subject.Length > 0 && string.Equals(subject, target, StringComparison.OrdinalIgnoreCase)) add(SchemaSeverity.Error, c, "A class cannot inherit or include itself.");

        bool targetAux = draft.Find(ConstructKind.Class, target) is { } t ? t.Flag("auxiliary") : silo.AuxiliaryClasses.Contains(target);
        bool targetKnown = draft.Find(ConstructKind.Class, target) is not null || silo.HasClass(target);
        if (targetKnown && rel == TypeRelation.Auxiliary && !targetAux) add(SchemaSeverity.Error, c, $"\"{target}\" is not an auxiliary class, so it cannot be included; mark it auxiliary, or make this an inheritance.");
        if (targetKnown && rel == TypeRelation.Inheritance && targetAux) add(SchemaSeverity.Error, c, $"\"{target}\" is an auxiliary class: it is included, not inherited.");

        int same = draft.Of(ConstructKind.Relation).Count(o => string.Equals(o.Get("subject"), subject, StringComparison.OrdinalIgnoreCase) &&
                                                               string.Equals(o.Get("target"),  target,  StringComparison.OrdinalIgnoreCase) &&
                                                               string.Equals(o.Get("relation", "Inheritance"), relation, StringComparison.OrdinalIgnoreCase));
        if (same > 1) add(SchemaSeverity.Error, c, "The same relation is in the draft more than once.");

        if (rel == TypeRelation.Inheritance && subject.Length > 0)
        {
            int bases = draft.Of(ConstructKind.Relation).Count(o => string.Equals(o.Get("subject"), subject, StringComparison.OrdinalIgnoreCase) &&
                                                                    string.Equals(o.Get("relation", "Inheritance"), nameof(TypeRelation.Inheritance), StringComparison.OrdinalIgnoreCase));
            if (bases > 1 && Ordinal(draft, c) == FirstOf(draft, subject)) add(SchemaSeverity.Warning, c, $"\"{subject}\" inherits more than one class; a world type has only one base class.");
        }
    }

    private static int Ordinal(SchemaDraft d, SchemaConstruct c) => d.Items.IndexOf(c);

    private static int FirstOf(SchemaDraft d, string subject)
    {
        return d.Items.FindIndex(o => o.Kind == ConstructKind.Relation && string.Equals(o.Get("subject"), subject, StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(o.Get("relation", "Inheritance"), nameof(TypeRelation.Inheritance), StringComparison.OrdinalIgnoreCase));
    }

    private static void CheckUnit(SchemaDraft draft, SchemaSnapshot silo, SchemaConstruct c, Action<SchemaSeverity, SchemaConstruct?, string> add)
    {
        if (!Enum.TryParse(c.Get("type", nameof(UnitType.Unit)), true, out UnitType type))
        {
            add(SchemaSeverity.Error, c, $"\"{c.Get("type")}\" is not a kind of unit.");
            return;
        }

        bool hasTarget = c.Has("target"), hasRelation = c.Has("relation");
        switch (type)
        {
            case UnitType.Alias:
                if (!hasTarget) add(SchemaSeverity.Error, c, "An alias needs a target unit.");
                if (hasRelation) add(SchemaSeverity.Error, c, "An alias has no relation.");
                break;
            case UnitType.Unit:
                if (hasRelation) add(SchemaSeverity.Error, c, "A base unit has no relation.");
                break;
            case UnitType.Conversion:
                if (!hasTarget && !hasRelation) add(SchemaSeverity.Error, c, "A conversion needs a target unit or a relation.");
                if (!c.Has("formula")) add(SchemaSeverity.Error, c, "A conversion needs a formula.");
                break;
            case UnitType.Prefix:
            case UnitType.Suffix:
                if (!hasTarget && !hasRelation) add(SchemaSeverity.Error, c, $"A {type.ToString().ToLowerInvariant()} needs a target unit or a relation.");
                break;
        }

        if (hasTarget && draft.Find(ConstructKind.Unit, c.Get("target")) is null && !silo.Units.Contains(c.Get("target"), StringComparer.OrdinalIgnoreCase))
            add(SchemaSeverity.Error, c, $"The target \"{c.Get("target")}\" is neither a unit of the draft nor of the Silo.");
        if (hasRelation && draft.Find(ConstructKind.UnitRelation, c.Get("relation")) is null && !silo.UnitRelations.Contains(c.Get("relation"), StringComparer.OrdinalIgnoreCase))
            add(SchemaSeverity.Error, c, $"The relation \"{c.Get("relation")}\" is neither a unit relation of the draft nor of the Silo.");
        if (hasTarget && string.Equals(c.Get("target"), c.Name, StringComparison.OrdinalIgnoreCase)) add(SchemaSeverity.Error, c, "A unit cannot be its own target.");
        if (c.Has("scale") && c.Number("scale") is null) add(SchemaSeverity.Error, c, $"The scale \"{c.Get("scale")}\" is not a number.");
        else if (c.Number("scale") is 0) add(SchemaSeverity.Warning, c, "A scale of 0 turns every value into 0.");
        if (!c.Has("namePlural")) add(SchemaSeverity.Info, c, "There is no plural name.");
    }

    private static void CheckUnitRelation(SchemaDraft draft, SchemaSnapshot silo, SchemaConstruct c, Action<SchemaSeverity, SchemaConstruct?, string> add)
    {
        if (c.Has("unit") && draft.Find(ConstructKind.Unit, c.Get("unit")) is null && !silo.Units.Contains(c.Get("unit"), StringComparer.OrdinalIgnoreCase))
            add(SchemaSeverity.Error, c, $"The unit \"{c.Get("unit")}\" is neither a unit of the draft nor of the Silo.");
        if (c.Has("next") && draft.Find(ConstructKind.UnitRelation, c.Get("next")) is null && !silo.UnitRelations.Contains(c.Get("next"), StringComparer.OrdinalIgnoreCase))
            add(SchemaSeverity.Error, c, $"The next relation \"{c.Get("next")}\" is neither in the draft nor in the Silo.");
        if (c.Has("scale") && c.Number("scale") is null) add(SchemaSeverity.Error, c, $"The scale \"{c.Get("scale")}\" is not a number.");
        if (!c.Has("unit") && !c.Has("next")) add(SchemaSeverity.Info, c, "The relation has neither a unit nor a next relation.");
    }

    private static void CheckCycles(SchemaDraft draft, SchemaSnapshot silo, Action<SchemaSeverity, SchemaConstruct?, string> add)
    {
        // inheritance: a class that is its own ancestor
        foreach (var c in draft.Of(ConstructKind.Class))
            if (Ancestors(draft, c.Name).Contains(c.Name, StringComparer.OrdinalIgnoreCase))
                add(SchemaSeverity.Error, c, $"\"{c.Name}\" is its own ancestor: the inheritance goes round in a circle.");

        foreach (var u in draft.Of(ConstructKind.Unit))
            if (Chain(u.Name, n => draft.Find(ConstructKind.Unit, n) is { } x && x.Has("target") ? x.Get("target") : null))
                add(SchemaSeverity.Error, u, $"The target of \"{u.Name}\" leads back to itself.");

        foreach (var r in draft.Of(ConstructKind.UnitRelation))
            if (Chain(r.Name, n => draft.Find(ConstructKind.UnitRelation, n) is { } x && x.Has("next") ? x.Get("next") : null))
                add(SchemaSeverity.Error, r, $"The chain of \"{r.Name}\" leads back to itself.");
    }

    private static bool Chain(string start, Func<string, string?> next)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        for (string? at = next(start); at is not null; at = next(at))
            if (!seen.Add(at))
                return seen.Contains(start) && string.Equals(at, start, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>The classes the class inherits directly in the draft.</summary>
    public static IEnumerable<string> Bases(SchemaDraft draft, string className)
    {
        return draft.Of(ConstructKind.Relation)
                    .Where(r => string.Equals(r.Get("subject"), className, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(r.Get("relation", "Inheritance"), nameof(TypeRelation.Inheritance), StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Get("target"))
                    .Where(t => t.Length > 0);
    }

    /// <summary>Every class the class inherits, near to far (a class that is its own ancestor is listed once more at the end of the loop).</summary>
    public static IReadOnlyList<string> Ancestors(SchemaDraft draft, string className)
    {
        var result  = new List<string>();
        var pending = new Queue<string>(Bases(draft, className));
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            string at = pending.Dequeue();
            result.Add(at);
            if (!seen.Add(at)) continue;
            foreach (string b in Bases(draft, at)) pending.Enqueue(b);
        }

        return result;
    }

    /// <summary>The auxiliary classes the class includes directly.</summary>
    public static IEnumerable<string> Includes(SchemaDraft draft, string className)
    {
        return draft.Of(ConstructKind.Relation)
                    .Where(r => string.Equals(r.Get("subject"), className, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(r.Get("relation"), nameof(TypeRelation.Auxiliary), StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Get("target"))
                    .Where(t => t.Length > 0);
    }

    /// <summary>The attributes a class has altogether: its own, then those of the classes it inherits and includes.</summary>
    public static IReadOnlyList<string> AllMembers(SchemaDraft draft, string className)
    {
        var result = new List<string>();
        void Take(IEnumerable<string> names)
        {
            foreach (string n in names)
                if (!result.Contains(n, StringComparer.OrdinalIgnoreCase))
                    result.Add(n);
        }

        if (draft.Find(ConstructKind.Class, className) is { } self) Take(self.Members);
        foreach (string a in Ancestors(draft, className).Distinct(StringComparer.OrdinalIgnoreCase).Take(64))
            if (draft.Find(ConstructKind.Class, a) is { } b)
                Take(b.Members);
        foreach (string i in Includes(draft, className))
            if (draft.Find(ConstructKind.Class, i) is { } x)
                Take(x.Members);
        return result;
    }
}
