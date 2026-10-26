using System.IO;
using System.Text;
using Microsoft.Win32;
using Silo.DbModel.Meta;
using Silo.Providers.Data;
using Silo.Providers.Definitions;

namespace Silo.Labs.Schemas;

/// <summary>One Schema Lab window: the draft, the construct that is selected, what is wrong with the draft, and what promoting it would do.</summary>
public sealed partial class SchemaLabSession : LabSession<SchemaDraft>, ILabLive
{
    private static readonly LabSaved<SchemaDraft> Saved = new("schema");

    private readonly Debouncer          _notify;
    private readonly List<LabInspector> _inspectors;
    private readonly HashSet<ConstructKind> _hidden = [];
    private          SchemaDraft        _draft = new();
    private          string?            _selected;
    private          SchemaSnapshot     _snapshot = SchemaSnapshot.Empty;
    private          DateTime           _snapshotAt = DateTime.MinValue;
    private          IReadOnlyList<SchemaProblem> _problems = [];
    private          PromotionPlan?     _plan;
    private          SchemaLayoutResult _layout = new([], [], 0, 0);
    private          PromoteOutcome?    _outcome;
    private          bool               _replace;
    private          bool               _copy;
    private          bool               _dirty;
    private          bool               _started;

    public SchemaLabSession(SchemaLab lab) : base(lab)
    {
        _notify     = new Debouncer(TimeSpan.FromMilliseconds(150), Rebuilt);
        _inspectors = [new SchemaPromoteInspector(this).OwnedBy(LabOwner.OfAspect("promote")), new SchemaTextInspector(this).OwnedBy(LabOwner.OfAspect("outline")), new SchemaJsonInspector(this).OwnedBy(LabOwner.OfAspect("json"))];
    }

    internal static LabSaved<SchemaDraft> Kept => Saved;

    private SchemaLab Lab_ => (SchemaLab)LabOf;

    public SchemaDraft Draft => _draft;

    public SchemaConstruct? Selected => _draft.FindRef(_selected);

    public IReadOnlyList<SchemaProblem> Problems => _problems;

    public SchemaSnapshot Snapshot => _snapshot;

    public PromotionPlan Plan => _plan ?? SchemaPromote.Plan(_draft, _draft.Target, _snapshot, SchemaSilo.Support);

    public SchemaLayoutResult Layout => _layout;

    public PromoteOutcome? LastOutcome => _outcome;

    public IReadOnlySet<ConstructKind> Hidden => _hidden;

    public bool ReplaceExisting => _replace;

    public bool KeepBoth => _copy;

    public bool IsDirty => _dirty;

    /// <summary>The draft changed (its constructs, what is wrong, or the plan).</summary>
    public event EventHandler? Changed;

    /// <summary>A different construct was selected.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>A different draft was opened: the boxes show it afresh.</summary>
    public event EventHandler? Loaded;

    public override SchemaDraft? CurrentValue => _draft.Clone();

    public override string ValueText => string.IsNullOrWhiteSpace(_draft.Name) ? "(new draft)" : "schema:" + _draft.Name;

    public override string Title => $"{LabOf.Name} - {(string.IsNullOrWhiteSpace(_draft.Name) ? "new draft" : _draft.Name)}{(_dirty ? " *" : string.Empty)}";

    /// <summary>3: the panes belong to aspects of the Lab's model now; windows saved with the old arrangement start afresh.</summary>
    public override int LayoutVersion => 3;

    public override IReadOnlyList<LabInspector> Inspectors => _inspectors;

    public void Say(string text) => Status = text;

    public string Json => SchemaJson.ToJson(_draft);

    public string Outline => SchemaText.Outline(_draft);

    public string CSharp => SchemaText.CSharp(_draft);

    public SchemaLayoutOptions LayoutOptions => new() { ShowMembers = Lab_.ShowMembers, MemberEdges = Lab_.MemberLines, Hidden = _hidden.Count == 0 ? null : _hidden };

    // ---- opening ---------------------------------------------------------------------------------

    protected override Task OpenValueAsync(SchemaDraft value)
    {
        Replace(value.Clone(), false);
        return Task.CompletedTask;
    }

    protected override Task OpenDefaultAsync()
    {
        if (!_started) Replace(new SchemaDraft(), false);
        return Task.CompletedTask;
    }

    protected override void OnAttached()
    {
        if (!_started) _ = OpenDefaultAsync();
    }

    protected override void OnDispose() => _notify.Cancel();

