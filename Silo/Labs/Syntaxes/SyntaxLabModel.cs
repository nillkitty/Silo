using Silo.Labs.Regexes;
using Silo.Providers.Data;
using Silo.Providers.Definitions;
using SyntaxKind = Silo.DbModel.Meta.SyntaxKind;

namespace Silo.Labs.Syntaxes;

// The Syntax Lab as a model (see LabModel.cs):
//   subject  a syntax (SyntaxDraft with its SyntaxBook)
//   parts    the rule (the pattern, with the groups written in it, or the built-in parser) and the sample sets (valid, invalid, to try)
//            with the productions: each string of a set and what the syntax made of it
//   aspects  Details (pane); Rule, Group, Sample set, Production (commands of the active part)
//   tools    Try (feed the syntax a string)
public sealed partial class SyntaxLabSession
{
    private const string SubjectId = "syntax";

    private static readonly LabPartType RuleType = new("rule", "Rule", "Coding", "What decides which strings are valid: a pattern or a built-in parser");

    private static readonly LabPartType GroupType = new("group", "Group", "Layers", "A group or alternation written in the pattern", null,
                                                        [new LabTemplate("capture", "Capturing group", "({{inside}})", [new("inside", "What it holds", @"\d+")], "Remembers what it matches"),
                                                         new LabTemplate("named", "Named group", "(?<{{name}}>{{inside}})", [new("name", "Name of the group", "name"), new("inside", "What it holds", @"\w+")], "A group with a name"),
                                                         new LabTemplate("either", "Either of two", "(?:{{first}}|{{second}})", [new("first", "First way", "yes"), new("second", "Second way", "no")], "Alternatives"),
                                                         new LabTemplate("optional", "Optional piece", "(?:{{inside}})?", [new("inside", "What may be there", @"-\d+")], "May be there or not")]);

    private static readonly LabPartType SetType = new("sampleset", "Sample set", "List", "Strings the syntax is tried on: ones that have to be valid, ones that must not be, ones to look at");

    private static readonly LabPartType ProductionType = new("production", "Production", "Check", "One string of a sample set, and what the syntax made of it", null, null, "sampleset");

    private static readonly string[] SetNames = ["valid", "invalid", "try"];

    /// <summary>One string of a sample set, with what it came to (when the syntax could be built).</summary>
    internal sealed record SampleTag(string Set, int Index, string Raw, TrialResult? Result);

    /// <summary>The window wants a production shown (the index in <see cref="Results" />).</summary>
    internal event Action<int>? ProductionFocusRequested;

    /// <summary>The window wants a stretch of the pattern selected.</summary>
    internal event Action<int, int>? PatternRangeRequested;

    /// <summary>The window wants a sample set's box to have the keyboard.</summary>
    internal event Action<string>? SetFocusRequested;

    protected override string SubjectNoun => "syntax";

    // ---- subject: prefabs and templates ---------------------------------------------------------------

    protected override IEnumerable<LabSubjectType> DefineSubjectTypes()
    {
        var prefabs = SyntaxParsers.All.Select(p => new LabPrefab("parser-" + p.Key, p.Title, "parser:" + p.Key, $"{p.Description} Makes a {p.ValueType.Name}.", "Modules", "Built-in parsers")).ToList();
        var templates = new List<LabTemplate>
                        {
                            new("pattern", "Pattern", "{{name}}\n{{pattern}}\n{{example}}", [new("name", "Name", "NewSyntax"), new("pattern", "Pattern all of a string has to match", @"[A-Z]{2}\d{4}"), new("example", "A valid string", "AB1234")], "A syntax of a pattern of your own", "Code"),
                            new("choice", "One of a few words", "{{name}}\n(?:{{words}})\n", [new("name", "Name", "NewChoice"), new("words", "The words, with | between", "red|green|blue")], "Only certain words are valid", "Code"),
                            new("prefixed", "Prefix and number", "{{name}}\n{{prefix}}-\\d+\n{{prefix}}-1", [new("name", "Name", "NewId"), new("prefix", "How it starts", "ID")], "Something like ID-17", "Code")
                        };
        yield return new LabSubjectType(SubjectId, "Syntax", "Coding", "A syntax of the Silo: a pattern or built-in parser, with the strings it is tried on", prefabs, templates);
    }

