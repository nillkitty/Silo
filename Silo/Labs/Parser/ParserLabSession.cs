using System.Diagnostics;
using Newtonsoft.Json;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Silo.Providers.Data;

namespace Silo.Labs.Parser;

/// <summary>One window of the Parser Lab: the spec being built, its canvas, its inspectors and its commands.</summary>
public sealed class ParserLabSession : LabSession<IDataSpec>
{
    private readonly List<LabInspector> _inspectors;
    private readonly ParserWorkspace    _ws;
    private readonly SamplesInspector   _samples;
    private readonly TestsInspector     _tests;
    private          SpecCanvasView?    _canvas;
    private          bool               _disposed;
    private          bool               _syncing;

    public override IEnumerable<ProvidedCommand> ContextActions
    {
        get
        {
            yield return ProvidedCommand.From("undo", "_Undo", _ws.Undo, "Undo", () => _ws.CanUndo && !TextHasFocus, "Take back the last change to the spec") with
                         {
                             Gesture = "Ctrl+Z"
                         };
            yield return ProvidedCommand.From("redo", "_Redo", _ws.Redo, "TbRedo", () => _ws.CanRedo && !TextHasFocus, "Do again what was taken back") with
                         {
                             Gesture = "Ctrl+Y"
                         };
        }
    }

    public override IDataSpec? CurrentValue => _ws.Spec;

    /// <summary>True when the keyboard is in a box or a list of the panes, where Cut, Copy, Paste, Delete and Undo mean what they mean to that control.</summary>
    private static bool TextHasFocus => Keyboard.FocusedElement is TextBoxBase or ComboBox or ListBoxItem or TreeViewItem or PasswordBox;

    // ---- actions ---------------------------------------------------------------------------------

    public override IEnumerable<ProvidedCommand> GlobalActions
    {
        get
        {
            yield return ProvidedCommand.From("settings", "Parser Lab _Preferences", () => Host?.ShowInspector(LabParametersInspector.InspectorId),
                                              "Sliders",
                                              null,
                                              "Show the Lab's parameters: the spec's name, OID, CLR type, what it converts to, and what it ignores and ends on");
        }
    }

    // ---- menus -----------------------------------------------------------------------------------

    private ProvidedCommand NewSpecCommand() =>
        ProvidedCommand.From("new-spec", "_New Spec...", NewSpec, "TbNew", null, "Start a new data specification") with { Gesture = "Ctrl+N" };

    private ProvidedCommand OpenSpecCommand()
    {
        var names = SpecStore.IsAvailable ? SpecStore.Names() : [];
        if (names.Count == 0)
            return ProvidedCommand.From("open-spec", "_Open Spec...", () => Host?.ShowInspector("syntax"), "TbOpen", null,
                                        "Show the specs kept in the Silo (the Syntax pane): double-click one to open it");

        return ProvidedCommand.Submenu("open-spec", "_Open Spec", names.Select(n => ProvidedCommand.From("open:" + n, n.Replace("_", "__"), () => OpenSpec(n), "Data1", null, $"Open {n}")),
                                       null, "TbOpen", "Open a spec kept in the Silo");
    }

    private ProvidedCommand SaveCommand() =>
        ProvidedCommand.From("save", "_Save Spec", Save, "TbSave", () => SpecStore.IsAvailable, "Keep the spec, its samples and its tests in the Silo") with { Gesture = "Ctrl+S" };

    private ProvidedCommand BuildCommand() =>
        ProvidedCommand.From("build", "_Build", Build, "Build", null, "Check the rules and make the parsers from them, reporting anything wrong") with { Gesture = "Ctrl+B" };

    private ProvidedCommand CompileCommand() =>
        ProvidedCommand.From("compile", "_Compile", CompileFunction, "Compile", null, "Make the parsers a function that is kept, and time it on a sample") with { Gesture = "Ctrl+Shift+B" };

