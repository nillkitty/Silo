using Newtonsoft.Json;

namespace Silo.Labs.Parser;

// The Parser Lab as a model (see LabModel.cs):
//   subject  a data spec (ParserSpecDoc)     parts   its rules (or, in tokenizer mode, its matchers), each with the pieces it is made of nested
//                                                    under it: a piece is a token, text or reference; a combinator holds other pieces
//   aspects  Palette (tokens and combinators), Syntax (the rules), Tests (samples and tests); Rule, Matcher, Piece and Combinator for the active part
//   tools    Add (put a piece into the rule) and Wrap (put the active piece in a combinator)
public sealed partial class ParserLabSession
{
    private const string SubjectId = "spec";

    private static readonly HashSet<string> LeafKinds = new(StringComparer.OrdinalIgnoreCase) { "token", "literal", "chars", "unicode", "ref", "eof" };

    private bool _followingModel;

    private static string NodeJson(SpecNode node) => JsonConvert.SerializeObject(node);

    private static LabPrefab NodePrefab(string id, string name, SpecNode node, string? description, string group) => new(id, name, NodeJson(node), description, "Data1", group);

    private static LabPrefab RulePrefab(string id, string name, RuleDef rule, string? description) => new(id, name, JsonConvert.SerializeObject(rule), description, "Rule", rule.Kind == RuleKind.Token ? "Tokens" : "Syntax");

    private static SpecNode Tok(string id) => SpecNode.Token(id);

    private static readonly LabPartType RuleType = new("rule", "Rule", "Rule", "A named rule of the spec",
                                                       [
                                                           RulePrefab("digits", "Digits as an integer", new RuleDef { Name = "digits", Kind = RuleKind.Token, Body = SpecNode.Convert(SpecNode.AtLeastOnce(Tok("decimal-digit")), "int") }, "One or more digits, read as a number"),
                                                           RulePrefab("hex-byte", "Hex byte", new RuleDef { Name = "hexByte", Kind = RuleKind.Token, Body = SpecNode.Convert(SpecNode.Repeat(Tok("hex-digit"), 2, 2), "byte-hex") }, "Two hex digits, read as a byte"),
                                                           RulePrefab("identifier", "Identifier", new RuleDef { Name = "identifier", Kind = RuleKind.Token, Body = SpecNode.Seq(SpecNode.Choice(Tok("alpha"), SpecNode.Literal("_")), SpecNode.Many(Tok("word-char"))) }, "A letter or _ followed by letters, digits and _"),
                                                           RulePrefab("white", "White space", new RuleDef { Name = "white", Kind = RuleKind.Token, Body = SpecNode.AtLeastOnce(Tok("whitespace")).Dropped() }, "A run of white space, left out of the value"),
                                                           RulePrefab("quoted", "Quoted text", new RuleDef { Name = "quoted", Kind = RuleKind.Syntax, Body = SpecNode.Seq(SpecNode.Literal("\"").Dropped(), SpecNode.Many(SpecNode.Chars("\"", true)), SpecNode.Literal("\"").Dropped()) }, "Text between double quotes"),
                                                           RulePrefab("comma-list", "Comma separated list", new RuleDef { Name = "list", Kind = RuleKind.Syntax, Body = new SpecNode { Kind = "delimited", Min = 1, Children = [SpecNode.Ref("item"), SpecNode.Chars(",").Dropped()] } }, "One or more of the rule 'item', with commas between")
                                                       ],
                                                       [
                                                           new LabTemplate("keyword", "Keyword", "{{name}}|keyword|{{text}}", [new("name", "Name of the rule", "keyword"), new("text", "The text it matches", "text")], "A rule that matches one piece of text"),
                                                           new LabTemplate("word-choice", "One of several words", "{{name}}|choice|{{words}}", [new("name", "Name of the rule", "word"), new("words", "The words, separated by semicolons", "yes;no")], "A rule that matches any of some words"),
                                                           new LabTemplate("list", "List", "{{name}}|list|{{item}}|{{separator}}", [new("name", "Name of the rule", "list"), new("item", "Rule an item is", "item"), new("separator", "What separates the items", ",")], "One or more of a rule, with a separator between")
                                                       ]);

    private static readonly LabPartType MatcherType = new("matcher", "Matcher", "Token", "A rule of the tokenizer: what it matches and the kind of token it makes",
                                                          [
                                                              new("whitespace", "White space (skipped)", JsonConvert.SerializeObject(new TokenRule { Matcher = SpecNode.AtLeastOnce(Tok("whitespace")) }), "Matches white space and makes no token", "Token", "Skipped"),
                                                              new("number", "Number", JsonConvert.SerializeObject(new TokenRule { Kind = "Number", Matcher = SpecNode.AtLeastOnce(Tok("decimal-digit")) }), "A run of digits", "Token", "Tokens"),
                                                              new("identifier", "Identifier", JsonConvert.SerializeObject(new TokenRule { Kind = "Identifier", Matcher = SpecNode.Seq(Tok("alpha"), SpecNode.Many(Tok("word-char"))) }), "A letter followed by letters, digits and _", "Token", "Tokens")
                                                          ],
                                                          [
                                                              new LabTemplate("keyword", "Keyword", "{{kind}}|literal|{{text}}", [new("kind", "Kind of token it makes", "Keyword"), new("text", "The text it matches", "text")], "Matches one piece of text"),
                                                              new LabTemplate("run", "Run of a token", "{{kind}}|run|{{token}}", [new("kind", "Kind of token it makes", "Word"), new("token", "What a character of the run is", "alpha", SpecCatalog.Tokens.Where(t => t.Test is not null).Select(t => t.Id).ToList())], "A run of one or more of a token")
                                                          ]);

