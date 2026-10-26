using Silo.DbModel.Meta;
using Silo.Labs.Schemas;
using Silo.Providers.Data;
using Silo.Providers.Definitions;

namespace Silo.Tests.Labs;

public class SchemaLabTests
{
    // --- stand-ins for the Silo's stores: they apply the same rules the real ones do ---

    private sealed class FakeSyntaxes : ISyntaxStore
    {
        public List<SyntaxRecord> Rows { get; } = [];
        public bool Refuse { get; set; }
        private int _next = 1;

        public IReadOnlyList<SyntaxRecord> GetSyntaxes() => Rows.OrderBy(r => r.Name).ToList();
        public SyntaxRecord? GetSyntax(int id) => Rows.FirstOrDefault(r => r.Id == id);
        public SyntaxRecord? FindSyntax(string nameOrAlias) => Rows.FirstOrDefault(r => string.Equals(r.Name, nameOrAlias, StringComparison.OrdinalIgnoreCase));

        public SyntaxRecord SaveSyntax(SyntaxRecord syntax)
        {
            if (Refuse) throw new DefinitionException("The store said no.");
            var others = Rows.Where(r => r.Id != syntax.Id);
            var ok     = SyntaxRules.Require(syntax, others);
            if (ok.Id == 0) ok = ok with { Id = _next++ };
            Rows.RemoveAll(r => r.Id == ok.Id);
            Rows.Add(ok);
            return ok;
        }

        public bool DeleteSyntax(int id) => Rows.RemoveAll(r => r.Id == id) > 0;
        public int RestoreDefaults() => 0;
    }

    private sealed class FakeEnums : IEnumStore
    {
        public List<EnumRecord> Rows { get; } = [];
        private int _next = 1;

        public IReadOnlyList<EnumRecord> GetEnums() => Rows.OrderBy(r => r.Name).ToList();
        public EnumRecord? GetEnum(int id) => Rows.FirstOrDefault(r => r.Id == id);
        public EnumRecord? FindEnum(string name) => Rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

        public EnumRecord SaveEnum(EnumRecord definition)
        {
            var ok = EnumRules.Require(definition, Rows.Where(r => r.Id != definition.Id).Select(r => r.Name));
            if (ok.Id == 0) ok = ok with { Id = _next++ };
            Rows.RemoveAll(r => r.Id == ok.Id);
            Rows.Add(ok);
            return ok;
        }

        public bool DeleteEnum(int id) => Rows.RemoveAll(r => r.Id == id) > 0;
        public EnumValueRecord SaveEnumValue(int enumId, EnumValueRecord value) => value;
        public bool DeleteEnumValue(int valueId) => false;
    }

    private sealed class FakeSchema : ISchemaStore
    {
        public SchemaChanges? Got { get; private set; }
        public bool Fail { get; set; }
        public SchemaSnapshot GetSnapshot() => SchemaSnapshot.Empty;

        public void Apply(SchemaChanges changes)
        {
            if (Fail) throw new InvalidOperationException("The database is read-only.");
            Got = changes;
        }
    }

    private static SchemaDraft People() => SchemaLibrary.Templates[0].Make();
    private static SchemaDraft Orders() => SchemaLibrary.Templates[1].Make();
    private static SchemaDraft Units() => SchemaLibrary.Templates[2].Make();

    private static List<SchemaProblem> Errors(SchemaDraft d, SchemaSnapshot? silo = null) => SchemaCheck.Validate(d, silo).Where(p => p.Severity == SchemaSeverity.Error).ToList();

    private static string Texts(IEnumerable<SchemaProblem> problems) => string.Join(" | ", problems.Select(p => p.Where + ": " + p.Text));

    private static SchemaConstruct Attr(SchemaDraft d, string name, string type = "String", string? flags = null)
    {
        var a = d.Add(ConstructKind.Attribute, name);
        a.Set("type",  type);
        a.Set("flags", flags);
        return a;
    }

    // --- the draft ---

    [Test]
    public void Names_are_unique_per_kind_but_may_repeat_across_kinds()
    {
        var d = new SchemaDraft();
        d.Add(ConstructKind.Class,     "Thing");
        d.Add(ConstructKind.Attribute, "Thing");
        Assert.Throws<InvalidOperationException>(() => d.Add(ConstructKind.Class, "thing"));
        Assert.Throws<InvalidOperationException>(() => d.Add(ConstructKind.Class, "  "));
        Assert.That(d.Find(ConstructKind.Class, " THING ")?.Name, Is.EqualTo("Thing"));
        Assert.That(d.FreeName(ConstructKind.Class), Is.EqualTo("Class1"));
        d.Add(ConstructKind.Class, "Class1");
        Assert.That(d.FreeName(ConstructKind.Class), Is.EqualTo("Class2"));
    }

    [Test]
    public void Renaming_follows_every_reference()
    {
        var d = People();
        d.Rename(ConstructKind.Class, "Person", "Human");
        Assert.That(d.Of(ConstructKind.Relation).Select(r => r.Name), Does.Contain("Employee inherits Human"));
        Assert.That(d.Of(ConstructKind.Relation).Select(r => r.Name), Does.Contain("Human includes Auditable"));
        Assert.That(SchemaCheck.Bases(d, "Employee").Single(), Is.EqualTo("Human"));

        d.Rename(ConstructKind.Attribute, "Name", "FullName");
        Assert.That(d.Find(ConstructKind.Class, "Human")!.Members, Does.Contain("FullName"));

        d.Rename(ConstructKind.Enum, "Role", "Job");
        Assert.That(d.Find(ConstructKind.Attribute, "Role")!.Get("type"), Is.EqualTo("enum:Job"));

        d.Rename(ConstructKind.Syntax, "EmailAddress", "Mail");
        Assert.That(d.Find(ConstructKind.Attribute, "Email")!.Get("syntax"), Is.EqualTo("Mail"));
        Assert.That(Errors(d), Is.Empty, Texts(Errors(d)));

        var u = Units();
        u.Rename(ConstructKind.Unit, "B", "Byte");
        Assert.That(u.Find(ConstructKind.Unit, "KB")!.Get("target"), Is.EqualTo("Byte"));
        u.Rename(ConstructKind.UnitRelation, "MegaChain", "Mega");
        Assert.That(u.Find(ConstructKind.UnitRelation, "KiloChain")!.Get("next"), Is.EqualTo("Mega"));
        Assert.That(Errors(u), Is.Empty, Texts(Errors(u)));
    }