    private ProvidedCommand RunAllCommand() =>
        ProvidedCommand.From("run-tests", "Run _All Tests", () => RunTests(false), "RunAll", () => _ws.Book.Tests.Count > 0, "Run every test") with { Gesture = "F5" };

    private ProvidedCommand RunFailingCommand() =>
        ProvidedCommand.From("run-failing", "Run _Failing Tests", () => RunTests(true), "RunFailing", () => _ws.Results.Values.Any(r => r.State == TestState.Failed),
                             "Run again the tests that failed") with { Gesture = "Shift+F5" };

    public override IEnumerable<LabMenu> Menus
    {
        get
        {
            bool tokenizer = _ws.Doc.Mode == SpecMode.Tokenizer;
            return
            [
                new LabMenu("_File",
                [
                    NewSpecCommand(),
                    OpenSpecCommand(),
                    null,
                    SaveCommand(),
                    ProvidedCommand.From("delete-spec", "_Delete Saved Spec", DeleteSaved, "TbDelete", () => SpecStore.IsAvailable && SpecStore.Exists(_ws.Doc.Name),
                                         "Remove the spec (and its samples and tests) from the Silo") with { IsDestructive = true }
                ]),
                new LabMenu("_Edit",
                [
                    _undoCommand(),
                    _redoCommand(),
                    null,
                    ProvidedCommand.From("cut", "Cu_t", () => _ws.CutSelection(), "TbCut", () => !TextHasFocus && _ws.HasSelection, "Cut the selected piece or matcher") with { Gesture = "Ctrl+X" },
                    ProvidedCommand.From("copy", "_Copy", () => _ws.CopySelection(), "TbCopy", () => !TextHasFocus && _ws.HasSelection, "Copy the selected piece or matcher") with { Gesture = "Ctrl+C" },
                    ProvidedCommand.From("paste", "_Paste", () => _ws.PasteSelection(), "TbPaste", () => !TextHasFocus && _ws.CanPaste, "Paste what was copied into or after the selection") with { Gesture = "Ctrl+V" },
                    null,
                    ProvidedCommand.From("delete", "_Delete", () => _ws.DeleteSelection(), "TbDelete", () => !TextHasFocus && _ws.HasSelection, "Remove the selected piece or matcher") with { Gesture = "Delete", IsDestructive = true },
                    null,
                    ProvidedCommand.From("duplicate", "D_uplicate", () => _ws.DuplicateSelection(), "TbCopy", () => !TextHasFocus && _ws.HasSelection, "Copy the selected piece and put the copy beside it"),
                    ProvidedCommand.From("duplicate-rule", "Duplicate _Rule", DuplicateRule, "Rule", () => !tokenizer && _ws.Current is not null, "Copy the current rule under a new name"),
                    null,
                    ProvidedCommand.From("copy-json", "Copy Spec as _JSON", CopyJson, "TbExport", null, "Put the spec on the clipboard as JSON")
                ]),
                new LabMenu("_Spec",
                [
                    ProvidedCommand.From("parser-mode", "_Parser Mode", () => SetMode(SpecMode.Parser), "Rule", null, "Build rules over characters") with { IsChecked = () => _ws.Doc.Mode == SpecMode.Parser },
                    ProvidedCommand.From("tokenizer-mode", "T_okenizer Mode", () => SetMode(SpecMode.Tokenizer), "Token", null, "Build the tokenizer that cuts text into tokens") with { IsChecked = () => _ws.Doc.Mode == SpecMode.Tokenizer },
                    null,
                    ProvidedCommand.From("separator-settings", "Se_parator and Ignored Tokens...", () => Host?.ShowInspector(LabParametersInspector.InspectorId), "Separator", null,
                                         "What the parser skips between tokens and what ends it: in the Lab's parameters"),
                    null,
                    BuildCommand(),
                    CompileCommand()
                ]),
                new LabMenu("_Rules",
                [
                    ProvidedCommand.From("add-rule", "Add _Rule...", AddRule, "Plus", () => _ws.Doc.Mode == SpecMode.Parser, "Add a rule to the spec"),
                    ProvidedCommand.From("add-token-rule", "Add _Tokenizer Rule", () => _ws.AddTokenRule(), "Plus", () => _ws.Doc.Mode == SpecMode.Tokenizer, "Add a matcher to the tokenizer"),
                    null,
                    ProvidedCommand.From("configure-rule", "_Configure Rule", () => _canvas?.ConfigureRule(), "Sliders", () => _ws.Current is not null || tokenizer,
                                         "Show the rule and put the cursor in its settings") with { Gesture = "F4" },
                    ProvidedCommand.From("rename-rule", "Re_name Rule...", RenameRule, "Rule", () => !tokenizer && _ws.Current is not null, "Give the current rule another name (and everything that uses it)"),
                    null,
                    ProvidedCommand.From("delete-rule", "_Delete Rule", DeleteRule, "TbDelete", () => !tokenizer && _ws.Current is not null, "Remove the current rule") with { IsDestructive = true }
                ]),
                new LabMenu("_Tests",
                [
                    ProvidedCommand.From("add-sample", "Add _Sample", () => { Host?.ShowInspector("samples"); _samples.FocusAdd(); }, "Droplet", null, "Show the Samples pane with the cursor in its box"),
                    ProvidedCommand.From("paste-samples", "Paste to Sa_mples", PasteSamples, "TbPaste", null, "Add each line on the clipboard as a sample"),
                    null,
                    ProvidedCommand.From("add-test", "Add _Test...", () => { Host?.ShowInspector("tests"); _tests.NewTest(); }, "Flask", null, "Make a test from the samples: what is expected of them"),
                    ProvidedCommand.From("run-test", "_Run Test", () => { Host?.ShowInspector("tests"); _tests.RunSelected(); }, "Run", () => _ws.Book.Tests.Count > 0, "Run the test selected in the Tests pane"),
                    null,
                    RunAllCommand(),
                    RunFailingCommand()
                ])
            ];
        }
    }

