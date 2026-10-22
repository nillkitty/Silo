using Silo.Labs.Parser;
using Silo.Providers.Data;

namespace Silo.Tests.Labs;

public class ParserLabTests
{
    private static ParserSpecDoc Doc(string name, params RuleDef[] rules) => new() { Name = name, Rules = [.. rules] };

    // ---- combinators and tokenizers ------------------------------------------------------------------

    [Test]
    public void Not_refuses_what_it_names_and_leaves_nothing_in_the_value()
    {
        var spec = CompiledSpec.Compile(Doc("T.Not", new RuleDef { Name = "w", Body = SpecNode.Seq(SpecNode.Not(SpecNode.Literal("end")), SpecNode.AtLeastOnce(SpecNode.Token("alpha"))) }));

        Assert.That(spec.Diagnostics, Is.Empty);
        Assert.That(spec.Parse("hello").Success, Is.True);
        Assert.That(spec.Parse("end").Success, Is.False);
        Assert.That(spec.Parse("ending").Success, Is.False);
        Assert.That(spec.Parse("ending").Error, Does.Contain("not \"end\""));
        Assert.That(spec.Parse("hello").Value, Is.InstanceOf<object[]>().And.Property("Length").EqualTo(5));
    }

    [Test]
    public void A_unicode_class_matches_by_category_and_can_be_negated()
    {
        var spec = CompiledSpec.Compile(Doc("T.Uni",
                                            new RuleDef { Name = "up", Body = SpecNode.AtLeastOnce(SpecNode.Unicode("Lu")) },
                                            new RuleDef { Name = "ln", Body = SpecNode.AtLeastOnce(SpecNode.Unicode("L, Nd")) },
                                            new RuleDef { Name = "nl", Body = SpecNode.AtLeastOnce(SpecNode.Unicode("L", true)) },
                                            new RuleDef { Name = "long", Body = SpecNode.Unicode("UppercaseLetter") },
                                            new RuleDef { Name = "bad", Body = SpecNode.Unicode("Qx") }));

        Assert.That(spec.Parse("ÀÉ", "up").Success, Is.True);
        Assert.That(spec.Parse("Àé", "up").Success, Is.False);
        Assert.That(spec.Parse("é9ж", "ln").Success, Is.True);
        Assert.That(spec.Parse("é-", "ln").Success, Is.False);
        Assert.That(spec.Parse("12 -", "nl").Success, Is.True);
        Assert.That(spec.Parse("1a", "nl").Success, Is.False);
        Assert.That(spec.Parse("Ж", "long").Success, Is.True);
        Assert.That(spec.Diagnostics.Any(d => d.Rule == "bad" && d.Message.Contains("'Qx'")), Is.True, "an unknown class is a problem");
    }

    [Test]
    public void Exactly_one_matches_one_and_only_one()
    {
        var spec = CompiledSpec.Compile(Doc("T.One", new RuleDef { Name = "r", Body = SpecNode.One(SpecNode.Token("alpha")) }));

        Assert.That(spec.Parse("a").Success, Is.True);
        Assert.That(spec.Parse("ab").Success, Is.False);
        Assert.That(spec.Parse(string.Empty).Success, Is.False);
        Assert.That(SpecNode.One(SpecNode.Token("alpha")).Describe(), Is.EqualTo("alpha[1]"));
        Assert.That(SpecCatalog.Combinator("one")!.Make(SpecNode.Token("alpha")), Is.Not.Null.And.Property("Kind").EqualTo("repeat"));
        Assert.That(SpecCatalog.Combinator("not")!.Make(SpecNode.Token("alpha")), Is.Not.Null.And.Property("Kind").EqualTo("not"));
    }