    [Test]
    public void A_name_that_is_taken_cannot_be_given()
    {
        var d = People();
        Assert.Throws<InvalidOperationException>(() => d.Rename(ConstructKind.Class, "Employee", "person"));
        Assert.Throws<InvalidOperationException>(() => d.Rename(ConstructKind.Class, "Nobody", "Somebody"));
        d.Rename(ConstructKind.Class, "Employee", "EMPLOYEE"); // only the case changes: that is allowed
        Assert.That(d.Find(ConstructKind.Class, "employee")!.Name, Is.EqualTo("EMPLOYEE"));
    }

    [Test]
    public void Removing_a_class_takes_its_relations_and_removing_an_attribute_leaves_its_classes()
    {
        var d = People();
        Assert.That(d.Remove(ConstructKind.Class, "Person"), Is.True);
        Assert.That(d.Of(ConstructKind.Relation).Count(), Is.EqualTo(0));

        Assert.That(d.Remove(ConstructKind.Attribute, "Hired"), Is.True);
        Assert.That(d.Find(ConstructKind.Class, "Employee")!.Members, Does.Not.Contain("Hired"));
        Assert.That(d.Remove(ConstructKind.Attribute, "Hired"), Is.False);
    }

    [Test]
    public void A_draft_goes_to_JSON_and_back_without_noise()
    {
        var d    = People();
        string j = SchemaJson.ToJson(d);
        Assert.That(j, Does.Not.Contain("\"values\": []"));
        Assert.That(j, Does.Not.Contain("\"members\": []"));
        Assert.That(j, Does.Not.Contain("\"props\": {}"));
        Assert.That(SchemaJson.TryFromJson(j, out var back, out var error), Is.True, error);
        Assert.That(back.Count,     Is.EqualTo(d.Count));
        Assert.That(SchemaJson.ToJson(back), Is.EqualTo(j));
        Assert.That(back.Find(ConstructKind.Enum, "Role")!.Values.Select(v => v.Name), Is.EqualTo(new[] { "Staff", "Manager", "Admin" }));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("{")]
    [TestCase("[1,2]")]
    [TestCase("{\"items\":[{\"kind\":\"Banana\"}]}")]
    public void JSON_that_is_not_a_draft_is_refused_with_a_reason(string text)
    {
        Assert.That(SchemaJson.TryFromJson(text, out _, out var error), Is.False);
        Assert.That(error, Is.Not.Empty);
    }

    [Test]
    public void JSON_with_missing_parts_still_makes_a_usable_draft()
    {
        Assert.That(SchemaJson.TryFromJson("{\"items\":[{\"kind\":\"Class\",\"name\":\"Lone\"}]}", out var d, out _), Is.True);
        Assert.That(d.Name, Is.EqualTo("Untitled schema"));
        var c = d.Find(ConstructKind.Class, "Lone")!;
        Assert.That(c.Members, Is.Empty);
        Assert.That(c.Get("anything", "fallback"), Is.EqualTo("fallback"));
    }

    [TestCase("class", ConstructKind.Class)]
    [TestCase("Classes", ConstructKind.Class)]
    [TestCase("attr", ConstructKind.Attribute)]
    [TestCase("Type Relation", ConstructKind.Relation)]
    [TestCase("hierarchy", ConstructKind.Relation)]
    [TestCase("unit-relation", ConstructKind.UnitRelation)]
    [TestCase("syntaxes", ConstructKind.Syntax)]
    [TestCase("ENUMS", ConstructKind.Enum)]
    public void The_terminal_words_for_a_kind_are_understood(string word, ConstructKind kind)
    {
        Assert.That(ConstructKinds.TryParse(word, out var k), Is.True);
        Assert.That(k, Is.EqualTo(kind));
    }

    [Test]
    public void Setting_a_property_checks_it_against_what_the_property_is()
    {
        var d = new SchemaDraft();
        var a = d.Add(ConstructKind.Attribute, "Count");

        Assert.That(SchemaProps.TrySet(d, a, "type", "int"), Is.Null);
        Assert.That(a.Get("type"), Is.EqualTo("Int32"));
        Assert.That(SchemaProps.TrySet(d, a, "type", "enum: Colour "), Is.Null);
        Assert.That(a.Get("type"), Is.EqualTo("enum:Colour"));
        Assert.That(SchemaProps.TrySet(d, a, "type", "banana"), Does.Contain("not a type"));

        Assert.That(SchemaProps.TrySet(d, a, "flags", "nullable, indexed"), Is.Null);
        Assert.That(a.Get("flags"), Is.EqualTo("Nullable, Indexed"));
        Assert.That(SchemaProps.TrySet(d, a, "flags", "nullable, sparkly"), Does.Contain("sparkly"));
        Assert.That(a.Get("flags"), Is.EqualTo("Nullable, Indexed")); // a refused value changes nothing

        Assert.That(SchemaProps.TrySet(d, a, "nonsense", "x"), Does.Contain("no property"));

        var c = d.Add(ConstructKind.Class, "Thing");
        Assert.That(SchemaProps.TrySet(d, c, "auxiliary", "yes"), Is.Null);
        Assert.That(c.Get("auxiliary"), Is.EqualTo("true"));
        Assert.That(SchemaProps.TrySet(d, c, "auxiliary", "no"), Is.Null);
        Assert.That(c.Has("auxiliary"), Is.False);
        Assert.That(SchemaProps.TrySet(d, c, "auxiliary", "perhaps"), Does.Contain("yes or no"));

        var u = d.Add(ConstructKind.Unit, "m");
        Assert.That(SchemaProps.TrySet(d, u, "scale", "2.5"), Is.Null);
        Assert.That(SchemaProps.TrySet(d, u, "scale", "lots"), Does.Contain("not a number"));
        Assert.That(SchemaProps.TrySet(d, u, "type", "alias"), Is.Null);
        Assert.That(u.Get("type"), Is.EqualTo("Alias"));
        Assert.That(SchemaProps.TrySet(d, u, "type", "tall"), Does.Contain("Alias"));
    }

    [Test]
    public void Changing_the_ends_of_a_relation_renames_it()
    {
        var d = People();
        var r = d.Of(ConstructKind.Relation).First(x => x.Get("subject") == "Employee");
        Assert.That(SchemaProps.TrySet(d, r, "relation", "auxiliary"), Is.Null);
        Assert.That(r.Name, Is.EqualTo("Employee includes Person"));
    }

    // --- the check ---