    private ProvidedCommand _undoCommand() => ContextActions.First(c => c.Id == "undo");

    private ProvidedCommand _redoCommand() => ContextActions.First(c => c.Id == "redo");

    public override IReadOnlyList<LabInspector> Inspectors => _inspectors;

    // ---- terminal --------------------------------------------------------------------------------

    public override IReadOnlyList<LabTerminalCommand> TerminalCommands =>
    [
        new("parse", "parse \"text\" [as rule|syntax:x|enum:X|type:T|tokens] [as format]   try text on the spec",
            call => Task.FromResult<string?>(ParserRepl.Run(_ws, call.Rest))),
        new("tokens", "tokens <text>              cut text into tokens with the tokenizer rules",
            call => Task.FromResult<string?>(Tokens(call.Rest))),
        new("rules", "rules                      list the rules (or the tokenizer's matchers)", _ => Task.FromResult<string?>(ListRules())),
        new("spec", "spec list|new <name>|open <name>|save|delete <name>|json   work with the specs kept in the Silo",
            call => Task.FromResult<string?>(Spec(call.Args))),
        new("test", "test [failing|<name>]       run all the tests, the failing ones, or one",
            call => Task.FromResult<string?>(Test(call.Args))),
        new("sample", "sample <set> <text>        add a sample to a set (made if it is new)",
            call => Task.FromResult<string?>(Sample(call))),
        new("mode", "mode parser|tokenizer      which part of the spec the canvas builds",
            call => Task.FromResult<string?>(Mode(call.Args)))
    ];

    public override string Title => $"{LabOf.Name} - {ValueText}{(_ws.IsDirty ? " *" : string.Empty)}";

