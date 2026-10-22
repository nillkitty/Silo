using Silo.Providers.Data;

namespace Silo.Labs.Parser;

/// <summary>What changed in a <see cref="ParserWorkspace" />.</summary>
[Flags]
public enum WorkspaceChange
{
    None      = 0,
    Header    = 1,
    Rules     = 2,
    Selection = 4,
    Book      = 8,
    Loaded    = 16,
    Saved     = 32,
    Results   = 64,

    /// <summary>Tests were run (<see cref="ParserWorkspace.LastTestLine" /> says how it went).</summary>
    TestsRan  = 128
}

/// <summary>Something dragged from an inspector onto the canvas (or double-clicked to add to the selection).</summary>
/// <param name="Kind">
///     <c>token</c> (a built-in token, or one the spec saved), <c>comb</c> (a combinator), <c>rule</c> (a reference to a rule of the spec), <c>xrule</c>
///     (a rule of an imported spec: <c>spec|rule</c>), <c>snippet</c> (a piece of syntax saved in the spec: it is copied in), <c>literal</c>,
///     <c>chars</c>, <c>unicode</c> (the classes) or <c>node</c> (a piece of a tree on the canvas, moved: see <see cref="ParserWorkspace.DraggedNode" />).
/// </param>
/// <param name="Id">The token id, combinator id, rule name...; the starting text of a literal, character set or class list.</param>
public sealed record DragItem(string Kind, string Id)
{
    /// <summary>The name of the data format a drag carries it in.</summary>
    public const string Format = "Silo.ParserLab.Item";

    public string Encode() => Kind + ":" + Id;

    public static DragItem? Decode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        int colon = text.IndexOf(':');
        return colon <= 0 ? null : new DragItem(text[..colon], text[(colon + 1)..]);
    }
}

/// <summary>Where a tree hangs: the body of a rule, or the matcher of a tokenizer rule.</summary>
public sealed record SpecSlot(Func<SpecNode?> Get, Action<SpecNode?> Set);

/// <summary>
///     What the Parser Lab is working on, shared by its canvas and inspectors: the spec being built, its samples and tests, what is selected,
///     and every edit that can be made to the spec (so each edit is made one way, is undoable and tells everyone). Edits raise
///     <see cref="Changed" /> on the calling thread; the Lab works on the UI thread.
/// </summary>
public sealed class ParserWorkspace
{
    private const int UndoDepth = 60;

    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private          string?       _currentRule;
    private          SpecNode?     _selected;
    private          TokenRule?    _selectedTokenRule;

    private int                              _version;
    private int                              _problemsVersion = -1;
    private IReadOnlyList<SpecDiagnostic>    _problems = [];

    public ParserWorkspace(ParserSpecDoc? doc = null, SpecEnvironment? env = null)
    {
        Env  = env ?? new SpecEnvironment();
        Spec = new ParserDataSpec(doc ?? NewDoc()) { Loader = LoadSpec };
        Book = new SpecTestBook();
        _currentRule = Doc.Root ?? Doc.Rules.FirstOrDefault()?.Name;
    }

    /// <summary>What the spec can reach outside itself: the other specs and the enums of the Silo.</summary>
    public SpecEnvironment Env { get; }

    /// <summary>The spec of this name: the one being built (as it is now, saved or not) or one kept in the Silo.</summary>
    public ParserSpecDoc? LoadSpec(string name)
    {
        return string.Equals(name, Doc.Name, StringComparison.OrdinalIgnoreCase) ? Doc : Env.LoadSpec(name);
    }

    /// <summary>The piece of a tree being dragged on the canvas (a <c>node</c> drag carries only that it is one).</summary>
    public SpecNode? DraggedNode { get; set; }

    /// <summary>How the latest run of tests went, in a line for the status bar.</summary>
    public string LastTestLine { get; private set; } = string.Empty;

    /// <summary>Everything wrong with the spec as it is now: what the compiler found and what needs the Silo (token type, imports). Kept until the spec changes.</summary>
    public IReadOnlyList<SpecDiagnostic> Problems
    {
        get
        {
            if (_problemsVersion != _version)
            {
                _problems        = [.. Compiled.Diagnostics, .. SpecChecks.Run(Doc, Env)];
                _problemsVersion = _version;
            }

            return _problems;
        }
    }

    /// <summary>The spec being built, as a spec (what the Lab studies).</summary>
    public ParserDataSpec Spec { get; }

    public ParserSpecDoc Doc => Spec.Doc;

    /// <summary>The samples and tests of the spec.</summary>
    public SpecTestBook Book { get; private set; }

    /// <summary>Whether the spec or its samples and tests changed since they were kept (or loaded).</summary>
    public bool IsDirty { get; private set; }

    /// <summary>The latest result of each test that was run, by the test's name.</summary>
    public Dictionary<string, TestResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public event Action<WorkspaceChange>? Changed;

    /// <summary>The rule shown in the header (the one being defined); null when there is none.</summary>
    public string? CurrentRule
    {
        get => _currentRule;
        set
        {
            if (string.Equals(_currentRule, value, StringComparison.Ordinal)) return;

            _currentRule = value;
            Raise(WorkspaceChange.Selection | WorkspaceChange.Header);
        }
    }