    [Test]
    public void The_templates_are_free_of_errors()
    {
        foreach (var (title, make) in SchemaLibrary.Templates)
        {
            var d = make();
            Assert.That(Errors(d), Is.Empty, title + ": " + Texts(Errors(d)));
            Assert.That(d.Count, Is.GreaterThan(3));
        }
    }

    [Test]
    public void An_empty_draft_is_only_a_note()
    {
        var p = SchemaCheck.Validate(new SchemaDraft());
        Assert.That(p.Count, Is.EqualTo(1));
        Assert.That(p[0].Severity, Is.EqualTo(SchemaSeverity.Info));
    }

    [TestCase("1st")]
    [TestCase("has space")]
    [TestCase("")]
    public void A_name_the_Silo_would_refuse_is_an_error(string name)
    {
        var d = new SchemaDraft();
        d.Items.Add(new SchemaConstruct { Kind = ConstructKind.Class, Name = name });
        Assert.That(Errors(d).Any(p => p.Text.Contains("name")), Is.True);
    }

    [Test]
    public void Units_are_named_the_way_people_write_them()
    {
        var d = new SchemaDraft();
        d.Add(ConstructKind.Unit, "°C");
        d.Add(ConstructKind.Unit, "$");
        d.Add(ConstructKind.Unit, "%");
        Assert.That(Errors(d).Where(p => p.Text.Contains("name")), Is.Empty);
        d.Items.Add(new SchemaConstruct { Kind = ConstructKind.Unit, Name = "square metre" });
        Assert.That(Errors(d).Any(p => p.Text.Contains("spaces")), Is.True);
    }

    [Test]
    public void A_name_used_twice_in_the_draft_is_an_error()
    {
        var d = new SchemaDraft();
        d.Items.Add(new SchemaConstruct { Kind = ConstructKind.Enum, Name = "Colour" });
        d.Items.Add(new SchemaConstruct { Kind = ConstructKind.Enum, Name = "colour" });
        Assert.That(Errors(d).Any(p => p.Text.Contains("more than one")), Is.True);
    }

    [Test]
    public void Enums_are_judged_by_the_Silos_own_rules()
    {
        var d = new SchemaDraft();
        var e = d.Add(ConstructKind.Enum, "Flags");
        e.Set("type", "String");
        e.Set("flags", "Bitmask");
        e.Values.AddRange([new SchemaValue { Name = "A", Value = "x" }, new SchemaValue { Name = "a", Value = "y" }, new SchemaValue { Name = "B", Value = "x" }]);
        string all = Texts(Errors(d));
        Assert.That(all, Does.Contain("bitmask"));
        Assert.That(all, Does.Contain("more than one value"));
        Assert.That(all, Does.Contain("both"));

        var n = d.Add(ConstructKind.Enum, "Num");
        n.Values.Add(new SchemaValue { Name = "Bad", Value = "twelve" });
        Assert.That(Texts(Errors(d)), Does.Contain("whole number"));
    }

    [Test]
    public void A_syntax_whose_pattern_or_example_is_wrong_is_an_error()
    {
        var d = new SchemaDraft();
        var s = d.Add(ConstructKind.Syntax, "Broken");
        s.Set("kind",    "Pattern");
        s.Set("pattern", "([unclosed");
        Assert.That(Errors(d).Any(p => p.Kind == ConstructKind.Syntax), Is.True);

        s.Set("pattern", @"^\d+$");
        s.Set("example", "abc");
        Assert.That(Texts(Errors(d)), Does.Contain("example"));
        s.Set("example", "123");
        Assert.That(Errors(d), Is.Empty, Texts(Errors(d)));

        s.Set("kind", "Banana");
        Assert.That(Texts(Errors(d)), Does.Contain("not a kind of syntax"));
    }

    [Test]
    public void Attributes_with_unknown_references_or_wrong_defaults_are_errors()
    {
        var d = new SchemaDraft();
        var a = Attr(d, "Size", "enum:Nope");
        Assert.That(Texts(Errors(d)), Does.Contain("Nope"));

        a.Set("type", "Int32");
        a.Set("default", "many");
        Assert.That(Texts(Errors(d)), Does.Contain("many"));
        a.Set("default", "12");
        Assert.That(Errors(d), Is.Empty);

        a.Set("syntax", "Missing");
        Assert.That(Texts(Errors(d)), Does.Contain("Missing"));
        a.Set("syntax", null);

        a.Set("flags", "Key, Nullable");
        Assert.That(Texts(Errors(d)), Does.Contain("key attribute cannot"));
        a.Set("flags", "Key");

        a.Set("type", "Uri");
        a.Set("default", "not a uri");
        Assert.That(Texts(Errors(d)), Does.Contain("absolute URI"));
    }

    [Test]
    public void An_enum_default_may_be_a_label_or_a_value()
    {
        var d = People();
        var role = d.Find(ConstructKind.Attribute, "Role")!;
        role.Set("default", "Manager");
        Assert.That(SchemaCheck.Validate(d).Any(p => p.Text.Contains("default")), Is.False);
        role.Set("default", "2");
        Assert.That(SchemaCheck.Validate(d).Any(p => p.Text.Contains("default")), Is.False);
        role.Set("default", "Janitor");
        Assert.That(SchemaCheck.Validate(d).Any(p => p.Severity == SchemaSeverity.Warning && p.Text.Contains("Janitor")), Is.True);
    }

    [Test]
    public void A_class_with_two_keys_or_a_missing_attribute_is_an_error()
    {
        var d = new SchemaDraft();
        Attr(d, "A", "Int32", "Key");
        Attr(d, "B", "Int32", "Key");
        var c = d.Add(ConstructKind.Class, "Thing");
        c.Members.AddRange(["A", "B", "Ghost"]);
        string all = Texts(Errors(d));
        Assert.That(all, Does.Contain("one primary key"));
        Assert.That(all, Does.Contain("Ghost"));
    }

    [Test]
    public void A_class_may_have_a_key_a_relative_key_and_an_absolute_key()
    {
        var d = new SchemaDraft();
        Attr(d, "Id",                "Guid",   "Key");
        Attr(d, "cn",                "String", "RelativeKey");
        Attr(d, "distinguishedName", "String", "AbsoluteKey");
        var c = d.Add(ConstructKind.Class, "entry");
        c.Members.AddRange(["Id", "cn", "distinguishedName"]);
        Assert.That(Errors(d), Is.Empty, Texts(Errors(d)));
        Assert.That(SchemaCheck.Validate(d).Any(p => p.Text.Contains("no absolute key") || p.Text.Contains("no relative key")), Is.False);

        Attr(d, "Name", "String", "RelativeKey");
        c.Members.Add("Name");
        Assert.That(Texts(Errors(d)), Does.Contain("one relative key"));
        c.Members.Remove("Name");

        d.Find(ConstructKind.Attribute, "cn")!.Set("flags", "RelativeKey, AbsoluteKey");
        Assert.That(Texts(Errors(d)), Does.Contain("both the relative key"));

        d.Find(ConstructKind.Attribute, "cn")!.Set("flags", "RelativeKey, Nullable");
        Assert.That(Texts(Errors(d)), Does.Contain("relative or absolute key cannot"));
    }

