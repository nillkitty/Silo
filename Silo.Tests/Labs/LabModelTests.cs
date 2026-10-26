using Silo.Contracts.Core;
using Silo.Labs;
using Silo.Labs.Regexes;

namespace Silo.Tests.Labs;

public class LabModelTests
{
    private static readonly LabPartType Group = new("group", "Group");
    private static readonly LabPartType Branch = new("branch", "Branch", null, null, null, null, "alternation");
    private static readonly LabPartType Alternation = new("alternation", "Alternation");

    /// <summary>A model with only what the rules look at.</summary>
    private sealed class FakeModel : ILabModel
    {
        public IReadOnlyList<LabSubjectType> SubjectTypes { get; set; } = [new LabSubjectType("regex", "Regex")];

        public LabSubjectType? SubjectType => SubjectTypes.FirstOrDefault();

        public IReadOnlyList<LabPartType> PartTypes { get; set; } = [Group, Branch, Alternation];

        public IReadOnlyList<LabPart> Parts { get; set; } = [];

        public LabPart? ActivePart { get; set; }

        public void ActivatePart(LabPart? part) => ActivePart = part;

        public IReadOnlyList<LabAspect> Aspects { get; set; } = [];

        public IReadOnlyList<LabTool> Tools { get; set; } = [];

        public LabTool? ActiveTool { get; set; }

        public void ActivateTool(LabTool? tool) => ActiveTool = tool;

        public IReadOnlyList<LabCommandEntry> ModelCommands { get; set; } = [];

        public IReadOnlyList<LabParameterSource> ParameterSources { get; set; } = [];

        public Task NewSubjectAsync(LabSubjectType type, string content) => Task.CompletedTask;

        public Task AddPartAsync(LabPartType type, LabPart? parent, string content) => Task.CompletedTask;

        public event EventHandler? ModelChanged
        {
            add { }
            remove { }
        }
    }

    private static LabPart Tree()
    {
        var alt = new LabPart(Alternation, "p0", "Either of 2");
        alt.Add(new LabPart(Branch, "p0.0", "Way 1")).Add(new LabPart(Group, "p0.0.0", "Group 1"));
        alt.Add(new LabPart(Branch, "p0.1", "Way 2"));
        return alt;
    }

    // ---- owners ------------------------------------------------------------------------------------

    [Test]
    public void An_owner_has_a_short_stable_key()
    {
        Assert.That(LabOwner.Lab.Key, Is.EqualTo("lab"));
        Assert.That(LabOwner.OfPartType("group").Key, Is.EqualTo("parttype:group"));
        Assert.That(LabOwner.OfAspect("matches").Key, Is.EqualTo("aspect:matches"));
    }

    [Test]
    public void The_lab_the_instance_and_the_subject_are_always_active()
    {
        var model = new FakeModel();
        Assert.That(LabModelRules.IsActive(model, LabOwner.Lab), Is.True);
        Assert.That(LabModelRules.IsActive(model, LabOwner.Instance), Is.True);
        Assert.That(LabModelRules.IsActive(model, LabOwner.Subject), Is.True);
    }

    [Test]
    public void A_subject_type_is_active_only_for_a_subject_of_that_type()
    {
        var model = new FakeModel();
        Assert.That(LabModelRules.IsActive(model, LabOwner.OfSubjectType("regex")), Is.True);
        Assert.That(LabModelRules.IsActive(model, LabOwner.OfSubjectType("menu")), Is.False);
    }

    [Test]
    public void A_part_owner_needs_an_active_part_and_a_part_type_needs_that_type()
    {
        var tree  = Tree();
        var model = new FakeModel { Parts = [tree] };
        Assert.That(LabModelRules.IsActive(model, LabOwner.Part), Is.False);

        model.ActivePart = tree.Children[0];
        Assert.That(LabModelRules.IsActive(model, LabOwner.Part), Is.True);
        Assert.That(LabModelRules.IsActive(model, LabOwner.OfPartType("branch")), Is.True);
        Assert.That(LabModelRules.IsActive(model, LabOwner.OfPartType("group")), Is.False);
    }

    [Test]
    public void An_aspect_is_active_when_its_owner_is()
    {
        var tree = Tree();
        var model = new FakeModel
                    {
                        Parts = [tree],
                        Aspects = [new LabAspect("group", "Group", LabOwner.OfPartType("group")), new LabAspect("matches", "Matches", LabOwner.OfSubjectType("regex"))]
                    };
        Assert.That(LabModelRules.ActiveAspects(model).Select(a => a.Id), Is.EqualTo(new[] { "matches" }));

        model.ActivePart = tree.Children[0].Children[0];
        Assert.That(LabModelRules.ActiveAspects(model).Select(a => a.Id), Is.EqualTo(new[] { "group", "matches" }));
    }