    public RuleDef? Current => Doc.Find(_currentRule);

    /// <summary>The node selected on the canvas, if any (and still in the tree).</summary>
    public SpecNode? SelectedNode
    {
        get => _selected is not null && SlotOf(_selected) is not null ? _selected : null;
        set
        {
            if (ReferenceEquals(_selected, value)) return;

            _selected = value;
            Raise(WorkspaceChange.Selection);
        }
    }

    /// <summary>The tokenizer rule selected (as a whole) on the canvas, if any; what Cut, Copy and Delete act on when no node is selected.</summary>
    public TokenRule? SelectedTokenRule
    {
        get => _selectedTokenRule is not null && Doc.Tokenizer.Rules.Contains(_selectedTokenRule) ? _selectedTokenRule : null;
        set
        {
            if (ReferenceEquals(_selectedTokenRule, value)) return;

            _selectedTokenRule = value;
            if (value is not null) _selected = null;
            Raise(WorkspaceChange.Selection);
        }
    }

    /// <summary>The spec's compiled parsers (made again whenever the spec changed).</summary>
    public CompiledSpec Compiled => Spec.Compiled;

    public static ParserSpecDoc NewDoc(string name = "My.Data.Spec") => new() { Name = name };

    private void Raise(WorkspaceChange change) => Changed?.Invoke(change);

    // ---- the spec as a whole ---------------------------------------------------------------------

    /// <summary>Starts a new spec, or takes <paramref name="doc" /> (with its samples and tests) in place of the one being worked on.</summary>
    public void Open(ParserSpecDoc doc, SpecTestBook? book = null, bool dirty = false)
    {
        SpecEnums.Resolve(doc.Tokenizer, Env);
        Spec.Replace(doc);
        _version++;
        Book = book ?? new SpecTestBook();
        LastTestLine = string.Empty;
        _undo.Clear();
        _redo.Clear();
        Results.Clear();
        _selected    = null;
        _selectedTokenRule = null;
        _currentRule = doc.Root is { Length: > 0 } root && doc.Find(root) is not null ? doc.Find(root)!.Name : doc.Rules.FirstOrDefault()?.Name;
        IsDirty      = dirty;
        Raise(WorkspaceChange.Loaded | WorkspaceChange.Header | WorkspaceChange.Rules | WorkspaceChange.Selection | WorkspaceChange.Book | WorkspaceChange.Results);
    }

    /// <summary>Loads a kept spec; false when there is none with the name.</summary>
    public bool Load(string name)
    {
        var doc = SpecStore.Load(name);
        if (doc is null) return false;

        Open(doc, SpecStore.LoadBook(name));
        return true;
    }

    /// <summary>
    ///     Keeps the spec and its samples and tests in the Silo; when the spec defines its token type and is to include it, the enum is added to the
    ///     Silo's Enums too. Returns a note on what else was done (null: nothing).
    /// </summary>
    /// <exception cref="InvalidOperationException">No Silo is open, the spec has no name, imports go in a circle, or the token type is not acceptable.</exception>
    public string? Save()
    {
        if (SpecImports.FindCycle(Doc, LoadSpec) is { } cycle)
            throw new InvalidOperationException("Specs cannot import each other in a circle: " + string.Join(" → ", cycle));

        string? note = null;
        var     t    = Doc.Tokenizer;
        if (!t.ExistingEnum && t.IncludeInEnums && Env.Enums is not null)
        {
            var problems = SpecEnums.Problems(Doc, Env);
            if (problems.Count > 0) throw new InvalidOperationException("The token type cannot be added to the Silo's Enums: " + problems[0]);

            string name = t.TokenType;
            try
            {
                Edit(d => SpecEnums.Include(d, Env), WorkspaceChange.Header);
                note = $"added the enum {name} to the Silo's Enums";
            }
            catch (DefinitionException ex)
            {
                throw new InvalidOperationException("The token type cannot be added to the Silo's Enums: " + ex.Message, ex);
            }
        }

        SpecStore.Save(Doc, Book);
        IsDirty = false;
        Raise(WorkspaceChange.Saved);
        return note;
    }

    /// <summary>Makes an edit to the spec: remembers how it was (for Undo), changes it, and tells everyone.</summary>
    public void Edit(Action<ParserSpecDoc> change, WorkspaceChange kind = WorkspaceChange.Rules)
    {
        _undo.Push(Doc.ToJson());
        Trim(_undo);
        _redo.Clear();

        change(Doc);
        AfterEdit(kind);
    }

    private static void Trim(Stack<string> stack)
    {
        if (stack.Count <= UndoDepth) return;

        var keep = stack.Take(UndoDepth).Reverse().ToList();
        stack.Clear();
        foreach (string s in keep) stack.Push(s);
    }

    private void AfterEdit(WorkspaceChange kind)
    {
        _version++;
        Spec.Invalidate();
        IsDirty = true;
        if (_currentRule is not null && Doc.Find(_currentRule) is null) _currentRule = Doc.Rules.FirstOrDefault()?.Name;
        Raise(kind | WorkspaceChange.Results | (kind.HasFlag(WorkspaceChange.Rules) ? WorkspaceChange.Selection : 0));
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;

        _redo.Push(Doc.ToJson());
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;

        _undo.Push(Doc.ToJson());
        Restore(_redo.Pop());
    }