    [Test]
    public void An_attribute_listed_twice_or_repeated_from_a_base_is_a_warning()
    {
        var d = new SchemaDraft();
        Attr(d, "A", "Int32", "Key");
        var b = d.Add(ConstructKind.Class, "Base");
        b.Members.Add("A");
        var c = d.Add(ConstructKind.Class, "Child");
        c.Members.AddRange(["A", "A"]);
        d.Connect("Child", "Base", false);
        var w = SchemaCheck.Validate(d).Where(p => p.Severity == SchemaSeverity.Warning).Select(p => p.Text).ToList();
        Assert.That(w.Any(t => t.Contains("listed twice")), Is.True);
        Assert.That(w.Any(t => t.Contains("already inherited")), Is.True);
    }

    [Test]
    public void Relations_between_classes_are_held_to_the_hierarchy_rules()
    {
        var d = new SchemaDraft();
        d.Add(ConstructKind.Class, "A");
        d.Add(ConstructKind.Class, "B");
        var aux = d.Add(ConstructKind.Class, "Aux");
        aux.Set("auxiliary", "true");

        d.Connect("A", "A", false);
        Assert.That(Texts(Errors(d)), Does.Contain("itself"));
        d.Remove(ConstructKind.Class, "A");
        d.Add(ConstructKind.Class, "A");

        d.Connect("A", "Aux", false);
        Assert.That(Texts(Errors(d)), Does.Contain("included, not inherited"));
        d.Items.RemoveAll(i => i.Kind == ConstructKind.Relation);

        d.Connect("A", "B", true);
        Assert.That(Texts(Errors(d)), Does.Contain("not an auxiliary class"));
        d.Items.RemoveAll(i => i.Kind == ConstructKind.Relation);

        d.Connect("A", "Nobody", false);
        Assert.That(Texts(Errors(d)), Does.Contain("Nobody"));
    }

    [Test]
    public void Inheritance_that_goes_round_in_a_circle_is_an_error_and_does_not_hang()
    {
        var d = new SchemaDraft();
        d.Add(ConstructKind.Class, "A");
        d.Add(ConstructKind.Class, "B");
        d.Add(ConstructKind.Class, "C");
        d.Connect("A", "B", false);
        d.Connect("B", "C", false);
        d.Connect("C", "A", false);
        var errors = Errors(d).Where(p => p.Text.Contains("circle")).ToList();
        Assert.That(errors.Count, Is.EqualTo(3));
        Assert.That(SchemaLayout.Compute(d).Nodes.Count, Is.EqualTo(3));
        Assert.That(SchemaCheck.AllMembers(d, "A"), Is.Empty);
    }

    [Test]
    public void A_class_with_two_base_classes_is_warned_once()
    {
        var d = new SchemaDraft();
        d.Add(ConstructKind.Class, "A");
        d.Add(ConstructKind.Class, "B");
        d.Add(ConstructKind.Class, "C");
        d.Connect("C", "A", false);
        d.Connect("C", "B", false);
        Assert.That(SchemaCheck.Validate(d).Count(p => p.Text.Contains("more than one class")), Is.EqualTo(1));
    }

    [Test]
    public void Units_follow_the_Silos_rules_for_their_kind()
    {
        var d = new SchemaDraft();
        var alias = d.Add(ConstructKind.Unit, "octet");
        alias.Set("type", "Alias");
        Assert.That(Texts(Errors(d)), Does.Contain("needs a target"));

        var conv = d.Add(ConstructKind.Unit, "MB");
        conv.Set("type",   "Conversion");
        conv.Set("target", "octet");
        Assert.That(Texts(Errors(d)), Does.Contain("needs a formula"));
        conv.Set("formula", "? * 2");
        conv.Set("scale",   "zero");
        Assert.That(Texts(Errors(d)), Does.Contain("scale"));
        conv.Set("scale", "0");
        Assert.That(SchemaCheck.Validate(d).Any(p => p.Severity == SchemaSeverity.Warning && p.Text.Contains("0")), Is.True);

        var pre = d.Add(ConstructKind.Unit, "K");
        pre.Set("type", "Prefix");
        Assert.That(Texts(Errors(d)), Does.Contain("needs a target"));

        var baseUnit = d.Add(ConstructKind.Unit, "B");
        baseUnit.Set("relation", "R");
        Assert.That(Texts(Errors(d)), Does.Contain("no relation"));
    }

    [Test]
    public void Unit_targets_and_relation_chains_that_loop_are_errors()
    {
        var d = new SchemaDraft();
        var a = d.Add(ConstructKind.Unit, "a");
        var b = d.Add(ConstructKind.Unit, "b");
        a.Set("type", "Alias");
        b.Set("type", "Alias");
        a.Set("target", "b");
        b.Set("target", "a");
        Assert.That(Errors(d).Count(p => p.Text.Contains("leads back")), Is.EqualTo(2));

        var r1 = d.Add(ConstructKind.UnitRelation, "r1");
        var r2 = d.Add(ConstructKind.UnitRelation, "r2");
        r1.Set("next", "r2");
        r2.Set("next", "r1");
        Assert.That(Errors(d).Count(p => p.Text.Contains("chain")), Is.EqualTo(2));
    }

    [Test]
    public void What_the_Silo_has_is_known_to_the_check()
    {
        var silo = new SchemaSnapshot
                   {
                       Enums    = [EnumRecord.Create("Colour")],
                       Syntaxes = [SyntaxRecord.ForPattern("Zip", @"^\d{5}$", alias: "zip5")],
                       Classes  = ["Person"],
                       Attributes = ["Id"],
                       Units    = ["B"]
                   };
        var d = new SchemaDraft();
        var a = Attr(d, "Shade", "enum:Colour");
        a.Set("syntax", "zip5");
        var c = d.Add(ConstructKind.Class, "Student");
        c.Members.Add("Id");
        d.Connect("Student", "Person", false);
        var kb = d.Add(ConstructKind.Unit, "KB");
        kb.Set("type",   "Prefix");
        kb.Set("target", "B");

        Assert.That(Errors(d, silo), Is.Empty, Texts(Errors(d, silo)));
        Assert.That(Errors(d, null).Count, Is.GreaterThan(3));

        var dup = new SchemaDraft();
        dup.Add(ConstructKind.Enum, "Colour").Values.Add(new SchemaValue { Name = "Red", Value = "0" });
        Assert.That(SchemaCheck.Validate(dup, silo).Any(p => p.Severity == SchemaSeverity.Warning && p.Text.Contains("already has")), Is.True);
    }