    [Test]
    public void A_saved_token_is_used_by_name_and_cannot_be_made_of_itself()
    {
        var doc = Doc("T.Frag", new RuleDef { Name = "two", Body = SpecNode.Repeat(SpecNode.Token("hexy"), 2, 2) });
        doc.Fragments = [new Fragment { Name = "hexy", Kind = FragmentKind.Token, Node = SpecNode.Chars("0123456789abcdef") }];
        var spec = CompiledSpec.Compile(doc);
        Assert.That(spec.Diagnostics, Is.Empty);
        Assert.That(spec.Parse("a9").Success, Is.True);
        Assert.That(spec.Parse("g9").Success, Is.False);

        var loop = Doc("T.Loop", new RuleDef { Name = "r", Body = SpecNode.Token("loop") });
        loop.Fragments = [new Fragment { Name = "loop", Kind = FragmentKind.Token, Node = SpecNode.Token("loop") }];
        Assert.That(CompiledSpec.Compile(loop).Diagnostics.Any(d => d.Message.Contains("made of itself")), Is.True);
    }

    // ---- moving, copying and the clipboard ------------------------------------------------------------

    private static (ParserWorkspace Ws, RuleDef R1, RuleDef R2) TwoRules()
    {
        var ws = new ParserWorkspace(new ParserSpecDoc { Name = "W.Test" });
        var r1 = ws.AddRule("r1");
        var r2 = ws.AddRule("r2");
        ws.Drop(new DragItem("token", "alpha"), null, ParserWorkspace.RuleSlot(r1));
        ws.Drop(new DragItem("token", "decimal-digit"), r1.Body, ParserWorkspace.RuleSlot(r1));
        ws.Drop(new DragItem("literal", "x"), null, ParserWorkspace.RuleSlot(r2));
        return (ws, r1, r2);
    }

    [Test]
    public void A_piece_dragged_to_another_rule_moves_and_one_undo_puts_it_back()
    {
        var (ws, r1, r2) = TwoRules();
        ws.DraggedNode = r1.Body!.Children[0];
        ws.Drop(new DragItem("node", string.Empty), r2.Body, ParserWorkspace.RuleSlot(r2));

        Assert.That(r2.Body!.SelfAndDescendants().Any(n => n.Text == "alpha"), Is.True);
        Assert.That(r1.Body!.SelfAndDescendants().Any(n => n.Text == "alpha"), Is.False);

        ws.Undo();
        Assert.That(ws.Doc.Find("r1")!.Body!.SelfAndDescendants().Any(n => n.Text == "alpha"), Is.True);
        Assert.That(ws.Doc.Find("r2")!.Body!.SelfAndDescendants().Any(n => n.Text == "alpha"), Is.False);
    }

    [Test]
    public void Dropping_with_copy_keeps_the_original_and_a_piece_cannot_go_into_itself()
    {
        var (ws, r1, r2) = TwoRules();
        ws.DraggedNode = r1.Body!.Children[0];
        ws.Drop(new DragItem("node", string.Empty), r2.Body, ParserWorkspace.RuleSlot(r2), copy: true);
        Assert.That(ws.Doc.Find("r1")!.Body!.SelfAndDescendants().Any(n => n.Text == "alpha"), Is.True);
        Assert.That(ws.Doc.Find("r2")!.Body!.SelfAndDescendants().Any(n => n.Text == "alpha"), Is.True);

        var body = ws.Doc.Find("r1")!.Body!;
        ws.DraggedNode = body;
        Assert.That(ws.MoveNode(body, body.Children[0], ParserWorkspace.RuleSlot(ws.Doc.Find("r1")!), false), Is.Null);
    }