    private void Restore(string json)
    {
        var doc = ParserSpecDoc.FromJson(json)!;
        Spec.Replace(doc);
        _version++;
        _selected = null;
        IsDirty   = true;
        if (_currentRule is not null && doc.Find(_currentRule) is null) _currentRule = doc.Rules.FirstOrDefault()?.Name;
        Raise(WorkspaceChange.Header | WorkspaceChange.Rules | WorkspaceChange.Selection | WorkspaceChange.Results);
    }

    // ---- rules -----------------------------------------------------------------------------------

    /// <summary>Adds an empty rule (called <paramref name="name" />, or a free name) and makes it the one being defined.</summary>
    public RuleDef AddRule(string? name = null, RuleKind kind = RuleKind.Syntax, SpecNode? body = null)
    {
        string unique = UniqueRuleName(string.IsNullOrWhiteSpace(name) ? "rule" : name.Trim());
        var    rule   = new RuleDef { Name = unique, Kind = kind, Body = body };
        Edit(d => d.Rules.Add(rule));
        if (Doc.Root is null or { Length: 0 }) Edit(d => d.Root = unique, WorkspaceChange.Header);
        CurrentRule = unique;
        return Doc.Find(unique)!;
    }

    public string UniqueRuleName(string wanted)
    {
        if (Doc.Find(wanted) is null) return wanted;

        for (int i = 2;; i++)
            if (Doc.Find(wanted + i) is null)
                return wanted + i;
    }

    /// <summary>Renames a rule, and every reference to it.</summary>
    public void RenameRule(string from, string to)
    {
        to = to.Trim();
        if (to.Length == 0 || string.Equals(from, to, StringComparison.Ordinal)) return;
        if (Doc.Find(to) is { } clash && !ReferenceEquals(clash, Doc.Find(from))) throw new InvalidOperationException($"There is already a rule called '{to}'.");

        Edit(d =>
             {
                 if (d.Find(from) is not { } rule) return;

                 rule.Name = to;
                 foreach (var node in AllNodes(d).Where(n => n.Kind == "ref" && string.IsNullOrEmpty(n.Spec) && string.Equals(n.Text, from, StringComparison.OrdinalIgnoreCase))) node.Text = to;
                 if (string.Equals(d.Root, from, StringComparison.OrdinalIgnoreCase)) d.Root = to;
                 foreach (var test in Book.Tests.Where(t => string.Equals(t.Rule, from, StringComparison.OrdinalIgnoreCase))) test.Rule = to;
                 foreach (var set in Book.Samples.Where(s => string.Equals(s.Rule, from, StringComparison.OrdinalIgnoreCase))) set.Rule = to;
             });
        if (string.Equals(_currentRule, from, StringComparison.OrdinalIgnoreCase)) _currentRule = to;
        Raise(WorkspaceChange.Header | WorkspaceChange.Selection | WorkspaceChange.Book);
    }

    public void DeleteRule(string name)
    {
        Edit(d =>
             {
                 d.Rules.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
                 if (string.Equals(d.Root, name, StringComparison.OrdinalIgnoreCase)) d.Root = d.Rules.FirstOrDefault()?.Name;
             });
    }

    /// <summary>Copies a rule of another spec in (with a free name).</summary>
    public RuleDef ImportRule(RuleDef from)
    {
        var copy = new RuleDef
                   {
                       Name = UniqueRuleName(from.Name), Kind = from.Kind, Description = from.Description, Returns = from.Returns, Shape = from.Shape,
                       Body = from.Body?.Clone()
                   };
        Edit(d => d.Rules.Add(copy));
        CurrentRule = copy.Name;
        return copy;
    }

    /// <summary>Changes the rule being defined (kind, shape, return type, description).</summary>
    public void EditCurrent(Action<RuleDef> change)
    {
        string? name = _currentRule;
        Edit(d =>
             {
                 if (d.Find(name) is { } rule) change(rule);
             }, WorkspaceChange.Header);
    }

    // ---- nodes -----------------------------------------------------------------------------------

    public static IEnumerable<SpecNode> AllNodes(ParserSpecDoc doc)
    {
        foreach (var rule in doc.Rules)
            if (rule.Body is not null)
                foreach (var n in rule.Body.SelfAndDescendants())
                    yield return n;

        foreach (var rule in doc.Tokenizer.Rules)
            if (rule.Matcher is not null)
                foreach (var n in rule.Matcher.SelfAndDescendants())
                    yield return n;

        foreach (var fragment in doc.Fragments)
            if (fragment.Node is not null)
                foreach (var n in fragment.Node.SelfAndDescendants())
                    yield return n;
    }

    /// <summary>The places a tree hangs in the spec.</summary>
    public IEnumerable<SpecSlot> Slots()
    {
        foreach (var rule in Doc.Rules) yield return RuleSlot(rule);
        foreach (var rule in Doc.Tokenizer.Rules) yield return MatcherSlot(rule);
    }

    public static SpecSlot RuleSlot(RuleDef rule) => new(() => rule.Body, n => rule.Body = n);