    [Test]
    public void The_members_of_a_class_include_what_it_inherits_and_includes()
    {
        var d = People();
        Assert.That(SchemaCheck.AllMembers(d, "Employee"), Is.EqualTo(new[] { "Hired", "Role", "Id", "Name", "Email" }));
        Assert.That(SchemaCheck.AllMembers(d, "Person"),   Is.EqualTo(new[] { "Id", "Name", "Email", "Modified" }));
        Assert.That(SchemaCheck.Ancestors(d, "Employee"),  Is.EqualTo(new[] { "Person" }));
    }

    // --- the drawing ---

    private static bool Overlap(LayoutNode a, LayoutNode b) => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    [Test]
    public void A_base_class_is_drawn_above_what_inherits_it()
    {
        var r      = SchemaLayout.Compute(People());
        var person = r.Node("Class:Person")!;
        var emp    = r.Node("Class:Employee")!;
        Assert.That(emp.Y, Is.GreaterThan(person.Y + person.Height - 1));
        Assert.That(r.Edges.Any(e => e.From == emp.Ref && e.To == person.Ref && e.Kind == EdgeKind.Inherits), Is.True);
        Assert.That(r.Edges.Any(e => e.From == person.Ref && e.To == "Class:Auditable" && e.Kind == EdgeKind.Includes), Is.True);
    }

    [Test]
    public void No_two_boxes_overlap_and_everything_stays_inside_the_drawing()
    {
        foreach (var (title, make) in SchemaLibrary.Templates)
        {
            var r = SchemaLayout.Compute(make());
            for (int i = 0; i < r.Nodes.Count; i++)
            {
                Assert.That(r.Nodes[i].X + r.Nodes[i].Width,  Is.AtMost(r.Width + 0.001), title);
                Assert.That(r.Nodes[i].Y + r.Nodes[i].Height, Is.AtMost(r.Height),        title);
                for (int j = i + 1; j < r.Nodes.Count; j++) Assert.That(Overlap(r.Nodes[i], r.Nodes[j]), Is.False, $"{title}: {r.Nodes[i].Ref} / {r.Nodes[j].Ref}");
            }
        }
    }

    [Test]
    public void Many_boxes_wrap_instead_of_running_off_to_the_right()
    {
        var d = new SchemaDraft();
        for (int i = 0; i < 40; i++) Attr(d, "Attribute" + i);
        var r = SchemaLayout.Compute(d, new SchemaLayoutOptions { MaxWidth = 700 });
        Assert.That(r.Nodes.Max(n => n.X + n.Width), Is.AtMost(700d + 340d));
        Assert.That(r.Nodes.Select(n => n.Y).Distinct().Count(), Is.GreaterThan(3));
    }

    [Test]
    public void A_class_lists_its_attributes_and_marks_the_key()
    {
        var node = SchemaLayout.Compute(People()).Node("Class:Person")!;
        Assert.That(node.Lines[0], Does.StartWith("# Id : Int64"));
        Assert.That(node.Lines[1], Does.StartWith("  Name : String"));
        Assert.That(SchemaLayout.Compute(People(), new SchemaLayoutOptions { ShowMembers = false }).Node("Class:Person")!.Lines, Is.Empty);
    }

    [Test]
    public void A_long_list_is_cut_with_a_count_of_what_is_left_out()
    {
        var d = new SchemaDraft();
        var e = d.Add(ConstructKind.Enum, "Big");
        for (int i = 0; i < 25; i++) e.Values.Add(new SchemaValue { Name = "V" + i, Value = i.ToString() });
        var lines = SchemaLayout.Compute(d, new SchemaLayoutOptions { MaxLines = 10 }).Node("Enum:Big")!.Lines;
        Assert.That(lines.Count, Is.EqualTo(10));
        Assert.That(lines[9], Is.EqualTo("... and 16 more"));
    }

    [Test]
    public void A_hidden_kind_leaves_the_drawing_and_so_do_its_lines()
    {
        var r = SchemaLayout.Compute(People(), new SchemaLayoutOptions { Hidden = new HashSet<ConstructKind> { ConstructKind.Enum, ConstructKind.Syntax } });
        Assert.That(r.Nodes.Any(n => n.Kind == ConstructKind.Enum), Is.False);
        Assert.That(r.Edges.Any(e => e.Kind is EdgeKind.Typed or EdgeKind.Syntax), Is.False);
        Assert.That(SchemaLayout.Compute(People()).Edges.Count(e => e.Kind is EdgeKind.Typed or EdgeKind.Syntax), Is.EqualTo(2));
    }

    [Test]
    public void Member_lines_are_drawn_only_when_asked_for()
    {
        Assert.That(SchemaLayout.Compute(People()).Edges.Any(e => e.Kind == EdgeKind.Member), Is.False);
        Assert.That(SchemaLayout.Compute(People(), new SchemaLayoutOptions { MemberEdges = true }).Edges.Count(e => e.Kind == EdgeKind.Member), Is.EqualTo(6));
    }

    [Test]
    public void An_empty_draft_has_a_small_drawing()
    {
        var r = SchemaLayout.Compute(new SchemaDraft());
        Assert.That(r.Nodes, Is.Empty);
        Assert.That(r.Width,  Is.GreaterThan(0d));
        Assert.That(r.Height, Is.GreaterThan(0d));
    }

    // --- promotion ---

    private static SchemaSupport Everything => SchemaSupport.All;

    private static PromoteStep Step(PromotionPlan p, ConstructKind kind, string name) => p.Steps.Single(s => s.Kind == kind && s.Name == name);