    [Test]
    public void An_aspect_of_an_aspect_follows_the_chain_and_a_loop_is_not_active()
    {
        var model = new FakeModel
                    {
                        Aspects = [new LabAspect("a", "A", LabOwner.OfAspect("b")), new LabAspect("b", "B", LabOwner.OfAspect("a")), new LabAspect("c", "C", LabOwner.OfAspect("d")), new LabAspect("d", "D", LabOwner.Lab)]
                    };
        Assert.That(LabModelRules.IsActive(model, LabOwner.OfAspect("a")), Is.False);
        Assert.That(LabModelRules.IsActive(model, LabOwner.OfAspect("c")), Is.True);
    }

    [Test]
    public void A_tool_can_be_selected_only_while_its_owner_is_active_and_its_commands_follow_it()
    {
        var tool = new LabTool("quantify", "Quantify", LabOwner.OfPartType("group"))
                   {
                       Commands = [ProvidedCommand.From("quantify-star", "Star", () => { })]
                   };
        var tree  = Tree();
        var model = new FakeModel { Parts = [tree], Tools = [tool] };
        Assert.That(LabModelRules.AvailableTools(model), Is.Empty);

        model.ActivePart = tree.Children[0].Children[0];
        Assert.That(LabModelRules.AvailableTools(model), Is.EqualTo(new[] { tool }));
        Assert.That(LabModelRules.ActiveCommands(model).Any(c => c.Command.Id == "quantify-star"), Is.False);

        model.ActiveTool = tool;
        Assert.That(LabModelRules.ActiveCommands(model).Any(c => c.Command.Id == "quantify-star"), Is.True);
    }

    // ---- parameter lists ---------------------------------------------------------------------------

    private static LabParameterSet Set(string key) => LabParameterSet.For("test", key, key, [new LabParameter("p", "P", LabParameterKind.Text, "")]);

    [Test]
    public void A_static_pane_shows_static_lists_always_and_instance_lists_while_active()
    {
        var tree  = Tree();
        var lab   = new LabParameterSource(LabOwner.Lab, "Lab", Set("lab"), true);
        var group = new LabParameterSource(LabOwner.OfPartType("group"), "Group", Set("group"), false);
        var model = new FakeModel { Parts = [tree], ParameterSources = [lab, group] };

        Assert.That(LabModelRules.SourcesFor(model, null, LabPaneType.Static), Is.EqualTo(new[] { lab }));
        Assert.That(LabModelRules.SourcesFor(model, null, LabPaneType.Instance), Is.EqualTo(new[] { lab }));

        model.ActivePart = tree.Children[0].Children[0];
        Assert.That(LabModelRules.SourcesFor(model, null, LabPaneType.Static), Is.EqualTo(new[] { lab, group }));
    }

    [Test]
    public void A_pane_of_an_aspect_shows_that_aspects_lists_and_not_another_aspects()
    {
        var mine   = new LabParameterSource(LabOwner.OfAspect("matches"), "Matches", Set("matches"), true);
        var theirs = new LabParameterSource(LabOwner.OfAspect("code"), "Code", Set("code"), true);
        var lab    = new LabParameterSource(LabOwner.Lab, "Lab", Set("lab"), true);
        var model  = new FakeModel
                     {
                         Aspects = [new LabAspect("matches", "Matches", LabOwner.OfSubjectType("regex")), new LabAspect("code", "Code", LabOwner.OfSubjectType("regex"))],
                         ParameterSources = [mine, theirs, lab]
                     };

        var shown = LabModelRules.SourcesFor(model, LabOwner.OfAspect("matches"), LabPaneType.Static);
        Assert.That(shown, Does.Contain(mine));
        Assert.That(shown, Does.Contain(lab));
        Assert.That(shown, Does.Not.Contain(theirs));
    }

    // ---- parts -------------------------------------------------------------------------------------

    [Test]
    public void Parts_nest_and_are_found_by_key_path_or_name()
    {
        var tree  = Tree();
        var model = new FakeModel { Parts = [tree] };

        Assert.That(LabModelRules.AllParts(model).Select(p => p.Key), Is.EqualTo(new[] { "p0", "p0.0", "p0.0.0", "p0.1" }));
        Assert.That(LabModelRules.FindPart(model, "P0.0.0")?.Name, Is.EqualTo("Group 1"));
        Assert.That(LabModelRules.FindPart(model, "Either of 2 / Way 1")?.Key, Is.EqualTo("p0.0"));
        Assert.That(LabModelRules.FindPart(model, "way 2")?.Key, Is.EqualTo("p0.1"));
        Assert.That(LabModelRules.FindPart(model, "nothing"), Is.Null);
        Assert.That(tree.Children[0].Children[0].Depth, Is.EqualTo(2));
    }