    public static SpecSlot MatcherSlot(TokenRule rule) => new(() => rule.Matcher, n => rule.Matcher = n);

    /// <summary>The slot whose tree holds <paramref name="node" />.</summary>
    public SpecSlot? SlotOf(SpecNode node)
    {
        return Slots().FirstOrDefault(s => s.Get() is { } root && root.SelfAndDescendants().Any(n => ReferenceEquals(n, node)));
    }

    private static SpecNode? ParentOf(SpecNode root, SpecNode node)
    {
        foreach (var n in root.SelfAndDescendants())
            if (n.Children.Any(c => ReferenceEquals(c, node)))
                return n;
        return null;
    }

    private static bool IsEmpty(SpecNode n) => n.Kind == "seq" && n.Children.Count == 0;

    private static bool IsWrapper(SpecNode n) => n.Kind is "repeat" or "try" or "convert" or "where" or "named" or "not";

    /// <summary>The node a drag item stands for; null when it stands for nothing (an unknown token...).</summary>
    public static SpecNode? Make(DragItem item, SpecNode? target)
    {
        switch (item.Kind)
        {
            case "token": return SpecNode.Token(item.Id);
            case "literal": return SpecNode.Literal(string.IsNullOrEmpty(item.Id) ? "text" : item.Id);
            case "chars": return SpecNode.Chars(string.IsNullOrEmpty(item.Id) ? "abc" : item.Id);
            case "unicode": return SpecNode.Unicode(string.IsNullOrEmpty(item.Id) ? "L" : item.Id);
            case "xrule" when item.Id.Split('|') is [{ Length: > 0 } spec, { Length: > 0 } rule]: return SpecNode.Ref(rule, spec);
            case "rule": return SpecNode.Ref(item.Id);
            case "comb": return SpecCatalog.Combinator(item.Id)?.Make(target);
            default: return null;
        }
    }

    /// <summary>
    ///     Puts a dragged thing onto the tree at <paramref name="target" /> (null: the rule as a whole) of the tree in <paramref name="slot" />.
    ///     A combinator wraps what it is dropped on; anything else is added next to it, into it (a sequence or a choice), or in place of the
    ///     empty place that is there. A piece of a tree (<c>node</c>) is moved there (copied, with <paramref name="copy" />), from this rule or
    ///     another. Returns the node that was made.
    /// </summary>
    /// <exception cref="InvalidOperationException">The thing is a rule of a spec that cannot be imported (it would make a circle).</exception>
    public SpecNode? Drop(DragItem item, SpecNode? target, SpecSlot slot, bool copy = false)
    {
        if (item.Kind == "node") return DraggedNode is { } source ? MoveNode(source, target, slot, copy) : null;

        string? importSpec = null;
        if (item.Kind == "xrule" && item.Id.Split('|') is [{ Length: > 0 } spec, _])
        {
            CheckImport(spec);
            importSpec = spec;
        }

        SpecNode? made = null;
        Edit(d =>
             {
                 if (importSpec is not null && !d.Imports.Contains(importSpec, StringComparer.OrdinalIgnoreCase)) d.Imports.Add(importSpec);

                 var root = slot.Get();
                 if (item.Kind == "comb")
                 {
                     var node = target ?? root;
                     made = Make(item, node);
                     if (made is null) return;

                     Replace(slot, node, made);
                     return;
                 }

                 made = item.Kind == "snippet" ? d.FindFragment(item.Id, FragmentKind.Syntax)?.Node?.Clone() : Make(item, null);
                 if (made is null) return;

                 if (root is null)
                 {
                     slot.Set(made);
                     return;
                 }

                 AddNear(slot, root, target ?? root, made);
             });
        if (made is not null) _selected = made;
        Raise(WorkspaceChange.Selection);
        return made;
    }

    /// <summary>
    ///     Moves a piece of a tree to <paramref name="target" /> of the tree in <paramref name="slot" /> (the same rule, or another one), or copies it.
    ///     Nothing happens when the target is the piece or inside it. One step of Undo.
    /// </summary>
    public SpecNode? MoveNode(SpecNode node, SpecNode? target, SpecSlot slot, bool copy)
    {
        if (target is not null && node.SelfAndDescendants().Any(n => ReferenceEquals(n, target))) return null;
        if (SlotOf(node) is not { } from) return null;

        SpecNode? made = null;
        Edit(_ =>
             {
                 made = copy ? node.Clone() : node;
                 if (!copy) Detach(from, node);

                 var root = slot.Get();
                 if (root is null)
                 {
                     slot.Set(made);
                     return;
                 }

                 AddNear(slot, root, target ?? root, made);
             });
        _selected = made;
        Raise(WorkspaceChange.Selection);
        return made;
    }

    // ---- the clipboard ---------------------------------------------------------------------------

    /// <summary>Whether there is something of this Lab's on the clipboard.</summary>
    public bool CanPaste => SpecClip.Get() is not null;

    public void CopyNode(SpecNode node) => SpecClip.Put(new ClipData { Nodes = [node.Clone()] });

    public void CutNode(SpecNode node)
    {
        CopyNode(node);
        Remove(node);
    }