    [Test]
    public void Copy_cut_and_paste_work_for_pieces_and_for_tokenizer_rules()
    {
        var (ws, r1, _) = TwoRules();
        ws.CopyNode(r1.Body!.Children[1]);
        Assert.That(ws.CanPaste, Is.True);
        Assert.That(ws.Paste(ws.Doc.Find("r2")!.Body), Is.Not.Null.And.Property("Text").EqualTo("decimal-digit"));
        ws.CutNode(ws.Doc.Find("r2")!.Body!.Children.Last());
        Assert.That(ws.Doc.Find("r2")!.Body!.SelfAndDescendants().Any(n => n.Text == "decimal-digit"), Is.False);

        var tokenizer = new ParserWorkspace(new ParserSpecDoc { Mode = SpecMode.Tokenizer });
        tokenizer.Doc.Tokenizer.Kinds = ["Word"];
        var rule = tokenizer.AddTokenRule("Word");
        tokenizer.Drop(new DragItem("token", "alpha"), null, ParserWorkspace.MatcherSlot(rule));
        tokenizer.CopyTokenRule(rule);
        tokenizer.PasteTokenRules(rule);
        tokenizer.PasteTokenRules(null);
        Assert.That(tokenizer.Doc.Tokenizer.Rules, Has.Count.EqualTo(3));
        Assert.That(tokenizer.Doc.Tokenizer.Rules[1].Matcher, Is.Not.SameAs(rule.Matcher), "a pasted rule is a copy");
        tokenizer.CutTokenRule(tokenizer.Doc.Tokenizer.Rules[2]);
        Assert.That(tokenizer.Doc.Tokenizer.Rules, Has.Count.EqualTo(2));
    }

    [Test]
    public void Saved_pieces_have_unique_names_and_renaming_a_token_renames_its_uses()
    {
        var ws   = new ParserWorkspace(new ParserSpecDoc { Name = "F.Test" });
        var rule = ws.AddRule("a");
        ws.Drop(new DragItem("chars", "01"), null, ParserWorkspace.RuleSlot(rule));
        ws.SaveFragment(rule.Body!, "bit", FragmentKind.Token);

        Assert.Throws<InvalidOperationException>(() => ws.SaveFragment(rule.Body!, "BIT", FragmentKind.Syntax), "a saved name is used once");
        Assert.Throws<InvalidOperationException>(() => ws.SaveFragment(rule.Body!, "alpha", FragmentKind.Syntax), "a built-in name is not taken");
        Assert.Throws<InvalidOperationException>(() => ws.SaveFragment(SpecNode.Ref("a"), "uses", FragmentKind.Token), "a token cannot use a rule");

        var other = ws.AddRule("b");
        ws.Drop(new DragItem("token", "bit"), null, ParserWorkspace.RuleSlot(other));
        ws.RenameFragment("bit", "binary");
        Assert.That(other.Body, Is.Not.Null.And.Property("Text").EqualTo("binary"));
        Assert.That(ws.Compiled.Parse("1", "b").Success, Is.True);
    }

    // ---- imports, and no cycles -------------------------------------------------------------------------

    private sealed class Env : SpecEnvironment
    {
        public Dictionary<string, ParserSpecDoc> Docs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public FakeEnums Store { get; } = new();

        public override ParserSpecDoc? LoadSpec(string name) => Docs.TryGetValue(name, out var d) ? d.Clone() : null;

        public override IReadOnlyList<string> SpecNames() => [.. Docs.Keys];

        public override IEnumStore? Enums => Store;
    }

    private sealed class FakeEnums : IEnumStore
    {
        private readonly List<EnumRecord> _all = [];
        private int _next = 1;

        public IReadOnlyList<EnumRecord> GetEnums() => [.. _all.OrderBy(e => e.Name)];

        public EnumRecord? GetEnum(int id) => _all.FirstOrDefault(e => e.Id == id);

        public EnumRecord? FindEnum(string name) => _all.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        public EnumRecord SaveEnum(EnumRecord definition)
        {
            var record = definition with { Id = definition.Id == 0 ? _next++ : definition.Id, Values = [.. definition.Values.Select(v => v with { Id = v.Id == 0 ? _next++ : v.Id })] };
            _all.RemoveAll(e => e.Id == record.Id);
            _all.Add(record);
            return record;
        }

        public bool DeleteEnum(int id) => _all.RemoveAll(e => e.Id == id) > 0;

        public EnumValueRecord SaveEnumValue(int enumId, EnumValueRecord value) => value;

        public bool DeleteEnumValue(int valueId) => false;
    }