    [Test]
    public void A_part_type_with_a_parent_type_can_only_be_added_inside_that_type()
    {
        var tree  = Tree();
        var model = new FakeModel { Parts = [tree] };

        Assert.That(LabModelRules.AddableTypes(model, null).Select(t => t.Id), Is.EqualTo(new[] { "group", "alternation" }));
        Assert.That(LabModelRules.AddableTypes(model, tree).Select(t => t.Id), Is.EqualTo(new[] { "group", "branch", "alternation" }));
    }

    // ---- templates ---------------------------------------------------------------------------------

    [Test]
    public void A_template_fills_its_holes_and_leaves_unknown_ones()
    {
        var template = new LabTemplate("t", "T", "(?<{{name}}>{{ inner }}) {{other}}", [new("name", "Name", "x"), new("inner", "Inner", ".+")]);
        var values   = new Dictionary<string, string> { ["name"] = "year", ["inner"] = @"\d{4}" };
        Assert.That(template.Render(values), Is.EqualTo(@"(?<year>\d{4}) {{other}}"));
    }

    [Test]
    public void A_binding_gives_a_value_without_asking_and_a_failing_binding_is_asked_about()
    {
        var model = new FakeModel { Parts = [Tree()] };
        model.ActivePart = model.Parts[0];
        var template = new LabTemplate("t", "T", "{{a}}{{b}}{{c}}",
                                       [new("a", "A", "da", Bind: m => m.ActivePart?.Name), new("b", "B", "db"), new("c", "C", "dc", Bind: _ => throw new InvalidOperationException())]);

        var values = template.Initial(model, out var bound);
        Assert.That(values["a"], Is.EqualTo("Either of 2"));
        Assert.That(bound.ToList(), Is.EqualTo(new[] { "a" }));
        Assert.That(values["b"], Is.EqualTo("db"));
        Assert.That(values["c"], Is.EqualTo("dc"));
        Assert.That(template.NeedsAnswers(model), Is.True);
        Assert.That(new LabTemplate("u", "U", "{{a}}", [new("a", "A", Bind: m => "x")]).NeedsAnswers(model), Is.False);
    }

    // ---- reading a pattern into parts ----------------------------------------------------------------

    [Test]
    public void Groups_are_numbered_as_the_engine_numbers_them()
    {
        var nodes = RegexPartParser.Parse("(?<n>a)(b)(?:c)");
        Assert.That(nodes.Select(n => n.Kind), Is.EqualTo(new[] { RegexPartKind.Group, RegexPartKind.Group, RegexPartKind.NonCapturing }));
        Assert.That(nodes[0].Name, Is.EqualTo("n"));
        Assert.That(nodes[0].Number, Is.EqualTo(2));
        Assert.That(nodes[1].Number, Is.EqualTo(1));
        Assert.That(nodes[2].Number, Is.Null);
    }

    [Test]
    public void Groups_nest_and_know_where_they_are()
    {
        const string pattern = "a(b(c))d";
        var nodes = RegexPartParser.Parse(pattern);
        Assert.That(nodes, Has.Count.EqualTo(1));
        Assert.That(nodes[0].Text(pattern), Is.EqualTo("(b(c))"));
        Assert.That(nodes[0].Content(pattern), Is.EqualTo("b(c)"));
        Assert.That(nodes[0].Children, Has.Count.EqualTo(1));
        Assert.That(nodes[0].Children[0].Text(pattern), Is.EqualTo("(c)"));
    }

    [Test]
    public void An_alternation_holds_its_branches_and_an_unclosed_group_is_noticed()
    {
        var alt = RegexPartParser.Parse("(a|b)|c");
        Assert.That(alt, Has.Count.EqualTo(1));
        Assert.That(alt[0].Kind, Is.EqualTo(RegexPartKind.Alternation));
        Assert.That(alt[0].Children.All(c => c.Kind == RegexPartKind.Branch), Is.True);
        Assert.That(alt[0].Children, Has.Count.EqualTo(2));

        var open = RegexPartParser.Parse("(abc");
        Assert.That(open[0].IsClosed, Is.False);
    }

    [Test]
    public void An_empty_pattern_has_no_parts()
    {
        Assert.That(RegexPartParser.Parse(null), Is.Empty);
        Assert.That(RegexPartParser.Parse(string.Empty), Is.Empty);
    }
}
