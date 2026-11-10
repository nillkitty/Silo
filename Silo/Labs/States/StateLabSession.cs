using System.Text;
using Silo.Commanding;

namespace Silo.Labs.States;

/// <summary>One State Lab window: the machine being designed, a run of it, what is selected on the canvas, and the actions on them.</summary>
public sealed class StateLabSession : LabSession<StateDoc>, ILabLive
{
    private static readonly LabSaved<StateDoc> Saved = new("state");

    private readonly List<LabInspector> _inspectors;
    private          StateDoc           _doc = new();
    private          StateRun           _run;
    private          string?            _selectedState;
    private          string?            _selectedTransition;
    private          string             _savedName = string.Empty;
    private          bool               _dirty;
    private          bool               _started;

    public StateLabSession(StateLab lab) : base(lab)
    {
        _run        = new StateRun(_doc);
        _inspectors =
        [
            new StateDesignInspector(this),
            new StateTestInspector(this),
            new StateCodeInspector(this)
        ];
    }

    internal static LabSaved<StateDoc> Kept => Saved;

    public StateDoc Doc => _doc;

    public StateRun Run => _run;

    public bool IsDirty => _dirty;

    /// <summary>The state selected on the canvas, or null.</summary>
    public string? SelectedState => _selectedState;

    /// <summary>The transition selected on the canvas, or null.</summary>
    public StateTransitionDef? SelectedTransition => _selectedTransition is null ? null : _doc.Transitions.FirstOrDefault(t => t.Id == _selectedTransition);

    /// <summary>True while the next two clicks on states make a transition.</summary>
    public bool Connecting { get; private set; }

    /// <summary>The machine or the run changed (what the views draw).</summary>
    public event EventHandler? Changed;

    /// <summary>The machine was replaced, or its parts were added, removed or renamed: the editors show it afresh (as opposed to <see cref="Changed" />, which also comes while something is typed).</summary>
    public event EventHandler? Loaded;

    /// <summary>What is selected changed.</summary>
    public event EventHandler? SelectionChanged;

    public override StateDoc? CurrentValue => _doc.Clone();

    public override string ValueText => string.IsNullOrWhiteSpace(_doc.Name) ? string.Empty : "state:" + _doc.Name;

    public override string Title => $"{LabOf.Name} - {(string.IsNullOrWhiteSpace(_doc.Name) ? "new machine" : _doc.Name)}{(_dirty ? " *" : string.Empty)}";

    public override int LayoutVersion => 1;

    public override IReadOnlyList<LabInspector> Inspectors => _inspectors;

    protected override string SubjectNoun => "state machine";

    public void Say(string text) => Status = text;

    public void ShowDesign() => Host?.ShowInspector("design");

    // ---- opening ---------------------------------------------------------------------------------

    protected override Task OpenValueAsync(StateDoc value)
    {
        Replace(value.Clone(), value.Name);
        return Task.CompletedTask;
    }

    protected override Task OpenDefaultAsync()
    {
        if (!_started) Replace(StateSamples.All[0].Make(), string.Empty, dirty: true);
        return Task.CompletedTask;
    }

    protected override void OnAttached()
    {
        if (!_started) _ = OpenDefaultAsync();
    }

    /// <summary>Starts again from another machine.</summary>
    public void Replace(StateDoc doc, string savedName, bool dirty = false)
    {
        _started            = true;
        _doc                = doc;
        _run                = new StateRun(doc);
        _savedName          = savedName;
        _dirty              = dirty;
        _selectedState      = null;
        _selectedTransition = null;
        Connecting          = false;
        Status              = Summary();
        Changed?.Invoke(this, EventArgs.Empty);
        Loaded?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        RaiseValueChanged();
        RaiseActionsChanged();
        RaiseProblemsChanged();
    }

    // ---- editing ---------------------------------------------------------------------------------

    /// <summary>Changes the machine (a box was typed in); the editors keep what they show.</summary>
    public void Edit(Action<StateDoc> change) => Apply(change, false);

    /// <summary>Changes the machine in a way that changes what the editors list (something added, removed or renamed).</summary>
    public void EditStructure(Action<StateDoc> change) => Apply(change, true);

    private void Apply(Action<StateDoc> change, bool structure)
    {
        change(_doc);
        _run.Sync(_doc);
        Touched(structure);
    }

