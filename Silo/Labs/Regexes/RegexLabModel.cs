using System.Text.RegularExpressions;
using System.Windows.Controls;
using Newtonsoft.Json;

namespace Silo.Labs.Regexes;

// The Regex Lab as a model (see LabModel.cs):
//   subject  a regex (RegexDoc)                parts   groups and alternations of the pattern, nested as written
//   aspects  Matches, Explain, Code (of the regex), Group (of a group part)
//   tools    Quantify and Wrap: edit the selected piece of the pattern (or the active part)
public sealed partial class RegexLabSession
{
    private const string SubjectId = "regex";

    private static readonly LabPartType GroupType       = new("group", "Group", "Layers", "A group that captures what it matches",
                                                              [new("capturing", "Capturing group", "(.+)", "Remembers what it matches"), new("named", "Named group", "(?<name>.+)", "Remembers what it matches under a name")],
                                                              [new LabTemplate("named-group", "Named group", "(?<{{name}}>{{inner}})",
                                                                               [new("name", "Name of the group", "name"), new("inner", "What it holds", ".+", Bind: m => m.ActivePart?.Detail)],
                                                                               "A group with a name; what it holds is the active part when there is one")]);
    private static readonly LabPartType NonCapturingType = new("noncapture", "Non-capturing group", "Modules", "A group that only groups",
                                                               [new("plain", "Non-capturing group", "(?:.+)"), new("case-insensitive", "Case-insensitive group", "(?i:.+)", "Options for this group only")]);
    private static readonly LabPartType LookaroundType  = new("lookaround", "Lookaround", "Search", "Looks ahead or behind without matching",
                                                              [new("ahead", "Followed by", "(?=.)"), new("not-ahead", "Not followed by", "(?!.)"), new("behind", "Preceded by", "(?<=.)"), new("not-behind", "Not preceded by", "(?<!.)")]);
    private static readonly LabPartType AtomicType      = new("atomic", "Atomic group", "Lock", "A group the engine never backs into", [new("atomic", "Atomic group", "(?>.+)")]);
    private static readonly LabPartType ConditionalType = new("conditional", "Conditional", "Puzzle", "One way if a group matched, another if not", [new("if-group", "If group 1 matched", "(?(1)yes|no)")]);
    private static readonly LabPartType AlternationType = new("alternation", "Alternation", "Nodes", "Either of several ways", null,
                                                              [new LabTemplate("alternatives", "Alternatives", "{{first}}|{{second}}", [new("first", "First way", "a"), new("second", "Second way", "b")], "Two ways of matching")]);
    private static readonly LabPartType BranchType      = new("branch", "Branch", "HierarchyStructure", "One way of an alternation", null, null, "alternation");

    private int _selStart;
    private int _selLength;

    /// <summary>The window wants a stretch of the pattern selected (a part was chosen, an edit was made).</summary>
    public event Action<int, int>? PatternRangeRequested;

    protected override string SubjectNoun => "regex";

    // ---- subject: prefabs and templates ---------------------------------------------------------------

    protected override IEnumerable<LabSubjectType> DefineSubjectTypes()
    {
        var prefabs = RegexLibrary.Entries.Select(e => new LabPrefab(Slug(e.Name), e.Name, JsonConvert.SerializeObject(RegexLibrary.ToDoc(e)), e.Description, "Search")).ToList();
        var templates = new List<LabTemplate>
                        {
                            new("whole-word", "Whole word", @"/\b{{word}}\b/", [new("word", "Word to find", "word")], "A word on its own, not as part of a longer one", "Search"),
                            new("key-value", "Key and value", @"/(?<key>[\w.-]+)\s*{{separator}}\s*(?<value>.+)/", [new("separator", "What separates them", "=", ["=", ":", "->", "=>"])], "Lines like name = value", "Search"),
                            new("delimited", "Delimited list", @"/{{item}}(?:{{separator}}{{item}})*/", [new("item", "What an item looks like", @"\w+"), new("separator", "What separates items", @",\s*")], "Items with something between them", "Search"),
                            new("line-start", "Lines starting with", "/^{{prefix}}.*$/m", [new("prefix", "How the line starts", "#")], "Every line that begins a certain way", "Search")
                        };
        yield return new LabSubjectType(SubjectId, "Regex", "DataSearching", "A regular expression with the text it is tried on", prefabs, templates);
    }