    public override IEnumerable<ProvidedCommand> ValueActions
    {
        get
        {
            yield return SaveCommand();
            yield return BuildCommand();
            yield return CompileCommand();
            yield return RunAllCommand();
            yield return RunFailingCommand();
        }
    }

    public override int LayoutVersion => 2;

    public override string ValueText => _ws.Spec.Name;

    private LabParameterSet Parameters => LabOf.Parameters;

    // ---- opening ---------------------------------------------------------------------------------

    protected override Task OpenValueAsync(IDataSpec value)
    {
        if (value is ParserDataSpec ours)
        {
            _ws.Open(ours.Doc.Clone(), SpecStore.Exists(ours.Name) ? SpecStore.LoadBook(ours.Name) : new SpecTestBook());
        }
        else
        {
            var doc = ParserWorkspace.NewDoc(value.Name);
            doc.Oid        = value.Oid;
            doc.ClrType    = value.ClrType?.FullName;
            doc.ConvertsTo = [.. value.ConvertsTo];
            doc.Ignore     = [.. value.IgnoredTokens];
            doc.ExitOn     = [.. value.ExitTokens];
            _ws.Open(doc);
            Status = $"{value.Name} is not a spec this Lab made: only its name, OID, types and tokens were taken";
        }

        RaiseValueChanged();
        return Task.CompletedTask;
    }

    protected override Task OpenDefaultAsync()
    {
        // the spec the parameters name, if it is kept in the Silo: where the last session left off
        string name = Parameters[ParserLabParameters.SpecName].Text.Trim();
        if (name.Length > 0 && SpecStore.Exists(name) && !_ws.IsDirty) _ws.Load(name);
        RaiseValueChanged();
        return Task.CompletedTask;
    }

    public override UIElement BuildMainView()
    {
        _canvas = new SpecCanvasView(_ws, Build, CompileFunction, () => RunTests(false), id => Host?.ShowInspector(id));
        return _canvas;
    }

    private bool ConfirmDiscard()
    {
        return !_ws.IsDirty || ParserUi.Confirm($"'{_ws.Spec.Name}' has changes that are not kept in the Silo. Discard them?");
    }

    // ---- parameters <-> spec ---------------------------------------------------------------------

    private ParserSpecDoc DocFromParameters()
    {
        var doc = new ParserSpecDoc();
        ApplyParameters(doc);
        return doc;
    }

    private void ApplyParameters(ParserSpecDoc doc)
    {
        var p = Parameters;
        doc.Name                = p[ParserLabParameters.SpecName].Text.Trim();
        doc.Oid                 = p[ParserLabParameters.SpecOid].Text.Trim() is { Length: > 0 } oid ? oid : null;
        doc.ClrType             = p[ParserLabParameters.ClrType].Text.Trim() is { Length: > 0 } clr ? clr : null;
        doc.ConvertsTo          = [.. p[ParserLabParameters.ConvertsTo].Lines];
        doc.Ignore              = [.. p[ParserLabParameters.Ignore].Lines];
        doc.ExitOn              = [.. p[ParserLabParameters.ExitOn].Lines];
        doc.Mode                = Enum.TryParse<SpecMode>(p[ParserLabParameters.Mode].Text, true, out var mode) ? mode : SpecMode.Parser;
        doc.Tokenizer.TokenType = p[ParserLabParameters.TokenType].Text.Trim() is { Length: > 0 } type ? type : "Token";
    }

    private bool ParametersMatch(ParserSpecDoc doc)
    {
        var probe = new ParserSpecDoc();
        ApplyParameters(probe);
        return probe.Name                == doc.Name && probe.Oid == doc.Oid && probe.ClrType == doc.ClrType && probe.Mode == doc.Mode &&
               probe.Tokenizer.TokenType == doc.Tokenizer.TokenType && probe.ConvertsTo.SequenceEqual(doc.ConvertsTo) &&
               probe.Ignore.SequenceEqual(doc.Ignore) && probe.ExitOn.SequenceEqual(doc.ExitOn);
    }