    [Test]
    public void A_clean_draft_is_all_additions_in_dependency_order()
    {
        var plan = SchemaPromote.Plan(People(), PromoteTarget.Meta, SchemaSnapshot.Empty, Everything);
        Assert.That(plan.HasErrors, Is.False, Texts(plan.Problems));
        Assert.That(plan.Steps.All(s => s.Action == StepAction.Add), Is.True, TextFor(plan));
        var order = plan.Steps.Select(s => s.Kind).ToList();
        Assert.That(order.IndexOf(ConstructKind.Syntax), Is.LessThan(order.IndexOf(ConstructKind.Attribute)));
        Assert.That(order.IndexOf(ConstructKind.Enum), Is.LessThan(order.IndexOf(ConstructKind.Attribute)));
        Assert.That(order.IndexOf(ConstructKind.Attribute), Is.LessThan(order.IndexOf(ConstructKind.Class)));
        Assert.That(order.IndexOf(ConstructKind.Class), Is.LessThan(order.LastIndexOf(ConstructKind.Relation)));
        Assert.That(plan.CanApply, Is.True);
    }

    private static string TextFor(PromotionPlan p) => SchemaText.Describe(p);

    [Test]
    public void A_name_the_Silo_has_is_skipped_unless_replace_or_copy_is_chosen()
    {
        var silo = new SchemaSnapshot { Enums = [EnumRecord.Create("Role") with { Id = 7 }], Syntaxes = [SyntaxRecord.ForPattern("EmailAddress", "x") with { Id = 3 }] };

        var skip = SchemaPromote.Plan(People(), PromoteTarget.Meta, silo, Everything);
        Assert.That(Step(skip, ConstructKind.Enum, "Role").Action,         Is.EqualTo(StepAction.Skip));
        Assert.That(Step(skip, ConstructKind.Syntax, "EmailAddress").Action, Is.EqualTo(StepAction.Skip));

        var replace = SchemaPromote.Plan(People(), PromoteTarget.Meta, silo, Everything, new PromoteOptions { Replace = true });
        Assert.That(Step(replace, ConstructKind.Enum, "Role").Action, Is.EqualTo(StepAction.Replace));
        var (syntaxes, enums) = SchemaPromote.BuildDefinitions(People(), replace, silo);
        Assert.That(enums.Single().Id,    Is.EqualTo(7)); // it replaces the row that has the name
        Assert.That(syntaxes.Single().Id, Is.EqualTo(3));

        var copy = SchemaPromote.Plan(People(), PromoteTarget.Meta, silo, Everything, new PromoteOptions { Copy = true });
        Assert.That(Step(copy, ConstructKind.Enum, "Role").StoredAs, Is.EqualTo("Copy_of_Role"));
        var (_, copies) = SchemaPromote.BuildDefinitions(People(), copy, silo);
        Assert.That(copies.Single().Id,   Is.EqualTo(0));
        Assert.That(copies.Single().Name, Is.EqualTo("Copy_of_Role"));
        Assert.That(DefinitionNames.IsValid("Copy_of_Role"), Is.True);
    }