    protected override Task OnNewSubjectAsync(LabSubjectType type, string content)
    {
        if (content.StartsWith("parser:", StringComparison.Ordinal))
        {
            if (SyntaxParsers.Find(content[7..]) is not { } parser) throw new InvalidOperationException($"There is no built-in parser '{content[7..]}'.");

            StartFromParser(parser);
            return Task.CompletedTask;
        }

        // name, pattern, example: a line each
        var lines = content.Replace("\r", string.Empty).Split('\n');
        string name    = lines.Length > 0 ? lines[0].Trim() : string.Empty;
        string pattern = lines.Length > 1 ? lines[1] : string.Empty;
        string? example = lines.Length > 2 && lines[2].Length > 0 ? lines[2] : null;
        if (pattern.Length == 0) throw new InvalidOperationException("A syntax of a pattern needs a pattern.");

        if (_dirty && !LabUi.Confirm("This syntax has changes that are not kept in the Silo. Start another anyway?", "Syntax Lab")) return Task.CompletedTask;

        var book = new SyntaxBook();
        if (example is not null) book.Valid = SyntaxTrial.Escape(example);
        Replace(SyntaxRecord.ForPattern(name, pattern, null, null, example), book);
        _dirty = true;
        RaiseValueChanged();
        return Task.CompletedTask;
    }

    // ---- parts ----------------------------------------------------------------------------------------

    protected override IEnumerable<LabPartType> DefinePartTypes() => [RuleType, GroupType, SetType, ProductionType];

    protected override IEnumerable<LabPart> BuildParts()
    {
        bool isParser = _draft.Kind == SyntaxKind.Parser;
        var parser    = isParser ? SyntaxParsers.Find(_draft.Parser) : null;
        string ruleName   = string.IsNullOrWhiteSpace(_draft.Name) ? "(new syntax)" : _draft.Name;
        string ruleDetail = isParser ? "Parser " + (parser?.Title ?? _draft.Parser ?? "(none)") : LabUi.Shorten(_draft.Pattern ?? string.Empty, 40);
        var rule = new LabPart(RuleType, "rule", ruleName, null, ruleDetail);
        if (!isParser)
        {
            var nodes = RegexPartParser.Parse(_draft.Pattern);
            for (int i = 0; i < nodes.Count; i++) rule.Add(GroupPart(nodes[i], "g" + i, i));
        }

        yield return rule;

        int total  = SetNames.Sum(s => RawStrings(SetText(_book, s)).Count);
        bool match = _results.Count == total;
        int offset = 0;
        foreach (string set in SetNames)
        {
            var raws = RawStrings(SetText(_book, set));
            var part = new LabPart(SetType, "set:" + set, SetTitle(set) + $" ({raws.Count})", set, null);
            for (int i = 0; i < raws.Count; i++)
            {
                var result = match ? _results[offset + i] : null;
                string shown = _book.Escapes ? raws[i] : SyntaxTrial.Escape(raws[i]);
                part.Add(new LabPart(ProductionType, $"s:{set}:{i}", "\"" + LabUi.Shorten(shown, 40) + "\"", new SampleTag(set, i, raws[i], result), Describe(result)));
            }

            offset += raws.Count;
            yield return part;
        }
    }

    private LabPart GroupPart(RegexPartNode node, string key, int index)
    {
        string content = LabUi.Shorten(node.Content(_draft.Pattern ?? string.Empty), 22);
        string name = node.Kind switch
                      {
                          RegexPartKind.Group        => node.Name is { } n ? $"{n} (group {node.Number})" : $"Group {node.Number}: {content}",
                          RegexPartKind.NonCapturing => "Non-capturing: " + content,
                          RegexPartKind.Lookaround   => "Lookaround: " + content,
                          RegexPartKind.Atomic       => "Atomic: " + content,
                          RegexPartKind.Conditional  => "Conditional: " + content,
                          RegexPartKind.Alternation  => $"Either of {node.Children.Count}",
                          _                          => $"Way {index + 1}: {content}"
                      };
        var part = new LabPart(GroupType, key, name, node, node.Text(_draft.Pattern ?? string.Empty));
        for (int i = 0; i < node.Children.Count; i++) part.Add(GroupPart(node.Children[i], $"{key}.{i}", i));
        return part;
    }

    private static string Describe(TrialResult? r)
    {
        if (r is null) return string.Empty;

        string what = r.IsValid ? $"valid: {r.TypeName} {LabUi.Shorten(r.Value ?? string.Empty, 30)}" : "invalid: " + LabUi.Shorten(r.Error ?? string.Empty, 40);
        return r.Passed ? what : what + "  (not as expected)";
    }