    /// <summary>The parameters were changed (in the pane or with <c>set</c>): the spec follows them.</summary>
    private void OnParametersChanged(LabParameter? changed)
    {
        if (_syncing || _disposed) return;

        var app = Application.Current;
        if (app is null) return;

        app.Dispatcher.InvokeAsync(() =>
                                   {
                                       if (_syncing || _disposed || ParametersMatch(_ws.Doc)) return;

                                       _syncing = true;
                                       try
                                       {
                                           _ws.Edit(ApplyParameters, WorkspaceChange.Header);
                                       }
                                       finally
                                       {
                                           _syncing = false;
                                       }
                                   });
    }

    /// <summary>The spec was changed (a header edited, another spec opened): the parameters show it.</summary>
    private void PushParameters()
    {
        var doc = _ws.Doc;
        if (ParametersMatch(doc)) return;

        _syncing = true;
        try
        {
            Parameters[ParserLabParameters.SpecName].Value   = doc.Name;
            Parameters[ParserLabParameters.SpecOid].Value    = doc.Oid     ?? string.Empty;
            Parameters[ParserLabParameters.ClrType].Value    = doc.ClrType ?? string.Empty;
            Parameters[ParserLabParameters.ConvertsTo].Value = doc.ConvertsTo.ToArray();
            Parameters[ParserLabParameters.Ignore].Value     = doc.Ignore.ToArray();
            Parameters[ParserLabParameters.ExitOn].Value     = doc.ExitOn.ToArray();
            Parameters[ParserLabParameters.Mode].Value       = doc.Mode.ToString();
            Parameters[ParserLabParameters.TokenType].Value  = doc.Tokenizer.TokenType;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnWorkspaceChanged(WorkspaceChange change)
    {
        if ((change & (WorkspaceChange.Header | WorkspaceChange.Loaded)) != 0) PushParameters();
        if ((change & (WorkspaceChange.Header | WorkspaceChange.Loaded | WorkspaceChange.Rules | WorkspaceChange.Saved |
                       WorkspaceChange.Book)) != 0) RaiseValueChanged();
        if ((change & (WorkspaceChange.Header | WorkspaceChange.Loaded | WorkspaceChange.Rules)) != 0) RaiseProblemsChanged();

        // the status line says how the tests went whenever they were run (from a button, the menu, the Tests pane or the terminal)
        if ((change & WorkspaceChange.TestsRan) != 0 && _ws.LastTestLine.Length > 0) Say(_ws.LastTestLine);
        RaiseActionsChanged();
    }

    // ---- building, compiling, testing ------------------------------------------------------------

    private void Say(string message)
    {
        Status = message;
        _canvas?.Say(message);
    }

    private void Build()
    {
        _ws.Spec.Invalidate();
        _ = _ws.Compiled;
        var problems = _ws.Problems;
        if (problems.Count == 0)
            Say(_ws.Doc.Mode == SpecMode.Parser
                    ? $"Built {_ws.Doc.Rules.Count} rule(s): no problems"
                    : $"Built {_ws.Doc.Tokenizer.Rules.Count} tokenizer rule(s): no problems");
        else
            Say($"{problems.Count} problem(s): {problems[0].Rule}: {problems[0].Message}");

        RaiseActionsChanged();
    }

    /// <summary>Makes the parser a function that is kept, and times it on the first sample there is.</summary>
    private void CompileFunction()
    {
        _ws.Spec.Invalidate();
        var compiled = _ws.Compiled;
        if (compiled.Diagnostics.Count > 0)
        {
            Say($"Cannot compile: {compiled.Diagnostics[0].Rule}: {compiled.Diagnostics[0].Message}");
            return;
        }

        string? sample = _ws.Book.Samples.SelectMany(s => s.Items).FirstOrDefault();
        if (_ws.Doc.Mode == SpecMode.Parser)
        {
            var run = compiled.Compile();
            if (sample is null)
            {
                Say("Compiled the parser (add a sample to time it)");
                return;
            }

            run(sample); // the first run makes the delegates ready
            int count = 0;
            var sw    = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 150 && count < 200_000)
            {
                run(sample);
                count++;
            }

            sw.Stop();
            Say($"Compiled: {sw.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, count):0.##} µs per parse of \"{Shorten(sample)}\" ({count:N0} runs)");
        }
        else
        {
            if (sample is null)
            {
                Say("Compiled the tokenizer (add a sample to time it)");
                return;
            }

            compiled.Tokenize(sample);
            int count = 0;
            var sw    = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 150 && count < 200_000)
            {
                compiled.Tokenize(sample);
                count++;
            }

            sw.Stop();
            Say($"Compiled: {sw.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, count):0.##} µs per tokenization of \"{Shorten(sample)}\" ({count:N0} runs)");
        }
    }