    /// <summary>
    ///     Pastes what is on the clipboard next to <paramref name="target" /> (the selected node when null; the rule being defined when nothing is selected,
    ///     made if there is none). Tokenizer rules that were copied are added to the tokenizer. Returns the last node made.
    /// </summary>
    public SpecNode? Paste(SpecNode? target = null)
    {
        if (SpecClip.Get() is not { } clip) return null;

        if (clip.Nodes.Count == 0)
        {
            PasteTokenRules(null);
            return null;
        }

        target ??= SelectedNode;
        var slot = target is not null ? SlotOf(target) : Current is { } rule ? RuleSlot(rule) : null;
        if (slot is null) slot = RuleSlot(AddRule());

        SpecNode? last = null;
        Edit(_ =>
             {
                 var near = target;
                 foreach (var piece in clip.Nodes)
                 {
                     var made = piece.Clone();
                     var root = slot.Get();
                     if (root is null) slot.Set(made);
                     else AddNear(slot, root, near ?? root, made);

                     near = made;
                     last = made;
                 }
             });
        _selected = last;
        Raise(WorkspaceChange.Selection);
        return last;
    }

    /// <summary>Whether Copy, Cut and Delete have something to act on: a selected piece, or a selected tokenizer rule.</summary>
    public bool HasSelection => SelectedNode is not null || SelectedTokenRule is not null;

    /// <summary>Copies the selected piece (or tokenizer rule).</summary>
    public bool CopySelection()
    {
        if (SelectedNode is { } node) CopyNode(node);
        else if (SelectedTokenRule is { } rule) CopyTokenRule(rule);
        else return false;

        return true;
    }

    /// <summary>Cuts the selected piece (or tokenizer rule).</summary>
    public bool CutSelection()
    {
        if (SelectedNode is { } node) CutNode(node);
        else if (SelectedTokenRule is { } rule) CutTokenRule(rule);
        else return false;

        return true;
    }

    /// <summary>Pastes next to the selected piece (or, for tokenizer rules, after the selected rule).</summary>
    public bool PasteSelection()
    {
        if (SpecClip.Get() is not { } clip) return false;

        if (clip.TokenRules.Count > 0 && clip.Nodes.Count == 0) PasteTokenRules(SelectedTokenRule);
        else Paste();
        return true;
    }

    /// <summary>Deletes the selected piece (or tokenizer rule).</summary>
    public bool DeleteSelection()
    {
        if (SelectedNode is { } node) Remove(node);
        else if (SelectedTokenRule is { } rule) RemoveTokenRule(rule);
        else return false;

        return true;
    }

    /// <summary>Duplicates the selected piece (or tokenizer rule).</summary>
    public bool DuplicateSelection()
    {
        if (SelectedNode is { } node) Duplicate(node);
        else if (SelectedTokenRule is { } rule) DuplicateTokenRule(rule);
        else return false;

        return true;
    }

    public void CopyTokenRule(TokenRule rule) => SpecClip.Put(new ClipData { TokenRules = [new TokenRule { Kind = rule.Kind, Matcher = rule.Matcher?.Clone() }] });

    public void CutTokenRule(TokenRule rule)
    {
        CopyTokenRule(rule);
        RemoveTokenRule(rule);
    }

    /// <summary>Puts copies of the tokenizer rules on the clipboard after <paramref name="after" /> (at the end when null or not there).</summary>
    public void PasteTokenRules(TokenRule? after)
    {
        if (SpecClip.Get() is not { TokenRules.Count: > 0 } clip) return;

        Edit(d =>
             {
                 int at = after is null ? -1 : d.Tokenizer.Rules.IndexOf(after);
                 int to = at < 0 ? d.Tokenizer.Rules.Count : at + 1;
                 foreach (var rule in clip.TokenRules) d.Tokenizer.Rules.Insert(to++, new TokenRule { Kind = rule.Kind, Matcher = rule.Matcher?.Clone() });
             });
    }

    public void DuplicateTokenRule(TokenRule rule)
    {
        Edit(d =>
             {
                 int at = d.Tokenizer.Rules.IndexOf(rule);
                 if (at >= 0) d.Tokenizer.Rules.Insert(at + 1, new TokenRule { Kind = rule.Kind, Matcher = rule.Matcher?.Clone() });
             });
    }

    /// <summary>Adds a dragged thing to the selected node (or the rule being defined when none is selected): what a double-click does.</summary>
    public SpecNode? AddToSelection(DragItem item)
    {
        var node = SelectedNode;
        var slot = node is not null ? SlotOf(node) : Current is { } rule ? RuleSlot(rule) : null;
        if (slot is null)
        {
            var created = AddRule();
            slot = RuleSlot(created);
        }

        return Drop(item, node, slot);
    }

    private static void Replace(SpecSlot slot, SpecNode? oldNode, SpecNode newNode)
    {
        var root = slot.Get();
        if (root is null || oldNode is null || ReferenceEquals(root, oldNode))
        {
            slot.Set(newNode);
            return;
        }

        var parent = ParentOf(root, oldNode);
        if (parent is null)
        {
            slot.Set(newNode);
            return;
        }

        int i = parent.Children.FindIndex(c => ReferenceEquals(c, oldNode));
        parent.Children[i] = newNode;
    }

