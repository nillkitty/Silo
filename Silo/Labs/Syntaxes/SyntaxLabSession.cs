using System.Text;
using Silo.Labs.Regexes;
using Silo.Providers.Data;
using Silo.Providers.Definitions;
using SyntaxKind = Silo.DbModel.Meta.SyntaxKind;

namespace Silo.Labs.Syntaxes;

/// <summary>One Syntax Lab window: the syntax being built, the strings it is tried on, what each came to, and the actions on it.</summary>
public sealed partial class SyntaxLabSession : LabSession<SyntaxRecord>, ILabLive
{
    private readonly Debouncer          _rebuild;
    private readonly List<LabInspector> _inspectors;
    private          SyntaxDraft        _draft = new();
    private          SyntaxBook         _book  = new();
    private          SyntaxBuild        _build = new(null, []);
    private          IReadOnlyList<TrialResult> _results = [];
    private          string             _savedName = string.Empty;
    private          bool               _dirty;
    private          bool               _started;

    public SyntaxLabSession(SyntaxLab lab) : base(lab)
    {
        _rebuild = new Debouncer(TimeSpan.FromMilliseconds(200), Rebuild);
        _inspectors = [new SyntaxDetailsInspector(this).OwnedBy(LabOwner.OfAspect("details"))];
    }

    /// <summary>The syntax as it is now.</summary>
    public SyntaxDraft Draft => _draft;

    /// <summary>The strings it is tried on.</summary>
    public SyntaxBook Book => _book;

    /// <summary>What building the syntax came to.</summary>
    public SyntaxBuild Build => _build;

    /// <summary>What each string came to.</summary>
    public IReadOnlyList<TrialResult> Results => _results;

    public bool IsDirty => _dirty;

    /// <summary>Whether the syntax is one the Silo has (it has an id).</summary>
    public bool IsSaved => _draft.Id != 0;

    /// <summary>The syntax or what it came to changed (what the views draw).</summary>
    public event EventHandler? Changed;

    /// <summary>A different syntax was opened: the boxes show it afresh (as opposed to <see cref="Changed" />, which comes after every key typed).</summary>
    public event EventHandler? Loaded;

    public override SyntaxRecord? CurrentValue => _draft.ToRecord();

    public override string ValueText => string.IsNullOrWhiteSpace(_draft.Name) ? "(new syntax)" : "syntax:" + _draft.Name;

    public override string Title => $"{LabOf.Name} - {(string.IsNullOrWhiteSpace(_draft.Name) ? "new syntax" : _draft.Name)}{(_dirty ? " *" : string.Empty)}";

    private SyntaxLab Lab_ => (SyntaxLab)LabOf;

    /// <summary>3: the panes belong to aspects of the Lab's model now; windows saved with the old arrangement start afresh.</summary>
    public override int LayoutVersion => 3;

    public override IReadOnlyList<LabInspector> Inspectors => _inspectors;

    /// <summary>How many of the strings that were expected to be valid or invalid came out that way, as (passed, expected).</summary>
    public (int Passed, int Expected) Score
    {
        get
        {
            var expected = _results.Where(r => r.Expect != TrialExpect.Nothing).ToList();
            return (expected.Count(r => r.Passed), expected.Count);
        }
    }

    // ---- opening ---------------------------------------------------------------------------------

    protected override Task OpenValueAsync(SyntaxRecord value)
    {
        Replace(value, SyntaxSilo.LoadBook(value.Name));
        return Task.CompletedTask;
    }

    protected override Task OpenDefaultAsync()
    {
        if (_started) return Task.CompletedTask;

        Replace(Lab_.Blank(), new SyntaxBook());
        return Task.CompletedTask;
    }

    protected override void OnAttached()
    {
        if (!_started) _ = OpenDefaultAsync();
    }

    protected override void OnDispose()
    {
        _rebuild.Cancel();
    }