    protected override async Task OnNewSubjectAsync(LabSubjectType type, string content)
    {
        if (_dirty && !LabUi.Confirm("This regex has changes that are not kept in the Silo. Start another anyway?", "Regex Lab")) return;

        RegexDoc? doc = null;
        if (content.TrimStart().StartsWith('{'))
        {
            try
            {
                doc = JsonConvert.DeserializeObject<RegexDoc>(content);
            }
            catch (JsonException)
            {
                doc = null;
            }
        }

        if (doc is null)
        {
            if (!TryParseValue(content, out var value) || value is not RegexDoc read) throw new InvalidOperationException("That is not a regex.");

            doc = read;
        }

        Replace(doc);
        await Task.CompletedTask;
    }

    private static string Slug(string name) => new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    // ---- parts: the groups and alternations of the pattern ------------------------------------------------

    protected override IEnumerable<LabPartType> DefinePartTypes() => [GroupType, NonCapturingType, LookaroundType, AtomicType, ConditionalType, AlternationType, BranchType];

    protected override IEnumerable<LabPart> BuildParts()
    {
        var nodes = RegexPartParser.Parse(_doc.Pattern, _doc.Flags);
        for (int i = 0; i < nodes.Count; i++) yield return ToPart(nodes[i], "p" + i, i);
    }

    private LabPart ToPart(RegexPartNode node, string key, int index)
    {
        string source  = node.Text(_doc.Pattern);
        string content = LabUi.Shorten(node.Content(_doc.Pattern), 22);
        (LabPartType type, string name) = node.Kind switch
                                          {
                                              RegexPartKind.Group        => (GroupType, node.Name is { } n ? $"{n} (group {node.Number})" : $"Group {node.Number}: {content}"),
                                              RegexPartKind.NonCapturing => (NonCapturingType, $"Non-capturing: {content}"),
                                              RegexPartKind.Lookaround   => (LookaroundType, $"{LookName(node, _doc.Pattern)}: {content}"),
                                              RegexPartKind.Atomic       => (AtomicType, $"Atomic: {content}"),
                                              RegexPartKind.Conditional  => (ConditionalType, $"Conditional: {content}"),
                                              RegexPartKind.Alternation  => (AlternationType, $"Alternation of {node.Children.Count}"),
                                              _                          => (BranchType, $"Branch {index + 1}: {content}")
                                          };

        var part = new LabPart(type, key, name, node, source);
        for (int i = 0; i < node.Children.Count; i++) part.Add(ToPart(node.Children[i], $"{key}.{i}", i));
        return part;
    }

    private static string LookName(RegexPartNode node, string pattern)
    {
        string head = pattern.Substring(node.Start, Math.Min(node.HeadLength, pattern.Length - node.Start));
        return head switch { "(?=" => "Followed by", "(?!" => "Not followed by", "(?<=" => "Preceded by", "(?<!" => "Not preceded by", _ => "Lookaround" };
    }

    /// <summary>The caret or selection in the pattern moved: the active part is the innermost one that holds it.</summary>
    public void NotePatternSelection(int start, int length)
    {
        _selStart  = start;
        _selLength = length;

        LabPart? deepest = null;

        void Walk(IEnumerable<LabPart> parts)
        {
            foreach (var part in parts)
            {
                if (part.Tag is not RegexPartNode node || start < node.Start || start + length > node.End) continue;

                deepest = part;
                Walk(part.Children);
            }
        }

        Walk(Parts);
        SetActivePart(deepest?.Key);
    }

    protected override void OnPartActivated(LabPart? part)
    {
        if (part?.Tag is RegexPartNode node) PatternRangeRequested?.Invoke(node.Start, node.Length);
    }

    private RegexPartNode? ActiveNode => ActivePart?.Tag as RegexPartNode;