    private static void AddNear(SpecSlot slot, SpecNode root, SpecNode target, SpecNode made)
    {
        // an empty place (an empty sequence a combinator left) takes it
        if (IsEmpty(target))
        {
            Replace(slot, target, made);
            return;
        }

        if (target.Kind is "seq" or "choice")
        {
            target.Children.Add(made);
            return;
        }

        var emptyChild = target.Children.FirstOrDefault(IsEmpty);
        if (emptyChild is not null)
        {
            Replace(slot, emptyChild, made);
            return;
        }

        var parent = ParentOf(root, target);
        if (parent is { Kind: "seq" or "choice" })
        {
            int i = parent.Children.FindIndex(c => ReferenceEquals(c, target));
            parent.Children.Insert(i + 1, made);
            return;
        }

        Replace(slot, target, SpecNode.Seq(target, made));
    }

    /// <summary>Takes a node out of its tree (and a wrapper that is left with nothing in it).</summary>
    public void Remove(SpecNode node)
    {
        if (SlotOf(node) is not { } slot) return;

        Edit(_ => Detach(slot, node));
        _selected = null;
        Raise(WorkspaceChange.Selection);
    }

    private static void Detach(SpecSlot slot, SpecNode node)
    {
        var root = slot.Get();
        var cur  = node;
        while (root is not null)
        {
            if (ReferenceEquals(root, cur))
            {
                slot.Set(null);
                break;
            }

            var parent = ParentOf(root, cur);
            if (parent is null) break;

            parent.Children.RemoveAll(c => ReferenceEquals(c, cur));
            if (parent.Children.Count == 0 && IsWrapper(parent))
            {
                cur = parent;
                continue;
            }

            break;
        }
    }

    /// <summary>Moves a node up or down among its brothers.</summary>
    public void Move(SpecNode node, int delta)
    {
        if (SlotOf(node) is not { } slot || slot.Get() is not { } root || ParentOf(root, node) is not { } parent) return;

        int from = parent.Children.FindIndex(c => ReferenceEquals(c, node));
        int to   = from + delta;
        if (to < 0 || to >= parent.Children.Count) return;

        Edit(_ => (parent.Children[from], parent.Children[to]) = (parent.Children[to], parent.Children[from]));
    }

    /// <summary>Puts a copy of a node right after it.</summary>
    public void Duplicate(SpecNode node)
    {
        if (SlotOf(node) is not { } slot || slot.Get() is not { } root) return;

        var copy = node.Clone();
        Edit(_ =>
             {
                 var parent = ParentOf(root, node);
                 if (parent is null) slot.Set(SpecNode.Seq(node, copy));
                 else parent.Children.Insert(parent.Children.FindIndex(c => ReferenceEquals(c, node)) + 1, copy);
             });
        _selected = copy;
        Raise(WorkspaceChange.Selection);
    }

    /// <summary>Wraps a node in a combinator (Many, Optional, Named...).</summary>
    public void Wrap(SpecNode node, string combinatorId)
    {
        if (SlotOf(node) is not { } slot) return;

        var made = SpecCatalog.Combinator(combinatorId)?.Make(node);
        if (made is null) return;

        Edit(_ => Replace(slot, node, made));
        _selected = made;
        Raise(WorkspaceChange.Selection);
    }

    /// <summary>Takes a wrapper away, leaving what was in it.</summary>
    public void Unwrap(SpecNode node)
    {
        if (node.Children.Count != 1 || SlotOf(node) is not { } slot) return;

        var inner = node.Children[0];
        Edit(_ => Replace(slot, node, inner));
        _selected = inner;
        Raise(WorkspaceChange.Selection);
    }

    /// <summary>Changes a node's settings.</summary>
    public void EditNode(SpecNode node, Action<SpecNode> change)
    {
        Edit(_ => change(node));
    }

    // ---- saved pieces (Syntax and Tokens) ---------------------------------------------------------

    public string UniqueFragmentName(string wanted)
    {
        wanted = string.IsNullOrWhiteSpace(wanted) ? "piece" : wanted.Trim();
        if (FragmentNameProblem(wanted) is null) return wanted;

        for (int i = 2;; i++)
            if (FragmentNameProblem(wanted + i) is null)
                return wanted + i;
    }