    /// <summary>Starts again from another syntax; the boxes show it.</summary>
    public void Replace(SyntaxRecord record, SyntaxBook? book = null)
    {
        _started   = true;
        _draft     = SyntaxDraft.From(record);
        _book      = book ?? SyntaxSilo.LoadBook(record.Name);
        _savedName = record.Id == 0 ? string.Empty : record.Name;
        _dirty     = false;
        Rebuild();
        Loaded?.Invoke(this, EventArgs.Empty);
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    // ---- editing ---------------------------------------------------------------------------------

    /// <summary>Changes the syntax; it is built again a moment after the last change.</summary>
    public void Edit(Action<SyntaxDraft> change)
    {
        change(_draft);
        Touched();
    }

    /// <summary>Changes the syntax and builds it at once.</summary>
    public void EditNow(Action<SyntaxDraft> change)
    {
        _rebuild.Cancel();
        change(_draft);
        _dirty = true;
        Rebuild();
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    /// <summary>Changes the strings the syntax is tried on.</summary>
    public void EditBook(Action<SyntaxBook> change)
    {
        change(_book);
        Touched();
    }

    public void EditBookNow(Action<SyntaxBook> change)
    {
        _rebuild.Cancel();
        change(_book);
        _dirty = true;
        Rebuild();
        Loaded?.Invoke(this, EventArgs.Empty);
        RaiseActionsChanged();
    }

    private void Touched()
    {
        _dirty = true;
        _rebuild.Poke();
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    private void Rebuild()
    {
        _rebuild.Cancel();
        _build   = SyntaxTrial.Build(_draft, SyntaxSilo.All());
        _results = _build.Syntax is { } syntax ? SyntaxTrial.RunAll(syntax, _book) : [];
        Status   = Summary();
        Changed?.Invoke(this, EventArgs.Empty);
        RaisePartsChanged();
    }

    /// <summary>One line on where the syntax stands.</summary>
    public string Summary()
    {
        if (!_build.IsBuilt) return _build.Problems.Count > 0 ? _build.Problems[0] : "The syntax cannot be built.";

        var (passed, expected) = Score;
        string samples = expected == 0 ? string.Empty : passed == expected ? $" All {expected} sample(s) came out as expected." : $" {expected - passed} of {expected} sample(s) did not come out as expected.";
        string problems = _build.Problems.Count > 0 ? $" {_build.Problems.Count} problem(s) to fix before it can be saved." : string.Empty;
        var type = _build.Syntax!.ValueType;
        return $"Built: it makes {(type == typeof(string) ? "text" : "a " + (type.IsArray ? type.GetElementType()!.Name + "[]" : type.Name))}.{samples}{problems}";
    }

    // ---- saving ----------------------------------------------------------------------------------

    /// <summary>Keeps the syntax and its samples in the Silo. Returns what happened, as a sentence.</summary>
    public string Save()
    {
        var store = SyntaxSilo.Store;
        if (store is null) return "Open a Silo to keep the syntax in.";

        try
        {
            var saved = store.SaveSyntax(_draft.ToRecord());
            string name = saved.Name;

            // the samples follow the name
            if (_savedName.Length > 0 && !string.Equals(_savedName, name, StringComparison.OrdinalIgnoreCase)) SyntaxSilo.DeleteBook(_savedName);
            SyntaxSilo.SaveBook(name, _book);

            _draft     = SyntaxDraft.From(saved);
            _savedName = name;
            _dirty     = false;
            Rebuild();
            Loaded?.Invoke(this, EventArgs.Empty);
            RaiseValueChanged();
            RaiseActionsChanged();
            return $"Kept {name} in the Silo";
        }
        catch (DefinitionException ex)
        {
            return ex.Message;
        }
    }

    private void SaveSyntax()
    {
        if (string.IsNullOrWhiteSpace(_draft.Name))
        {
            string? name = LabUi.Ask("Save syntax", "What is it called?", "NewSyntax");
            if (string.IsNullOrWhiteSpace(name)) return;

            _draft.Name = name.Trim();
            Rebuild();
            Loaded?.Invoke(this, EventArgs.Empty);
        }

        Status = Save();
    }

    private void DeleteSyntax()
    {
        var store = SyntaxSilo.Store;
        if (store is null || !IsSaved) return;
        if (!LabUi.Confirm($"Delete the syntax '{_draft.Name}' from the Silo? Anything that uses it stops being understood.", "Syntax Lab")) return;

        string name = _draft.Name;
        try
        {
            store.DeleteSyntax(_draft.Id);
        }
        catch (DefinitionException ex)
        {
            Status = ex.Message;
            return;
        }

        SyntaxSilo.DeleteBook(name);
        _draft.Id  = 0;
        _savedName = string.Empty;
        _dirty     = true;
        Rebuild();
        RaiseActionsChanged();
        Status = $"Deleted {name} from the Silo (the draft is still here)";
    }

    private void NewSyntax()
    {
        if (_dirty && !LabUi.Confirm("This syntax has changes that are not kept in the Silo. Start a new one anyway?", "Syntax Lab")) return;

        Replace(Lab_.Blank(), new SyntaxBook());
    }

    private void OpenSyntax(string nameOrAlias)
    {
        if (SyntaxSilo.Find(nameOrAlias) is { } record) Replace(record);
        else Status = $"The Silo has no syntax called '{nameOrAlias}'";
    }

    /// <summary>Opens a syntax of the Silo in this window (asks first when there are changes).</summary>
    public void Open(SyntaxRecord record)
    {
        if (_dirty && !LabUi.Confirm("This syntax has changes that are not kept in the Silo. Open another anyway?", "Syntax Lab")) return;

        Replace(record);
        RememberRecent(record.Name);
    }

    // ---- library, recent and problems (the panes every Lab window has) -------------------------------

    protected override bool RecentOnOpen => false;

    public override bool LibraryOpensWithWindow => true;

    public override Task OpenRecentAsync(string text)
    {
        if (SyntaxSilo.Find(text) is not { } record) throw new InvalidOperationException($"The Silo has no syntax called '{text}'.");

        Open(record);
        return Task.CompletedTask;
    }

    public override IReadOnlyList<LabLibrarySection> LibrarySections(string filter)
    {
        bool Wanted(string a, string? b) => filter.Length == 0 || a.Contains(filter, StringComparison.OrdinalIgnoreCase) || (b?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

        var sections = new List<LabLibrarySection>();
        if (!SyntaxSilo.IsAvailable)
        {
            sections.Add(new LabLibrarySection("In the Silo", "Database5", [], Note: "No Silo is open"));
        }
        else
        {
            var silo = SyntaxSilo.All().Where(x => Wanted(x.Name, x.Alias) || Wanted(x.Pattern ?? string.Empty, x.Parser))
                                 .Select(x =>
                                         {
                                             bool parser  = x.Kind == SyntaxKind.Parser;
                                             string what  = parser ? x.Parser ?? string.Empty : x.Pattern ?? string.Empty;
                                             bool current = x.Id == _draft.Id && x.Id != 0;
                                             return new LabLibraryEntry(x.Name, parser ? "Parser" : "Pattern", LabUi.Shorten(what, 36), parser ? "Modules" : "Code", () => Open(x), current, ToolTip: x.Description);
                                         })
                                 .ToList();
            sections.Add(new LabLibrarySection("In the Silo", "Database5", silo));
        }

        // the built-in parsers are prefabs of the model now (File > New Syntax)
        return sections;
    }

    protected override IEnumerable<LabProblem> Validate()
    {
        if (!_build.IsBuilt)
            foreach (string problem in _build.Problems)
                yield return new LabProblem(LabProblemSeverity.Error, "Syntax", problem);
    }

    /// <summary>Starts a syntax that uses a built-in parser (and has its example as a sample).</summary>
    public void StartFromParser(SyntaxParser parser)
    {
        if (_dirty && !LabUi.Confirm("This syntax has changes that are not kept in the Silo. Start another anyway?", "Syntax Lab")) return;

        var record = SyntaxRecord.ForParser(Capitalize(parser.Key), parser.Key, null, parser.Description, parser.Example);
        Replace(record, new SyntaxBook { Valid = SyntaxTrial.Escape(parser.Example) });
        _dirty = true;
        RaiseValueChanged();
    }

    private static string Capitalize(string key) => key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..];

    // ---- actions ---------------------------------------------------------------------------------

    public override IEnumerable<ProvidedCommand> GlobalActions
    {
        get
        {
            yield return ProvidedCommand.From("new-syntax", "_New Syntax", NewSyntax, "TbNew", null, "Start a new syntax");
            yield return ProvidedCommand.From("show-syntaxes", "Show _Syntaxes", () => Host?.ShowInspector("syntaxes"), "TbOpen", null, "Show the syntaxes of the Silo and the built-in parsers");
            yield return ProvidedCommand.From("restore-defaults", "_Restore Predefined Syntaxes", RestoreDefaults, "Refresh", () => SyntaxSilo.IsAvailable,
                                              "Add back the syntaxes that come with Silo and were deleted");
            yield return ProvidedCommand.From("settings", "Open _Settings", () => Host?.ShowInspector(LabParametersInspector.InspectorId), "Settings", null, "Show the Lab's parameters");
        }
    }

    public override IEnumerable<ProvidedCommand> ContextActions
    {
        get
        {
            yield return ProvidedCommand.From("run", "_Run", Rebuild, "Refresh", null, "Build the syntax and try every string again") with { Gesture = "F5" };
            yield return ProvidedCommand.From("keep-valid", "Keep _Valid Tries", () => MoveTries(true), "Check", () => _results.Any(r => r.Expect == TrialExpect.Nothing && r.IsValid),
                                              "Move the strings of the Try list that are valid to the list of strings that have to be valid");
            yield return ProvidedCommand.From("keep-invalid", "Keep _Invalid Tries", () => MoveTries(false), "TbDelete", () => _results.Any(r => r.Expect == TrialExpect.Nothing && !r.IsValid),
                                              "Move the strings of the Try list that are not valid to the list of strings that must not be valid");
        }
    }

    public override IEnumerable<ProvidedCommand> ValueActions
    {
        get
        {
            yield return ProvidedCommand.From("save", "_Save Syntax", SaveSyntax, "TbSave", () => SyntaxSilo.IsAvailable, "Keep the syntax, with its strings, in the Silo") with { Gesture = "Ctrl+S" };
            yield return ProvidedCommand.From("copy-json", "Copy as _JSON", () => CopyText(SyntaxTrial.ToJson(_draft), "JSON"), "TbCopy", null, "Put the syntax as a JSON document on the clipboard");
            yield return ProvidedCommand.From("copy-reference", "Copy _syntax:Name", () => CopyText(ValueText, "reference"), "TbCopy", () => !string.IsNullOrWhiteSpace(_draft.Name),
                                              "Put the way to refer to the syntax in a type on the clipboard");
            yield return ProvidedCommand.From("to-regex", "Edit Pattern in _Regex Lab", SendToRegexLab, "DataSearching", () => _draft.Kind == SyntaxKind.Pattern && !string.IsNullOrEmpty(_draft.Pattern),
                                              "Open the Regex Lab with the pattern and the strings, each tried as a whole line");
            yield return ProvidedCommand.From("delete", "_Delete Syntax", DeleteSyntax, "TbDelete", () => SyntaxSilo.IsAvailable && IsSaved, "Remove the syntax from the Silo");
        }
    }

    private void CopyText(string text, string what)
    {
        LabUi.Copy(text);
        Status = $"Copied the {what}";
    }

    private void RestoreDefaults()
    {
        try
        {
            int added = SyntaxSilo.Store?.RestoreDefaults() ?? 0;
            Status = added == 0 ? "Every predefined syntax is already there" : $"Added back {added} predefined syntax(es)";
            Rebuild();
        }
        catch (DefinitionException ex)
        {
            Status = ex.Message;
        }
    }

    /// <summary>Moves the strings of the Try list that came out valid (or not) into the list they belong in.</summary>
    private void MoveTries(bool valid)
    {
        var moved = _results.Where(r => r.Expect == TrialExpect.Nothing && r.IsValid == valid).Select(r => SyntaxTrial.Escape(r.Text)).Distinct().ToList();
        if (moved.Count == 0) return;

        var keep = _results.Where(r => r.Expect == TrialExpect.Nothing && r.IsValid != valid).Select(r => _book.Escapes ? SyntaxTrial.Escape(r.Text) : r.Text).ToList();
        EditBookNow(b =>
                    {
                        string list = string.Join('\n', moved.Select(m => b.Escapes ? m : SyntaxTrial.Unescape(m)));
                        if (valid) b.Valid = Append(b.Valid, list);
                        else b.Invalid = Append(b.Invalid, list);
                        b.Try = string.Join('\n', keep);
                    });
        Status = $"Moved {moved.Count} string(s) to the {(valid ? "valid" : "invalid")} list";
    }

    private static string Append(string list, string more)
    {
        return string.IsNullOrWhiteSpace(list) ? more : list.TrimEnd('\r', '\n') + "\n" + more;
    }

    private void ExampleToValid()
    {
        if (string.IsNullOrWhiteSpace(_draft.Example)) return;

        EditBookNow(b => b.Valid = Append(b.Valid, b.Escapes ? SyntaxTrial.Escape(_draft.Example) : _draft.Example));
    }

    private void SendToRegexLab()
    {
        var lab = LabHost.Labs.FirstOrDefault(l => l.Id == "regex");
        if (lab is null)
        {
            Status = "The Regex Lab is not available";
            return;
        }

        // a syntax of the Pattern kind has to match all of a string, so each string is a line and the pattern is anchored at both ends of it
        var lines = SyntaxTrial.Lines(_book.Valid, _book.Escapes).Concat(SyntaxTrial.Lines(_book.Invalid, _book.Escapes)).Concat(SyntaxTrial.Lines(_book.Try, _book.Escapes))
                               .Select(l => l.Replace("\r", string.Empty).Replace("\n", " "));
        var doc = new RegexDoc
                  {
                      Name        = _draft.Name,
                      Pattern     = $"^(?:{_draft.Pattern})$",
                      Flags       = "m",
                      Description = _draft.Description ?? string.Empty,
                      TestText    = string.Join('\n', lines)
                  };
        LabHost.Open(lab, doc);
        Status = "Opened the Regex Lab";
    }

    // ---- terminal --------------------------------------------------------------------------------

    public override IReadOnlyList<LabTerminalCommand> TerminalCommands =>
    [
        new("try", "try <text>               build the syntax and see what it makes of the text (\\n and \\t are read)", call => Task.FromResult<string?>(TryCommand(call.Rest))),
        new("name", "name <name>             set the name (or show it)", call => Task.FromResult<string?>(SetText(call.Rest, d => d.Name, (d, v) => d.Name = v ?? string.Empty, "name"))),
        new("alias", "alias <name>            set the alias (- removes it)", call => Task.FromResult<string?>(SetText(call.Rest, d => d.Alias, (d, v) => d.Alias = v, "alias"))),
        new("kind", "kind <pattern|parser>   choose what decides what is valid", call => Task.FromResult<string?>(KindCommand(call.Rest))),
        new("parser", "parser [key]            choose a built-in parser (no key lists them)", call => Task.FromResult<string?>(ParserCommand(call.Rest))),
        new("pattern", "pattern <regex>         set the pattern, which all of a string has to match", call => Task.FromResult<string?>(SetText(call.Rest, d => d.Pattern, (d, v) => d.Pattern = v, "pattern"))),
        new("example", "example <text>          set the example of a valid string", call => Task.FromResult<string?>(SetText(call.Rest, d => d.Example, (d, v) => d.Example = v, "example"))),
        new("save", "save                    keep the syntax in the Silo", _ => Task.FromResult<string?>(Save())),
        new("list", "list                    the syntaxes of the Silo", _ => Task.FromResult<string?>(ListCommand())),
        new("open", "open <name>             open a syntax of the Silo", call => Task.FromResult<string?>(OpenCommand(call.Rest))),
        new("json", "json                    the syntax as a JSON document", _ => Task.FromResult<string?>(SyntaxTrial.ToJson(_draft)))
    ];

    private string TryCommand(string text)
    {
        if (!_build.IsBuilt) return string.Join('\n', _build.Problems);

        string one = SyntaxTrial.Unescape(text);
        var r = SyntaxTrial.Run(_build.Syntax!, one);
        if (!r.IsValid) return "Not valid: " + r.Error;

        return new StringBuilder().AppendLine($"Valid. A {r.TypeName}: {r.Value}")
                                  .AppendLine($"Written back: {(r.Canonical is null ? "(cannot be written back)" : SyntaxTrial.Escape(r.Canonical))}")
                                  .Append(r.RoundTrips ? "It round trips." : "It does not round trip: the written text does not become the same value.").ToString();
    }

    private string SetText(string text, Func<SyntaxDraft, string?> get, Action<SyntaxDraft, string?> set, string what)
    {
        if (text.Length == 0)
        {
            string? now = get(_draft);
            return string.IsNullOrEmpty(now) ? $"(no {what})" : now;
        }

        EditNow(d => set(d, text == "-" ? null : text));
        Loaded?.Invoke(this, EventArgs.Empty);
        return Summary();
    }

    private string KindCommand(string text)
    {
        if (!Enum.TryParse<SyntaxKind>(text.Trim(), true, out var kind)) return $"The kind is {_draft.Kind}. Say 'kind pattern' or 'kind parser'.";

        EditNow(d => d.Kind = kind);
        Loaded?.Invoke(this, EventArgs.Empty);
        return $"The kind is {kind}. {Summary()}";
    }

    private string ParserCommand(string key)
    {
        if (key.Length == 0)
            return string.Join('\n', SyntaxParsers.All.Select(p => $"  {p.Key,-18} {p.Title} ({p.ValueType.Name}), like {LabUi.Shorten(p.Example, 30)}"));

        if (SyntaxParsers.Find(key.Trim()) is not { } parser) return $"There is no built-in parser '{key}'. Say 'parser' to list them.";

        EditNow(d =>
                {
                    d.Kind   = SyntaxKind.Parser;
                    d.Parser = parser.Key;
                    d.Example ??= parser.Example;
                });
        Loaded?.Invoke(this, EventArgs.Empty);
        return Summary();
    }

    private static string ListCommand()
    {
        var all = SyntaxSilo.All();
        if (all.Count == 0) return SyntaxSilo.IsAvailable ? "The Silo has no syntaxes." : "No Silo is open.";

        return string.Join('\n', all.Select(s => $"  {s.Name,-22} {(s.Kind == SyntaxKind.Parser ? "parser " + s.Parser : "pattern " + LabUi.Shorten(s.Pattern ?? string.Empty, 40))}"));
    }

    private string OpenCommand(string name)
    {
        if (name.Length == 0) return "Which one? open <name>";

        OpenSyntax(name);
        return Status;
    }

    public override UIElement BuildMainView()
    {
        return new SyntaxView(this);
    }
}