    protected override Task OnAddPartAsync(LabPartType type, LabPart? parent, string content)
    {
        string pattern = _doc.Pattern;
        int    at;
        if (parent?.Tag is RegexPartNode node)
        {
            // inside a group, before its closing parenthesis; at the end of an alternation or branch
            at = Math.Min(node.ContentEnd, pattern.Length);
            if (node.Kind == RegexPartKind.Alternation) content = "|" + content;
        }
        else
        {
            at = Math.Clamp(_selStart + _selLength, 0, pattern.Length);
        }

        EditPattern(p => p.Insert(at, content), at, content.Length);
        return Task.CompletedTask;
    }

    /// <summary>Changes the pattern, has the boxes show it, and (when asked) selects a stretch of it.</summary>
    private void EditPattern(Func<string, string> change, int? selectStart = null, int selectLength = 0)
    {
        EditNow(d => d.Pattern = change(d.Pattern));
        Loaded?.Invoke(this, EventArgs.Empty);
        RaisePartsChanged();
        if (selectStart is { } at) PatternRangeRequested?.Invoke(at, selectLength);
    }

    // ---- aspects, tools and commands ------------------------------------------------------------------------

    protected override IEnumerable<LabAspect> DefineAspects()
    {
        var regex = LabOwner.OfSubjectType(SubjectId);
        yield return new LabAspect("matches", "Matches", regex) { IconKey = "Checklist", Description = "What the regex matched in the text, and what a replacement makes of it" };
        yield return new LabAspect("explain", "Explain", regex) { IconKey = "TbHelp", Description = "The pattern piece by piece, and a reference of the syntax" };
        yield return new LabAspect("code", "Code", regex) { IconKey = "Coding", Description = "The regex as code in other languages" };
        yield return new LabAspect("group", "Group", LabOwner.OfPartType(GroupType.Id))
                     {
                         IconKey     = "Layers",
                         Description = "What the active group captured, and ways of changing the group",
                         Commands =
                         [
                             ProvidedCommand.From("group-name", "_Name Group...", NameGroup, "Property", () => ActiveNode is { Kind: RegexPartKind.Group }, "Give the group a name"),
                             ProvidedCommand.From("group-noncapturing", "Make _Non-Capturing", MakeNonCapturing, null, () => ActiveNode is { Kind: RegexPartKind.Group }, "Keep the group but stop it capturing"),
                             ProvidedCommand.From("group-ungroup", "_Ungroup", Ungroup, null, () => ActiveNode is { HeadLength: > 0 }, "Take the parentheses away and keep what was inside")
                         ]
                     };
    }

    protected override IEnumerable<LabTool> DefineTools()
    {
        var regex = LabOwner.OfSubjectType(SubjectId);
        yield return new LabTool("quantify", "_Quantify", regex)
                     {
                         IconKey     = "Indent",
                         Description = "Repeat the selected piece of the pattern (or the active part)",
                         Parameters  = LabParameterSet.For(LabOf.Id, "tool:quantify", "Quantify", [new LabParameter("lazy", "Lazy", LabParameterKind.Bool, false, "Match as little as possible: the quantifier gets a ? after it.")]),
                         Commands =
                         [
                             ProvidedCommand.From("q-optional", "_Optional  ?", () => Quantify("?"), null, CanEditPattern, "At most once"),
                             ProvidedCommand.From("q-star", "_Zero or More  *", () => Quantify("*"), null, CanEditPattern, "Any number of times, none included"),
                             ProvidedCommand.From("q-plus", "_One or More  +", () => Quantify("+"), null, CanEditPattern, "At least once"),
                             ProvidedCommand.From("q-exactly", "_Exactly...", () => QuantifyAsked("How many times?", "3", n => "{" + n + "}"), null, CanEditPattern, "A set number of times"),
                             ProvidedCommand.From("q-between", "_Between...", () => QuantifyAsked("From how many to how many times? (for example 2,5; 2, for no upper limit)", "2,5", n => "{" + n + "}"), null, CanEditPattern, "A range of times")
                         ]
                     };
        yield return new LabTool("wrap", "_Wrap", regex)
                     {
                         IconKey     = "Layers",
                         Description = "Put the selected piece of the pattern (or the active part) in a group",
                         Commands =
                         [
                             ProvidedCommand.From("w-group", "In a _Group", () => Wrap("("), null, CanEditPattern, "A capturing group"),
                             ProvidedCommand.From("w-noncapturing", "In a _Non-Capturing Group", () => Wrap("(?:"), null, CanEditPattern, "A group that only groups"),
                             ProvidedCommand.From("w-named", "In a Na_med Group...", WrapNamed, null, CanEditPattern, "A group with a name"),
                             ProvidedCommand.From("w-ahead", "In a _Lookahead", () => Wrap("(?="), null, CanEditPattern, "Followed by"),
                             ProvidedCommand.From("w-atomic", "In an _Atomic Group", () => Wrap("(?>"), null, CanEditPattern, "A group the engine never backs into")
                         ]
                     };
    }