    private static readonly LabPartType PieceType = new("piece", "Piece", "Data1", "A token, some text or a reference to a rule",
                                                        SpecCatalog.Tokens.Select(t => NodePrefab("token-" + t.Id, t.Title, Tok(t.Id), t.Description, "Tokens")).Concat(
                                                        [
                                                            NodePrefab("digit-chars", "Digits 0-9", SpecNode.Chars("0123456789"), "One of the characters 0 to 9", "Characters"),
                                                            NodePrefab("sign", "Sign", SpecNode.Chars("+-"), "A plus or a minus", "Characters"),
                                                            NodePrefab("dot", "Dot", SpecNode.Literal("."), "A full stop", "Characters")
                                                        ]).ToList(),
                                                        [
                                                            new LabTemplate("literal", "Text", "literal|{{text}}", [new("text", "The text to match", "text")], "A fixed piece of text"),
                                                            new LabTemplate("literal-nocase", "Text, any case", "literal|{{text}}|ignorecase", [new("text", "The text to match", "text")], "A fixed piece of text, capitals or small letters"),
                                                            new LabTemplate("chars", "One of these characters", "chars|{{set}}", [new("set", "The characters", "abc")], "Any one of some characters"),
                                                            new LabTemplate("not-chars", "Any but these characters", "chars|{{set}}|negate", [new("set", "The characters", "\"")], "Any one character except some"),
                                                            new LabTemplate("unicode", "Unicode class", "unicode|{{classes}}", [new("classes", "Classes (for example L, Nd)", "L")], "A character of the Unicode classes"),
                                                            new LabTemplate("ref", "Reference to a rule", "ref|{{rule}}", [new("rule", "Name of the rule", "rule")], "Whatever a rule of the spec matches")
                                                        ]);

    private static readonly LabPartType CombinatorType = new("combinator", "Combinator", "Rule", "Puts pieces together: a sequence, a choice, a repeat...",
                                                             SpecCatalog.Combinators.Select(c => new LabPrefab(c.Id, c.Title, c.Id, c.Description, "Rule", c.Group)).ToList(),
                                                             [
                                                                 new LabTemplate("repeat-n", "Repeat from p to q", "repeat:{{min}}:{{max}}", [new("min", "Fewest times", "1"), new("max", "Most times (empty: as many as there are)", "3")], "Wraps the piece in a repeat with limits"),
                                                                 new LabTemplate("named", "Named", "named:{{name}}", [new("name", "The name of the value", "name")], "Gives the value of the piece a name"),
                                                                 new LabTemplate("convert", "Convert", "apply:{{to}}", [new("to", "What the value is made into", "int", SpecCatalog.Conversions)], "Turns the value of the piece into another")
                                                             ]);

    protected override string SubjectNoun => "spec";

    // ---- subject: prefabs and templates ---------------------------------------------------------------

    protected override IEnumerable<LabSubjectType> DefineSubjectTypes()
    {
        static ParserSpecDoc Doc(string name, string root, params RuleDef[] rules) => new() { Name = name, Root = root, Rules = [.. rules] };

        var integer = Doc("Data.Integer", "int", new RuleDef { Name = "int", Kind = RuleKind.Token, Returns = "int", Body = SpecNode.Convert(SpecNode.AtLeastOnce(Tok("decimal-digit")), "int") });
        integer.ClrType = "System.Int32";
        var mac = Doc("Net.Mac.Address", "mac",
                      new RuleDef { Name = "byte", Kind = RuleKind.Token, Body = SpecNode.Convert(SpecNode.Repeat(Tok("hex-digit"), 2, 2), "byte-hex") },
                      new RuleDef { Name = "mac", Kind = RuleKind.Syntax, Body = SpecNode.Seq(SpecNode.Ref("byte"), SpecNode.Repeat(SpecNode.Seq(SpecNode.Chars(":-").Dropped(), SpecNode.Ref("byte")), 5, 5)) });
        mac.ClrType = "System.Byte[]";
        var yesNo = Doc("Data.YesNo", "answer", new RuleDef { Name = "answer", Kind = RuleKind.Syntax, Body = SpecNode.Choice(SpecNode.Literal("yes", true), SpecNode.Literal("no", true)) });

        var prefabs = new List<LabPrefab>
                      {
                          new("integer", "Integer", integer.ToJson(), "Digits read as a number", "Data1", "Examples"),
                          new("mac", "MAC address", mac.ToJson(), "Six hex bytes with : or - between them", "Data1", "Examples"),
                          new("yes-no", "Yes or no", yesNo.ToJson(), "One of two words, any case", "Data1", "Examples")
                      };
        var templates = new List<LabTemplate>
                        {
                            new("empty", "Empty spec", "{{name}}|{{mode}}", [new("name", "Fully qualified name of the spec", "My.Data.Spec"), new("mode", "What the canvas builds", "parser", ["parser", "tokenizer"])], "A spec with nothing in it yet", "Data1"),
                            new("keywords", "Keywords", "{{name}}|parser|{{words}}", [new("name", "Fully qualified name of the spec", "My.Data.Keyword"), new("words", "The words, separated by semicolons", "red;green;blue")], "A spec that accepts one of some words", "Data1")
                        };
        yield return new LabSubjectType(SubjectId, "Spec", "Data1", "A data specification: the rules that read text into a value", prefabs, templates);
    }