    private static string Shorten(string text)
    {
        return text.Length <= 24 ? text : text[..24] + "…";
    }

    private string RunTests(bool failingOnly)
    {
        if (failingOnly) _ws.RunFailingTests();
        else _ws.RunTests();

        // the workspace raised TestsRan, which put the line in the status bar
        return _ws.LastTestLine;
    }

    private void NewSpec()
    {
        if (!ConfirmDiscard()) return;

        string? name = ParserUi.Ask("New spec", "Fully qualified name of the spec:", "My.Data.Spec");
        if (string.IsNullOrWhiteSpace(name)) return;

        var doc        = new ParserSpecDoc { Name = name };
        // what the parameters say about reading text carries over; what identifies a spec does not
        var parameters = DocFromParameters();
        doc.Ignore              = parameters.Ignore;
        doc.ExitOn              = parameters.ExitOn;
        doc.Mode                = parameters.Mode;
        doc.Tokenizer.TokenType = parameters.Tokenizer.TokenType;
        _ws.Open(doc, null, true);
        RaiseValueChanged();
    }

    private void AddRule()
    {
        string? name = ParserUi.Ask("New rule", "Name of the rule:", _ws.UniqueRuleName("rule"));
        if (!string.IsNullOrWhiteSpace(name)) _ws.AddRule(name);
    }

    private void SetMode(SpecMode mode)
    {
        _ws.Edit(d => d.Mode = mode, WorkspaceChange.Header);
    }

    private void Save()
    {
        try
        {
            string? note = _ws.Save();
            Say(note is null ? $"Kept {_ws.Doc.Name} in the Silo" : $"Kept {_ws.Doc.Name} in the Silo; {note}");
        }
        catch (InvalidOperationException ex)
        {
            Say(ex.Message);
        }
    }

    /// <summary>Opens a spec kept in the Silo (after the chance to keep the one that is open).</summary>
    private void OpenSpec(string name)
    {
        if (!ConfirmDiscard()) return;

        if (_ws.Load(name))
        {
            RememberRecent(name);
            RaiseValueChanged();
        }
        else
        {
            Say($"There is no spec called '{name}' in the Silo");
        }
    }

    // ---- library, recent and problems (the panes every Lab window has) -------------------------------

    protected override bool RecentOnOpen => false;

    public override Task OpenRecentAsync(string text)
    {
        OpenSpec(text);
        return Task.CompletedTask;
    }

    public override IReadOnlyList<LabLibrarySection> LibrarySections(string filter)
    {
        var kept = SpecStore.Names().Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            .Select(name => new LabLibraryEntry(name, "Spec", null, "Coding", () => OpenSpec(name), string.Equals(name, _ws.Doc.Name, StringComparison.OrdinalIgnoreCase),
                                                                ToolTip: "Double-click to open", Delete: () => SpecStore.Delete(name)))
                            .ToList();
        return [new("In the Silo", "Database5", kept, Note: SpecStore.IsAvailable ? null : "No Silo is open")];
    }