    [Test]
    public void A_copy_name_avoids_the_names_that_are_taken()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Copy_of_X", "copy_of_x_2" };
        Assert.That(SchemaPromote.CopyName("X", taken), Is.EqualTo("Copy_of_X_3"));
    }

    [Test]
    public void A_copied_enum_is_followed_by_the_attributes_that_use_it()
    {
        var silo = new SchemaSnapshot { Enums = [EnumRecord.Create("Role")] };
        var d    = People();
        var plan = SchemaPromote.Plan(d, PromoteTarget.Meta, silo, Everything, new PromoteOptions { Copy = true });
        var changes = SchemaPromote.BuildSchema(d, plan, _ => 1);
        Assert.That(changes.Attributes.Single(a => a.Name == "Role").Type, Is.EqualTo("enum:Copy_of_Role"));
    }

    [Test]
    public void Without_a_schema_store_classes_are_blocked_but_enums_and_syntaxes_are_not()
    {
        var plan = SchemaPromote.Plan(People(), PromoteTarget.Meta, SchemaSnapshot.Empty, new SchemaSupport(true, true, false));
        Assert.That(Step(plan, ConstructKind.Enum,   "Role").Action,   Is.EqualTo(StepAction.Add));
        Assert.That(Step(plan, ConstructKind.Class,  "Person").Action, Is.EqualTo(StepAction.Blocked));
        Assert.That(Step(plan, ConstructKind.Class,  "Person").Why,    Does.Contain("no schema store"));
        Assert.That(plan.Steps.Where(s => s.Kind is ConstructKind.Attribute or ConstructKind.Relation).All(s => s.Action == StepAction.Blocked), Is.True);
        Assert.That(plan.CanApply, Is.True); // the enum and the syntax can still go
    }

    [Test]
    public void Units_are_not_promoted_and_the_reason_is_given()
    {
        var plan = SchemaPromote.Plan(Units(), PromoteTarget.Meta, SchemaSnapshot.Empty, Everything);
        Assert.That(plan.Steps.All(s => s.Action == StepAction.Blocked), Is.True);
        Assert.That(plan.Steps[0].Why, Does.Contain("Universal"));
        Assert.That(plan.CanApply, Is.False);
    }

    [Test]
    public void A_world_takes_classes_but_not_enums_syntaxes_or_units()
    {
        var plan = SchemaPromote.Plan(People(), PromoteTarget.World, SchemaSnapshot.Empty, Everything);
        Assert.That(Step(plan, ConstructKind.Enum,      "Role").Action,   Is.EqualTo(StepAction.Blocked));
        Assert.That(Step(plan, ConstructKind.Syntax,    "EmailAddress").Action, Is.EqualTo(StepAction.Blocked));
        Assert.That(Step(plan, ConstructKind.Attribute, "Id").Action,     Is.EqualTo(StepAction.Skip));
        Assert.That(Step(plan, ConstructKind.Class,     "Person").Action, Is.EqualTo(StepAction.Add));
        Assert.That(Step(plan, ConstructKind.Relation,  "Employee inherits Person").Action, Is.EqualTo(StepAction.Skip));
        Assert.That(plan.HasErrors, Is.False, Texts(plan.Problems));
    }

    [Test]
    public void A_world_type_needs_its_base_to_be_in_the_world()
    {
        var d = new SchemaDraft();
        d.Add(ConstructKind.Class, "Employee");
        d.Connect("Employee", "Person", false);

        // Person is a class of the metaverse, but not a type of the local world: the world type cannot inherit it
        var meta = new SchemaSnapshot { Classes = ["Person"] };
        var lost = SchemaPromote.Plan(d, PromoteTarget.World, meta, Everything);
        Assert.That(lost.HasErrors, Is.True);
        Assert.That(Texts(lost.Problems.Where(p => p.Severity == SchemaSeverity.Error)), Does.Contain("needs \"Person\" in the local world"));

        var there = SchemaPromote.Plan(d, PromoteTarget.World, new SchemaSnapshot { Classes = ["Person"], LocalWorldTypes = ["Person"] }, Everything);
        Assert.That(there.HasErrors, Is.False, Texts(there.Problems));

        // or both go in the same run
        d.Add(ConstructKind.Class, "Person");
        var together = SchemaPromote.Plan(d, PromoteTarget.World, SchemaSnapshot.Empty, Everything);
        Assert.That(together.HasErrors, Is.False, Texts(together.Problems));
    }

    [Test]
    public void A_draft_with_errors_writes_nothing()
    {
        var d = People();
        d.Find(ConstructKind.Attribute, "Role")!.Set("type", "enum:Nope");
        var syntaxes = new FakeSyntaxes();
        var enums    = new FakeEnums();
        var schema   = new FakeSchema();
        var plan     = SchemaPromote.Plan(d, PromoteTarget.Meta, SchemaSnapshot.Empty, Everything);
        Assert.That(plan.HasErrors, Is.True);
        Assert.That(plan.CanApply,  Is.False);
        var outcome = SchemaPromote.Apply(d, plan, SchemaSnapshot.Empty, syntaxes, enums, schema);
        Assert.That(outcome.Ok, Is.False);
        Assert.That(syntaxes.Rows, Is.Empty);
        Assert.That(enums.Rows,    Is.Empty);
        Assert.That(schema.Got,    Is.Null);
    }

    [Test]
    public void Applying_saves_syntaxes_then_enums_then_the_schema_with_the_syntax_id()
    {
        var d        = People();
        var syntaxes = new FakeSyntaxes();
        var enums    = new FakeEnums();
        var schema   = new FakeSchema();
        var plan     = SchemaPromote.Plan(d, PromoteTarget.Meta, SchemaSnapshot.Empty, Everything);
        var outcome  = SchemaPromote.Apply(d, plan, SchemaSnapshot.Empty, syntaxes, enums, schema);

        Assert.That(outcome.Ok, Is.True, string.Join("; ", outcome.Failed));
        Assert.That(syntaxes.Rows.Single().Name, Is.EqualTo("EmailAddress"));
        Assert.That(enums.Rows.Single().Values.Select(v => v.Name), Is.EqualTo(new[] { "Staff", "Manager", "Admin" }));

        var got = schema.Got!;
        Assert.That(got.Types.Select(t => t.Name).OrderBy(n => n), Is.EqualTo(new[] { "Auditable", "Employee", "Person" }));
        Assert.That(got.Types.Single(t => t.Name == "Auditable").Flags, Is.EqualTo(MvTypeFlags.Auxiliary));
        var email = got.Attributes.Single(a => a.Name == "Email");
        Assert.That(email.Syntax,  Is.EqualTo(syntaxes.Rows.Single().Id));
        Assert.That(email.Flags,   Is.EqualTo(MvAttributeFlags.Nullable));
        Assert.That(got.Attributes.Single(a => a.Name == "Id").Flags, Is.EqualTo(MvAttributeFlags.Key));
        Assert.That(got.Attributes.Single(a => a.Name == "Role").Type, Is.EqualTo("enum:Role"));
        Assert.That(got.Hierarchy.Single(h => h.Subject.Name == "Employee").Target.Name, Is.EqualTo("Person"));
        Assert.That(got.Hierarchy.Single(h => h.Subject.Name == "Employee").Relation,    Is.EqualTo(TypeRelation.Inheritance));
        Assert.That(got.Hierarchy.Single(h => h.Subject.Name == "Person").Relation,       Is.EqualTo(TypeRelation.Auxiliary));
        Assert.That(got.Members.Count(m => m.Class == "Person"), Is.EqualTo(3));
        Assert.That(outcome.Done.Any(x => x.StartsWith("Enum Role")), Is.True);
    }

    [Test]
    public void The_enum_values_left_blank_are_numbered_as_the_enum_window_would()
    {
        var d       = Orders();
        var enums   = new FakeEnums();
        var plan    = SchemaPromote.Plan(d, PromoteTarget.Meta, SchemaSnapshot.Empty, Everything);
        var outcome = SchemaPromote.Apply(d, plan, SchemaSnapshot.Empty, new FakeSyntaxes(), enums, new FakeSchema());
        Assert.That(outcome.Ok, Is.True, string.Join("; ", outcome.Failed));
        Assert.That(enums.Rows.Single().Values.Select(v => v.Value), Is.EqualTo(new[] { "0", "1", "2" }));
    }

    [Test]
    public void A_syntax_the_Silo_refuses_stops_the_attributes_that_use_it_and_is_reported()
    {
        var d        = People();
        var syntaxes = new FakeSyntaxes { Refuse = true };
        var schema   = new FakeSchema();
        var plan     = SchemaPromote.Plan(d, PromoteTarget.Meta, SchemaSnapshot.Empty, Everything);
        var outcome  = SchemaPromote.Apply(d, plan, SchemaSnapshot.Empty, syntaxes, new FakeEnums(), schema);

        Assert.That(outcome.Ok, Is.False);
        Assert.That(outcome.Failed.Any(f => f.Contains("store said no")), Is.True);
        Assert.That(outcome.Failed.Any(f => f.StartsWith("Attribute Email")), Is.True);
        Assert.That(schema.Got!.Attributes.Any(a => a.Name == "Email"), Is.False);
        Assert.That(schema.Got!.Members.Any(m => m.Attribute == "Email"), Is.False);
        Assert.That(schema.Got!.Attributes.Any(a => a.Name == "Name"), Is.True);
    }

    [Test]
    public void A_schema_store_that_fails_is_reported_with_its_message()
    {
        var d       = Orders();
        var plan    = SchemaPromote.Plan(d, PromoteTarget.Meta, SchemaSnapshot.Empty, Everything);
        var outcome = SchemaPromote.Apply(d, plan, SchemaSnapshot.Empty, new FakeSyntaxes(), new FakeEnums(), new FakeSchema { Fail = true });
        Assert.That(outcome.Ok, Is.False);
        Assert.That(outcome.Failed.Any(f => f.Contains("read-only")), Is.True);
        Assert.That(outcome.Done.Any(x => x.StartsWith("Enum")), Is.True); // what was already written stays written
    }

    [Test]
    public void Without_stores_the_apply_says_so_instead_of_pretending()
    {
        var d       = People();
        var plan    = SchemaPromote.Plan(d, PromoteTarget.Meta, SchemaSnapshot.Empty, Everything);
        var outcome = SchemaPromote.Apply(d, plan, SchemaSnapshot.Empty, null, null, null);
        Assert.That(outcome.Done, Is.Empty);
        Assert.That(outcome.Failed.Count, Is.GreaterThan(1));
    }

    [Test]
    public void A_world_promotion_builds_world_types_with_fields_in_the_local_world()
    {
        var d      = People();
        var schema = new FakeSchema();
        var plan   = SchemaPromote.Plan(d, PromoteTarget.World, SchemaSnapshot.Empty, Everything);
        var outcome = SchemaPromote.Apply(d, plan, SchemaSnapshot.Empty, null, null, schema);
        Assert.That(outcome.Ok, Is.True, string.Join("; ", outcome.Failed));

        var types = schema.Got!.WorldTypes;
        Assert.That(types.Select(t => t.Name).OrderBy(n => n), Is.EqualTo(new[] { "Auditable", "Employee", "Person" }));
        var person = types.Single(t => t.Name == "Person");
        Assert.That(person.World.Key,  Is.EqualTo("local"));
        Assert.That(person.Provider,   Is.EqualTo("local"));
        Assert.That(person.Items.Select(i => ((Silo.DbModel.Worlds.WorldField)i).Name), Is.EqualTo(new[] { "Id", "Name", "Email" }));
        var id = (Silo.DbModel.Worlds.WorldField)person.Items[0];
        Assert.That(id.DataType, Is.EqualTo("Int64"));
        Assert.That(id.Flags,    Is.EqualTo(Silo.DbModel.Worlds.WorldFieldFlags.Key));
        var email = (Silo.DbModel.Worlds.WorldField)person.Items[2];
        Assert.That(email.Flags, Is.EqualTo(Silo.DbModel.Worlds.WorldFieldFlags.Nullable));
        Assert.That(person.Include!.Value, Is.EqualTo("Auditable"));

        var employee = types.Single(t => t.Name == "Employee");
        Assert.That(employee.Inherits!.Value, Is.EqualTo("Person"));
        var role = (Silo.DbModel.Worlds.WorldField)employee.Items[1];
        Assert.That(role.DataType, Is.EqualTo("Int64")); // an enum field is its values' type
        Assert.That(employee.Items.All(i => ReferenceEquals(((Silo.DbModel.Worlds.WorldField)i).WorldType, employee)), Is.True);
    }

    [Test]
    public void The_stored_draft_text_outputs_are_readable()
    {
        var d = People();
        string outline = SchemaText.Outline(d);
        Assert.That(outline, Does.Contain("Employee : Person [Hired, Role]"));
        Assert.That(outline, Does.Contain("Person + Auditable [Id, Name, Email]"));
        Assert.That(outline, Does.Contain("Role (integral): Staff=0, Manager=1, Admin=2"));
        Assert.That(outline, Does.Contain("Email: String; syntax EmailAddress; Nullable"));

        string text = SchemaText.Describe(SchemaPromote.Plan(d, PromoteTarget.Meta, SchemaSnapshot.Empty, Everything));
        Assert.That(text, Does.Contain("Add     Class Person"));
    }

    [Test]
    public void The_C_sharp_sketch_has_enums_classes_and_nullability()
    {
        string cs = SchemaText.CSharp(People());
        Assert.That(cs, Does.Contain("public enum Role : ulong"));
        Assert.That(cs, Does.Contain("Manager = 1,"));
        Assert.That(cs, Does.Contain("public class Employee : Person"));
        Assert.That(cs, Does.Contain("public long Id { get; set; }"));
        Assert.That(cs, Does.Contain("public DateTime? Hired { get; set; }"));
        Assert.That(cs, Does.Contain("public Role Role { get; set; }"));
        Assert.That(cs, Does.Contain("Email { get; set; }"));
        Assert.That(cs, Does.Contain("// includes: Auditable"));
        Assert.That(cs, Does.Contain("// syntax: EmailAddress"));

        var d = new SchemaDraft();
        var tags = Attr(d, "Tags", "String", "Multivalue");
        d.Add(ConstructKind.Class, "Post").Members.Add("Tags");
        Assert.That(SchemaText.CSharp(d), Does.Contain("public List<string> Tags { get; set; } = [];"));
        var c = d.Add(ConstructKind.Enum, "Mood");
        c.Set("type", "String");
        c.Values.Add(new SchemaValue { Name = "Happy", Value = "glad" });
        Assert.That(SchemaText.CSharp(d), Does.Contain("public const string Happy = \"glad\";"));
        Assert.That(SchemaText.Ident("1st place!"), Is.EqualTo("_1st_place_"));
    }

    [Test]
    public void A_definition_of_the_Silo_comes_into_the_draft_and_goes_back_the_same()
    {
        var en = new EnumRecord(5, "Perm", "What may be done", EnumType.Integral, EnumFlags.Bitmask | EnumFlags.KnownValuesOnly,
                                [new EnumValueRecord(1, "Read", "1", "Look"), new EnumValueRecord(2, "Write", "2")]);
        var c = SchemaRecords.FromEnum(en);
        Assert.That(c.Get("flags"), Is.EqualTo("Bitmask, KnownValuesOnly"));
        var back = SchemaRecords.ToEnum(c);
        Assert.That(back.Type,  Is.EqualTo(en.Type));
        Assert.That(back.Flags, Is.EqualTo(en.Flags));
        Assert.That(back.Description, Is.EqualTo(en.Description));
        Assert.That(back.Values.Select(v => (v.Name, v.Value, v.Description)), Is.EqualTo(en.Values.Select(v => (v.Name, v.Value, v.Description))));

        var syn = SyntaxRecord.ForPattern("Zip", @"^\d{5}$", alias: "zip5", description: "US zip", example: "12345");
        var sc  = SchemaRecords.FromSyntax(syn);
        var sb  = SchemaRecords.ToSyntax(sc);
        Assert.That((sb.Name, sb.Alias, sb.Pattern, sb.Example, sb.Kind, sb.Description), Is.EqualTo((syn.Name, syn.Alias, syn.Pattern, syn.Example, syn.Kind, syn.Description)));
        var parser = SchemaRecords.FromSyntax(SyntaxRecord.ForParser("Stamp", "timestamp"));
        Assert.That(parser.Get("kind"), Is.EqualTo("Parser"));
        Assert.That(parser.Get("parser"), Is.EqualTo("timestamp"));
    }
}