    protected override IEnumerable<LabCommandEntry> DefineModelCommands()
    {
        yield return new LabCommandEntry(LabOwner.Part, ProvidedCommand.From("part-select", "_Select in Pattern", () => OnPartActivated(ActivePart), "Search", () => ActiveNode is not null, "Select the part in the pattern"));
        yield return new LabCommandEntry(LabOwner.Part, ProvidedCommand.From("part-copy", "Copy _Part Text", () => CopyText(ActiveNode?.Text(_doc.Pattern) ?? string.Empty, "part"), "TbCopy", () => ActiveNode is not null, "Put the text of the part on the clipboard"));
        yield return new LabCommandEntry(LabOwner.Part, ProvidedCommand.From("part-delete", "_Delete Part", DeletePart, "TbDelete", () => ActiveNode is not null, "Take the part out of the pattern") with { IsDestructive = true });
    }

    private bool CanEditPattern() => _selLength > 0 || ActiveNode is not null;

    /// <summary>The stretch of the pattern a tool works on: what is selected, else the active part.</summary>
    private (int Start, int Length, RegexPartNode? Node)? EditTarget()
    {
        if (_selLength > 0 && _selStart + _selLength <= _doc.Pattern.Length) return (_selStart, _selLength, ActiveNode is { } n && n.Start == _selStart && n.Length == _selLength ? n : null);
        if (ActiveNode is { } node) return (node.Start, node.Length, node);
        return null;
    }

    private static readonly Regex OneAtom = new(@"^(?:.|\\.|\\[pP]\{[^}]*\}|\\k<[^>]*>|\\x[0-9A-Fa-f]{2}|\\u[0-9A-Fa-f]{4}|\\c.|\[(?:\\.|[^\]\\])*\])$", RegexOptions.Compiled | RegexOptions.Singleline);

    private static bool IsAtom(string text, RegexPartNode? node) => node is { HeadLength: > 0 } || OneAtom.IsMatch(text);

    private void Quantify(string quantifier)
    {
        if (EditTarget() is not { } target)
        {
            Status = "Select a piece of the pattern, or a part, first";
            return;
        }

        bool lazy = Tools.FirstOrDefault(t => t.Id == "quantify")?.Parameters?["lazy"].Bool == true;
        if (lazy) quantifier += "?";

        string pattern = _doc.Pattern;
        string text    = pattern.Substring(target.Start, target.Length);
        bool   wrap    = !IsAtom(text, target.Node);
        string result  = wrap ? $"(?:{text}){quantifier}" : text + quantifier;
        EditPattern(p => p[..target.Start] + result + p[(target.Start + target.Length)..], target.Start, result.Length);
    }

    private void QuantifyAsked(string question, string initial, Func<string, string> format)
    {
        string? answer = LabUi.Ask("Quantify", question, initial);
        if (string.IsNullOrWhiteSpace(answer)) return;

        string clean = answer.Replace(" ", string.Empty);
        if (!Regex.IsMatch(clean, @"^\d+(,\d*)?$"))
        {
            Status = "Give a number, or two numbers with a comma: 3 or 2,5";
            return;
        }

        Quantify(format(clean));
    }