    private string? FragmentNameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A saved piece needs a name.";
        if (SpecCatalog.Token(name) is not null) return $"'{name}' is the name of a built-in token.";
        if (Doc.FindFragment(name) is not null) return $"There is already a saved piece called '{name}'.";
        return null;
    }

    /// <summary>
    ///     Keeps a copy of <paramref name="node" /> in the spec under a name: a piece of syntax (dropped into a rule as a copy) or a token (used by name,
    ///     so that changing it changes every use). A token cannot use a rule.
    /// </summary>
    /// <exception cref="InvalidOperationException">The name is taken, or the piece cannot be a token.</exception>
    public Fragment SaveFragment(SpecNode node, string name, FragmentKind kind, string? description = null)
    {
        name = (name ?? string.Empty).Trim();
        if (FragmentNameProblem(name) is { } problem) throw new InvalidOperationException(problem);
        if (kind == FragmentKind.Token && node.SelfAndDescendants().Any(n => n.Kind == "ref"))
            throw new InvalidOperationException("A token is made of characters and other tokens: it cannot use a rule.");

        var fragment = new Fragment { Name = name, Kind = kind, Description = description, Node = node.Clone() };
        fragment.Node!.Ignore = false;
        Edit(d => d.Fragments.Add(fragment), WorkspaceChange.Header | WorkspaceChange.Rules);
        return fragment;
    }

    public void DeleteFragment(string name)
    {
        Edit(d => d.Fragments.RemoveAll(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)), WorkspaceChange.Header | WorkspaceChange.Rules);
    }

    /// <summary>Renames a saved piece (and, for a token, every use of it).</summary>
    public void RenameFragment(string from, string to)
    {
        to = to.Trim();
        if (to.Length == 0 || string.Equals(from, to, StringComparison.Ordinal)) return;
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && FragmentNameProblem(to) is { } problem) throw new InvalidOperationException(problem);

        Edit(d =>
             {
                 if (d.FindFragment(from) is not { } fragment) return;

                 fragment.Name = to;
                 if (fragment.Kind == FragmentKind.Token)
                     foreach (var n in AllNodes(d).Where(n => n.Kind == "token" && string.Equals(n.Text, from, StringComparison.OrdinalIgnoreCase)))
                         n.Text = to;
             }, WorkspaceChange.Header | WorkspaceChange.Rules);
    }

    // ---- imports ---------------------------------------------------------------------------------

    /// <summary>Whether this spec may use the rules of <paramref name="spec" />; throws when it may not (itself, one that is not kept, one that would make a circle).</summary>
    /// <exception cref="InvalidOperationException">Why not.</exception>
    public void CheckImport(string spec)
    {
        if (string.Equals(spec, Doc.Name, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A spec cannot import itself: use its own rules directly.");
        if (LoadSpec(spec) is null) throw new InvalidOperationException($"The spec '{spec}' is not in the Silo.");
        if (SpecImports.CycleIfImported(Doc, spec, LoadSpec) is { } cycle)
            throw new InvalidOperationException("Specs cannot import each other in a circle: " + string.Join(" → ", cycle));
    }

    /// <summary>Lets this spec use the rules of <paramref name="spec" />.</summary>
    /// <exception cref="InvalidOperationException">See <see cref="CheckImport" />.</exception>
    public void AddImport(string spec)
    {
        CheckImport(spec);
        if (Doc.Imports.Contains(spec, StringComparer.OrdinalIgnoreCase)) return;

        Edit(d => d.Imports.Add(spec), WorkspaceChange.Header | WorkspaceChange.Rules);
    }

    /// <summary>Stops using the rules of <paramref name="spec" />; refused while a rule still uses one.</summary>
    public void RemoveImport(string spec)
    {
        int uses = AllNodes(Doc).Count(n => n.Kind == "ref" && string.Equals(n.Spec, spec, StringComparison.OrdinalIgnoreCase));
        if (uses > 0) throw new InvalidOperationException($"{uses} reference(s) still use rules of '{spec}': take them out first.");

        Edit(d => d.Imports.RemoveAll(i => string.Equals(i, spec, StringComparison.OrdinalIgnoreCase)), WorkspaceChange.Header | WorkspaceChange.Rules);
    }

    /// <summary>The rules of the specs this one imports (the ones that can be read).</summary>
    public IEnumerable<(string Spec, RuleDef Rule)> ImportedRules()
    {
        foreach (string import in Doc.Imports)
            if (LoadSpec(import) is { } other)
                foreach (var rule in other.Rules)
                    yield return (import, rule);
    }

    // ---- the token type --------------------------------------------------------------------------

    /// <summary>Sets what the kinds of token are called: an enum of the Silo with that name is used as it is; any other name is an enum this spec defines.</summary>
    /// <returns>What is wrong with the name; null when it was taken.</returns>
    public string? SetTokenType(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) name = "Token";
        if (Silo.Providers.Definitions.DefinitionNames.Check("The token type", name) is { } problem) return problem;

        Edit(d =>
             {
                 d.Tokenizer.TokenType = name;
                 d.Tokenizer.ExistingEnum = false;
                 SpecEnums.Resolve(d.Tokenizer, Env);
             }, WorkspaceChange.Header);
        return null;
    }

    /// <summary>Adds a kind of token to the enum the spec defines.</summary>
    /// <returns>What is wrong with the name; null when it was added.</returns>
    public string? AddKind(string name)
    {
        if (SpecEnums.CheckKind(Doc.Tokenizer, name) is { } problem) return problem;

        Edit(d => SpecEnums.AddKind(d.Tokenizer, name), WorkspaceChange.Header);
        return null;
    }

    /// <summary>Takes a kind out of the enum the spec defines; refused while a tokenizer rule makes it.</summary>
    /// <returns>Why not; null when it was taken out.</returns>
    public string? RemoveKind(string kind)
    {
        if (Doc.Tokenizer.ExistingEnum) return $"{Doc.Tokenizer.TokenType} is an enum of the Silo: change its values in the Enums view.";

        int at = Doc.Tokenizer.Rules.FindIndex(r => string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) return $"Tokenizer rule {at + 1} makes '{kind}'.";

        Edit(d => d.Tokenizer.Kinds.RemoveAll(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase)), WorkspaceChange.Header);
        return null;
    }

    public void SetIncludeInEnums(bool include) => Edit(d => d.Tokenizer.IncludeInEnums = include, WorkspaceChange.Header);

    /// <summary>Asks the Silo again about the token type (its enums may have changed) and forgets the problems found so far.</summary>
    public void RefreshFromSilo()
    {
        SpecEnums.Resolve(Doc.Tokenizer, Env);
        _version++;
        Raise(WorkspaceChange.Header | WorkspaceChange.Results);
    }

    // ---- tokenizer -------------------------------------------------------------------------------

    public TokenRule AddTokenRule(string? kind = null)
    {
        var rule = new TokenRule { Kind = kind ?? Doc.Tokenizer.Kinds.FirstOrDefault() };
        Edit(d => d.Tokenizer.Rules.Add(rule));
        return rule;
    }

    public void RemoveTokenRule(TokenRule rule) => Edit(d => d.Tokenizer.Rules.Remove(rule));

    public void MoveTokenRule(TokenRule rule, int delta)
    {
        int from = Doc.Tokenizer.Rules.IndexOf(rule);
        int to   = from + delta;
        if (from < 0 || to < 0 || to >= Doc.Tokenizer.Rules.Count) return;

        Edit(d => (d.Tokenizer.Rules[from], d.Tokenizer.Rules[to]) = (d.Tokenizer.Rules[to], d.Tokenizer.Rules[from]));
    }

    // ---- samples and tests -----------------------------------------------------------------------

    /// <summary>Call after changing <see cref="Book" /> directly.</summary>
    public void BookChanged()
    {
        IsDirty = true;
        Raise(WorkspaceChange.Book);
    }

    /// <summary>Adds a sample to a set (the set is made when it is new).</summary>
    public SampleSet AddSample(string set, string text)
    {
        var target = Book.Samples.FirstOrDefault(s => string.Equals(s.Name, set, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            target = new SampleSet { Name = set };
            Book.Samples.Add(target);
        }

        target.Items.Add(text);
        BookChanged();
        return target;
    }

    public string UniqueTestName(string wanted)
    {
        if (!Book.Tests.Any(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase))) return wanted;

        for (int i = 2;; i++)
            if (!Book.Tests.Any(t => string.Equals(t.Name, wanted + " " + i, StringComparison.OrdinalIgnoreCase)))
                return wanted + " " + i;
    }

    /// <summary>
    ///     Makes a test of what is expected of some sample sets: the samples stay samples (they only match or do not), the test is what passes or fails.
    /// </summary>
    public TestCase AddTest(string? name, IEnumerable<string> sets, Expectation expect, string? rule = null, IEnumerable<string>? extra = null)
    {
        var test = new TestCase { Name = UniqueTestName(string.IsNullOrWhiteSpace(name) ? "Test" : name.Trim()), Samples = sets.ToList(), Expect = expect, Rule = rule, Extra = extra?.ToList() ?? [] };
        Book.Tests.Add(test);
        BookChanged();
        return test;
    }

    /// <summary>The line the status bar shows for a run of tests.</summary>
    public string Summarize(IReadOnlyList<TestResult> results, bool failingOnly = false)
    {
        if (results.Count == 0) return failingOnly ? "No failing tests to run" : Book.Tests.Count == 0 ? "There are no tests (add some in the Tests pane)" : "No tests were run";

        int passed = results.Count(r => r.State == TestState.Passed);
        string failed = string.Join(", ", results.Where(r => r.State == TestState.Failed).Select(r => r.Test.Name).Take(3));
        return $"{passed} of {results.Count} test(s) passed" + (passed < results.Count ? $"; failing: {failed}" : string.Empty);
    }

    /// <summary>Runs one test and remembers the result.</summary>
    public TestResult RunTest(TestCase test)
    {
        var result = ParserTestRunner.Run(Compiled, Book, test);
        Results[test.Name] = result;
        LastTestLine = $"{test.Name}: {result.State} ({result.Summary})";
        Raise(WorkspaceChange.Results | WorkspaceChange.TestsRan);
        return result;
    }

    /// <summary>Runs the tests that satisfy <paramref name="which" /> (all when null).</summary>
    public IReadOnlyList<TestResult> RunTests(Func<TestCase, TestResult?, bool>? which = null, bool failingOnly = false)
    {
        var compiled = Compiled;
        var done     = new List<TestResult>();
        foreach (var test in Book.Tests.ToList())
        {
            Results.TryGetValue(test.Name, out var before);
            if (which is not null && !which(test, before)) continue;

            var r = ParserTestRunner.Run(compiled, Book, test);
            Results[test.Name] = r;
            done.Add(r);
        }

        LastTestLine = Summarize(done, failingOnly);
        Raise(WorkspaceChange.Results | WorkspaceChange.TestsRan);
        return done;
    }

    /// <summary>Runs the tests that failed the last time they were run.</summary>
    public IReadOnlyList<TestResult> RunFailingTests() => RunTests((_, before) => before?.State == TestState.Failed, true);

    /// <summary>Parses or tokenizes a text.</summary>
    public EvalOutcome Eval(string text, string? rule = null) => SpecEval.Eval(Compiled, text, rule);
}