    /// <summary>Something that does not change what the machine is (a state was dragged): only the title shows it.</summary>
    public void Moved()
    {
        _dirty = true;
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    private void Touched(bool structure)
    {
        _dirty = true;
        Status = Summary();
        Changed?.Invoke(this, EventArgs.Empty);
        if (structure) Loaded?.Invoke(this, EventArgs.Empty);
        RaiseValueChanged();
        RaiseActionsChanged();
        RaiseProblemsChanged();
    }

    public string Summary()
    {
        var problems = StateCheck.Problems(_doc);
        int errors   = problems.Count(p => p.Severity == LabProblemSeverity.Error);
        string head  = $"{_doc.States.Count} state(s), {_doc.Transitions.Count} transition(s), {_doc.Fields.Count} field(s).";
        return errors == 0 ? head : $"{head} {errors} problem(s) to fix.";
    }

    /// <summary>Adds a state where the canvas was clicked (or in the next free place) and selects it.</summary>
    public StateDef AddState(double? x = null, double? y = null, string? name = null)
    {
        var state = new StateDef { Name = name ?? _doc.NewStateName() };
        int count = _doc.States.Count;
        state.X = x ?? 60 + count % 4 * 200;
        state.Y = y ?? 60 + count / 4 * 140;
        EditStructure(d =>
                      {
                          d.States.Add(state);
                          if (d.Find(d.Initial) is null) d.Initial = state.Name;
                      });
        Select(state.Name, null);
        return state;
    }

    /// <summary>Renames a state, and everything that refers to it. Returns what is wrong with the name, or null.</summary>
    public string? RenameState(string old, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "A state needs a name";
        if (!string.Equals(old, name, StringComparison.OrdinalIgnoreCase) && _doc.Find(name) is not null) return $"There is already a state called '{name}'";
        if (name == StateTransitionDef.Any) return "'*' stands for any state";

        EditStructure(d =>
                      {
                          if (d.Find(old) is not { } state) return;

                          state.Name = name;
                          foreach (var t in d.Transitions)
                          {
                              if (string.Equals(t.From, old, StringComparison.OrdinalIgnoreCase)) t.From = name;
                              if (string.Equals(t.To, old, StringComparison.OrdinalIgnoreCase)) t.To = name;
                          }

                          if (string.Equals(d.Initial, old, StringComparison.OrdinalIgnoreCase)) d.Initial = name;
                      });
        if (string.Equals(_selectedState, old, StringComparison.OrdinalIgnoreCase)) Select(name, null);
        return null;
    }

    public void DeleteState(string name)
    {
        EditStructure(d =>
                      {
                          d.States.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                          d.Transitions.RemoveAll(t => string.Equals(t.From, name, StringComparison.OrdinalIgnoreCase) || string.Equals(t.To, name, StringComparison.OrdinalIgnoreCase));
                          if (string.Equals(d.Initial, name, StringComparison.OrdinalIgnoreCase)) d.Initial = d.States.FirstOrDefault()?.Name ?? string.Empty;
                      });
        Select(null, null);
    }

    public void SetInitial(string name)
    {
        EditStructure(d => d.Initial = name);
        _run.Reset();
        Changed?.Invoke(this, EventArgs.Empty);
        Status = $"{name} is the initial state; the run started again from it";
    }

    /// <summary>Adds a transition and selects it.</summary>
    public StateTransitionDef AddTransition(string from, string to)
    {
        var transition = new StateTransitionDef { From = from, To = to };
        EditStructure(d => d.Transitions.Add(transition));
        Select(null, transition.Id);
        return transition;
    }

    public void DeleteTransition(string id)
    {
        EditStructure(d => d.Transitions.RemoveAll(t => t.Id == id));
        Select(null, null);
    }

    public void Select(string? state, string? transition)
    {
        if (string.Equals(_selectedState, state, StringComparison.Ordinal) && string.Equals(_selectedTransition, transition, StringComparison.Ordinal)) return;

        _selectedState      = state;
        _selectedTransition = transition;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseActionsChanged();
    }

    /// <summary>Starts or stops connecting two states with a click on each.</summary>
    public void SetConnecting(bool on)
    {
        Connecting = on;
        Status     = on ? "Click the state the transition leaves, then the state it leads to (Esc stops)" : Summary();
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseActionsChanged();
    }

    /// <summary>Puts the states in a circle, the initial one at the top.</summary>
    public void AutoLayout()
    {
        EditStructure(d =>
                      {
                          var ordered = d.States.OrderBy(s => string.Equals(s.Name, d.Initial, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
                          double radius = Math.Max(120, ordered.Count * 38);
                          for (int i = 0; i < ordered.Count; i++)
                          {
                              double angle = -Math.PI / 2 + 2 * Math.PI * i / Math.Max(1, ordered.Count);
                              ordered[i].X = radius + 100 + Math.Cos(angle) * radius;
                              ordered[i].Y = radius + 60 + Math.Sin(angle) * radius;
                          }
                      });
        Status = "Placed the states in a circle";
    }

    // ---- running ---------------------------------------------------------------------------------

    public StateStep Fire(StateTransitionDef transition) => Ran(_run.Fire(transition));

    public StateStep Update() => Ran(_run.Update());

    public void RunToRest()
    {
        var steps = _run.RunToRest();
        Ran(new StateStep(steps.Count > 0, steps.Count == 0 ? $"Nothing leaves {_run.Current} right now" : $"Took {steps.Count} step(s), now in {_run.Current}"));
    }

    public void ResetRun()
    {
        _run.Reset();
        Ran(new StateStep(true, "Run started again"));
    }

    public string? SetField(string name, string text)
    {
        string? error = _run.SetField(name, text);
        if (error is null) Changed?.Invoke(this, EventArgs.Empty);
        return error;
    }

    private StateStep Ran(StateStep step)
    {
        Status = step.Message;
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseActionsChanged();
        return step;
    }

    // ---- keeping ---------------------------------------------------------------------------------

    private void SaveMachine()
    {
        string? name = _doc.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = LabUi.Ask("Save machine", "What is the machine called?", "NewMachine");
            if (string.IsNullOrWhiteSpace(name)) return;
        }

        Status = Save(name.Trim());
    }

    /// <summary>Keeps the machine in the Silo under its name; returns what happened, as a sentence.</summary>
    public string Save(string name)
    {
        try
        {
            if (_savedName.Length > 0 && !string.Equals(_savedName, name, StringComparison.OrdinalIgnoreCase) && Saved.Exists(_savedName)) Saved.Delete(_savedName);

            _doc.Name = name;
            Saved.Save(name, _doc);
            _savedName = name;
            _dirty     = false;
            Loaded?.Invoke(this, EventArgs.Empty);
            RaiseValueChanged();
            RaiseActionsChanged();
            return $"Kept {name} in the Silo";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    private void DeleteSaved()
    {
        if (_savedName.Length == 0 || !LabUi.Confirm($"Remove '{_savedName}' from the Silo? The machine stays in this window.", "State Lab")) return;

        Status     = Saved.Delete(_savedName) ? $"Removed {_savedName} from the Silo (the machine is still here)" : $"{_savedName} was not kept";
        _savedName = string.Empty;
        _dirty     = true;
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    private void NewMachine()
    {
        if (_dirty && !LabUi.Confirm("This machine has changes that are not kept in the Silo. Start a new one anyway?", "State Lab")) return;

        Replace(new StateDoc(), string.Empty, dirty: true);
        AddState(name: "Start");
        Status = "A new machine: double-click the canvas to add states";
    }

    /// <summary>Opens a kept machine (asks first when there are changes).</summary>
    public void OpenSaved(string name)
    {
        if (_dirty && !LabUi.Confirm("This machine has changes that are not kept in the Silo. Open another anyway?", "State Lab")) return;

        if (Saved.Load(name) is not { } doc)
        {
            Status = $"The Silo keeps no machine called '{name}'";
            return;
        }

        Replace(doc, name);
        RememberRecent(name);
    }

    private void OpenSample(StateSamples.Sample sample)
    {
        if (_dirty && !LabUi.Confirm("This machine has changes that are not kept in the Silo. Open the sample anyway?", "State Lab")) return;

        Replace(sample.Make(), string.Empty, dirty: true);
    }

    protected override bool RecentOnOpen => false;

    public override bool LibraryOpensWithWindow => true;

    public override Task OpenRecentAsync(string text)
    {
        OpenSaved(text);
        return Task.CompletedTask;
    }

    public override IReadOnlyList<LabLibrarySection> LibrarySections(string filter)
    {
        bool Wanted(string name) => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var kept = Saved.Names().Where(Wanted)
                        .Select(name => new LabLibraryEntry(name, "State machine", null, "FlowDiagram", () => OpenSaved(name), name == _savedName, ToolTip: "Double-click to open", Delete: () => Saved.Delete(name)))
                        .ToList();
        var samples = StateSamples.All.Where(s => Wanted(s.Name))
                                  .Select(s => new LabLibraryEntry(s.Name, "Sample", null, "FlowDiagram", () => OpenSample(s), false, true, s.Description))
                                  .ToList();
        return [new("In the Silo", "Database5", kept, Note: Saved.IsAvailable ? null : "No Silo is open"), new("Samples", "FlowDiagram", samples, true)];
    }

    protected override IEnumerable<LabProblem> Validate()
    {
        foreach (var issue in StateCheck.Problems(_doc))
        {
            string? where = issue.Where;
            yield return new LabProblem(issue.Severity, "State", issue.Message, where, where is null ? null : () => Go(where));
        }
    }

    private void Go(string where)
    {
        if (_doc.Find(where) is { } state) Select(state.Name, null);
        else if (_doc.Transitions.Any(t => t.Id == where)) Select(null, where);
    }

    // ---- actions ---------------------------------------------------------------------------------

    public override IEnumerable<ProvidedCommand> GlobalActions
    {
        get
        {
            yield return ProvidedCommand.From("new-machine", "_New Machine", NewMachine, "TbNew", null, "Start a new state machine");
            yield return ProvidedCommand.From("auto-layout", "Auto _Layout", AutoLayout, "FlowDiagram", () => _doc.States.Count > 1, "Place the states in a circle, the initial state at the top");
            yield return ProvidedCommand.From("show-design", "Show _Design", () => Host?.ShowInspector("design"), "Settings", null, "Show the editor of the selected state, transition or the machine");
            yield return ProvidedCommand.From("show-test", "Show _Test", () => Host?.ShowInspector("test"), "Check", null, "Show the run: the fields, the transitions that can be taken and the log");
        }
    }

    public override IEnumerable<ProvidedCommand> ContextActions
    {
        get
        {
            yield return ProvidedCommand.From("add-state", "Add _State", () => AddState(), "Plus", null, "Add a state to the machine (or double-click the canvas)");
            yield return ProvidedCommand.From("connect", Connecting ? "Stop _Connecting" : "_Connect", () => SetConnecting(!Connecting), "FlowDiagram", () => _doc.States.Count > 0,
                                              "Click one state and then another to make a transition between them");
            yield return ProvidedCommand.From("any-transition", "Add _Any Transition", AddAnyTransition, "FlowDiagram", () => _selectedState is not null,
                                              "Add a transition to the selected state that can be taken from every state");
            yield return ProvidedCommand.From("set-initial", "Set _Initial", () => SetInitial(_selectedState!), "Check", () => _selectedState is not null && !string.Equals(_selectedState, _doc.Initial, StringComparison.OrdinalIgnoreCase),
                                              "Make the selected state the one the machine starts in");
            yield return ProvidedCommand.From("delete-selected", "_Delete Selected", DeleteSelected, "TbDelete", () => _selectedState is not null || _selectedTransition is not null,
                                              "Remove the selected state (and the transitions that touch it) or transition") with { Gesture = "Delete" };
            yield return ProvidedCommand.From("update", "_Update", () => Update(), "Refresh", () => _run.Current is not null, "Take the transition that goes first of those that can be taken") with { Gesture = "F5" };
            yield return ProvidedCommand.From("run-to-rest", "Run to _Rest", RunToRest, "Refresh", () => _run.Current is not null, "Update until nothing can be taken");
            yield return ProvidedCommand.From("reset-run", "Rese_t Run", ResetRun, "Refresh", null, "Start the run again from the initial state, with the default values");
        }
    }

    public override IEnumerable<ProvidedCommand> ValueActions
    {
        get
        {
            yield return ProvidedCommand.From("save", "_Save Machine", SaveMachine, "TbSave", () => Saved.IsAvailable, "Keep the machine in the Silo") with { Gesture = "Ctrl+S" };
            yield return ProvidedCommand.From("copy-json", "Copy as _JSON", () => Copy(_doc.ToJson(), "JSON"), "TbCopy", null, "Put the machine as a JSON document on the clipboard");
            yield return ProvidedCommand.From("copy-code", "Copy as _C#", () => Copy(StateCode.ToCSharp(_doc), "C# code"), "TbCopy", null, "Put code that builds the machine with Telefrag.State on the clipboard");
            yield return ProvidedCommand.From("delete", "_Delete Saved Machine", DeleteSaved, "TbDelete", () => Saved.IsAvailable && _savedName.Length > 0 && Saved.Exists(_savedName), "Remove the machine from the Silo");
        }
    }

    private void Copy(string text, string what)
    {
        LabUi.Copy(text);
        Status = $"Copied the {what}";
    }

    private void AddAnyTransition()
    {
        if (_selectedState is not null) AddTransition(StateTransitionDef.Any, _selectedState);
    }

    /// <summary>Removes what is selected.</summary>
    public void DeleteSelected()
    {
        if (_selectedTransition is not null) DeleteTransition(_selectedTransition);
        else if (_selectedState is not null) DeleteState(_selectedState);
    }

    // ---- terminal --------------------------------------------------------------------------------

    public override IReadOnlyList<LabTerminalCommand> TerminalCommands =>
    [
        new("state", "state                   where the machine is, its fields and what can be taken", _ => Done(Describe())),
        new("fire", "fire <state|id>         take the transition that leads to the state (or has the id)", call => Done(FireCommand(call.Rest))),
        new("update", "update                  take the transition that goes first of those that can be taken", _ => Done(Update().Message)),
        new("run", "run                     update until nothing can be taken", _ => Done(RunCommand())),
        new("reset", "reset                   start the run again", _ => Done(ResetCommand())),
        new("set", "set <field> <value>     change a field of the run", call => Done(SetCommand(call.Args))),
        new("add", "add <name>              add a state", call => Done(AddCommand(call.Rest))),
        new("link", "link <from> <to>        add a transition (from * is from any state)", call => Done(LinkCommand(call.Args))),
        new("initial", "initial <state>        choose the state the machine starts in", call => Done(InitialCommand(call.Rest))),
        new("save", "save [name]             keep the machine in the Silo", call => Done(Save(call.Rest.Length > 0 ? call.Rest : _doc.Name.Length > 0 ? _doc.Name : "Machine"))),
        new("list", "list                    the machines kept in the Silo", _ => Done(Saved.IsAvailable ? string.Join('\n', Saved.Names()) : "No Silo is open.")),
        new("open", "open <name>             open a machine kept in the Silo", call => Done(OpenCommand(call.Rest))),
        new("code", "code                    the machine as Telefrag.State code", _ => Done(StateCode.ToCSharp(_doc))),
        new("json", "json                    the machine as a JSON document", _ => Done(_doc.ToJson()))
    ];

    private static Task<string?> Done(string? text) => Task.FromResult(text);

    private string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine(_run.Current is null ? "Not in a state." : $"In {_run.Current} after {_run.Steps} step(s).");
        foreach (var (name, value) in _run.Values) text.AppendLine($"  {name} = {StateValues.Show(value)}");
        foreach (var c in _run.Candidates()) text.AppendLine($"  {(c.Enabled ? "can" : "cannot")} {c.Transition}{(c.Transition.Priority != 0 ? $" (priority {c.Transition.Priority})" : string.Empty)}{(c.Reason is null ? string.Empty : ": " + c.Reason)}");
        return text.ToString().TrimEnd();
    }

    private string FireCommand(string word)
    {
        if (word.Length == 0) return "Which one? fire <state|id>";

        var candidates = _run.Candidates();
        var pick = candidates.FirstOrDefault(c => c.Transition.Id == word) ?? candidates.Where(c => string.Equals(c.Transition.To, word, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.Enabled).FirstOrDefault();
        return pick is null ? $"Nothing that leaves {_run.Current} leads to '{word}'." : Fire(pick.Transition).Message;
    }

    private string RunCommand()
    {
        RunToRest();
        return Status;
    }

    private string ResetCommand()
    {
        ResetRun();
        return Status;
    }

    private string SetCommand(IReadOnlyList<string> args)
    {
        if (args.Count < 2) return "set <field> <value>";

        return SetField(args[0], string.Join(' ', args.Skip(1))) ?? $"{args[0]} = {StateValues.Show(_run.Values.GetValueOrDefault(args[0]))}";
    }

    private string AddCommand(string name)
    {
        if (name.Length == 0) return "Which name? add <name>";
        if (_doc.Find(name) is not null) return $"There is already a state called '{name}'.";

        AddState(name: name);
        return $"Added {name}.";
    }

    private string LinkCommand(IReadOnlyList<string> args)
    {
        if (args.Count != 2) return "link <from> <to>";
        if (args[0] != StateTransitionDef.Any && _doc.Find(args[0]) is null) return $"There is no state '{args[0]}'.";
        if (_doc.Find(args[1]) is not { } to) return $"There is no state '{args[1]}'.";

        var t = AddTransition(args[0] == StateTransitionDef.Any ? args[0] : _doc.Find(args[0])!.Name, to.Name);
        return $"Added {t} ({t.Id}).";
    }

    private string InitialCommand(string name)
    {
        if (_doc.Find(name) is not { } state) return $"There is no state '{name}'.";

        SetInitial(state.Name);
        return Status;
    }

    private string OpenCommand(string name)
    {
        if (name.Length == 0) return "Which one? open <name>";

        OpenSaved(name);
        return Status;
    }

    public override UIElement BuildMainView()
    {
        return new StateView(this);
    }
}