    private static string SetTitle(string set) => set switch { "valid" => "Has to be valid", "invalid" => "Must not be valid", _ => "To try" };

    private static string SetText(SyntaxBook book, string set) => set switch { "valid" => book.Valid, "invalid" => book.Invalid, _ => book.Try };

    private static void SetSetText(SyntaxBook book, string set, string text)
    {
        switch (set)
        {
            case "valid":
                book.Valid = text;
                break;
            case "invalid":
                book.Invalid = text;
                break;
            default:
                book.Try = text;
                break;
        }
    }

    /// <summary>The strings of a list as written (the lines that are not empty), in the order <see cref="SyntaxTrial.RunAll" /> tries them.</summary>
    private static List<string> RawStrings(string text)
    {
        return string.IsNullOrEmpty(text) ? [] : text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
    }

    // ---- keeping the active part and the window's selection together ---------------------------------

    /// <summary>A row of what each string came to was chosen: its production is the active part.</summary>
    internal void NoteResult(int resultIndex)
    {
        if (resultIndex < 0 || resultIndex >= _results.Count) return;

        int valid   = _results.Count(r => r.Expect == TrialExpect.Valid);
        int invalid = _results.Count(r => r.Expect == TrialExpect.Invalid);
        (string set, int n) = resultIndex < valid ? ("valid", resultIndex) : resultIndex < valid + invalid ? ("invalid", resultIndex - valid) : ("try", resultIndex - valid - invalid);
        SetActivePart($"s:{set}:{n}");
    }

    /// <summary>The caret of the pattern box is somewhere in the pattern: the innermost group around it (or the rule) is the active part.</summary>
    internal void NotePatternCaret(int caret)
    {
        LabPart? best = null;
        foreach (var part in Parts.SelectMany(p => p.Self()))
            if (part.Tag is RegexPartNode node && node.Start <= caret && caret <= node.End && (best is null || part.Depth > best.Depth))
                best = part;

        SetActivePart(best?.Key ?? "rule");
    }

    /// <summary>A box of a sample set has the keyboard: the set is the active part.</summary>
    internal void NoteSet(string set) => SetActivePart("set:" + set);

    protected override void OnPartActivated(LabPart? part)
    {
        switch (part?.Tag)
        {
            case RegexPartNode node:
                PatternRangeRequested?.Invoke(node.Start, node.Length);
                break;
            case SampleTag tag:
            {
                int index = ResultIndexOf(tag.Set, tag.Index);
                if (index >= 0) ProductionFocusRequested?.Invoke(index);
                break;
            }
            case string set when part.Type == SetType:
                SetFocusRequested?.Invoke(set);
                break;
        }
    }

    private int ResultIndexOf(string set, int n)
    {
        int valid   = _results.Count(r => r.Expect == TrialExpect.Valid);
        int invalid = _results.Count(r => r.Expect == TrialExpect.Invalid);
        int index   = set switch { "valid" => n, "invalid" => valid + n, _ => valid + invalid + n };
        return index < _results.Count ? index : -1;
    }

    // ---- adding parts ----------------------------------------------------------------------------------

