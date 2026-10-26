using Silo.DbModel.Meta;
using Silo.Providers.Data;
using Silo.Providers.Definitions;

namespace Silo.Labs.Schemas;

// The Schema Lab as a model (see LabModel.cs):
//   subject  a draft (SchemaDraft): classes, attributes, enums, syntaxes, type relations and units
//   parts    the constructs of the draft; a class holds its members (attributes), an enum its values
//   aspects  Promotion, Outline and JSON (panes); Class, Attribute, Member, Enum, Value, Syntax (commands of the active part)
//   tools    Connect (a class inherits or includes another), Rename (the selected construct)
public sealed partial class SchemaLabSession
{
    private const string SubjectId = "schema";

    private static readonly string[] AttributeFlagNames = ["Key", "Nullable"];

    private static readonly LabPartType TClass = new("class", "Class", "DataModel", "A kind of thing, with attributes", null,
                                                     [new LabTemplate("class", "Class", "{{name}}|{{kind}}", [new("name", "Name", "NewClass"), new("kind", "Kind", "Plain", ["Plain", "Auxiliary"])], "A class; an auxiliary one supplies attributes to classes that include it")]);

    private static readonly LabPartType TAttribute = new("attribute", "Attribute", "Property", "One thing a class has",
                                                         [new("id", "Id (key)", "Id|Int64|Key", "A number that identifies each one"),
                                                          new("name", "Name", "Name|String", "Text"),
                                                          new("created", "Created", "Created|DateTime", "When it was made"),
                                                          new("modified", "Modified", "Modified|DateTime", "When it last changed"),
                                                          new("active", "Active", "Active|Boolean", "Yes or no")],
                                                         [new LabTemplate("attribute", "Attribute", "{{name}}|{{type}}|{{flags}}", [new("name", "Name", "NewAttribute"), new("type", "Type", "String", SchemaProps.ClrTypes), new("flags", "Flags", "None", ["None", "Key", "Nullable"])], "An attribute of a type")]);

    private static readonly LabPartType TEnum = new("enum", "Enum", "Enum", "A set of named values",
                                                    [new("yesno", "Yes / No", "YesNo|No,Yes"), new("status", "Status", "Status|Draft,Active,Retired")],
                                                    [new LabTemplate("enum", "Enum", "{{name}}|{{values}}", [new("name", "Name", "NewEnum"), new("values", "Values, with commas", "One,Two,Three")], "An enum with its values")]);

    private static readonly LabPartType TSyntax = new("syntax", "Syntax", "Code", "Rules a text value has to follow",
                                                      [new("digits", "Digits", @"Digits|Pattern|^\d+$"), new("word", "Word", @"Word|Pattern|^\w+$"), new("email", "Email address", @"EmailAddress|Pattern|^[^@\s]+@[^@\s]+\.[^@\s]+$")],
                                                      [new LabTemplate("pattern", "Pattern", "{{name}}|Pattern|{{pattern}}", [new("name", "Name", "NewSyntax"), new("pattern", "Pattern", @"^[A-Z]{3}-\d{4}$")], "A pattern all of the text has to match"),
                                                       new LabTemplate("parser", "Built-in parser", "{{name}}|Parser|{{parser}}", [new("name", "Name", "NewSyntax"), new("parser", "Parser", "guid", SyntaxParsers.All.Select(p => p.Key).ToList())], "One of the parsers built into Silo")]);

    private static readonly LabPartType TRelation = new("relation", "Type relation", "HierarchyStructure", "One class inherits or includes another");

    private static readonly LabPartType TUnit = new("unit", "Unit", "Ruler", "A unit of measure", null,
                                                    [new LabTemplate("unit", "Unit", "{{name}}|{{type}}", [new("name", "Name", "NewUnit"), new("type", "Type", "Unit", ["Unit", "Alias", "Prefix", "Conversion"])], "A unit")]);

    private static readonly LabPartType TUnitRel = new("unitrelation", "Unit relation", "Link", "How one unit leads to the next", null,
                                                       [new LabTemplate("unitrelation", "Unit relation", "{{name}}|{{unit}}", [new("name", "Name", "NewChain"), new("unit", "Unit", "")], "A step in a chain of units")]);

    private static readonly LabPartType TMember = new("member", "Member", "Property", "An attribute a class has", null,
                                                      [new LabTemplate("member", "Member", "{{attribute}}", [new("attribute", "Which attribute (made when the draft has none of that name)", "")], "Give the class an attribute")], "class");