    protected override IEnumerable<LabProblem> Validate()
    {
        foreach (var d in _ws.Problems) yield return new LabProblem(LabProblemSeverity.Error, "Spec", d.Message, d.Rule);
    }

    private void DuplicateRule()
    {
        if (_ws.Current is not { } rule) return;

        _ws.ImportRule(rule);
        Say($"Copied {rule.Name} as {_ws.CurrentRule}");
    }

    private void RenameRule()
    {
        if (_ws.Current is not { } rule) return;

        string? name = ParserUi.Ask("Rename rule", "New name of the rule:", rule.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            _ws.RenameRule(rule.Name, name);
        }
        catch (InvalidOperationException ex)
        {
            Say(ex.Message);
        }
    }

    private void DeleteRule()
    {
        if (_ws.Current is not { } rule || !ParserUi.Confirm($"Remove the rule '{rule.Name}'?")) return;

        _ws.DeleteRule(rule.Name);
    }

    /// <summary>Adds each line of the clipboard's text as a sample (the Tests menu's Paste to Samples).</summary>
    private void PasteSamples()
    {
        try
        {
            string text = Clipboard.GetText();
            if (text.Length == 0)
            {
                Say("The clipboard has no text");
                return;
            }

            Host?.ShowInspector("samples");
            _samples.AddSamples(text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Say("The clipboard is in use by another program");
        }
    }

    private void DeleteSaved()
    {
        string name = _ws.Doc.Name;
        if (!ParserUi.Confirm($"Remove '{name}' and its samples and tests from the Silo?")) return;

        Say(SpecStore.Delete(name) ? $"Removed {name} from the Silo" : $"{name} was not kept");
        _ws.BookChanged();
        RaiseActionsChanged();
    }

    private void CopyJson()
    {
        try
        {
            Clipboard.SetText(JsonConvert.SerializeObject(_ws.Doc, Formatting.Indented));
            Say("Copied the spec as JSON");
        }
        catch (Exception ex)
        {
            Say("Could not copy: " + ex.Message);
        }
    }

    private string Tokens(string text)
    {
        var outcome = SpecEval.Tokens(_ws.Compiled, text);
        return outcome.Success ? outcome.Value : outcome.Summary;
    }

    private string ListRules()
    {
        if (_ws.Doc.Mode == SpecMode.Tokenizer)
            return string.Join('\n', _ws.Doc.Tokenizer.Rules.Select((r, i) => $"{i + 1}. {r.Kind ?? "(skip)"}"));

        return string.Join('\n',
                           _ws.Doc.Rules.Select(r =>
                                                    $"{(string.Equals(r.Name, _ws.Doc.Root, StringComparison.OrdinalIgnoreCase) ? "*" : " ")} {r.Name}{(r.Returns is null ? string.Empty : " → " + r.Returns)}"));
    }

    private string Spec(IReadOnlyList<string> args)
    {
        string  verb = args.Count > 0 ? args[0].ToLowerInvariant() : "list";
        string? name = args.Count > 1 ? args[1] : null;
        switch (verb)
        {
            case "list":
            {
                var names = SpecStore.Names();
                return names.Count == 0 ? "No specs are kept in the Silo." : string.Join('\n', names);
            }
            case "new":
                if (name is null) throw new ArgumentException("spec new <name>");
                if (!ConfirmDiscard()) return "Cancelled.";
                _ws.Open(new ParserSpecDoc { Name = name }, null, true);
                RaiseValueChanged();
                return $"New spec {name}";
            case "open":
                if (name is null) throw new ArgumentException("spec open <name>");
                if (!ConfirmDiscard()) return "Cancelled.";
                if (!_ws.Load(name)) throw new ArgumentException($"There is no spec called '{name}' in the Silo.");
                RaiseValueChanged();
                return $"Opened {name}";
            case "save":
                _ws.Save();
                return $"Kept {_ws.Doc.Name} in the Silo";
            case "delete":
                if (name is null) throw new ArgumentException("spec delete <name>");
                return SpecStore.Delete(name) ? $"Removed {name}" : $"There is no spec called '{name}'.";
            case "json":
                return JsonConvert.SerializeObject(_ws.Doc, Formatting.Indented);
            default:
                throw new ArgumentException("spec list|new <name>|open <name>|save|delete <name>|json");
        }
    }

    private string Test(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return RunTests(false);
        if (string.Equals(args[0], "failing", StringComparison.OrdinalIgnoreCase)) return RunTests(true);

        var test = _ws.Book.Tests.FirstOrDefault(t => string.Equals(t.Name, args[0], StringComparison.OrdinalIgnoreCase)) ??
                   throw new ArgumentException($"There is no test called '{args[0]}'.");
        var r  = _ws.RunTest(test);
        var sb = new StringBuilder($"{test.Name}: {r.State} ({r.Summary})");
        foreach (var o in r.Outcomes.Where(o => !o.Passed)) sb.AppendLine().Append("  ✖ ").Append(o.Sample).Append(": ").Append(o.Detail);
        return sb.ToString();
    }

    private string Sample(LabTerminalCall call)
    {
        if (call.Args.Count < 2) throw new ArgumentException("sample <set> <text>");

        string set    = call.Args[0];
        string text   = call.Rest[call.Rest.IndexOf(set, StringComparison.Ordinal)..].Substring(set.Length).Trim().Trim('"');
        var    target = _ws.Book.Samples.FirstOrDefault(s => string.Equals(s.Name, set, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            target = new SampleSet { Name = set };
            _ws.Book.Samples.Add(target);
        }

        target.Items.Add(text);
        _ws.BookChanged();
        return $"Added to {target.Name} ({target.Items.Count} sample(s))";
    }

    private string Mode(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return _ws.Doc.Mode.ToString();
        if (!Enum.TryParse<SpecMode>(args[0], true, out var mode)) throw new ArgumentException("mode parser|tokenizer");

        SetMode(mode);
        return mode.ToString();
    }

    protected override void OnDispose()
    {
        _disposed          =  true;
        _ws.Changed        -= OnWorkspaceChanged;
        Parameters.Changed -= OnParametersChanged;
    }

    public ParserLabSession(ParserLab lab) : base(lab)
    {
        WireClipboard();
        _ws      = new ParserWorkspace(DocFromParameters());
        _samples = new SamplesInspector(_ws);
        _tests   = new TestsInspector(_ws);
        _inspectors =
        [
            // the lanes, left to right: Syntax | Tokens | Combinators; along the bottom: Tests | Samples | Terminal
            new SyntaxInspector(_ws, OpenSpec),
            new TokensInspector(_ws),
            new CombinatorsInspector(_ws),
            _tests,
            _samples,
            new TerminalInspector(_ws)
        ];

        _ws.Changed        += OnWorkspaceChanged;
        Parameters.Changed += OnParametersChanged;
    }

    private static bool _clipboardWired;

    /// <summary>Pieces copied in one Parser Lab window paste into another (and into other programs as JSON); the Silo's enums are the ones a spec's token type can be.</summary>
    private static void WireClipboard()
    {
        if (_clipboardWired) return;

        _clipboardWired = true;
        SpecClip.Read = () =>
                        {
                            try
                            {
                                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
                            }
                            catch (System.Runtime.InteropServices.COMException)
                            {
                                return null;
                            }
                        };
        SpecClip.Write = text =>
                         {
                             try
                             {
                                 Clipboard.SetText(text);
                             }
                             catch (System.Runtime.InteropServices.COMException)
                             {
                                 // the clipboard is in use by another program
                             }
                         };
        SpecEnvironment.EnumSource = () => App.Instance?.ActiveSilo?.Data as IEnumStore;
    }
}