    private void Wrap(string open)
    {
        if (EditTarget() is not { } target)
        {
            Status = "Select a piece of the pattern, or a part, first";
            return;
        }

        EditPattern(p => p[..target.Start] + open + p.Substring(target.Start, target.Length) + ")" + p[(target.Start + target.Length)..], target.Start, target.Length + open.Length + 1);
    }

    private void WrapNamed()
    {
        string? name = LabUi.Ask("Wrap in a named group", "Name of the group:", "name");
        if (!string.IsNullOrWhiteSpace(name)) Wrap($"(?<{name.Trim()}>");
    }

    private void NameGroup()
    {
        if (ActiveNode is not { Kind: RegexPartKind.Group } node) return;

        string? name = LabUi.Ask("Name group", "Name of the group:", node.Name ?? "name");
        if (string.IsNullOrWhiteSpace(name)) return;

        string head = $"(?<{name.Trim()}>";
        EditPattern(p => p[..node.Start] + head + p[node.ContentStart..], node.Start, node.Length - node.HeadLength + head.Length);
    }

    private void MakeNonCapturing()
    {
        if (ActiveNode is not { Kind: RegexPartKind.Group } node) return;

        EditPattern(p => p[..node.Start] + "(?:" + p[node.ContentStart..], node.Start, node.Length - node.HeadLength + 3);
    }

    private void Ungroup()
    {
        if (ActiveNode is not { HeadLength: > 0 } node) return;

        string inside = node.Content(_doc.Pattern);
        SetActivePart(null);
        EditPattern(p => p[..node.Start] + inside + p[node.End..], node.Start, inside.Length);
    }

    private void DeletePart()
    {
        if (ActiveNode is not { } node) return;

        int start = node.Start, end = node.End;
        if (node.Kind == RegexPartKind.Branch)
        {
            // a branch goes with one of the bars around it
            if (start > 0 && _doc.Pattern[start - 1] == '|') start--;
            else if (end < _doc.Pattern.Length && _doc.Pattern[end] == '|') end++;
        }

        SetActivePart(null);
        EditPattern(p => p[..start] + p[end..], start, 0);
    }
}

/// <summary>What the active group captured in each match.</summary>
internal sealed class RegexGroupInspector(RegexLabSession session) : RegexInspector(session)
{
    private ListBox?   _list;
    private TextBlock? _head;

    public override string Id => "group";

    public override string Title => "Group";

    public override string IconKey => "Layers";

    protected override UIElement Create()
    {
        Session.ModelChanged += (_, _) => Poke();
        _head                = LabUi.Label(string.Empty, muted: true);
        _head.Margin         = new Thickness(8, 6, 8, 4);
        _list                = LabUi.List();
        _list.MouseDoubleClick += (_, _) =>
                                  {
                                      if ((_list.SelectedItem as ListBoxItem)?.Tag is ValueTuple<int, int> (int index, int length)) Session.Reveal(index, length);
                                  };
        return LabUi.Layout(_head, _list);
    }

    protected override void Refresh()
    {
        if (_list is null || _head is null) return;

        _list.Items.Clear();
        if (Session.ActivePart is not { Tag: RegexPartNode { Kind: RegexPartKind.Group, Number: { } number } node })
        {
            _head.Text = "Choose a group in the pattern (or in the Part box) to see what it captured.";
            return;
        }

        var run = Session.Run;
        _head.Text = (node.Name is null ? $"Group {number}" : $"{node.Name} (group {number})") + (run.IsValid ? $" in {run.Matches.Count:N0} match(es)" : string.Empty);
        foreach (var match in run.Matches.Take(500))
        {
            var group = match.Groups.FirstOrDefault(g => g.Number == number);
            string text = group is { Success: true } ? $"\"{LabUi.Shorten(group.Value, 80)}\"" : "(did not take part)";
            _list.Items.Add(LabUi.Item((match.Index, match.Length), LabUi.Row(group is { Success: true } ? "Check" : "Hide", $"Match {match.Number}", null, text), "Double-click to show the match in the text"));
        }
    }
}