    private static readonly LabPartType TValue = new("value", "Value", "Tag", "One value of an enum", null,
                                                     [new LabTemplate("value", "Value", "{{name}}|{{value}}", [new("name", "Name", "NewValue"), new("value", "Its value (blank: the next one)", "")], "A value of the enum")], "enum");

    private bool _followingModel;

    protected override string SubjectNoun => "draft";

    // ---- subject: prefabs and templates ---------------------------------------------------------------

    protected override IEnumerable<LabSubjectType> DefineSubjectTypes()
    {
        var prefabs = SchemaLibrary.Templates.Select((t, i) => new LabPrefab("draft-" + i, t.Title.Split(':')[0], SchemaJson.ToJson(t.Make()), t.Title, "DataModel", "Drafts")).ToList();
        var templates = new List<LabTemplate>
                        {
                            new("entity", "One class with a key and a name", "{{entity}}\nclass {{entity}}\nattribute {{entity}}Id Int64 Key\nattribute {{entity}}Name String\nmember {{entity}} {{entity}}Id\nmember {{entity}} {{entity}}Name",
                                [new("entity", "What the class is called", "Customer")], "A class to start from", "DataModel"),
                            new("lookup", "An enum", "{{name}}\nenum {{name}} {{values}}", [new("name", "Name of the enum", "Status"), new("values", "Its values, with commas", "Draft,Active,Retired")], "A set of named values", "DataModel")
                        };
        yield return new LabSubjectType(SubjectId, "Schema draft", "HierarchyStructure", "Classes, attributes, enums, syntaxes, relations and units, drawn, checked and promoted together", prefabs, templates);
    }

    protected override Task OnNewSubjectAsync(LabSubjectType type, string content)
    {
        SchemaDraft? draft;
        if (content.TrimStart().StartsWith('{'))
        {
            if (!SchemaJson.TryFromJson(content, out draft, out string? error)) throw new InvalidOperationException(error ?? "That is not a schema draft.");
        }
        else
        {
            // a draft name, then a line to a construct: class Name | enum Name a,b,c | attribute Name Type [Flags] | member Class Attribute
            var lines = content.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            draft = new SchemaDraft { Name = lines.Length > 0 ? lines[0].Trim() : "Untitled schema" };
            foreach (string line in lines.Skip(1))
            {
                var words = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 2) continue;

                switch (words[0].ToLowerInvariant())
                {
                    case "class":
                        draft.Add(ConstructKind.Class, words[1]);
                        break;
                    case "enum":
                    {
                        var e = draft.Add(ConstructKind.Enum, words[1]);
                        e.Set("type", nameof(EnumType.Integral));
                        if (words.Length > 2)
                            foreach (string v in words[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                                AddValueNamed(e, v, null);
                        break;
                    }
                    case "attribute":
                    {
                        var a = draft.Add(ConstructKind.Attribute, words[1]);
                        a.Set("type", words.Length > 2 ? words[2] : "String");
                        if (words.Length > 3) a.Set("flags", words[3]);
                        break;
                    }
                    case "member" when words.Length > 2:
                        draft.Find(ConstructKind.Class, words[1])?.Members.Add(words[2]);
                        break;
                }
            }
        }

        StartFrom(draft);
        return Task.CompletedTask;
    }

    private static void AddValueNamed(SchemaConstruct e, string name, string? value)
    {
        string next = string.IsNullOrWhiteSpace(value) ? EnumRules.SuggestNextValue(SchemaRecords.ToEnum(e)) : value.Trim();
        e.Values.Add(new SchemaValue { Name = name.Trim(), Value = next });
    }

    // ---- parts: the constructs, with members and values below ------------------------------------------

    protected override IEnumerable<LabPartType> DefinePartTypes() => [TClass, TAttribute, TEnum, TSyntax, TRelation, TUnit, TUnitRel, TMember, TValue];

    private static LabPartType TypeOf(ConstructKind kind)
    {
        return kind switch
               {
                   ConstructKind.Class        => TClass,
                   ConstructKind.Attribute    => TAttribute,
                   ConstructKind.Enum         => TEnum,
                   ConstructKind.Syntax       => TSyntax,
                   ConstructKind.Relation     => TRelation,
                   ConstructKind.Unit         => TUnit,
                   _                          => TUnitRel
               };
    }

    protected override IEnumerable<LabPart> BuildParts()
    {
        var owned = _draft.Of(ConstructKind.Class).SelectMany(c => c.Members).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _draft.Of(ConstructKind.Class)) yield return ClassPart(c);
        foreach (var e in _draft.Of(ConstructKind.Enum)) yield return EnumPart(e);
        foreach (var s in _draft.Of(ConstructKind.Syntax)) yield return ConstructPart(s, s.Get("kind", "Pattern") == "Parser" ? "parser " + s.Get("parser") : LabUi.Shorten(s.Get("pattern"), 30));
        foreach (var a in _draft.Of(ConstructKind.Attribute).Where(a => !owned.Contains(a.Name))) yield return ConstructPart(a, AttributeDetail(a));
        foreach (var r in _draft.Of(ConstructKind.Relation)) yield return ConstructPart(r, r.Get("relation", "Inheritance"));
        foreach (var u in _draft.Of(ConstructKind.Unit)) yield return ConstructPart(u, u.Get("type", "Unit"));
        foreach (var u in _draft.Of(ConstructKind.UnitRelation)) yield return ConstructPart(u, u.Get("unit"));
    }

