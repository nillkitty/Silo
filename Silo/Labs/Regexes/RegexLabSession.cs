using System.Text;
using Newtonsoft.Json;
using Silo.Providers.Data;
using SyntaxKind = Silo.DbModel.Meta.SyntaxKind;

namespace Silo.Labs.Regexes;

/// <summary>One Regex Lab window: the regex being built, what trying it came to, and the actions on it.</summary>
public sealed partial class RegexLabSession : LabSession<RegexDoc>
{
    private readonly Debouncer          _rerun;
    private readonly List<LabInspector> _inspectors;
    private          RegexDoc           _doc = new();
    private          RegexRun           _run = new() { IsValid = true };
    private          bool               _dirty;
    private          bool               _started;

    public RegexLabSession(RegexLab lab) : base(lab)
    {
        _rerun = new Debouncer(TimeSpan.FromMilliseconds(180), Rerun);
        // each pane belongs to an aspect: it is there while its aspect applies (see DefineAspects)
        _inspectors =
        [
            new RegexMatchesInspector(this).OwnedBy(LabOwner.OfAspect("matches")),
            new RegexReplaceInspector(this).OwnedBy(LabOwner.OfAspect("matches")),
            new RegexExplainInspector(this).OwnedBy(LabOwner.OfAspect("explain")),
            new RegexReferenceInspector(this).OwnedBy(LabOwner.OfAspect("explain")),
            new RegexCodeInspector(this).OwnedBy(LabOwner.OfAspect("code")),
            new RegexGroupInspector(this).OwnedBy(LabOwner.OfAspect("group"))
        ];
    }

    /// <summary>The regex as it is now.</summary>
    public RegexDoc Doc => _doc;

    /// <summary>What trying the regex on the text came to.</summary>
    public RegexRun Run => _run;

    public bool IsDirty => _dirty;

    /// <summary>The regex or its result changed (what the views draw).</summary>
    public event EventHandler? Changed;

    /// <summary>A different regex was opened: the boxes show it afresh (as opposed to <see cref="Changed" />, which comes after every key typed).</summary>
    public event EventHandler? Loaded;

    /// <summary>The person wants a stretch of the text to be shown (a match was chosen in the list).</summary>
    public event Action<int, int>? RevealRequested;

    /// <summary>The person wants something typed into the pattern at the cursor (an entry of the reference was chosen).</summary>
    public event Action<string>? InsertRequested;

    public override RegexDoc? CurrentValue => _doc;

    public override string ValueText => RegexLiteral.Format(_doc);

    public override string Title => $"{LabOf.Name} - {(string.IsNullOrEmpty(_doc.Name) ? LabUi.Shorten(ValueText, 30) : _doc.Name)}{(_dirty ? " *" : string.Empty)}";

    private RegexLab Lab_ => (RegexLab)LabOf;

    /// <summary>3: the panes belong to aspects, and there is a pane for the active group; windows saved with the old arrangement start afresh.</summary>
    public override int LayoutVersion => 3;

    public override IReadOnlyList<LabInspector> Inspectors => _inspectors;

    // ---- opening ---------------------------------------------------------------------------------

    protected override Task OpenValueAsync(RegexDoc value)
    {
        _doc   = value.Clone();
        _dirty = false;
        Rerun();
        Loaded?.Invoke(this, EventArgs.Empty);
        RaiseValueChanged();
        RaiseActionsChanged();
        return Task.CompletedTask;
    }

    protected override Task OpenDefaultAsync()
    {
        if (_started) return Task.CompletedTask;

        _started = true;
        var start = new RegexDoc { Flags = RegexFlags.Normalize(string.Concat(Lab_.Parameters[RegexLabParameters.NewFlags].Lines.Select(RegexLab.FlagLetter))) };
        return OpenValueAsync(start);
    }

    protected override void OnAttached()
    {
        Lab_.Parameters.Changed += OnParametersChanged;
        if (!_started) _ = OpenDefaultAsync();
    }

    private void OnParametersChanged(LabParameter? _)
    {
        Application.Current?.Dispatcher.InvokeAsync(Rerun);
    }

    protected override void OnDispose()
    {
        Lab_.Parameters.Changed -= OnParametersChanged;
        _rerun.Cancel();
    }

    // ---- editing ---------------------------------------------------------------------------------