    protected override Task OnNewSubjectAsync(LabSubjectType type, string content)
    {
        if (!ConfirmDiscard()) return Task.CompletedTask;

        ParserSpecDoc? doc;
        if (content.TrimStart().StartsWith('{'))
        {
            doc = ParserSpecDoc.FromJson(content) ?? throw new InvalidOperationException("That is not a spec.");
        }
        else
        {
            var    f  = content.Split('|');
            string At(int i) => i < f.Length ? f[i].Trim() : string.Empty;
            doc = new ParserSpecDoc { Name = At(0).Length > 0 ? At(0) : "My.Data.Spec", Mode = Enum.TryParse<SpecMode>(At(1), true, out var mode) ? mode : SpecMode.Parser };
            var words = At(2).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length > 0)
            {
                doc.Rules.Add(new RuleDef { Name = "keyword", Kind = RuleKind.Syntax, Body = SpecNode.Choice(words.Select(w => SpecNode.Literal(w)).ToArray()) });
                doc.Root = "keyword";
            }
        }

        _ws.Open(doc, null, true);
        RaiseValueChanged();
        return Task.CompletedTask;
    }

    // ---- parts: rules (or matchers) and the pieces under them ----------------------------------------------------

    protected override IEnumerable<LabPartType> DefinePartTypes() => [RuleType, MatcherType, PieceType, CombinatorType];

    protected override IEnumerable<LabPart> BuildParts()
    {
        if (_ws.Doc.Mode == SpecMode.Tokenizer)
        {
            for (int i = 0; i < _ws.Doc.Tokenizer.Rules.Count; i++)
            {
                var    rule = _ws.Doc.Tokenizer.Rules[i];
                string key  = "t:" + i;
                var    part = new LabPart(MatcherType, key, $"{i + 1}. {rule.Kind ?? "(skipped)"}", rule, rule.Matcher?.Describe());
                if (rule.Matcher is not null) part.Add(NodePart(rule.Matcher, key, []));
                yield return part;
            }

            yield break;
        }

        foreach (var rule in _ws.Doc.Rules)
        {
            string key    = "r:" + rule.Name;
            bool   root   = string.Equals(rule.Name, _ws.Doc.Root, StringComparison.OrdinalIgnoreCase);
            string detail = (rule.Kind == RuleKind.Token ? "token" : "syntax") + (root ? ", root" : string.Empty) + (rule.Returns is null ? string.Empty : " → " + rule.Returns);
            var    part   = new LabPart(RuleType, key, rule.Name, rule, detail);
            if (rule.Body is not null) part.Add(NodePart(rule.Body, key, []));
            yield return part;
        }
    }

    private static LabPart NodePart(SpecNode node, string prefix, int[] path)
    {
        bool   leaf = node.Children.Count == 0 && LeafKinds.Contains(node.Kind);
        string key  = prefix + "/n" + string.Join('.', path);
        string name = leaf ? LabUi.Shorten(node.Describe(), 40) : $"{node.Kind}  {LabUi.Shorten(node.Describe(), 34)}";
        var    part = new LabPart(leaf ? PieceType : CombinatorType, key, name, node, node.Ignore ? "left out of the result" : null);
        for (int i = 0; i < node.Children.Count; i++) part.Add(NodePart(node.Children[i], prefix, [.. path, i]));
        return part;
    }

    private static int[]? PathIn(SpecNode root, SpecNode node)
    {
        if (ReferenceEquals(root, node)) return [];

        for (int i = 0; i < root.Children.Count; i++)
        {
            if (PathIn(root.Children[i], node) is { } inner) return [i, .. inner];
        }

        return null;
    }

    /// <summary>The key of the part the workspace's selection stands for.</summary>
    private string? KeyOfSelection()
    {
        var doc = _ws.Doc;
        if (_ws.SelectedNode is { } node)
        {
            if (doc.Mode == SpecMode.Parser)
            {
                foreach (var rule in doc.Rules)
                    if (rule.Body is not null && PathIn(rule.Body, node) is { } path)
                        return "r:" + rule.Name + "/n" + string.Join('.', path);
            }
            else
            {
                for (int i = 0; i < doc.Tokenizer.Rules.Count; i++)
                    if (doc.Tokenizer.Rules[i].Matcher is { } matcher && PathIn(matcher, node) is { } path)
                        return "t:" + i + "/n" + string.Join('.', path);
            }
        }

        if (doc.Mode == SpecMode.Tokenizer) return _ws.SelectedTokenRule is { } tr ? "t:" + doc.Tokenizer.Rules.IndexOf(tr) : null;

        return _ws.Current is { } current ? "r:" + current.Name : null;
    }

    /// <summary>The selection in the workspace moved (or the spec changed): the active part follows it.</summary>
    private void NoteSelection()
    {
        if (!_followingModel) SetActivePart(KeyOfSelection());
    }

    protected override void OnPartActivated(LabPart? part)
    {
        _followingModel = true;
        try
        {
            switch (part?.Tag)
            {
                case RuleDef rule:
                    _ws.SelectedNode = null;
                    _ws.CurrentRule  = rule.Name;
                    break;
                case TokenRule matcher:
                    _ws.SelectedTokenRule = matcher;
                    break;
                case SpecNode node:
                    var owner = part.Parent;
                    while (owner?.Tag is SpecNode) owner = owner.Parent;
                    if (owner?.Tag is RuleDef holder) _ws.CurrentRule = holder.Name;
                    else if (owner?.Tag is TokenRule matcherHolder) _ws.SelectedTokenRule = matcherHolder;
                    _ws.SelectedNode = node;
                    break;
                default:
                    _ws.SelectedNode      = null;
                    _ws.SelectedTokenRule = null;
                    break;
            }
        }
        finally
        {
            _followingModel = false;
        }
    }

    // ---- adding parts -------------------------------------------------------------------------------------------------

    protected override Task OnAddPartAsync(LabPartType type, LabPart? parent, string content)
    {
        switch (type.Id)
        {
            case "rule":
                var rule = ReadRule(content);
                _ws.AddRule(rule.Name, rule.Kind, rule.Body);
                if (rule.Returns is not null || rule.Description is not null) _ws.EditCurrent(r => (r.Returns, r.Description) = (rule.Returns, rule.Description));
                break;
            case "matcher":
                AddMatcher(content);
                break;
            case "piece":
                AddNodeTo(parent, ReadNode(content));
                break;
            case "combinator":
                AddCombinatorTo(parent, content);
                break;
            default:
                throw new InvalidOperationException($"A {type.Name.ToLowerInvariant()} cannot be added this way.");
        }

        return Task.CompletedTask;
    }

    private RuleDef ReadRule(string content)
    {
        if (content.TrimStart().StartsWith('{')) return JsonConvert.DeserializeObject<RuleDef>(content) ?? new RuleDef { Name = "rule" };

        var    f  = content.Split('|');
        string At(int i) => i < f.Length ? f[i].Trim() : string.Empty;

        var rule = new RuleDef { Name = At(0).Length > 0 ? At(0) : "rule" };
        switch (At(1).ToLowerInvariant())
        {
            case "keyword":
                rule.Body = SpecNode.Literal(At(2));
                break;
            case "choice":
                rule.Body = SpecNode.Choice(At(2).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => SpecNode.Literal(w)).ToArray());
                break;
            case "list":
                rule.Body = new SpecNode { Kind = "delimited", Min = 1, Children = [SpecNode.Ref(At(2).Length > 0 ? At(2) : "item"), SpecNode.Chars(At(3).Length > 0 ? At(3) : ",").Dropped()] };
                break;
        }

        return rule;
    }

    /// <summary>A piece from JSON, or from "kind|text|flags" (flags: ignore, ignorecase, negate).</summary>
    private static SpecNode ReadNode(string content)
    {
        if (content.TrimStart().StartsWith('{')) return JsonConvert.DeserializeObject<SpecNode>(content) ?? throw new InvalidOperationException("That is not a piece.");

        var    f    = content.Split('|');
        string At(int i) => i < f.Length ? f[i] : string.Empty;
        string text = At(1);

        var node = At(0).ToLowerInvariant() switch
                   {
                       "token"   => SpecNode.Token(text.Trim()),
                       "literal" => SpecNode.Literal(text),
                       "chars"   => SpecNode.Chars(text),
                       "unicode" => SpecNode.Unicode(text.Trim()),
                       "ref"     => SpecNode.Ref(text.Trim()),
                       "eof"     => new SpecNode { Kind = "eof" },
                       _         => throw new InvalidOperationException($"'{At(0)}' is not a kind of piece.")
                   };
        foreach (string flag in At(2).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (flag.ToLowerInvariant())
            {
                case "ignore": node.Ignore         = true; break;
                case "ignorecase": node.IgnoreCase = true; break;
                case "negate": node.Negate         = true; break;
            }
        }

        return node;
    }

    private void AddMatcher(string content)
    {
        TokenRule rule;
        if (content.TrimStart().StartsWith('{'))
        {
            rule = JsonConvert.DeserializeObject<TokenRule>(content) ?? new TokenRule();
        }
        else
        {
            var    f  = content.Split('|');
            string At(int i) => i < f.Length ? f[i].Trim() : string.Empty;

            rule = new TokenRule { Kind = At(0).Length > 0 ? At(0) : null };
            rule.Matcher = At(1).ToLowerInvariant() == "run" ? SpecNode.AtLeastOnce(SpecNode.Token(At(2))) : ReadNode(At(1) + "|" + At(2));
        }

        if (_ws.Doc.Mode != SpecMode.Tokenizer) _ws.Edit(d => d.Mode = SpecMode.Tokenizer, WorkspaceChange.Header);

        if (rule.Kind is { Length: > 0 } kind && !_ws.Doc.Tokenizer.Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase) && _ws.AddKind(kind) is { } refused) Status = refused;

        _ws.Edit(d => d.Tokenizer.Rules.Add(rule));
        _ws.SelectedTokenRule = rule;
    }

    /// <summary>Where a new piece goes: the tree and the node it is put at (null: at the rule as a whole), from the part it is added to, else the selection.</summary>
    private (SpecSlot Slot, SpecNode? Target) AddTarget(LabPart? parent)
    {
        switch (parent?.Tag)
        {
            case SpecNode node when _ws.SlotOf(node) is { } slot:
                return (slot, node);
            case RuleDef rule:
                return (ParserWorkspace.RuleSlot(rule), null);
            case TokenRule matcher:
                return (ParserWorkspace.MatcherSlot(matcher), null);
        }

        if (_ws.SelectedNode is { } selected && _ws.SlotOf(selected) is { } selectedSlot) return (selectedSlot, selected);

        if (_ws.Doc.Mode == SpecMode.Tokenizer)
        {
            var matcher = _ws.SelectedTokenRule ?? _ws.Doc.Tokenizer.Rules.LastOrDefault() ?? _ws.AddTokenRule();
            return (ParserWorkspace.MatcherSlot(matcher), null);
        }

        var current = _ws.Current ?? _ws.AddRule();
        return (ParserWorkspace.RuleSlot(current), null);
    }

    private void AddNodeTo(LabPart? parent, SpecNode node)
    {
        var (slot, target) = AddTarget(parent);
        _ws.Drop(new DragItem("json", NodeJson(node)), target, slot);
    }

    /// <summary>"id" or "id:arg:arg" (repeat:1:3, named:x, apply:int): wraps the piece in the combinator.</summary>
    private void AddCombinatorTo(LabPart? parent, string content)
    {
        var    pieces = content.Split(':');
        string id     = pieces[0].Trim();
        if (SpecCatalog.Combinator(id) is null) throw new InvalidOperationException($"'{id}' is not a combinator.");

        var (slot, target) = AddTarget(parent);
        SpecNode? made;
        if (target is not null)
        {
            _ws.Wrap(target, id);
            made = _ws.SelectedNode;
        }
        else
        {
            made = _ws.Drop(new DragItem("comb", id), null, slot);
        }

        if (made is null || pieces.Length < 2) return;

        _ws.EditNode(made, n =>
                           {
                               switch (id)
                               {
                                   case "repeat" when int.TryParse(pieces[1], out int min):
                                       n.Min = min;
                                       n.Max = pieces.Length > 2 && int.TryParse(pieces[2], out int max) ? max : null;
                                       break;
                                   case "named":
                                       n.Name = pieces[1];
                                       break;
                                   case "apply":
                                       n.Text = pieces[1];
                                       break;
                                   case "where":
                                       n.Text = string.Join(':', pieces[1..]);
                                       break;
                               }
                           });
    }

    // ---- aspects, tools and commands ------------------------------------------------------------------------

    private SpecNode? SelectedNode => _ws.SelectedNode;

    private bool CanEditNode() => SelectedNode is not null;

    protected override IEnumerable<LabAspect> DefineAspects()
    {
        var spec = LabOwner.OfSubjectType(SubjectId);
        yield return new LabAspect("palette", "Palette", spec) { IconKey = "Token", Description = "The tokens and combinators a rule is built from" };
        yield return new LabAspect("syntax", "Syntax", spec) { IconKey = "Rule", Description = "The rules of the spec, its saved pieces and the specs kept in the Silo" };
        yield return new LabAspect("tests", "Tests", spec) { IconKey = "Flask", Description = "Samples to try the spec on, and tests of what is expected of them" };

        yield return new LabAspect("rule", "Rule", LabOwner.OfPartType(RuleType.Id))
                     {
                         IconKey     = "Rule",
                         Description = "The active rule: whether it is the root, a token or a syntax, and what it returns",
                         Commands =
                         [
                             ProvidedCommand.From("rule-root", "Make _Root", () => _ws.Edit(d => d.Root = _ws.CurrentRule, WorkspaceChange.Header), "Rule", () => _ws.Current is not null, "Samples are parsed with the root rule unless a rule is named") with
                             {
                                 IsChecked = () => _ws.Current is { } r && string.Equals(r.Name, _ws.Doc.Root, StringComparison.OrdinalIgnoreCase)
                             },
                             ProvidedCommand.From("rule-token", "_Token Rule", () => _ws.EditCurrent(r => r.Kind = r.Kind == RuleKind.Token ? RuleKind.Syntax : RuleKind.Token), "Token", () => _ws.Current is not null, "A token is made of characters; a syntax is made of tokens and other rules") with
                             {
                                 IsChecked = () => _ws.Current?.Kind == RuleKind.Token
                             },
                             ProvidedCommand.From("rule-linear", "_Linear Shape", () => _ws.EditCurrent(r => r.Shape = r.Shape == "Linear" ? "Tree" : "Linear"), null, () => _ws.Current is not null, "Flatten the nested values of the rule into one list") with
                             {
                                 IsChecked = () => _ws.Current?.Shape == "Linear"
                             },
                             ProvidedCommand.From("rule-returns", "Set Re_turns...", SetReturns, null, () => _ws.Current is not null, "The CLR type the rule yields (for example int, byte[6]); empty: whatever the body's value is"),
                             ProvidedCommand.From("rule-describe", "Set _Description...", SetDescription, null, () => _ws.Current is not null, "What the rule is for")
                         ]
                     };
        yield return new LabAspect("matcher", "Matcher", LabOwner.OfPartType(MatcherType.Id))
                     {
                         IconKey     = "Token",
                         Description = "The active tokenizer rule: the kind of token it makes, and its place among the rules (they are tried in order)",
                         Commands =
                         [
                             ProvidedCommand.From("matcher-kind", "Set _Kind...", SetMatcherKind, null, () => _ws.SelectedTokenRule is not null, "The kind of token the rule makes; empty: the text is skipped"),
                             ProvidedCommand.From("matcher-up", "Move _Up", () => MoveMatcher(-1), null, () => _ws.SelectedTokenRule is not null, "Try the rule earlier"),
                             ProvidedCommand.From("matcher-down", "Move _Down", () => MoveMatcher(1), null, () => _ws.SelectedTokenRule is not null, "Try the rule later"),
                             ProvidedCommand.From("matcher-dup", "D_uplicate", () => { if (_ws.SelectedTokenRule is { } r) _ws.DuplicateTokenRule(r); }, "TbCopy", () => _ws.SelectedTokenRule is not null, "Copy the rule after itself"),
                             ProvidedCommand.From("matcher-delete", "_Delete", () => { if (_ws.SelectedTokenRule is { } r) _ws.RemoveTokenRule(r); }, "TbDelete", () => _ws.SelectedTokenRule is not null, "Take the rule out of the tokenizer") with { IsDestructive = true }
                         ]
                     };
        yield return new LabAspect("piece", "Piece", LabOwner.OfPartType(PieceType.Id))
                     {
                         IconKey     = "Data1",
                         Description = "The active piece: how it reads and whether its value is kept",
                         Commands =
                         [
                             ProvidedCommand.From("piece-ignore", "_Leave Out of Result", () => ToggleNode(n => n.Ignore = !n.Ignore), null, CanEditNode, "The value of the piece is not part of the value of the sequence it is in") with { IsChecked = () => SelectedNode?.Ignore == true },
                             ProvidedCommand.From("piece-nocase", "Ignore _Case", () => ToggleNode(n => n.IgnoreCase = !n.IgnoreCase), null, () => SelectedNode is { Kind: "literal" }, "Capitals and small letters are the same") with { IsChecked = () => SelectedNode?.IgnoreCase == true },
                             ProvidedCommand.From("piece-negate", "_Negate", () => ToggleNode(n => n.Negate = !n.Negate), null, () => SelectedNode is { Kind: "chars" or "unicode" }, "Any character except these") with { IsChecked = () => SelectedNode?.Negate == true },
                             ProvidedCommand.From("piece-text", "Set _Text...", SetNodeText, null, () => SelectedNode is { Kind: "literal" or "chars" or "unicode" or "ref" or "token" }, "The text, characters, classes, rule or token the piece stands for"),
                             ProvidedCommand.From("piece-save-token", "Save as _Token...", () => SaveNodeAs(FragmentKind.Token), null, () => SelectedNode is not null, "Keep the piece in the spec as a token used by name"),
                             ProvidedCommand.From("piece-save-syntax", "Save as _Syntax...", () => SaveNodeAs(FragmentKind.Syntax), null, () => SelectedNode is not null, "Keep the piece in the spec to drop into rules as a copy")
                         ]
                     };
        yield return new LabAspect("combinator", "Combinator", LabOwner.OfPartType(CombinatorType.Id))
                     {
                         IconKey     = "Rule",
                         Description = "The active combinator: its limits, name and conversion, and taking it away",
                         Commands =
                         [
                             ProvidedCommand.From("comb-unwrap", "_Unwrap", () => { if (SelectedNode is { } n) _ws.Unwrap(n); }, null, () => SelectedNode is { Children.Count: 1 }, "Take the combinator away, leaving what was in it"),
                             ProvidedCommand.From("comb-ignore", "_Leave Out of Result", () => ToggleNode(n => n.Ignore = !n.Ignore), null, CanEditNode, "The value is not part of the value of the sequence it is in") with { IsChecked = () => SelectedNode?.Ignore == true },
                             ProvidedCommand.From("comb-count", "Set _Count...", SetRepeatLimits, null, () => SelectedNode is { Kind: "repeat" or "delimited" }, "Fewest and most times (for example 1,3; 2, for no upper limit)"),
                             ProvidedCommand.From("comb-name", "Set _Name...", SetNodeName, null, () => SelectedNode is { Kind: "named" }, "The name of the value"),
                             ProvidedCommand.From("comb-convert", "Set Con_version...", SetConversion, null, () => SelectedNode is { Kind: "convert" }, "What the value is made into"),
                             ProvidedCommand.From("comb-test", "Set T_est...", SetWhere, null, () => SelectedNode is { Kind: "where" }, "The test the text must satisfy (len:2-4, range:0..255, regex:...)"),
                             ProvidedCommand.From("comb-save-syntax", "Save as _Syntax...", () => SaveNodeAs(FragmentKind.Syntax), null, () => SelectedNode is not null, "Keep the piece in the spec to drop into rules as a copy")
                         ]
                     };
    }

    protected override IEnumerable<LabTool> DefineTools()
    {
        var spec = LabOwner.OfSubjectType(SubjectId);

        var wrapCommands = new List<ProvidedCommand>();
        foreach (var c in SpecCatalog.Combinators)
        {
            string id = c.Id;
            if (id == "repeat")
            {
                wrapCommands.Add(ProvidedCommand.From("wrap-repeat", "Repeat _p to q", WrapRepeat, null, CanEditNode, "Repeat the piece from the tool's Min to its Max times", c.Group));
                continue;
            }

            wrapCommands.Add(ProvidedCommand.From("wrap-" + id, c.Title, () => { if (SelectedNode is { } n) _ws.Wrap(n, id); }, null, CanEditNode, c.Description, c.Group));
        }

        yield return new LabTool("wrap", "_Wrap", spec)
                     {
                         IconKey     = "Rule",
                         Description = "Put the active piece in a combinator (Many, Optional, Named...)",
                         Parameters  = LabParameterSet.For(LabOf.Id, "tool:wrap", "Wrap", [new LabParameter("min", "Min", LabParameterKind.Text, "1", "The fewest times, for Repeat p to q."), new LabParameter("max", "Max", LabParameterKind.Text, "3", "The most times, for Repeat p to q; empty for as many as there are.")]),
                         Commands    = wrapCommands
                     };

        var tokenCommands = SpecCatalog.Tokens.Select(t => ProvidedCommand.From("add-token-" + t.Id, t.Title, () => AddFromTool(new DragItem("token", t.Id)), null, null, t.Description)).ToList();
        yield return new LabTool("add", "_Add", spec)
                     {
                         IconKey     = "Plus",
                         Description = "Put a token, some text or a reference into the active piece or rule",
                         Parameters  = LabParameterSet.For(LabOf.Id, "tool:add", "Add", [new LabParameter("ignore", "Leave out of result", LabParameterKind.Bool, false, "The new piece's value is not part of the value of the sequence it is in."), new LabParameter("nocase", "Ignore case", LabParameterKind.Bool, false, "For text: capitals and small letters are the same.")]),
                         Commands =
                         [
                             ProvidedCommand.Submenu("add-token", "_Token", tokenCommands, null, "Data1", "A built-in token"),
                             ProvidedCommand.From("add-literal", "_Text...", () => AddAsked("literal", "Text to match:", "text"), null, null, "A fixed piece of text"),
                             ProvidedCommand.From("add-chars", "_Characters...", () => AddAsked("chars", "Characters, any one of which matches:", "abc"), null, null, "Any one of some characters"),
                             ProvidedCommand.From("add-unicode", "_Unicode Classes...", () => AddAsked("unicode", "Unicode classes (for example L, Nd):", "L"), null, null, "A character of the Unicode classes"),
                             ProvidedCommand.From("add-ref", "Rule _Reference...", () => AddAsked("rule", "Name of the rule:", _ws.Doc.Rules.FirstOrDefault()?.Name ?? "rule"), null, null, "Whatever a rule of the spec matches"),
                             ProvidedCommand.From("add-eof", "_End of Input", () => AddFromTool(new DragItem("token", "EOF")), null, null, "Matches only at the end of the text")
                         ]
                     };
    }

    protected override IEnumerable<LabCommandEntry> DefineModelCommands()
    {
        yield return new LabCommandEntry(LabOwner.Part, ProvidedCommand.From("part-copy-json", "Copy Part _JSON", CopyPartJson, "TbCopy", () => ActivePart is not null, "Put the active part on the clipboard as JSON"));
        yield return new LabCommandEntry(LabOwner.Part, ProvidedCommand.From("part-parent", "Select _Parent", () => ActivatePart(ActivePart?.Parent), null, () => ActivePart?.Parent is not null, "Select the rule or piece the active part is in"));
    }

    private void CopyPartJson()
    {
        string json = ActivePart?.Tag switch
                      {
                          SpecNode node  => JsonConvert.SerializeObject(node, Formatting.Indented),
                          RuleDef rule   => JsonConvert.SerializeObject(rule, Formatting.Indented),
                          TokenRule tr   => JsonConvert.SerializeObject(tr, Formatting.Indented),
                          _              => string.Empty
                      };
        if (json.Length == 0) return;

        LabUi.Copy(json);
        Say("Copied the part as JSON");
    }

    private void ToggleNode(Action<SpecNode> change)
    {
        if (SelectedNode is { } node) _ws.EditNode(node, change);
    }

    private void SetReturns()
    {
        if (_ws.Current is not { } rule) return;

        string? answer = ParserUi.Ask("Returns", "The type the rule yields (for example int, byte[6]); empty for whatever the body's value is:", rule.Returns ?? string.Empty);
        if (answer is not null) _ws.EditCurrent(r => r.Returns = answer.Trim().Length == 0 ? null : answer.Trim());
    }

    private void SetDescription()
    {
        if (_ws.Current is not { } rule) return;

        string? answer = ParserUi.Ask("Description", "What the rule is for:", rule.Description ?? string.Empty);
        if (answer is not null) _ws.EditCurrent(r => r.Description = answer.Trim().Length == 0 ? null : answer.Trim());
    }

    private void SetMatcherKind()
    {
        if (_ws.SelectedTokenRule is not { } rule) return;

        string? answer = ParserUi.Ask("Kind of token", "The kind of token the rule makes; empty to skip the text:", rule.Kind ?? string.Empty);
        if (answer is null) return;

        string? kind = answer.Trim().Length == 0 ? null : answer.Trim();
        if (kind is not null && !_ws.Doc.Tokenizer.Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase) && _ws.AddKind(kind) is { } refused)
        {
            Say(refused);
            return;
        }

        _ws.Edit(_ => rule.Kind = kind);
    }

    private void MoveMatcher(int delta)
    {
        if (_ws.SelectedTokenRule is { } rule) _ws.MoveTokenRule(rule, delta);
    }

    private void SetNodeText()
    {
        if (SelectedNode is not { } node) return;

        string? answer = ParserUi.Ask("Piece", "The text, characters, classes, rule or token:", node.Text ?? string.Empty);
        if (answer is not null) _ws.EditNode(node, n => n.Text = answer);
    }

    private void SetNodeName()
    {
        if (SelectedNode is not { Kind: "named" } node) return;

        string? answer = ParserUi.Ask("Name", "The name of the value:", node.Name ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(answer)) _ws.EditNode(node, n => n.Name = answer.Trim());
    }

    private void SetConversion()
    {
        if (SelectedNode is not { Kind: "convert" } node) return;

        string? answer = ParserUi.Ask("Conversion", "What the value is made into (" + string.Join(", ", SpecCatalog.Conversions) + "):", node.Text ?? "text");
        if (!string.IsNullOrWhiteSpace(answer)) _ws.EditNode(node, n => n.Text = answer.Trim());
    }

    private void SetWhere()
    {
        if (SelectedNode is not { Kind: "where" } node) return;

        string? answer = ParserUi.Ask("Test", "The test the text must satisfy (len:2-4, range:0..255, regex:...):", node.Text ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(answer)) _ws.EditNode(node, n => n.Text = answer.Trim());
    }

    private void SetRepeatLimits()
    {
        if (SelectedNode is not { } node) return;

        string initial = node.Max is { } max ? $"{node.Min},{max}" : $"{node.Min},";
        string? answer = ParserUi.Ask("Count", "Fewest and most times, for example 1,3 (2, for no upper limit):", initial);
        if (answer is null) return;

        var parts = answer.Replace(" ", string.Empty).Split(',');
        if (!int.TryParse(parts[0], out int min) || min < 0)
        {
            Say("Give the fewest times as a number, then the most: 1,3");
            return;
        }

        int? most = parts.Length > 1 && int.TryParse(parts[1], out int m) ? m : parts.Length == 1 ? min : null;
        _ws.EditNode(node, n => (n.Min, n.Max) = (min, most));
    }

    private void SaveNodeAs(FragmentKind kind)
    {
        if (SelectedNode is not { } node) return;

        string? name = ParserUi.Ask(kind == FragmentKind.Token ? "Save as token" : "Save as syntax", "What is it called?", _ws.UniqueFragmentName("piece"));
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            _ws.SaveFragment(node, name, kind);
            Say($"Kept {name.Trim()} in the spec");
        }
        catch (InvalidOperationException ex)
        {
            Say(ex.Message);
        }
    }

    private bool ToolFlag(string tool, string name) => Tools.FirstOrDefault(t => t.Id == tool)?.Parameters?[name].Bool == true;

    private string ToolText(string tool, string name) => Tools.FirstOrDefault(t => t.Id == tool)?.Parameters?[name].Text ?? string.Empty;

    private void WrapRepeat()
    {
        if (SelectedNode is not { } node) return;

        if (!int.TryParse(ToolText("wrap", "min").Trim(), out int min) || min < 0)
        {
            Say("The tool's Min is not a number");
            return;
        }

        int? max = int.TryParse(ToolText("wrap", "max").Trim(), out int m) ? m : null;
        _ws.Wrap(node, "repeat");
        if (_ws.SelectedNode is { Kind: "repeat" } made) _ws.EditNode(made, n => (n.Min, n.Max) = (min, max));
    }

    private void AddAsked(string kind, string question, string initial)
    {
        string? answer = ParserUi.Ask("Add", question, initial);
        if (string.IsNullOrEmpty(answer)) return;

        AddFromTool(new DragItem(kind, answer));
    }

    private void AddFromTool(DragItem item)
    {
        var (slot, target) = AddTarget(null);
        try
        {
            var made = _ws.Drop(item, target, slot);
            if (made is null) return;

            bool ignore = ToolFlag("add", "ignore");
            bool nocase = ToolFlag("add", "nocase") && made.Kind == "literal";
            if (ignore || nocase) _ws.EditNode(made, n => (n.Ignore, n.IgnoreCase) = (n.Ignore || ignore, n.IgnoreCase || nocase));
        }
        catch (InvalidOperationException ex)
        {
            Say(ex.Message);
        }
    }
}