    private static LabPart ConstructPart(SchemaConstruct c, string? detail) => new(TypeOf(c.Kind), c.Ref, c.Name, c, detail);

    private static string AttributeDetail(SchemaConstruct a)
    {
        string flags = a.Get("flags");
        return a.Get("type", "String") + (flags.Length > 0 ? ", " + flags : string.Empty);
    }

    private LabPart ClassPart(SchemaConstruct c)
    {
        var part = new LabPart(TClass, c.Ref, c.Name + (c.Flag("auxiliary") ? " (auxiliary)" : string.Empty), c, $"{c.Members.Count} attribute(s)");
        foreach (string m in c.Members)
        {
            var attribute = _draft.Find(ConstructKind.Attribute, m);
            part.Add(new LabPart(TMember, $"{c.Ref}/m:{m}", m, attribute, attribute is null ? "(not in the draft)" : AttributeDetail(attribute)));
        }

        return part;
    }

    private static LabPart EnumPart(SchemaConstruct e)
    {
        var part = new LabPart(TEnum, e.Ref, e.Name, e, $"{e.Values.Count} value(s)");
        foreach (var v in e.Values) part.Add(new LabPart(TValue, $"{e.Ref}/v:{v.Name}", v.Name, v, v.Value));
        return part;
    }

    /// <summary>The part that stands for the selected construct (an attribute that a class has is shown as that class's member).</summary>
    private string? KeyFor(string? reference)
    {
        var item = _draft.FindRef(reference);
        if (item is null) return reference;
        if (item.Kind != ConstructKind.Attribute) return item.Ref;

        if (ActivePart is { } active && active.Type == TMember && ReferenceEquals(active.Tag, item)) return active.Key;

        var owner = _draft.Of(ConstructKind.Class).FirstOrDefault(c => c.Members.Any(m => string.Equals(m, item.Name, StringComparison.OrdinalIgnoreCase)));
        return owner is null ? item.Ref : $"{owner.Ref}/m:{item.Name}";
    }