    /// <summary>Changes the regex; the result is worked out again a moment after the last change.</summary>
    public void Edit(Action<RegexDoc> change)
    {
        change(_doc);
        _dirty = true;
        _rerun.Poke();
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    /// <summary>Changes the regex and works out the result at once.</summary>
    public void EditNow(Action<RegexDoc> change)
    {
        _rerun.Cancel();
        change(_doc);
        _dirty = true;
        Rerun();
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    public void Reveal(int index, int length) => RevealRequested?.Invoke(index, length);

    public void Insert(string text) => InsertRequested?.Invoke(text);

    /// <summary>Starts again from another regex (a library entry, a saved one); the boxes show it.</summary>
    public void Replace(RegexDoc doc, bool keepText = false)
    {
        var next = doc.Clone();
        if (keepText) next.TestText = _doc.TestText;
        _doc   = next;
        _dirty = false;
        Rerun();
        Loaded?.Invoke(this, EventArgs.Empty);
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    private void Rerun()
    {
        _rerun.Cancel();
        _run = RegexEngine.Run(_doc, Lab_.Timeout, Lab_.MaxMatches);
        Status = _run.Summary;
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseProblemsChanged();
    }

    // ---- library, recent and problems (the panes every Lab window has) -------------------------------

    protected override bool RecentOnOpen => false;

    public override bool LibraryOpensWithWindow => true;

    public override Task OpenRecentAsync(string text)
    {
        OpenKept(text);
        return Task.CompletedTask;
    }

    private void OpenKept(string name)
    {
        if (RegexStore.Load(name) is not { } kept)
        {
            Status = $"There is no regex called {name} in the Silo.";
            return;
        }

        Replace(kept);
        RememberRecent(name);
    }

    public override IReadOnlyList<LabLibrarySection> LibrarySections(string filter)
    {
        bool Wanted(string text) => filter.Length == 0 || text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var kept = RegexStore.Names().Where(Wanted)
                             .Select(name => new LabLibraryEntry(name, "Regex", LabUi.Shorten(RegexStore.Load(name)?.Pattern ?? string.Empty, 40), "Search", () => OpenKept(name), name == _doc.Name,
                                                                 ToolTip: "Double-click to open", Delete: () => RegexStore.Delete(name)))
                             .ToList();
        // the ready-made regexes are the prefabs of the Regex subject type (the window lists them under Prefabs)
        return [new("In the Silo", "Database5", kept)];
    }

    protected override IEnumerable<LabProblem> Validate()
    {
        if (!_run.IsValid) yield return new LabProblem(LabProblemSeverity.Error, "Pattern", _run.Error ?? "The pattern is not valid.", _run.ErrorOffset is { } at ? $"at {at}" : null);
    }

    public TimeSpan Timeout => Lab_.Timeout;

    // ---- actions ---------------------------------------------------------------------------------

    public override IEnumerable<ProvidedCommand> GlobalActions
    {
        get
        {
            yield return ProvidedCommand.From("new-regex", "_New Regex", NewRegex, "TbNew", null, "Start a new regex");
            yield return ProvidedCommand.From("open-library", "Show _Library", () => Host?.ShowInspector("library"), "TbOpen", null,
                                              "Show the patterns the Lab comes with and the ones you kept");
            yield return ProvidedCommand.From("settings", "Open _Settings", () => Host?.ShowInspector(LabParametersInspector.InspectorId), "Settings", null,
                                              "Show the Lab's parameters: the time limit, the most matches, the options of a new regex");
        }
    }

    public override IEnumerable<ProvidedCommand> ContextActions
    {
        get
        {
            yield return ProvidedCommand.From("run", "_Run", Rerun, "Refresh", null, "Try the regex on the text again") with { Gesture = "F5" };
            yield return ProvidedCommand.From("clear-text", "Clear _Text", () => EditNow(d => d.TestText = string.Empty), null, () => _doc.TestText.Length > 0, "Empty the test text");
        }
    }

    public override IEnumerable<ProvidedCommand> ValueActions
    {
        get
        {
            yield return ProvidedCommand.From("save", "_Save Regex...", SaveRegex, "TbSave", () => RegexStore.IsAvailable, "Keep the regex in the Silo (listed in the Library and under Saved in the Explorer)") with { Gesture = "Ctrl+S" };
            yield return ProvidedCommand.From("copy-pattern", "Copy _Pattern", () => CopyText(_doc.Pattern, "pattern"), "TbCopy", () => _doc.Pattern.Length > 0, "Put the pattern on the clipboard");
            yield return ProvidedCommand.From("copy-literal", "Copy as _/pattern/flags", () => CopyText(ValueText, "regex"), "TbCopy", () => _doc.Pattern.Length > 0, "Put the regex on the clipboard in the slash way of writing it");
            yield return ProvidedCommand.From("copy-csharp", "Copy as _C#", () => CopyText(RegexCode.Generate("C#", _doc), "C# code"), "TbCopy", () => _doc.Pattern.Length > 0, "Put C# code that makes the regex on the clipboard");
            yield return ProvidedCommand.From("copy-matches", "Copy _Matches", CopyMatches, "TbCopy", () => _run.Matches.Count > 0, "Put the matched text, a match on each line, on the clipboard");
            yield return ProvidedCommand.From("to-syntax", "Make a _Syntax from This", SendToSyntaxLab, "Coding", () => _run.IsValid && _doc.Pattern.Length > 0,
                                              "Open the Syntax Lab with this pattern as a Syntax of the Pattern kind (the text is tried against all of it)");
            yield return ProvidedCommand.From("benchmark", "_Benchmark", Benchmark, "Check", () => _run.IsValid && _doc.Pattern.Length > 0, "Time the regex on the text");
            yield return ProvidedCommand.From("delete-saved", "_Delete Saved Regex", DeleteSaved, "TbDelete", () => RegexStore.IsAvailable && _doc.Name.Length > 0 && RegexStore.Exists(_doc.Name), "Remove the regex from the Silo");
        }
    }

    public override IReadOnlyList<LabTerminalCommand> TerminalCommands =>
    [
        new("pattern", "pattern <regex>            set the pattern (or show it)", call => Task.FromResult<string?>(SetPattern(call.Rest))),
        new("flags", "flags <letters>            set the options: " + string.Join(" ", RegexFlags.All.Select(f => $"{f.Letter}={f.Name}")), call => Task.FromResult<string?>(SetFlags(call.Rest))),
        new("text", "text <text>               set the test text (\\n for a new line)", call => Task.FromResult<string?>(SetText(call.Rest))),
        new("match", "match [text]              try the regex (on the text given, or the test text) and list the matches", call => Task.FromResult<string?>(MatchCommand(call.Rest))),
        new("replace", "replace <replacement>    replace the matches in the test text and show the result", call => Task.FromResult<string?>(ReplaceCommand(call.Rest))),
        new("split", "split                     cut the test text at every match", _ => Task.FromResult<string?>(SplitCommand())),
        new("explain", "explain                   explain the pattern piece by piece", _ => Task.FromResult<string?>(RegexExplainer.ToText(RegexExplainer.Explain(_doc.Pattern, _doc.Flags)))),
        new("lib", "lib [name]                list the library, or open an entry or a kept regex", call => Task.FromResult<string?>(LibCommand(call.Rest))),
        new("save", "save [name]               keep the regex in the Silo", call => Task.FromResult<string?>(SaveCommand(call.Rest))),
        new("code", "code <language>           the regex as code: " + string.Join(", ", RegexCode.Languages), call => Task.FromResult<string?>(CodeCommand(call.Rest))),
        new("bench", "bench                     time the regex on the text", _ => Task.FromResult<string?>(BenchText())),
        new("json", "json                      the regex document as JSON", _ => Task.FromResult<string?>(JsonConvert.SerializeObject(_doc, Formatting.Indented)))
    ];

    private void NewRegex()
    {
        if (_dirty && !LabUi.Confirm("This regex has changes that are not kept in the Silo. Start a new one anyway?", "Regex Lab")) return;

        _started = false;
        _ = OpenDefaultAsync();
    }

    private void CopyText(string text, string what)
    {
        LabUi.Copy(text);
        Status = $"Copied the {what}";
    }

    private void CopyMatches()
    {
        LabUi.Copy(string.Join("\r\n", _run.Matches.Select(m => m.Value)));
        Status = $"Copied {_run.Matches.Count:N0} match(es)";
    }

    private void SaveRegex()
    {
        string? name = LabUi.Ask("Save regex", "What is it called?", string.IsNullOrEmpty(_doc.Name) ? "My regex" : _doc.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        _doc.Name = name.Trim();
        Status    = SaveNow();
        RaiseValueChanged();
        RaiseActionsChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string SaveNow()
    {
        try
        {
            RegexStore.Save(_doc);
            _dirty = false;
            return $"Kept {_doc.Name} in the Silo";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    private void DeleteSaved()
    {
        if (!LabUi.Confirm($"Remove '{_doc.Name}' from the Silo?", "Regex Lab")) return;

        Status = RegexStore.Delete(_doc.Name) ? $"Removed {_doc.Name} from the Silo" : $"{_doc.Name} was not kept";
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseActionsChanged();
    }

    private void SendToSyntaxLab()
    {
        var lab = LabHost.Labs.FirstOrDefault(l => l.Id == "syntax");
        if (lab is null)
        {
            Status = "The Syntax Lab is not available";
            return;
        }

        // the Syntax Lab reads a pattern as a whole-text match, so the options of the regex are written into it as inline options
        string inline  = string.Concat(_doc.Flags.Where(c => "imsnx".Contains(c)));
        string pattern = inline.Length == 0 ? _doc.Pattern : $"(?{inline}:{_doc.Pattern})";

        string name    = string.IsNullOrEmpty(_doc.Name) ? "New syntax" : _doc.Name;
        string? example = _run.Matches.Count > 0 ? _run.Matches[0].Value : null;
        var record = SyntaxRecord.ForPattern(name.Replace(' ', '_'), pattern, null, _doc.Description.Length == 0 ? null : _doc.Description, example);
        LabHost.Open(lab, record);
        Status = "Opened the Syntax Lab";
    }

    private void Benchmark()
    {
        Status = BenchText();
    }

    private string BenchText()
    {
        var (runs, micros, error) = RegexEngine.Benchmark(_doc, TimeSpan.FromMilliseconds(250), Lab_.Timeout);
        if (error is not null) return error;
        return $"{micros:0.##} µs per run over {_doc.TestText.Length:N0} character(s) ({runs:N0} runs)";
    }

    // ---- terminal --------------------------------------------------------------------------------

    private string SetPattern(string text)
    {
        if (text.Length == 0) return _doc.Pattern.Length == 0 ? "(no pattern)" : _doc.Pattern;

        EditNow(d => d.Pattern = text);
        Loaded?.Invoke(this, EventArgs.Empty);
        return _run.Summary;
    }

    private string SetFlags(string text)
    {
        string flags = RegexFlags.Normalize(text);
        if (text.Trim().Length > 0 && flags.Length == 0 && text.Trim() != "-") return "No such options. " + string.Join(" ", RegexFlags.All.Select(f => $"{f.Letter}={f.Name}"));

        EditNow(d => d.Flags = text.Trim() == "-" ? string.Empty : flags);
        Loaded?.Invoke(this, EventArgs.Empty);
        return $"Options: {(_doc.Flags.Length == 0 ? "none" : _doc.Flags)}. {_run.Summary}";
    }

    private string SetText(string text)
    {
        EditNow(d => d.TestText = text.Replace("\\n", "\n").Replace("\\t", "\t"));
        Loaded?.Invoke(this, EventArgs.Empty);
        return _run.Summary;
    }

    private string MatchCommand(string text)
    {
        var probe = text.Length > 0 ? new RegexDoc { Pattern = _doc.Pattern, Flags = _doc.Flags, TestText = text.Replace("\\n", "\n") } : _doc;
        var run   = RegexEngine.Run(probe, Lab_.Timeout, Lab_.MaxMatches);
        if (!run.IsValid || run.TimedOut || run.Matches.Count == 0) return run.Summary;

        var lines = new StringBuilder();
        lines.AppendLine(run.Summary);
        foreach (var m in run.Matches.Take(50))
        {
            lines.Append($"{m.Number,3}. [{m.Index}..{m.Index + m.Length}) \"{LabUi.Shorten(m.Value, 60)}\"");
            foreach (var g in m.Groups.Where(g => g.Number != 0 && g.Success)) lines.Append($"  {g.Name}=\"{LabUi.Shorten(g.Value, 30)}\"");
            lines.AppendLine();
        }

        if (run.Matches.Count > 50) lines.AppendLine($"… and {run.Matches.Count - 50:N0} more");
        return lines.ToString().TrimEnd();
    }

    private string ReplaceCommand(string replacement)
    {
        EditNow(d => d.Replacement = replacement);
        var (text, error) = RegexEngine.Replace(_doc, Lab_.Timeout);
        return error ?? text ?? string.Empty;
    }

    private string SplitCommand()
    {
        var (parts, error) = RegexEngine.Split(_doc, Lab_.Timeout);
        return error ?? string.Join('\n', parts!.Select((p, i) => $"{i + 1,3}. \"{LabUi.Shorten(p, 80)}\""));
    }

    private string LibCommand(string name)
    {
        if (name.Length == 0)
            return string.Join('\n', RegexLibrary.Entries.Select(e => $"  {e.Name,-30} {e.Description}")) +
                   (RegexStore.Names().Count > 0 ? "\nKept in the Silo: " + string.Join(", ", RegexStore.Names()) : string.Empty);

        if (RegexStore.Load(name) is { } kept)
        {
            Replace(kept);
            return $"Opened {name} from the Silo";
        }

        if (RegexLibrary.Find(name) is { } entry)
        {
            Replace(RegexLibrary.ToDoc(entry));
            return $"Opened {entry.Name}";
        }

        return $"There is no entry or kept regex called '{name}'";
    }

    private string SaveCommand(string name)
    {
        if (name.Length > 0) _doc.Name = name;
        if (string.IsNullOrWhiteSpace(_doc.Name)) return "Give it a name: save <name>";

        string result = SaveNow();
        RaiseValueChanged();
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private string CodeCommand(string language)
    {
        string? found = RegexCode.Languages.FirstOrDefault(l => string.Equals(l, language.Trim(), StringComparison.OrdinalIgnoreCase));
        return found is null ? "Languages: " + string.Join(", ", RegexCode.Languages) : RegexCode.Generate(found, _doc);
    }

    public override UIElement BuildMainView()
    {
        return new RegexView(this);
    }
}