    protected override Task OnAddPartAsync(LabPartType type, LabPart? parent, string content)
    {
        if (type.Id == ProductionType.Id)
        {
            string set = parent?.Tag switch { SampleTag t => t.Set, string s when parent.Type == SetType => s, _ => "valid" };
            if (content.Length == 0) throw new InvalidOperationException("A production needs a string.");

            string line = _book.Escapes ? SyntaxTrial.Escape(content) : content.Replace("\r", string.Empty).Replace("\n", " ");
            EditBookNow(b => SetSetText(b, set, Append(SetText(b, set), line)));
            return Task.CompletedTask;
        }

        if (type.Id == GroupType.Id)
        {
            if (_draft.Kind != SyntaxKind.Pattern) throw new InvalidOperationException("Only a syntax of a pattern has groups.");

            string pattern = _draft.Pattern ?? string.Empty;
            int at = parent?.Tag is RegexPartNode { IsClosed: true } node && node.Kind is not RegexPartKind.Alternation ? node.ContentEnd : pattern.Length;
            EditNow(d => d.Pattern = pattern.Insert(Math.Clamp(at, 0, pattern.Length), content));
            Loaded?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        return base.OnAddPartAsync(type, parent, content);
    }

    // ---- aspects, tools and commands ------------------------------------------------------------------------

    private RegexPartNode? ActiveGroup => ActivePart?.Tag as RegexPartNode;

    private SampleTag? ActiveSample => ActivePart?.Tag as SampleTag;

    private string? ActiveSet => ActivePart is { } p && p.Type == SetType ? p.Tag as string : null;

    protected override IEnumerable<LabAspect> DefineAspects()
    {
        var syntax = LabOwner.OfSubjectType(SubjectId);
        yield return new LabAspect("details", "Details", syntax) { IconKey = "Analysis", Description = "The syntax as the Silo would keep it, and what it is made of" };

        yield return new LabAspect("rule", "Rule", LabOwner.OfPartType(RuleType.Id))
                     {
                         IconKey     = "Coding",
                         Description = "What decides which strings are valid",
                         Commands =
                         [
                             ProvidedCommand.From("example-sample", "Example to _Valid", ExampleToValid, "Check", () => !string.IsNullOrWhiteSpace(_draft.Example), "Add the syntax's example to the strings that have to be valid"),
                             ProvidedCommand.From("rule-copy", "Copy _Rule", () => CopyText(_draft.Kind == SyntaxKind.Parser ? _draft.Parser ?? string.Empty : _draft.Pattern ?? string.Empty, "rule"), "TbCopy", () => _draft.Kind == SyntaxKind.Parser ? !string.IsNullOrEmpty(_draft.Parser) : !string.IsNullOrEmpty(_draft.Pattern), "Put the pattern (or the parser's key) on the clipboard")
                         ]
                     };

        yield return new LabAspect("group", "Group", LabOwner.OfPartType(GroupType.Id))
                     {
                         IconKey     = "Layers",
                         Description = "The active group of the pattern",
                         Commands =
                         [
                             ProvidedCommand.From("group-select", "_Select in Pattern", () => { if (ActiveGroup is { } g) PatternRangeRequested?.Invoke(g.Start, g.Length); }, "TbSelect", () => ActiveGroup is not null, "Select the group in the pattern box"),
                             ProvidedCommand.From("group-copy", "_Copy Group", () => { if (ActiveGroup is { } g) CopyText(g.Text(_draft.Pattern ?? string.Empty), "group"); }, "TbCopy", () => ActiveGroup is not null, "Put the group's text on the clipboard"),
                             ProvidedCommand.From("group-optional", "Make _Optional", () => ChangeGroup(g => g.Text(_draft.Pattern ?? string.Empty) + "?"), null, () => ActiveGroup is { Kind: not RegexPartKind.Alternation and not RegexPartKind.Branch }, "Put a ? after the group, so it may be there or not"),
                             ProvidedCommand.From("group-unwrap", "_Unwrap", () => ChangeGroup(g => g.Content(_draft.Pattern ?? string.Empty)), null, () => ActiveGroup is { IsClosed: true, Kind: not RegexPartKind.Alternation and not RegexPartKind.Branch }, "Take the parentheses off and keep what was inside") with { IsDestructive = true }
                         ]
                     };

        yield return new LabAspect("sampleset", "Sample set", LabOwner.OfPartType(SetType.Id))
                     {
                         IconKey     = "List",
                         Description = "The active list of strings",
                         Commands =
                         [
                             ProvidedCommand.From("set-sort", "S_ort and Remove Repeats", () => ChangeSet(lines => lines.Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList()), "Sort", () => ActiveSet is not null, "Put the strings in order and keep each once"),
                             ProvidedCommand.From("set-paste", "_Paste Lines", PasteLines, "TbPaste", () => ActiveSet is not null, "Add the lines on the clipboard to the list"),
                             ProvidedCommand.From("set-copy", "Copy _Lines", () => { if (ActiveSet is { } s) CopyText(SetText(_book, s), "lines"); }, "TbCopy", () => ActiveSet is { } s && SetText(_book, s).Length > 0, "Put the list on the clipboard"),
                             ProvidedCommand.From("set-clear", "C_lear", () => ChangeSet(_ => []), "TbDelete", () => ActiveSet is { } s && SetText(_book, s).Length > 0, "Empty the list") with { IsDestructive = true }
                         ]
                     };

        yield return new LabAspect("production", "Production", LabOwner.OfPartType(ProductionType.Id))
                     {
                         IconKey     = "Check",
                         Description = "The active string and what the syntax made of it",
                         Commands =
                         [
                             ProvidedCommand.From("prod-valid", "Move to _Valid", () => MoveSample("valid"), "Check", () => ActiveSample is { } t && t.Set != "valid", "Make it a string that has to be valid"),
                             ProvidedCommand.From("prod-invalid", "Move to _Invalid", () => MoveSample("invalid"), "TbDelete", () => ActiveSample is { } t && t.Set != "invalid", "Make it a string that must not be valid"),
                             ProvidedCommand.From("prod-try", "Move to _Try", () => MoveSample("try"), null, () => ActiveSample is { } t && t.Set != "try", "Make it a string to look at"),
                             ProvidedCommand.From("prod-copy", "Copy _String", () => { if (ActiveSample is { } t) CopyText(t.Raw, "string"); }, "TbCopy", () => ActiveSample is not null, "Put the string on the clipboard"),
                             ProvidedCommand.From("prod-canonical", "Copy _Written Back", () => { if (ActiveSample?.Result?.Canonical is { } c) CopyText(c, "written-back string"); }, "TbCopy", () => ActiveSample?.Result?.Canonical is not null, "Put the way the syntax writes the value back on the clipboard"),
                             ProvidedCommand.From("prod-remove", "_Remove", RemoveSample, "TbDelete", () => ActiveSample is not null, "Take the string out of its list") with { IsDestructive = true }
                         ]
                     };
    }

    protected override IEnumerable<LabTool> DefineTools()
    {
        yield return new LabTool("try", "_Try", LabOwner.OfSubjectType(SubjectId))
                     {
                         IconKey     = "Play",
                         Description = "Feed the syntax a string and see what it makes of it",
                         Parameters  = LabParameterSet.For(LabOf.Id, "tool:try", "Try", [new LabParameter("text", "String", LabParameterKind.Text, string.Empty, "The string to try (\\n and \\t are read).", hint: "Something to try")]),
                         Commands =
                         [
                             ProvidedCommand.From("try-run", "_Try It", () => Status = TryCommand(ToolText()), "Play", () => ToolText().Length > 0, "Build the syntax and see what it makes of the string"),
                             ProvidedCommand.From("try-add", "_Add to the Try List", () => AddTry(ToolText()), "TbAdd", () => ToolText().Length > 0, "Put the string on the list of strings to look at")
                         ]
                     };
    }

    private string ToolText() => Tools.FirstOrDefault(t => t.Id == "try")?.Parameters?["text"].Text ?? string.Empty;

    private void AddTry(string text)
    {
        if (text.Length == 0) return;

        string line = _book.Escapes ? text : SyntaxTrial.Unescape(text).Replace("\n", " ");
        EditBookNow(b => b.Try = Append(b.Try, line));
        Status = "Added the string to the list to try";
    }

    private void ChangeGroup(Func<RegexPartNode, string> replacement)
    {
        if (ActiveGroup is not { } node) return;

        string pattern = _draft.Pattern ?? string.Empty;
        string text    = replacement(node);
        EditNow(d => d.Pattern = pattern.Remove(node.Start, Math.Min(node.Length, pattern.Length - node.Start)).Insert(node.Start, text));
        Loaded?.Invoke(this, EventArgs.Empty);
    }

    private void ChangeSet(Func<List<string>, List<string>> change)
    {
        if (ActiveSet is not { } set) return;

        EditBookNow(b => SetSetText(b, set, string.Join('\n', change(RawStrings(SetText(b, set))))));
    }

    private void PasteLines()
    {
        if (ActiveSet is not { } set) return;

        string? text = null;
        try
        {
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // the clipboard is in use by another program
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Status = "There is no text on the clipboard.";
            return;
        }

        string lines = string.Join('\n', RawStrings(text));
        EditBookNow(b => SetSetText(b, set, Append(SetText(b, set), lines)));
    }

    private void MoveSample(string to)
    {
        if (ActiveSample is not { } tag) return;

        EditBookNow(b =>
                    {
                        RemoveLine(b, tag);
                        SetSetText(b, to, Append(SetText(b, to), tag.Raw));
                    });
        SetActivePart($"s:{to}:{RawStrings(SetText(_book, to)).Count - 1}");
    }

    private void RemoveSample()
    {
        if (ActiveSample is not { } tag) return;

        EditBookNow(b => RemoveLine(b, tag));
        SetActivePart("set:" + tag.Set);
    }

    /// <summary>Takes the string out of its list (the n-th line that is not empty).</summary>
    private static void RemoveLine(SyntaxBook book, SampleTag tag)
    {
        var lines = SetText(book, tag.Set).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int seen = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length == 0) continue;
            if (++seen != tag.Index) continue;

            lines.RemoveAt(i);
            break;
        }

        SetSetText(book, tag.Set, string.Join('\n', lines));
    }
}