    private void Replace(SchemaDraft draft, bool dirty)
    {
        _started  = true;
        _draft    = draft;
        _dirty    = dirty;
        _outcome  = null;
        _selected = draft.Items.FirstOrDefault(i => i.Kind == ConstructKind.Class)?.Ref ?? draft.Items.FirstOrDefault(i => i.Kind != ConstructKind.Relation)?.Ref;
        RefreshSnapshot();
        Rebuilt();
        Loaded?.Invoke(this, EventArgs.Empty);
        RaiseSelection();
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    /// <summary>Looks at what the Silo holds again (the Silo may have changed since the draft was last looked at).</summary>
    public void RefreshSnapshot()
    {
        _snapshot   = SchemaSilo.Snapshot();
        _snapshotAt = DateTime.UtcNow;
    }

    private void Rebuilt()
    {
        _notify.Cancel();
        if ((DateTime.UtcNow - _snapshotAt).TotalSeconds > 5) RefreshSnapshot();

        _problems = SchemaCheck.Validate(_draft, _snapshot);
        _plan     = SchemaPromote.Plan(_draft, _draft.Target, _snapshot, SchemaSilo.Support, new PromoteOptions { Replace = _replace, Copy = _copy });
        _layout   = SchemaLayout.Compute(_draft, LayoutOptions);

        int errors   = _problems.Count(p => p.Severity == SchemaSeverity.Error);
        int warnings = _problems.Count(p => p.Severity == SchemaSeverity.Warning);
        Status = $"{_draft.Count} construct{(_draft.Count == 1 ? string.Empty : "s")}; " + (errors + warnings == 0 ? "nothing wrong" : $"{errors} error(s), {warnings} warning(s)");
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseActionsChanged();
    }

    private void Touched(bool now)
    {
        _dirty = true;
        RaiseValueChanged();
        if (now) Rebuilt();
        else _notify.Poke();
    }

    private void Select(string? reference)
    {
        _selected = reference;
        RaiseSelection();
        RaiseActionsChanged();
    }

    public void Select(SchemaConstruct? item) => Select(item?.Ref);

    // ---- editing ---------------------------------------------------------------------------------

    /// <summary>Adds a construct of the kind with a name nobody has, and selects it. Type relations are made with <see cref="Connect" />.</summary>
    public SchemaConstruct? AddConstruct(ConstructKind kind, string? name = null)
    {
        if (kind == ConstructKind.Relation)
        {
            Status = "A type relation joins two classes: select a class and use Connect.";
            return null;
        }

        try
        {
            var item = _draft.Add(kind, string.IsNullOrWhiteSpace(name) ? _draft.FreeName(kind) : name);
            switch (kind)
            {
                case ConstructKind.Attribute:
                    item.Set("type", "String");
                    break;
                case ConstructKind.Enum:
                    item.Set("type", nameof(EnumType.Integral));
                    break;
                case ConstructKind.Syntax:
                    item.Set("kind", nameof(SyntaxKind.Pattern));
                    break;
                case ConstructKind.Unit:
                    item.Set("type", nameof(UnitType.Unit));
                    break;
            }

            // a new attribute goes to the class that is selected, which is nearly always what was meant
            if (kind == ConstructKind.Attribute && Selected is { Kind: ConstructKind.Class } owner) owner.Members.Add(item.Name);

            _selected = item.Ref;
            Touched(true);
            RaiseSelection();
            return item;
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
            return null;
        }
    }

    public void RemoveSelected()
    {
        if (Selected is not { } item) return;

        _draft.Remove(item.Kind, item.Name);
        _selected = _draft.Items.FirstOrDefault(i => i.Kind != ConstructKind.Relation)?.Ref;
        Touched(true);
        RaiseSelection();
    }

    public void RemoveRelation(string name)
    {
        if (_draft.Find(ConstructKind.Relation, name) is null) return;

        _draft.Remove(ConstructKind.Relation, name);
        if (_selected is not null && _draft.FindRef(_selected) is null) _selected = null;
        Touched(true);
        RaiseSelection();
    }

    /// <summary>Renames the selected construct (everything that refers to it follows). Returns what is wrong, or null.</summary>
    public string? RenameSelected(string newName)
    {
        if (Selected is not { } item) return "Nothing is selected.";
        if (item.Kind == ConstructKind.Relation) return "A type relation is named by its two classes.";

        try
        {
            _draft.Rename(item.Kind, item.Name, newName);
            _selected = item.Ref;
            Touched(true);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Sets a property of the selected construct; a value that cannot be set is refused with the reason, and nothing changes.</summary>
    public string? SetProp(string key, string? text, bool now = true)
    {
        if (Selected is not { } item) return "Nothing is selected.";

        string? error = SchemaProps.TrySet(_draft, item, key, text);
        if (error is not null) return error;

        _selected = item.Ref; // a relation is named by its ends
        Touched(now);
        return null;
    }

    public void AddMember(string attribute)
    {
        if (Selected is not { Kind: ConstructKind.Class } c || string.IsNullOrWhiteSpace(attribute)) return;

        string name = _draft.Find(ConstructKind.Attribute, attribute)?.Name ?? attribute.Trim();
        if (c.Members.Contains(name, StringComparer.OrdinalIgnoreCase)) { Status = $"{c.Name} has {name} already."; return; }

        c.Members.Add(name);
        Touched(true);
    }

    /// <summary>Makes an attribute of the draft and gives it to the selected class.</summary>
    public void NewMember()
    {
        if (Selected is not { Kind: ConstructKind.Class }) return;

        AddConstruct(ConstructKind.Attribute); // goes to the selected class, and is selected
    }

    public void RemoveMember(int index)
    {
        if (Selected is not { Kind: ConstructKind.Class } c || index < 0 || index >= c.Members.Count) return;

        c.Members.RemoveAt(index);
        Touched(true);
    }

    public void MoveMember(int index, int delta)
    {
        if (Selected is not { Kind: ConstructKind.Class } c) return;

        int to = index + delta;
        if (index < 0 || index >= c.Members.Count || to < 0 || to >= c.Members.Count) return;

        (c.Members[index], c.Members[to]) = (c.Members[to], c.Members[index]);
        Touched(true);
    }

    public void AddValue()
    {
        if (Selected is not { Kind: ConstructKind.Enum } e) return;

        var rec   = SchemaRecords.ToEnum(e);
        string n  = "Value" + (e.Values.Count + 1);
        e.Values.Add(new SchemaValue { Name = n, Value = EnumRules.SuggestNextValue(rec) });
        Touched(true);
    }

    public void SetValue(int index, string name, string? value, string? description)
    {
        if (Selected is not { Kind: ConstructKind.Enum } e || index < 0 || index >= e.Values.Count) return;

        var v = e.Values[index];
        v.Name        = name.Trim();
        v.Value       = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        v.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Touched(false);
    }

    public void RemoveValue(int index)
    {
        if (Selected is not { Kind: ConstructKind.Enum } e || index < 0 || index >= e.Values.Count) return;

        e.Values.RemoveAt(index);
        Touched(true);
    }

    public void MoveValue(int index, int delta)
    {
        if (Selected is not { Kind: ConstructKind.Enum } e) return;

        int to = index + delta;
        if (index < 0 || index >= e.Values.Count || to < 0 || to >= e.Values.Count) return;

        (e.Values[index], e.Values[to]) = (e.Values[to], e.Values[index]);
        Touched(true);
    }

    /// <summary>Joins two classes: <paramref name="subject" /> inherits <paramref name="target" />, or includes it when it is auxiliary.</summary>
    public string? Connect(string subject, string target, bool auxiliary)
    {
        if (_draft.Find(ConstructKind.Class, subject) is not { } s || _draft.Find(ConstructKind.Class, target) is not { } t) return "Both ends have to be classes of the draft.";
        if (ReferenceEquals(s, t)) return "A class cannot inherit or include itself.";

        try
        {
            var r = _draft.Connect(s.Name, t.Name, auxiliary);
            if (auxiliary && !t.Flag("auxiliary")) t.Set("auxiliary", "true"); // an included class is auxiliary by definition
            _selected = s.Ref;
            Touched(true);
            RaiseSelection();
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    public void SetDraftName(string name)
    {
        _draft.Name = string.IsNullOrWhiteSpace(name) ? "Untitled schema" : name.Trim();
        Touched(true);
    }

    public void SetDescription(string text)
    {
        _draft.Description = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        Touched(false);
    }

    public void SetTarget(PromoteTarget target)
    {
        if (_draft.Target == target) return;

        _draft.Target = target;
        Touched(true);
    }

    public void SetPromoteOptions(bool replace, bool keepBoth)
    {
        _replace = replace;
        _copy    = keepBoth && !replace;
        Rebuilt();
    }

    public void ToggleHidden(ConstructKind kind, bool hidden)
    {
        if (hidden ? !_hidden.Add(kind) : !_hidden.Remove(kind)) return;

        Rebuilt();
    }

    // ---- the Silo --------------------------------------------------------------------------------

    /// <summary>Takes a copy of an enum of the Silo into the draft (replacing the draft's own of that name, after asking).</summary>
    public string? PullEnum(string name)
    {
        var rec = SchemaSilo.Enums?.FindEnum(name);
        if (rec is null) return $"The Silo has no enum called {name}.";

        return Pull(SchemaRecords.FromEnum(rec));
    }

    public string? PullSyntax(string name)
    {
        var rec = SchemaSilo.Syntaxes?.FindSyntax(name);
        if (rec is null) return $"The Silo has no syntax called {name}.";

        return Pull(SchemaRecords.FromSyntax(rec));
    }

    private string? Pull(SchemaConstruct item)
    {
        if (_draft.Find(item.Kind, item.Name) is { } existing)
        {
            if (!LabUi.Confirm($"The draft already has the {item.Kind.Title().ToLowerInvariant()} {item.Name}. Replace it with the one in the Silo?", "Schema Lab")) return null;

            _draft.Items[_draft.Items.IndexOf(existing)] = item;
        }
        else
        {
            _draft.Items.Add(item);
        }

        _selected = item.Ref;
        Touched(true);
        RaiseSelection();
        Status = $"Took a copy of {item.Name} from the Silo.";
        return null;
    }

    /// <summary>Writes the draft into the Silo as the plan says.</summary>
    public void Promote()
    {
        RefreshSnapshot();
        Rebuilt();
        var plan = Plan;
        if (plan.HasErrors) { Status = "The draft has errors; nothing was written. See Problems."; Host?.ShowInspector("problems"); return; }
        if (!plan.CanApply) { Status = "There is nothing to write: every construct is skipped or cannot go to this target. See Promote."; Host?.ShowInspector("promote"); return; }

        if (Lab_.Confirm && !LabUi.Confirm(SchemaText.Describe(plan) + "\n\nThe stores are separate: if one fails, what was already written stays written.\nWrite this into the Silo?", "Promote " + _draft.Name)) return;

        _outcome = SchemaPromote.Apply(_draft, plan, _snapshot, SchemaSilo.Syntaxes, SchemaSilo.Enums, SchemaSilo.Schema);
        RefreshSnapshot();
        Rebuilt();
        Status = _outcome.Ok ? $"Promoted: {_outcome.Done.Count} written." : $"{_outcome.Done.Count} written, {_outcome.Failed.Count} problem(s). See Promote.";
        Host?.ShowInspector("promote");
    }

    // ---- saved and files -------------------------------------------------------------------------

    public void OpenSaved(string name)
    {
        if (Saved.Load(name) is not { } draft) { Status = $"There is no draft called {name} in the Silo."; return; }

        if (_dirty && !LabUi.Confirm("This draft has changes that are not kept in the Silo. Open another anyway?", "Schema Lab")) return;

        if (string.IsNullOrWhiteSpace(draft.Name)) draft.Name = name;
        Replace(draft, false);
        RememberRecent(name);
    }

    // ---- library, recent and problems (the panes every Lab window has) -------------------------------

    protected override bool RecentOnOpen => false;

    public override bool LibraryOpensWithWindow => true;

    public override Task OpenRecentAsync(string text)
    {
        OpenSaved(text);
        return Task.CompletedTask;
    }

    public override IReadOnlyList<LabLibrarySection> LibrarySections(string filter)
    {
        bool Wanted(string text) => filter.Length == 0 || text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var sections = new List<LabLibrarySection>();
        var drafts = Saved.Names().Where(Wanted)
                          .Select(name => new LabLibraryEntry(name, "Draft", null, "DataModel", () => OpenSaved(name), name == _draft.Name, ToolTip: "Double-click to open", Delete: () => Saved.Delete(name)))
                          .ToList();
        sections.Add(new LabLibrarySection("Drafts in the Silo", "Database5", drafts, Note: Saved.IsAvailable ? null : "No Silo is open"));

        var enums = Snapshot.Enums.Where(e => Wanted(e.Name))
                            .Select(e => new LabLibraryEntry(e.Name, "Enum", $"{e.Type.ToString().ToLowerInvariant()}, {e.Values.Count} values", "Enum", () => { if (PullEnum(e.Name) is { } said) Say(said); }, ToolTip: e.Description ?? "Double-click to copy into the draft"))
                            .ToList();
        sections.Add(new LabLibrarySection("Enums of the Silo (double-click to copy into the draft)", "Enum", enums));

        var syntaxes = Snapshot.Syntaxes.Where(x => Wanted(x.Name))
                               .Select(x => new LabLibraryEntry(x.Name, "Syntax", x.Kind.ToString(), "Code", () => { if (PullSyntax(x.Name) is { } said) Say(said); }, ToolTip: x.Description ?? "Double-click to copy into the draft"))
                               .ToList();
        sections.Add(new LabLibrarySection("Syntaxes of the Silo (double-click to copy into the draft)", "Code", syntaxes));

        // the drafts to start from are prefabs of the model now (File > New Schema draft)
        return sections;
    }

    protected override IEnumerable<LabProblem> Validate()
    {
        foreach (var p in _problems)
        {
            var severity = p.Severity switch { SchemaSeverity.Error => LabProblemSeverity.Error, SchemaSeverity.Warning => LabProblemSeverity.Warning, _ => LabProblemSeverity.Info };
            var kind     = p.Kind;
            string? name = p.Name;
            Action? go   = kind is { } k && name is not null ? () => { if (_draft.Find(k, name) is { } item) Select(item); } : null;
            yield return new LabProblem(severity, "Schema", p.Text, p.Where, go);
        }
    }

    public void StartFrom(SchemaDraft draft)
    {
        if (_dirty && !LabUi.Confirm("This draft has changes that are not kept in the Silo. Start another anyway?", "Schema Lab")) return;

        Replace(draft, true);
    }

    private void NewDraft()
    {
        if (_dirty && !LabUi.Confirm("This draft has changes that are not kept in the Silo. Start another anyway?", "Schema Lab")) return;

        Replace(new SchemaDraft(), false);
    }

    private void SaveDraft()
    {
        string name = _draft.Name;
        if (string.IsNullOrWhiteSpace(name) || name == "Untitled schema")
        {
            string? asked = LabUi.Ask("Keep draft", "What is the draft called?", "MySchema");
            if (string.IsNullOrWhiteSpace(asked)) return;

            _draft.Name = asked.Trim();
        }

        try
        {
            Saved.Save(_draft.Name, _draft);
            _dirty = false;
            Status = $"Kept {_draft.Name} in the Silo";
            Changed?.Invoke(this, EventArgs.Empty);
            RaiseValueChanged();
            RaiseActionsChanged();
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
    }

    private void DeleteSaved()
    {
        if (string.IsNullOrWhiteSpace(_draft.Name)) return;

        Status = Saved.Delete(_draft.Name) ? $"Removed {_draft.Name} from the Silo" : $"{_draft.Name} was not kept";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string? ApplyJson(string text)
    {
        if (!SchemaJson.TryFromJson(text, out var draft, out var error)) return error;

        _draft    = draft;
        _selected = _draft.FindRef(_selected) is not null ? _selected : _draft.Items.FirstOrDefault(i => i.Kind != ConstructKind.Relation)?.Ref;
        _draft.Relink();
        Touched(true);
        Loaded?.Invoke(this, EventArgs.Empty);
        RaiseSelection();
        return null;
    }

    private void Copy(string text, string what)
    {
        LabUi.Copy(text);
        Status = $"Copied the {what}";
    }

    private void PasteJson()
    {
        string? text = null;
        try
        {
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // the clipboard is in use by another program
        }

        if (string.IsNullOrWhiteSpace(text)) { Status = "There is no text on the clipboard."; return; }

        if (_dirty && !LabUi.Confirm("This draft has changes that are not kept in the Silo. Replace it with the JSON on the clipboard?", "Schema Lab")) return;

        Status = ApplyJson(text) is { } error ? "That is not a schema draft: " + error : "The draft is now the JSON from the clipboard.";
    }

    private void SaveFile(string extension, string text)
    {
        string name = string.IsNullOrWhiteSpace(_draft.Name) ? "Schema" : _draft.Name;
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var dialog = new SaveFileDialog
                     {
                         Title = "Save the draft", FileName = name + "." + extension,
                         Filter = extension == "cs" ? "C# (*.cs)|*.cs" : extension == "txt" ? "Text (*.txt)|*.txt" : "JSON (*.json)|*.json"
                     };
        if (dialog.ShowDialog() != true) return;

        File.WriteAllText(dialog.FileName, text, new UTF8Encoding(true));
        Status = $"Saved {Path.GetFileName(dialog.FileName)}";
    }

    // ---- actions ---------------------------------------------------------------------------------

    public override IEnumerable<ProvidedCommand> GlobalActions
    {
        get
        {
            yield return ProvidedCommand.From("new-draft", "_New Draft", NewDraft, "TbNew", null, "Start with an empty draft");
            yield return ProvidedCommand.From("show-library", "Show _Library", () => Host?.ShowInspector("library"), "Folder17", null, "Show the drafts kept in the Silo, the Silo's enums and syntaxes, and the templates");
            yield return ProvidedCommand.From("show-promote", "Show _Promotion", () => Host?.ShowInspector("promote"), "Checklist", null, "Show what promoting the draft would write");
            yield return ProvidedCommand.From("paste-json", "_Paste Draft JSON", PasteJson, "TbPaste", null, "Take the JSON on the clipboard as the draft");
            yield return ProvidedCommand.From("refresh", "_Refresh from the Silo", () =>
                                                                                  {
                                                                                      RefreshSnapshot();
                                                                                      Rebuilt();
                                                                                  }, null, null, "Look at what the Silo holds again");
            yield return ProvidedCommand.From("settings", "Open Se_ttings", () => Host?.ShowInspector(LabParametersInspector.InspectorId), "Settings", null, "Show the Lab's parameters");
        }
    }

    public override IEnumerable<ProvidedCommand> ContextActions
    {
        get
        {
            yield return ProvidedCommand.Submenu("add", "_Add", ConstructKinds.All.Where(k => k != ConstructKind.Relation)
                                                                .Select(k => ProvidedCommand.From("add-" + k.Word(), k.Title(), () => AddConstruct(k), null, null, $"Add a {k.Title().ToLowerInvariant()} to the draft")), null, "TbNew", "Add to the draft");
            yield return ProvidedCommand.From("add-class", "Add _Class", () => AddConstruct(ConstructKind.Class), null, null, "Add a class") with { Gesture = "Ctrl+Shift+N" };
            yield return ProvidedCommand.From("add-member", "Add Attribute to Cl_ass", NewMember, null, () => Selected is { Kind: ConstructKind.Class }, "Make an attribute and give it to the selected class");
            yield return ProvidedCommand.From("add-value", "Add Enum _Value", AddValue, null, () => Selected is { Kind: ConstructKind.Enum }, "Add a value to the selected enum");
            yield return ProvidedCommand.From("delete-construct", "_Delete", RemoveSelected, "TbDelete", () => Selected is not null, "Take the selected construct out of the draft") with { Gesture = "Delete" };
        }
    }

    public override IEnumerable<ProvidedCommand> ValueActions
    {
        get
        {
            yield return ProvidedCommand.From("promote", "_Promote to the Silo", Promote, "TbExport", () => Plan.CanApply, "Write the draft into the Silo as the Promote pane says") with { Gesture = "Ctrl+Shift+P" };
            yield return ProvidedCommand.From("save", "_Keep Draft", SaveDraft, "TbSave", () => Saved.IsAvailable, "Keep the draft in the Silo (as scratch, until it is promoted)") with { Gesture = "Ctrl+S" };
            yield return ProvidedCommand.From("copy-json", "Copy _JSON", () => Copy(Json, "JSON"), "TbCopy", null, "Put the draft on the clipboard as JSON");
            yield return ProvidedCommand.From("copy-outline", "Copy _Outline", () => Copy(Outline, "outline"), "TbCopy", null, "Put the draft on the clipboard as an outline");
            yield return ProvidedCommand.From("copy-cs", "Copy _C# Sketch", () => Copy(CSharp, "C# sketch"), "TbCopy", null, "Put the draft's enums and classes on the clipboard as C#");
            yield return ProvidedCommand.From("save-json", "Save as JS_ON File...", () => SaveFile("json", Json), "TbExport", null, "Write the draft to a JSON file");
            yield return ProvidedCommand.From("save-cs", "Save as C# _File...", () => SaveFile("cs", CSharp), "TbExport", null, "Write the C# sketch to a file");
            yield return ProvidedCommand.From("delete", "Delete Kept Dra_ft", DeleteSaved, "TbDelete", () => Saved.IsAvailable && !string.IsNullOrWhiteSpace(_draft.Name) && Saved.Exists(_draft.Name), "Remove the draft from the Silo");
        }
    }

    // ---- terminal --------------------------------------------------------------------------------

    private static Task<string?> Done(string? text) => Task.FromResult(text);

    public override IReadOnlyList<LabTerminalCommand> TerminalCommands =>
    [
        new("outline", "outline                 the draft as an outline", _ => Done(_draft.IsEmpty ? "The draft is empty. Type add class Person to begin." : Outline)),
        new("check", "check                   what is wrong with the draft", _ => Done(CheckCommand())),
        new("add", "add <kind> [name]       add a class, attribute, enum, syntax, unit or unitrelation (an attribute goes to the selected class)", call => Done(AddCommand(call.Args))),
        new("select", "select <kind> <name>   select a construct", call => Done(SelectCommand(call.Args))),
        new("rm", "rm <kind> <name>         take a construct out", call => Done(RemoveCommand(call.Args))),
        new("rename", "rename <kind> <old> <new>  rename a construct; what refers to it follows", call => Done(RenameCommand(call.Args))),
        new("set", "set <kind> <name> <property> [value]  set a property (say props to see them)", call => Done(SetCommand(call.Args))),
        new("props", "props <kind>            the properties of a kind of construct", call => Done(PropsCommand(call.Args))),
        new("member", "member <class> add|rm <attribute>  give or take an attribute", call => Done(MemberCommand(call.Args))),
        new("value", "value <enum> add|rm <name> [value] [description]  add or take a value", call => Done(ValueCommand(call.Args))),
        new("connect", "connect <class> <base> [aux]  a class inherits another (aux: includes an auxiliary class)", call => Done(ConnectCommand(call.Args))),
        new("disconnect", "disconnect <class> <other>  take away the relation between two classes", call => Done(DisconnectCommand(call.Args))),
        new("target", "target [meta|world]    where the draft is promoted to", call => Done(TargetCommand(call.Rest))),
        new("plan", "plan [replace|both]     what promoting would do (replace: replace names the Silo has; both: keep both)", call => Done(PlanCommand(call.Rest))),
        new("promote", "promote [replace|both]  write the draft into the Silo", call => Done(PromoteCommand(call.Rest))),
        new("pull", "pull enum|syntax <name>  take a copy of an enum or syntax of the Silo", call => Done(PullCommand(call.Args))),
        new("template", "template [number]      list the templates, or start from one", call => Done(TemplateCommand(call.Rest))),
        new("json", "json                    the draft as JSON", _ => Done(Json)),
        new("csharp", "csharp                  the draft's enums and classes as C#", _ => Done(CSharp)),
        new("import", "import <json>           take JSON as the draft", call => Done(ImportCommand(call.Rest))),
        new("save", "save [name]             keep the draft in the Silo", call => Done(SaveCommand(call.Rest))),
        new("list", "list                    the drafts kept in the Silo", _ => Done(Saved.IsAvailable ? string.Join('\n', Saved.Names()) : "No Silo is open.")),
        new("open", "open <name>             open a draft kept in the Silo", call => Done(OpenCommand(call.Rest))),
        new("new", "new                     start an empty draft", _ => Done(Do(NewDraft)))
    ];

    private string Do(Action action)
    {
        action();
        return Status;
    }

    private static ConstructKind KindArg(IReadOnlyList<string> args, string usage)
    {
        if (args.Count == 0 || !ConstructKinds.TryParse(args[0], out var kind)) throw new InvalidOperationException($"Which kind? {usage}. The kinds are class, attribute, enum, syntax, relation, unit and unitrelation.");

        return kind;
    }

    private SchemaConstruct Need(ConstructKind kind, string? name)
    {
        return _draft.Find(kind, name) ?? throw new InvalidOperationException($"There is no {kind.Title().ToLowerInvariant()} called {name}. Type outline to see what the draft has.");
    }

    private string CheckCommand()
    {
        var shown = _problems.Where(p => p.Severity != SchemaSeverity.Info || _problems.Count < 8).ToList();
        return shown.Count == 0 ? "Nothing is wrong with the draft." : string.Join('\n', shown.Select(p => $"{p.Severity.ToString().ToLowerInvariant(),-7} {p.Where,-26} {p.Text}"));
    }

    private string AddCommand(IReadOnlyList<string> args)
    {
        var kind = KindArg(args, "add <kind> [name]");
        if (kind == ConstructKind.Relation) throw new InvalidOperationException("Join two classes with connect <class> <base>.");

        var item = AddConstruct(kind, args.Count > 1 ? args[1] : null);
        return item is null ? Status : $"Added {kind.Title().ToLowerInvariant()} {item.Name}.";
    }

    private string SelectCommand(IReadOnlyList<string> args)
    {
        var kind = KindArg(args, "select <kind> <name>");
        var item = Need(kind, args.Count > 1 ? args[1] : null);
        Select(item);
        return $"Selected {kind.Title().ToLowerInvariant()} {item.Name}.";
    }

    private string RemoveCommand(IReadOnlyList<string> args)
    {
        var kind = KindArg(args, "rm <kind> <name>");
        var item = Need(kind, args.Count > 1 ? args[1] : null);
        Select(item);
        RemoveSelected();
        return $"Took out {kind.Title().ToLowerInvariant()} {item.Name}.";
    }

    private string RenameCommand(IReadOnlyList<string> args)
    {
        var kind = KindArg(args, "rename <kind> <old> <new>");
        if (args.Count < 3) throw new InvalidOperationException("rename <kind> <old> <new>");

        var item = Need(kind, args[1]);
        Select(item);
        if (RenameSelected(args[2]) is { } error) throw new InvalidOperationException(error);

        return $"{args[1]} is now {args[2]}.";
    }

    private string SetCommand(IReadOnlyList<string> args)
    {
        var kind = KindArg(args, "set <kind> <name> <property> [value]");
        if (args.Count < 3) throw new InvalidOperationException("set <kind> <name> <property> [value]; props <kind> lists the properties.");

        var item = Need(kind, args[1]);
        Select(item);
        if (SetProp(args[2], args.Count > 3 ? string.Join(' ', args.Skip(3)) : string.Empty) is { } error) throw new InvalidOperationException(error);

        return $"{kind.Title()} {item.Name}: {args[2]} is {(Selected?.Get(args[2]) is { Length: > 0 } v ? v : "not set")}.";
    }

    private static string PropsCommand(IReadOnlyList<string> args)
    {
        var kind  = KindArg(args, "props <kind>");
        var props = SchemaProps.For(kind);
        return $"{kind.Title()} properties:\n" + string.Join('\n', props.Select(p => $"  {p.Key,-12} {p.Type.ToString().ToLowerInvariant(),-7} {p.Title}{(p.Choices is { Count: > 0 } ? " (" + string.Join(", ", p.Choices) + ")" : string.Empty)}{(p.Hint.Length > 0 ? " - " + p.Hint : string.Empty)}")) +
               (kind == ConstructKind.Class ? "\n  (members are given with member <class> add <attribute>)" : kind == ConstructKind.Enum ? "\n  (values are added with value <enum> add <name> [value])" : string.Empty);
    }

    private string MemberCommand(IReadOnlyList<string> args)
    {
        if (args.Count < 3) throw new InvalidOperationException("member <class> add|rm <attribute>");

        var c = Need(ConstructKind.Class, args[0]);
        Select(c);
        switch (args[1].ToLowerInvariant())
        {
            case "add":
                AddMember(args[2]);
                break;
            case "rm":
                RemoveMember(c.Members.FindIndex(m => string.Equals(m, args[2], StringComparison.OrdinalIgnoreCase)));
                break;
            default:
                throw new InvalidOperationException("member <class> add|rm <attribute>");
        }

        return $"{c.Name}: {(c.Members.Count == 0 ? "no attributes" : string.Join(", ", c.Members))}";
    }

    private string ValueCommand(IReadOnlyList<string> args)
    {
        if (args.Count < 3) throw new InvalidOperationException("value <enum> add|rm <name> [value] [description]");

        var e = Need(ConstructKind.Enum, args[0]);
        Select(e);
        switch (args[1].ToLowerInvariant())
        {
            case "add":
                AddValue();
                SetValue(e.Values.Count - 1, args[2], args.Count > 3 ? args[3] : e.Values[^1].Value, args.Count > 4 ? string.Join(' ', args.Skip(4)) : null);
                break;
            case "rm":
                int at = e.Values.FindIndex(v => string.Equals(v.Name, args[2], StringComparison.OrdinalIgnoreCase));
                if (at < 0) throw new InvalidOperationException($"{e.Name} has no value called {args[2]}.");

                RemoveValue(at);
                break;
            default:
                throw new InvalidOperationException("value <enum> add|rm <name> [value] [description]");
        }

        Rebuilt();
        return $"{e.Name}: {(e.Values.Count == 0 ? "no values" : string.Join(", ", e.Values.Select(v => v.Value is null ? v.Name : $"{v.Name}={v.Value}")))}";
    }

    private string ConnectCommand(IReadOnlyList<string> args)
    {
        if (args.Count < 2) throw new InvalidOperationException("connect <class> <base> [aux]");

        if (Connect(args[0], args[1], args.Count > 2 && args[2].ToLowerInvariant() is "aux" or "auxiliary" or "include" or "includes") is { } error) throw new InvalidOperationException(error);

        return Status;
    }

    private string DisconnectCommand(IReadOnlyList<string> args)
    {
        if (args.Count < 2) throw new InvalidOperationException("disconnect <class> <other>");

        var r = _draft.Of(ConstructKind.Relation).FirstOrDefault(x => string.Equals(x.Get("subject"), args[0], StringComparison.OrdinalIgnoreCase) && string.Equals(x.Get("target"), args[1], StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{args[0]} has no relation to {args[1]}.");
        RemoveRelation(r.Name);
        return $"Took away: {r.Name}.";
    }

    private string TargetCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) return $"The draft is promoted to {(_draft.Target == PromoteTarget.Meta ? "the metaverse" : "the local world")}.";
        if (!Enum.TryParse<PromoteTarget>(rest.Trim(), true, out var target)) throw new InvalidOperationException("The target is meta or world.");

        SetTarget(target);
        return $"The draft is promoted to {(target == PromoteTarget.Meta ? "the metaverse" : "the local world")}.";
    }

    private void SetOptionsFrom(string rest)
    {
        string t = rest.Trim().ToLowerInvariant();
        if (t.Length > 0 && t is not ("replace" or "both")) throw new InvalidOperationException("Say replace (replace what the Silo has) or both (keep both), or nothing to skip what the Silo has.");

        SetPromoteOptions(t == "replace", t == "both");
    }

    private string PlanCommand(string rest)
    {
        SetOptionsFrom(rest);
        RefreshSnapshot();
        Rebuilt();
        return SchemaText.Describe(Plan);
    }

    private string PromoteCommand(string rest)
    {
        SetOptionsFrom(rest);
        RefreshSnapshot();
        Rebuilt();
        var plan = Plan;
        if (plan.HasErrors) throw new InvalidOperationException("The draft has errors, so nothing was written (type check).");
        if (!plan.CanApply) throw new InvalidOperationException("There is nothing to write (type plan).");

        _outcome = SchemaPromote.Apply(_draft, plan, _snapshot, SchemaSilo.Syntaxes, SchemaSilo.Enums, SchemaSilo.Schema);
        RefreshSnapshot();
        Rebuilt();
        return string.Join('\n', _outcome.Done.Select(d => "wrote  " + d).Concat(_outcome.Failed.Select(f => "failed " + f)));
    }

    private string PullCommand(IReadOnlyList<string> args)
    {
        if (args.Count < 2) throw new InvalidOperationException("pull enum|syntax <name>");

        string? error = args[0].ToLowerInvariant() switch
                        {
                            "enum"   => PullEnum(args[1]),
                            "syntax" => PullSyntax(args[1]),
                            _        => throw new InvalidOperationException("pull enum|syntax <name>")
                        };
        if (error is not null) throw new InvalidOperationException(error);

        return Status;
    }

    private string TemplateCommand(string rest)
    {
        if (!int.TryParse(rest.Trim(), out int n)) return string.Join('\n', SchemaLibrary.Templates.Select((t, i) => $"{i + 1}  {t.Title}"));
        if (n < 1 || n > SchemaLibrary.Templates.Count) throw new InvalidOperationException("There is no such template. Type template to see them.");

        StartFrom(SchemaLibrary.Templates[n - 1].Make());
        return $"Started from {SchemaLibrary.Templates[n - 1].Title}.";
    }

    private string ImportCommand(string rest)
    {
        if (ApplyJson(rest) is { } error) throw new InvalidOperationException(error);

        return Status;
    }

    private string SaveCommand(string rest)
    {
        if (!string.IsNullOrWhiteSpace(rest)) _draft.Name = rest.Trim();
        if (string.IsNullOrWhiteSpace(_draft.Name)) throw new InvalidOperationException("Give it a name: save <name>.");

        try
        {
            Saved.Save(_draft.Name, _draft);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }

        _dirty = false;
        RaiseValueChanged();
        return $"Kept {_draft.Name} in the Silo.";
    }

    private string OpenCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) throw new InvalidOperationException("Open which? Type list to see them.");

        OpenSaved(rest.Trim());
        return Status;
    }

    public override UIElement BuildMainView()
    {
        return new SchemaView(this);
    }
}
