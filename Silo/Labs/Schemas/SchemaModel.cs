using System.ComponentModel;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Silo.Labs.Schemas;

/// <summary>The kinds of schema construct a draft holds (the Schema rows of the placement sheet that exist or are near).</summary>
public enum ConstructKind
{
    Class,
    Attribute,
    Enum,
    Syntax,

    /// <summary>An edge between two classes: inheritance or inclusion of an auxiliary class (today's <c>MvTypeHierarchy</c>).</summary>
    Relation,
    Unit,
    UnitRelation
}

/// <summary>Where a draft is promoted to.</summary>
public enum PromoteTarget
{
    /// <summary>The metaverse (the Silo's <c>y</c> database).</summary>
    Meta,

    /// <summary>The built-in local world (the Silo's own <c>w-local</c> database).</summary>
    World
}

public static class ConstructKinds
{
    public static IReadOnlyList<ConstructKind> All { get; } = Enum.GetValues<ConstructKind>();

    /// <summary>What to call one of them on screen.</summary>
    public static string Title(this ConstructKind kind)
    {
        return kind switch
               {
                   ConstructKind.Class        => "Class",
                   ConstructKind.Attribute    => "Attribute",
                   ConstructKind.Enum         => "Enum",
                   ConstructKind.Syntax       => "Syntax",
                   ConstructKind.Relation     => "Type Relation",
                   ConstructKind.Unit         => "Unit",
                   ConstructKind.UnitRelation => "Unit Relation",
                   _                          => kind.ToString()
               };
    }

    public static string Plural(this ConstructKind kind)
    {
        return kind switch
               {
                   ConstructKind.Class    => "Classes",
                   ConstructKind.Syntax   => "Syntaxes",
                   ConstructKind.Relation => "Type Relations",
                   _                      => Title(kind) + "s"
               };
    }

    /// <summary>The one word the terminal uses for it.</summary>
    public static string Word(this ConstructKind kind)
    {
        return kind switch
               {
                   ConstructKind.Relation     => "relation",
                   ConstructKind.UnitRelation => "unitrelation",
                   _                          => kind.ToString().ToLowerInvariant()
               };
    }

    public static bool TryParse(string? text, out ConstructKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string t = text.Trim().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
        foreach (var k in All)
        {
            string w = k.Word();
            string p = k.Plural().Replace(" ", string.Empty).ToLowerInvariant();
            if (t == w || t == p || t == k.Title().Replace(" ", string.Empty).ToLowerInvariant())
            {
                kind = k;
                return true;
            }
        }

        switch (t)
        {
            case "type" or "types":
                kind = ConstructKind.Class;
                return true;
            case "attr" or "attrs" or "field" or "fields":
                kind = ConstructKind.Attribute;
                return true;
            case "enums":
                kind = ConstructKind.Enum;
                return true;
            case "typerelation" or "typerelations" or "hierarchy" or "edge" or "edges":
                kind = ConstructKind.Relation;
                return true;
            case "units":
                kind = ConstructKind.Unit;
                return true;
            case "unitrel" or "unitrelations":
                kind = ConstructKind.UnitRelation;
                return true;
        }

        return false;
    }
}