    [Test]
    public void Dropping_a_rule_of_another_spec_imports_it_and_imports_cannot_go_in_a_circle()
    {
        var env = new Env();
        var b   = Doc("B.Spec", new RuleDef { Name = "digit2", Body = SpecNode.Repeat(SpecNode.Token("decimal-digit"), 2, 2) });
        var c   = Doc("C.Spec", new RuleDef { Name = "c1", Body = SpecNode.Ref("digit2", "B.Spec") });
        c.Imports                = ["B.Spec"];
        env.Docs["B.Spec"]       = b;
        env.Docs["C.Spec"]       = c;
        var a = new ParserWorkspace(new ParserSpecDoc { Name = "A.Spec" }, env);
        a.Spec.Loader = a.LoadSpec;
        var top = a.AddRule("top");

        a.Drop(new DragItem("xrule", "C.Spec|c1"), null, ParserWorkspace.RuleSlot(top));
        Assert.That(a.Doc.Imports, Is.EqualTo(new[] { "C.Spec" }));
        Assert.That(a.Compiled.Parse("42", "top").Success, Is.True);
        Assert.That(a.Compiled.Parse("4", "top").Success, Is.False);
        Assert.That(a.Problems, Is.Empty);

        // B -> A -> C -> B would be a circle
        env.Docs["A.Spec"] = a.Doc;
        var cycle = SpecImports.CycleIfImported(b, "A.Spec", env.LoadSpec);
        Assert.That(cycle, Is.Not.Null);
        Assert.That(cycle![0], Is.EqualTo("B.Spec"));
        Assert.That(cycle[^1], Is.EqualTo("B.Spec"));

        var wb = new ParserWorkspace(b.Clone(), env);
        Assert.That(Assert.Throws<InvalidOperationException>(() => wb.AddImport("A.Spec"))!.Message, Does.Contain("circle"));
        Assert.Throws<InvalidOperationException>(() => wb.AddImport("B.Spec"), "a spec cannot import itself");
        Assert.Throws<InvalidOperationException>(() => wb.AddImport("No.Such"), "the spec must exist");
        Assert.That(wb.Doc.Imports, Is.Empty);

        // a hand-edited circle is reported and cannot be kept
        var bad = b.Clone();
        bad.Imports = ["A.Spec"];
        bad.Rules.Add(new RuleDef { Name = "x", Body = SpecNode.Ref("top", "A.Spec") });
        var wbad = new ParserWorkspace(bad, env);
        Assert.That(wbad.Compiled.Diagnostics.Any(d => d.Message.Contains("circle")), Is.True);
        Assert.That(wbad.Problems.Any(p => p.Rule == "(imports)"), Is.True);
        Assert.Throws<InvalidOperationException>(() => wbad.Save());
    }

    // ---- the token type ----------------------------------------------------------------------------------

    [Test]
    public void The_token_type_is_an_enum_of_the_Silo_or_one_defined_on_the_spot_with_unique_values()
    {
        var env = new Env();
        env.Store.SaveEnum(new EnumRecord(0, "Shared", null, Silo.DbModel.Meta.EnumType.Integral, Silo.DbModel.Meta.EnumFlags.None, [new(0, "Word", "0"), new(0, "Num", "1")]));
        var ws = new ParserWorkspace(new ParserSpecDoc { Name = "E.Test", Mode = SpecMode.Tokenizer }, env);

        Assert.That(ws.SetTokenType("Shared"), Is.Null);
        Assert.That(ws.Doc.Tokenizer.ExistingEnum, Is.True);
        Assert.That(ws.Doc.Tokenizer.Kinds, Is.EqualTo(new[] { "Word", "Num" }));
        Assert.That(ws.AddKind("X"), Does.Contain("Silo"), "an enum of the Silo is not edited here");

        Assert.That(ws.SetTokenType("Mine"), Is.Null);
        Assert.That(ws.Doc.Tokenizer.ExistingEnum, Is.False);
        ws.Doc.Tokenizer.Kinds.Clear();
        Assert.That(ws.AddKind("Word"), Is.Null);
        Assert.That(ws.AddKind("word"), Does.Contain("already"), "value names are unique whatever the case");
        Assert.That(ws.AddKind(string.Empty), Is.Not.Null);
        Assert.That(ws.SetTokenType("1bad"), Is.Not.Null);

        ws.AddTokenRule("Word");
        ws.AddTokenRule("Nope");
        Assert.That(ws.Problems.Any(p => p.Message.Contains("'Nope'")), Is.True, "a rule making a kind the enum does not have");
        Assert.That(ws.RemoveKind("Word"), Does.Contain("rule 1"), "a kind in use cannot be removed");

        var dup = new ParserWorkspace(new ParserSpecDoc { Name = "E.Dup", Mode = SpecMode.Tokenizer, Tokenizer = new TokenizerSpec { TokenType = "Dup", Kinds = ["a", "A", "b"] } }, env);
        Assert.That(dup.Problems.Any(p => p.Message.Contains("more than one")), Is.True);
    }