    /// <summary>The selection changed in the window (the diagram, the editor): the construct is the active part.</summary>
    private void RaiseSelection()
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        if (!_followingModel) SetActivePart(KeyFor(_selected));
    }

    protected override void OnPartActivated(LabPart? part)
    {
        if (part is null) return;

        string? reference = part.Tag switch
                            {
                                SchemaConstruct c => c.Ref,
                                SchemaValue       => (part.Parent?.Tag as SchemaConstruct)?.Ref,
                                _                 => (part.Parent?.Tag as SchemaConstruct)?.Ref
                            };
        if (reference is null) return;

        _followingModel = true;
        try
        {
            Select(reference);
        }
        finally
        {
            _followingModel = false;
        }
    }

    // ---- adding parts ----------------------------------------------------------------------------------

    protected override Task OnAddPartAsync(LabPartType type, LabPart? parent, string content)
    {
        var words = content.Split('|');
        string name = words[0].Trim();
        string Word(int i) => words.Length > i ? words[i].Trim() : string.Empty;
        if (name.Length == 0) throw new InvalidOperationException($"A {type.Name.ToLowerInvariant()} needs a name.");

        SchemaConstruct Made(ConstructKind kind) => AddConstruct(kind, name) ?? throw new InvalidOperationException(Status);

        switch (type.Id)
        {
            case "class":
            {
                var c = Made(ConstructKind.Class);
                if (Word(1).Equals("Auxiliary", StringComparison.OrdinalIgnoreCase)) SetProp("auxiliary", "true");
                break;
            }
            case "attribute":
            {
                // inside a class: the class gets it
                if (parent?.Tag is SchemaConstruct { Kind: ConstructKind.Class } owner) Select(owner.Ref);

                var a = Made(ConstructKind.Attribute);
                if (Word(1).Length > 0) a.Set("type", Word(1));
                if (Word(2).Length > 0 && !Word(2).Equals("None", StringComparison.OrdinalIgnoreCase)) a.Set("flags", Word(2));
                Touched(true);
                break;
            }
            case "enum":
            {
                var e = Made(ConstructKind.Enum);
                foreach (string v in Word(1).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) AddValueNamed(e, v, null);
                Touched(true);
                break;
            }
            case "syntax":
            {
                var s = Made(ConstructKind.Syntax);
                string kind = Word(1).Equals("Parser", StringComparison.OrdinalIgnoreCase) ? "Parser" : "Pattern";
                s.Set("kind", kind);
                // a pattern may have | in it: all that is left of the content is the pattern
                string rest = words.Length > 2 ? string.Join('|', words.Skip(2)) : string.Empty;
                s.Set(kind == "Parser" ? "parser" : "pattern", rest);
                Touched(true);
                break;
            }
            case "unit":
            {
                var u = Made(ConstructKind.Unit);
                u.Set("type", Word(1).Length > 0 ? Word(1) : nameof(UnitType.Unit));
                Touched(true);
                break;
            }
            case "unitrelation":
            {
                var r = Made(ConstructKind.UnitRelation);
                r.Set("unit", Word(1));
                Touched(true);
                break;
            }
            case "member":
            {
                var owner = parent?.Tag as SchemaConstruct ?? (parent?.Parent?.Tag as SchemaConstruct) ?? Selected;
                if (owner is not { Kind: ConstructKind.Class }) throw new InvalidOperationException("Choose the class that is to have it.");

                Select(owner.Ref);
                if (_draft.Find(ConstructKind.Attribute, name) is null)
                {
                    var a = _draft.Add(ConstructKind.Attribute, name);
                    a.Set("type", "String");
                }

                AddMember(name);
                break;
            }
            case "value":
            {
                var owner = parent?.Tag as SchemaConstruct ?? (parent?.Parent?.Tag as SchemaConstruct) ?? Selected;
                if (owner is not { Kind: ConstructKind.Enum }) throw new InvalidOperationException("Choose the enum that is to have it.");

                AddValueNamed(owner, name, Word(1));
                Select(owner.Ref);
                Touched(true);
                break;
            }
            default:
                return base.OnAddPartAsync(type, parent, content);
        }

        return Task.CompletedTask;
    }

    // ---- aspects, tools and commands ---------------------------------------------------------------------

    private bool Has(ConstructKind kind) => Selected?.Kind == kind;

    private (SchemaConstruct Class, int Index)? ActiveMember()
    {
        if (ActivePart is not { } p || p.Type != TMember || p.Parent?.Tag is not SchemaConstruct c) return null;

        int index = c.Members.FindIndex(m => string.Equals(m, p.Name, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : (c, index);
    }

    private (SchemaConstruct Enum, int Index)? ActiveValue()
    {
        if (ActivePart is not { } p || p.Type != TValue || p.Parent?.Tag is not SchemaConstruct e || p.Tag is not SchemaValue v) return null;

        int index = e.Values.IndexOf(v);
        return index < 0 ? null : (e, index);
    }

    private void ToggleFlag(string flag)
    {
        if (Selected is not { Kind: ConstructKind.Attribute } a) return;

        var flags = a.List("flags").ToList();
        int at = flags.FindIndex(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) flags.RemoveAt(at);
        else flags.Add(flag);

        if (SetProp("flags", string.Join(", ", flags)) is { } error) Status = error;
    }

    protected override IEnumerable<LabAspect> DefineAspects()
    {
        var draft = LabOwner.OfSubjectType(SubjectId);
        yield return new LabAspect("promote", "Promotion", draft) { IconKey = "Checklist", Description = "What writing the draft into the Silo would do, and what it did" };
        yield return new LabAspect("outline", "Outline", draft) { IconKey = "Document", Description = "The draft as an outline or as C#" };
        yield return new LabAspect("json", "JSON", draft) { IconKey = "Coding", Description = "The draft as JSON, which is what is kept" };

        yield return new LabAspect("class", "Class", LabOwner.OfPartType(TClass.Id))
                     {
                         IconKey     = "DataModel",
                         Description = "The active class",
                         Commands =
                         [
                             ProvidedCommand.From("class-attribute", "Add _Attribute", NewMember, "TbNew", () => Has(ConstructKind.Class), "Make an attribute and give it to the class"),
                             ProvidedCommand.From("class-auxiliary", "_Auxiliary", () => { if (Selected is { Kind: ConstructKind.Class } c && SetProp("auxiliary", c.Flag("auxiliary") ? string.Empty : "true") is { } e) Status = e; }, null, () => Has(ConstructKind.Class),
                                                  "An auxiliary class supplies attributes to the classes that include it") with { IsChecked = () => Selected is { Kind: ConstructKind.Class } c && c.Flag("auxiliary") }
                         ]
                     };

        yield return new LabAspect("attribute", "Attribute", LabOwner.OfPartType(TAttribute.Id))
                     {
                         IconKey     = "Property",
                         Description = "The active attribute",
                         Commands    = AttributeCommands()
                     };

        yield return new LabAspect("member", "Member", LabOwner.OfPartType(TMember.Id))
                     {
                         IconKey     = "Property",
                         Description = "An attribute of the active class",
                         Commands =
                         [
                             ProvidedCommand.From("member-up", "Move _Up", () => { if (ActiveMember() is var (c, i)) MoveMember(i, -1); }, "TbUp", () => ActiveMember() is var (_, i) && i > 0, "Move it before the one above"),
                             ProvidedCommand.From("member-down", "Move _Down", () => { if (ActiveMember() is var (c, i)) MoveMember(i, 1); }, "TbDown", () => ActiveMember() is var (c, i) && i < c.Members.Count - 1, "Move it after the one below"),
                             ProvidedCommand.From("member-remove", "_Take Out of Class", () => { if (ActiveMember() is var (c, i)) RemoveMember(i); }, "TbDelete", () => ActiveMember() is not null, "The class stops having it (the attribute stays in the draft)") with { IsDestructive = true }
                         ]
                     };

        yield return new LabAspect("enum", "Enum", LabOwner.OfPartType(TEnum.Id))
                     {
                         IconKey     = "Enum",
                         Description = "The active enum",
                         Commands    = [ProvidedCommand.From("enum-value", "Add _Value", AddValue, "TbNew", () => Has(ConstructKind.Enum), "Add a value to the enum")]
                     };

        yield return new LabAspect("value", "Value", LabOwner.OfPartType(TValue.Id))
                     {
                         IconKey     = "Tag",
                         Description = "A value of the active enum",
                         Commands =
                         [
                             ProvidedCommand.From("value-up", "Move _Up", () => { if (ActiveValue() is var (e, i)) MoveValue(i, -1); }, "TbUp", () => ActiveValue() is var (_, i) && i > 0, "Move it before the one above"),
                             ProvidedCommand.From("value-down", "Move _Down", () => { if (ActiveValue() is var (e, i)) MoveValue(i, 1); }, "TbDown", () => ActiveValue() is var (e, i) && i < e.Values.Count - 1, "Move it after the one below"),
                             ProvidedCommand.From("value-remove", "_Remove", () => { if (ActiveValue() is var (e, i)) RemoveValue(i); }, "TbDelete", () => ActiveValue() is not null, "Take the value out of the enum") with { IsDestructive = true }
                         ]
                     };

        yield return new LabAspect("syntax", "Syntax", LabOwner.OfPartType(TSyntax.Id))
                     {
                         IconKey     = "Code",
                         Description = "The active syntax",
                         Commands    = [ProvidedCommand.From("syntax-lab", "Edit in _Syntax Lab", EditInSyntaxLab, "Coding", () => Has(ConstructKind.Syntax), "Open the Syntax Lab with this syntax, to try strings on it")]
                     };
    }

    private IReadOnlyList<ProvidedCommand> AttributeCommands()
    {
        return AttributeFlagNames.Select(flag => ProvidedCommand.From("attribute-" + flag.ToLowerInvariant(), "_" + flag, () => ToggleFlag(flag), null, () => Has(ConstructKind.Attribute), $"Whether the attribute is {flag.ToLowerInvariant()}")
                                                 with { IsChecked = () => Selected is { Kind: ConstructKind.Attribute } a && a.List("flags").Contains(flag, StringComparer.OrdinalIgnoreCase) }).ToList();
    }

    private void EditInSyntaxLab()
    {
        if (Selected is not { Kind: ConstructKind.Syntax } s) return;

        var lab = LabHost.Labs.FirstOrDefault(l => l.Id == "syntax");
        if (lab is null)
        {
            Status = "The Syntax Lab is not available";
            return;
        }

        LabHost.Open(lab, SchemaRecords.ToSyntax(s));
        Status = "Opened the Syntax Lab";
    }

    protected override IEnumerable<LabTool> DefineTools()
    {
        yield return new LabTool("connect", "_Connect", LabOwner.OfPartType(TClass.Id))
                     {
                         IconKey     = "HierarchyStructure",
                         Description = "Make the active class inherit another class, or include an auxiliary one",
                         Parameters = LabParameterSet.For(LabOf.Id, "tool:connect", "Connect",
                                                          [new LabParameter("base", "The other class", LabParameterKind.Text, string.Empty, "The class to inherit or include.", hint: "Person"),
                                                           new LabParameter("auxiliary", "Include it (auxiliary)", LabParameterKind.Bool, false, "Include an auxiliary class instead of inheriting.")]),
                         Commands = [ProvidedCommand.From("connect-run", "_Connect", ConnectFromTool, "HierarchyStructure", () => Has(ConstructKind.Class), "Join the active class to the other one")]
                     };
        yield return new LabTool("rename", "_Rename", LabOwner.OfSubjectType(SubjectId))
                     {
                         IconKey     = "TbEdit",
                         Description = "Give the selected construct another name; whatever refers to it follows",
                         Parameters  = LabParameterSet.For(LabOf.Id, "tool:rename", "Rename", [new LabParameter("name", "New name", LabParameterKind.Text, string.Empty, "What the selected construct is to be called.", hint: "NewName")]),
                         Commands    = [ProvidedCommand.From("rename-run", "_Rename", RenameFromTool, "TbEdit", () => Selected is { Kind: not ConstructKind.Relation }, "Rename the selected construct")]
                     };
    }

    private LabParameterSet? ToolSet(string id) => Tools.FirstOrDefault(t => t.Id == id)?.Parameters;

    private void ConnectFromTool()
    {
        if (Selected is not { Kind: ConstructKind.Class } c) return;

        var set = ToolSet("connect");
        string other = set?["base"].Text ?? string.Empty;
        Status = Connect(c.Name, other.Trim(), set?["auxiliary"].Bool ?? false) ?? Status;
    }

    private void RenameFromTool()
    {
        string name = ToolSet("rename")?["name"].Text ?? string.Empty;
        if (name.Trim().Length == 0)
        {
            Status = "Say the new name in the tool's New name.";
            return;
        }

        Status = RenameSelected(name) ?? $"Renamed to {name.Trim()}";
    }

    protected override IEnumerable<LabCommandEntry> DefineModelCommands()
    {
        yield return new LabCommandEntry(LabOwner.Subject, ProvidedCommand.From("schema-unused", "Remove _Unused Attributes", RemoveUnusedAttributes, "TbDelete",
                                                                                () => _draft.Of(ConstructKind.Attribute).Any(IsUnused), "Take out the attributes that no class has") with { IsDestructive = true });
    }

    private bool IsUnused(SchemaConstruct a) => !_draft.Of(ConstructKind.Class).Any(c => c.Members.Any(m => string.Equals(m, a.Name, StringComparison.OrdinalIgnoreCase)));

    private void RemoveUnusedAttributes()
    {
        var unused = _draft.Of(ConstructKind.Attribute).Where(IsUnused).ToList();
        if (unused.Count == 0) return;

        foreach (var a in unused) _draft.Remove(ConstructKind.Attribute, a.Name);
        if (_selected is not null && _draft.FindRef(_selected) is null) _selected = _draft.Items.FirstOrDefault(i => i.Kind != ConstructKind.Relation)?.Ref;
        Touched(true);
        RaiseSelection();
        Status = $"Took out {unused.Count} attribute(s) no class had";
    }
}