/// <summary>One known value of an enum in a draft.</summary>
public sealed class SchemaValue
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("value", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public string? Value { get; set; }

    [JsonProperty("description", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public string? Description { get; set; }

    public SchemaValue Clone()
    {
        return new SchemaValue { Name = Name, Value = Value, Description = Description };
    }
}

/// <summary>
///     One construct of a draft. Everything about it other than its name is a text property (see <see cref="SchemaProps" />), so
///     the draft is plain JSON and one editor serves every kind; the typed Silo objects are made from it only when it is promoted.
/// </summary>
public sealed class SchemaConstruct
{
    [JsonProperty("kind")]
    [JsonConverter(typeof(StringEnumConverter))]
    public ConstructKind Kind { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The properties that are set (a property that is empty is not kept).</summary>
    [JsonProperty("props", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public Dictionary<string, string> Props { get; set; } = new(StringComparer.Ordinal);

    /// <summary>For an enum: its known values, in order.</summary>
    [JsonProperty("values", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public List<SchemaValue> Values { get; set; } = [];

    /// <summary>For a class: the names of its attributes, in order.</summary>
    [JsonProperty("members", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public List<string> Members { get; set; } = [];

    public bool ShouldSerializeProps()
    {
        return Props.Count > 0;
    }

    public bool ShouldSerializeValues()
    {
        return Values.Count > 0;
    }

    public bool ShouldSerializeMembers()
    {
        return Members.Count > 0;
    }

    /// <summary>What names it inside the draft: <c>Class:Person</c>.</summary>
    [JsonIgnore]
    public string Ref => $"{Kind}:{Name}";

    public string Get(string key, string fallback = "")
    {
        return Props.TryGetValue(key, out string? v) && !string.IsNullOrEmpty(v) ? v : fallback;
    }

    public bool Has(string key)
    {
        return Props.TryGetValue(key, out string? v) && !string.IsNullOrEmpty(v);
    }

    /// <summary>Sets the property (trimmed); an empty value removes it.</summary>
    public void Set(string key, string? value)
    {
        string v = (value ?? string.Empty).Trim();
        if (v.Length == 0) Props.Remove(key);
        else Props[key] = v;
    }

    /// <summary>The property as a yes/no (true, yes, on, 1).</summary>
    public bool Flag(string key)
    {
        return Get(key).ToLowerInvariant() is "true" or "yes" or "on" or "1";
    }

    /// <summary>The names in a comma list property (<c>Nullable, Indexed</c>).</summary>
    public IReadOnlyList<string> List(string key)
    {
        return Get(key).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    public double? Number(string key)
    {
        return double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
    }

    public SchemaConstruct Clone()
    {
        return new SchemaConstruct
               {
                   Kind    = Kind,
                   Name    = Name,
                   Props   = new Dictionary<string, string>(Props, StringComparer.Ordinal),
                   Values  = Values.Select(v => v.Clone()).ToList(),
                   Members = [..Members]
               };
    }

    public override string ToString() => Ref;
}

/// <summary>
///     A schema being worked on: classes, attributes, enums, syntaxes, type relations and units, kept together so they can be
///     drawn, checked and promoted as one. A draft is scratch (X) until it is promoted; references between constructs are by name.
/// </summary>
public sealed class SchemaDraft
{
    [JsonProperty("name")]
    public string Name { get; set; } = "Untitled schema";

    [JsonProperty("description", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public string? Description { get; set; }

    [JsonProperty("target")]
    [JsonConverter(typeof(StringEnumConverter))]
    [DefaultValue(PromoteTarget.Meta)]
    public PromoteTarget Target { get; set; } = PromoteTarget.Meta;

    [JsonProperty("items")]
    public List<SchemaConstruct> Items { get; set; } = [];

    [JsonIgnore]
    public int Count => Items.Count;

    [JsonIgnore]
    public bool IsEmpty => Items.Count == 0;

    public IEnumerable<SchemaConstruct> Of(ConstructKind kind)
    {
        return Items.Where(i => i.Kind == kind);
    }

    public SchemaConstruct? Find(ConstructKind kind, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        string n = name.Trim();
        return Items.FirstOrDefault(i => i.Kind == kind && string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finds by <c>Kind:Name</c>.</summary>
    public SchemaConstruct? FindRef(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        int colon = reference.IndexOf(':');
        if (colon <= 0 || !Enum.TryParse(reference[..colon], true, out ConstructKind kind)) return null;
        return Find(kind, reference[(colon + 1)..]);
    }

    /// <summary>Adds a new construct. Throws <see cref="InvalidOperationException" /> when the name is empty or taken by another of the same kind.</summary>
    public SchemaConstruct Add(ConstructKind kind, string name)
    {
        string n = (name ?? string.Empty).Trim();
        if (n.Length == 0) throw new InvalidOperationException($"Give the {kind.Title().ToLowerInvariant()} a name.");
        if (Find(kind, n) is not null) throw new InvalidOperationException($"There is already a {kind.Title().ToLowerInvariant()} called \"{n}\".");

        var item = new SchemaConstruct { Kind = kind, Name = n };
        Items.Add(item);
        return item;
    }

    /// <summary>A name for a new construct of the kind that is not taken: <c>Class1</c>, <c>Class2</c> ...</summary>
    public string FreeName(ConstructKind kind, string? stem = null)
    {
        string s = string.IsNullOrWhiteSpace(stem) ? kind.ToString() : stem.Trim();
        for (int i = 1;; i++)
        {
            string candidate = s + i.ToString(CultureInfo.InvariantCulture);
            if (Find(kind, candidate) is null) return candidate;
        }
    }

    /// <summary>
    ///     Removes the construct. A removed class takes its type relations with it, and a removed attribute leaves the classes that
    ///     had it; other references (an attribute naming a removed enum, say) are left for the check to report.
    /// </summary>
    public bool Remove(ConstructKind kind, string name)
    {
        var item = Find(kind, name);
        if (item is null) return false;

        Items.Remove(item);
        switch (kind)
        {
            case ConstructKind.Class:
                Items.RemoveAll(i => i.Kind == ConstructKind.Relation && (Same(i.Get("subject"), item.Name) || Same(i.Get("target"), item.Name)));
                break;
            case ConstructKind.Attribute:
                foreach (var c in Of(ConstructKind.Class)) c.Members.RemoveAll(m => Same(m, item.Name));
                break;
        }

        return true;
    }

    /// <summary>
    ///     Gives the construct a new name and follows every reference to it: a class's members, type relations, an attribute's enum
    ///     and syntax, a unit's target. Throws <see cref="InvalidOperationException" /> when the name is empty or taken.
    /// </summary>
    public void Rename(ConstructKind kind, string name, string newName)
    {
        var item = Find(kind, name) ?? throw new InvalidOperationException($"There is no {kind.Title().ToLowerInvariant()} called \"{name}\".");
        string n = (newName ?? string.Empty).Trim();
        if (n.Length == 0) throw new InvalidOperationException("Give it a name.");
        if (Find(kind, n) is { } other && !ReferenceEquals(other, item)) throw new InvalidOperationException($"There is already a {kind.Title().ToLowerInvariant()} called \"{n}\".");

        string old = item.Name;
        item.Name = n;
        foreach (var i in Items)
            switch (kind)
            {
                case ConstructKind.Class:
                    Follow(i, ConstructKind.Relation, "subject", old, n);
                    Follow(i, ConstructKind.Relation, "target",  old, n);
                    break;
                case ConstructKind.Attribute when i.Kind == ConstructKind.Class:
                    for (int m = 0; m < i.Members.Count; m++)
                        if (Same(i.Members[m], old))
                            i.Members[m] = n;
                    break;
                case ConstructKind.Enum when i.Kind == ConstructKind.Attribute:
                    if (Same(i.Get("type"), "enum:" + old)) i.Set("type", "enum:" + n);
                    break;
                case ConstructKind.Syntax:
                    Follow(i, ConstructKind.Attribute, "syntax", old, n);
                    break;
                case ConstructKind.Unit:
                    Follow(i, ConstructKind.Unit,         "target", old, n);
                    Follow(i, ConstructKind.UnitRelation, "unit",   old, n);
                    break;
                case ConstructKind.UnitRelation:
                    Follow(i, ConstructKind.Unit,         "relation", old, n);
                    Follow(i, ConstructKind.UnitRelation, "next",     old, n);
                    break;
            }

        Relink();
    }

    private static void Follow(SchemaConstruct item, ConstructKind kind, string key, string old, string now)
    {
        if (item.Kind == kind && Same(item.Get(key), old)) item.Set(key, now);
    }

    private static bool Same(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name a type relation is given: <c>Employee inherits Person</c>.</summary>
    public static string RelationName(string subject, string relation, string target)
    {
        string verb = string.Equals(relation, "Auxiliary", StringComparison.OrdinalIgnoreCase) ? "includes" : "inherits";
        return $"{subject} {verb} {target}";
    }

    /// <summary>Gives every type relation the name its two ends and kind make (after one of them changed).</summary>
    public void Relink()
    {
        foreach (var r in Of(ConstructKind.Relation))
            r.Name = RelationName(r.Get("subject"), r.Get("relation", "Inheritance"), r.Get("target"));
    }

    /// <summary>Adds a type relation between two classes (it need not check that they exist; the check reports that).</summary>
    public SchemaConstruct Connect(string subject, string target, bool auxiliary)
    {
        string name = RelationName(subject, auxiliary ? "Auxiliary" : "Inheritance", target);
        var    item = Add(ConstructKind.Relation, name);
        item.Set("subject",  subject);
        item.Set("target",   target);
        item.Set("relation", auxiliary ? "Auxiliary" : "Inheritance");
        return item;
    }

    public SchemaDraft Clone()
    {
        return new SchemaDraft { Name = Name, Description = Description, Target = Target, Items = Items.Select(i => i.Clone()).ToList() };
    }

    /// <summary>How many constructs of each kind (kinds with none are left out).</summary>
    public IReadOnlyList<(ConstructKind Kind, int Count)> Tally()
    {
        return ConstructKinds.All.Select(k => (k, Of(k).Count())).Where(t => t.Item2 > 0).ToList();
    }
}

/// <summary>A draft as JSON: what is kept in the Silo and what is exported.</summary>
public static class SchemaJson
{
    private static readonly JsonSerializerSettings Settings = new()
                                                              {
                                                                  Formatting        = Newtonsoft.Json.Formatting.Indented,
                                                                  NullValueHandling = NullValueHandling.Ignore
                                                              };

    public static string ToJson(SchemaDraft draft)
    {
        return JsonConvert.SerializeObject(draft, Settings);
    }

    /// <summary>Reads a draft; never throws (the reason goes in <paramref name="error" />).</summary>
    public static bool TryFromJson(string? text, out SchemaDraft draft, out string? error)
    {
        draft = new SchemaDraft();
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "There is no JSON.";
            return false;
        }

        try
        {
            var read = JsonConvert.DeserializeObject<SchemaDraft>(text, new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Ignore });
            if (read is null)
            {
                error = "The JSON is empty.";
                return false;
            }

            read.Items ??= [];
            read.Items.RemoveAll(i => i is null);
            foreach (var i in read.Items)
            {
                i.Name    ??= string.Empty;
                i.Props   =   i.Props is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(i.Props, StringComparer.Ordinal);
                i.Values  ??= [];
                i.Members ??= [];
            }

            if (string.IsNullOrWhiteSpace(read.Name)) read.Name = "Untitled schema";
            draft = read;
            return true;
        }
        catch (Exception ex)
        {
            error = ex is JsonException ? ex.Message : "The JSON is not a schema: " + ex.Message;
            return false;
        }
    }
}