    [Test]
    public void Include_in_Enums_adds_the_enum_only_when_asked_and_only_when_acceptable()
    {
        var env = new Env();
        var asked = ParserWorkspace.NewDoc("E.One");
        asked.Mode      = SpecMode.Tokenizer;
        asked.Tokenizer = new TokenizerSpec { TokenType = "Mine", Kinds = ["A", "B"], IncludeInEnums = true };
        Assert.That(SpecChecks.Run(asked, env), Is.Empty);
        Assert.That(SpecEnums.Include(asked, env), Is.True);
        Assert.That(env.Store.FindEnum("Mine")!.Values.Select(v => v.Name), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(asked.Tokenizer.ExistingEnum, Is.True, "the spec then uses it as an enum of the Silo");

        var quiet = ParserWorkspace.NewDoc("E.Two");
        quiet.Tokenizer = new TokenizerSpec { TokenType = "Quiet", Kinds = ["Q"] };
        Assert.That(SpecEnums.Include(quiet, env), Is.False);
        Assert.That(env.Store.FindEnum("Quiet"), Is.Null);

        var bad = ParserWorkspace.NewDoc("E.Three");
        bad.Tokenizer = new TokenizerSpec { TokenType = "Bad", Kinds = ["x", "X"], IncludeInEnums = true };
        Assert.That(SpecEnums.Problems(bad, env), Is.Not.Empty);
        Assert.That(env.Store.FindEnum("Bad"), Is.Null);
    }

    // ---- samples and tests -------------------------------------------------------------------------------

    [Test]
    public void Samples_have_no_expectation_and_the_status_line_follows_the_tests()
    {
        var ws = new ParserWorkspace(new ParserSpecDoc { Name = "S.Test", Rules = [new RuleDef { Name = "n", Body = SpecNode.AtLeastOnce(SpecNode.Token("decimal-digit")) }], Root = "n" });
        Assert.That(typeof(SampleSet).GetProperty("Kind"), Is.Null, "a sample only matches or does not; what is expected of it is a test");
        Assert.That(ws.Summarize([]), Does.Contain("no tests"));

        var events = new List<WorkspaceChange>();
        ws.Changed += events.Add;
        ws.RunTests();
        Assert.That(ws.LastTestLine, Does.Contain("no tests"));
        Assert.That(events.Any(e => e.HasFlag(WorkspaceChange.TestsRan)), Is.True);

        ws.AddSample("nums", "12");
        ws.AddSample("nums", "x");
        ws.AddTest("matches", ["nums"], Expectation.Matches);
        ws.AddTest("matches", ["nums"], Expectation.DoesNotMatch);
        Assert.That(ws.Book.Tests.Select(t => t.Name), Is.EqualTo(new[] { "matches", "matches 2" }), "test names are unique");

        ws.RunTests();
        Assert.That(ws.LastTestLine, Does.StartWith("0 of 2").And.Contains("failing: matches, matches 2"));

        ws.Book.Samples[0].Items.RemoveAll(x => x == "x");
        ws.Book.Tests.RemoveAll(t => t.Name == "matches 2");
        ws.RunTests();
        Assert.That(ws.LastTestLine, Is.EqualTo("1 of 1 test(s) passed"));
        ws.RunFailingTests();
        Assert.That(ws.LastTestLine, Is.EqualTo("No failing tests to run"));
    }
}
