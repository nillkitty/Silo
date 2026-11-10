using System.Windows.Controls;
using System.Windows.Media;

namespace Silo.Labs.States;

/// <summary>Edits what is selected on the canvas: the machine (name, initial state, fields), a state, or a transition with its constraints and effects.</summary>
internal sealed class StateDesignInspector : LabInspector
{
    private readonly StateLabSession _session;
    private readonly StackPanel      _content = new();
    private          bool            _pending;
    private          bool            _ready;

    public StateDesignInspector(StateLabSession session)
    {
        _session                  =  session;
        session.Loaded           += (_, _) => Rebuild();
        session.SelectionChanged += (_, _) => Rebuild();
    }

    public override string Id => "design";

    public override string Title => "Design";

    public override string IconKey => "Settings";

    public override LabPaneKind Kind => LabPaneKind.Entity;

    protected override UIElement BuildView()
    {
        _ready = true;
        Fill();
        return new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    public override void OnShown() => Fill();

    /// <summary>Builds the editor again once the control that asked for it is done (a box is not taken from under the hand that is using it).</summary>
    private void Rebuild()
    {
        if (!_ready || _pending) return;

        _pending = true;
        Application.Current?.Dispatcher.InvokeAsync(() =>
                                                    {
                                                        _pending = false;
                                                        Fill();
                                                    }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Fill()
    {
        _content.Children.Clear();
        if (_session.SelectedTransition is { } transition) Transition(transition);
        else if (_session.SelectedState is { } name && _session.Doc.Find(name) is { } state) State(state);
        else Machine();
    }

    private string[] StateNames => _session.Doc.States.Select(s => s.Name).Where(n => n.Length > 0).ToArray();

    private string[] FieldNames => _session.Doc.Fields.Select(f => f.Name).Where(n => n.Length > 0).ToArray();

    private void Add(UIElement element) => _content.Children.Add(element);

    private static int Number(string text) => int.TryParse(text.Trim(), out int n) ? n : 0;

    // ---- the machine -----------------------------------------------------------------------------

    private void Machine()
    {
        var doc = _session.Doc;
        Add(LabUi.Heading("State machine"));
        Add(LabUi.Form([
                           ("Name", LabUi.Field(doc.Name, v => _session.Edit(d => d.Name = v.Trim()), 160, "What the machine is called in the Silo")),
                           ("Description", LabUi.Field(doc.Description, v => _session.Edit(d => d.Description = v), 160)),
                           ("Initial state", LabUi.Combo(StateNames, doc.Initial, name => _session.SetInitial(name), 160))
                       ], 90));

        Add(LabUi.Heading("Fields"));
        Add(LabUi.Label("What constraints look at and effects change. A run starts with each field's default.", muted: true, wrap: true).Also(t => t.Margin = new Thickness(6, 0, 6, 4)));
        foreach (var field in doc.Fields) Add(FieldRow(field));
        Add(LabUi.Bar(LabUi.Button("Add Field", () => _session.EditStructure(d => d.Fields.Add(new StateFieldDef { Name = NewFieldName(d) })), "Add a number, text or true/false field", icon: "Plus")));
        Add(LabUi.Label("Select a state or a transition on the canvas to edit it; double-click empty space to add a state.", muted: true, wrap: true).Also(t => t.Margin = new Thickness(6, 10, 6, 6)));
    }

    private static string NewFieldName(StateDoc doc)
    {
        for (int i = doc.Fields.Count + 1;; i++)
            if (doc.FindField($"Field{i}") is null)
                return $"Field{i}";
    }

    private UIElement FieldRow(StateFieldDef field)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 1, 4, 1) };
        row.Children.Add(LabUi.Field(field.Name, v => Rename(field, v.Trim()), 90, "The name of the field"));
        row.Children.Add(LabUi.Combo(Enum.GetNames<StateFieldKind>(), field.Kind.ToString(), k => _session.Edit(_ => field.Kind = Enum.Parse<StateFieldKind>(k)), 70));
        row.Children.Add(LabUi.Field(field.Default, v => _session.Edit(_ => field.Default = v), 70, "The value a run starts with"));
        row.Children.Add(LabUi.IconButton("TbDelete", () => _session.EditStructure(d => d.Fields.Remove(field)), "Remove the field"));
        return row;
    }

    /// <summary>Renames a field and what refers to it.</summary>
    private void Rename(StateFieldDef field, string name)
    {
        string old = field.Name;
        _session.EditStructure(d =>
                               {
                                   field.Name = name;
                                   foreach (var t in d.Transitions)
                                   {
                                       foreach (var c in t.Constraints.Where(c => string.Equals(c.Field, old, StringComparison.OrdinalIgnoreCase))) c.Field = name;
                                       foreach (var e in t.Effects.Where(e => string.Equals(e.Field, old, StringComparison.OrdinalIgnoreCase))) e.Field = name;
                                   }
                               });
    }

    // ---- a state ---------------------------------------------------------------------------------

    private void State(StateDef state)
    {
        var doc = _session.Doc;
        Add(LabUi.Heading($"State {state.Name}"));
        Add(LabUi.Form([
                           ("Name", LabUi.Field(state.Name, v =>
                                                            {
                                                                if (_session.RenameState(state.Name, v) is { } problem) _session.Say(problem);
                                                            }, 160)),
                           ("Priority", LabUi.Field(state.Priority.ToString(), v => _session.Edit(_ => state.Priority = Number(v)), 60, "Which state wins when more than one can be entered: the higher")),
                           ("Description", LabUi.Field(state.Description, v => _session.Edit(_ => state.Description = string.IsNullOrWhiteSpace(v) ? null : v), 160))
                       ], 90));

        bool initial = string.Equals(doc.Initial, state.Name, StringComparison.OrdinalIgnoreCase);
        Add(LabUi.Bar(LabUi.Button(initial ? "Initial State" : "Make Initial", () => _session.SetInitial(state.Name), "The machine starts in this state", !initial, "Check"),
                      LabUi.Button("Add Any Transition", () => _session.AddTransition(StateTransitionDef.Any, state.Name), "A transition to this state that can be taken from every state", icon: "Plus"),
                      LabUi.Button("Delete", () => _session.DeleteState(state.Name), "Remove the state and the transitions that touch it", icon: "TbDelete")));

        Add(LabUi.Heading("Leaves to"));
        foreach (var t in doc.Transitions.Where(t => string.Equals(t.From, state.Name, StringComparison.OrdinalIgnoreCase))) Add(TransitionLink(t, t.To));
        Add(LabUi.Heading("Comes from"));
        foreach (var t in doc.Transitions.Where(t => string.Equals(t.To, state.Name, StringComparison.OrdinalIgnoreCase))) Add(TransitionLink(t, t.FromAny ? "any state" : t.From));
    }

    private UIElement TransitionLink(StateTransitionDef t, string other)
    {
        var button = LabUi.Button($"{other}{(t.Priority != 0 ? $"  (priority {t.Priority})" : string.Empty)}", () => _session.Select(null, t.Id), "Edit the transition", icon: "FlowDiagram");
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Margin                     = new Thickness(8, 1, 8, 1);
        return button;
    }

    // ---- a transition ----------------------------------------------------------------------------

    private void Transition(StateTransitionDef t)
    {
        var from = new[] { StateTransitionDef.Any }.Concat(StateNames).ToArray();
        Add(LabUi.Heading($"Transition {t}"));
        Add(LabUi.Form([
                           ("From", LabUi.Combo(from, t.From, v => _session.Edit(_ => t.From = v), 160).Also(c => c.ToolTip = "* is any state")),
                           ("To", LabUi.Combo(StateNames, t.To, v => _session.Edit(_ => t.To = v), 160)),
                           ("Priority", LabUi.Field(t.Priority.ToString(), v => _session.Edit(_ => t.Priority = Number(v)), 60, "When several can be taken the higher goes first"))
                       ], 90));

        Add(LabUi.Heading("Constraints (all must hold)"));
        foreach (var c in t.Constraints) Add(ConstraintRow(t, c));
        Add(LabUi.Bar(LabUi.Button("Add Constraint", () => _session.EditStructure(_ => t.Constraints.Add(new StateConstraintDef { Field = FieldNames.FirstOrDefault() ?? string.Empty })), "Add a condition on a field", _session.Doc.Fields.Count > 0, "Plus")));
        if (_session.Doc.Fields.Count == 0) Add(LabUi.Label("Add a field to the machine first (select nothing to edit the machine).", muted: true, wrap: true).Also(x => x.Margin = new Thickness(6, 0, 6, 4)));

        Add(LabUi.Heading("Effects (in order)"));
        foreach (var e in t.Effects) Add(EffectRow(t, e));
        Add(LabUi.Bar(LabUi.Button("Add Effect", () => _session.EditStructure(_ => t.Effects.Add(new StateEffectDef { Kind = FieldNames.Length > 0 ? StateEffectKind.Set : StateEffectKind.Log, Field = FieldNames.FirstOrDefault() ?? string.Empty })),
                                   "Add something that happens when the transition is taken", icon: "Plus"),
                      LabUi.Button("Delete Transition", () => _session.DeleteTransition(t.Id), "Remove the transition", icon: "TbDelete")));
    }

    private UIElement ConstraintRow(StateTransitionDef t, StateConstraintDef c)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 1, 4, 1) };
        row.Children.Add(LabUi.Combo(FieldNames, c.Field, v => _session.Edit(_ => c.Field = v), 90));
        row.Children.Add(LabUi.Combo(StateOps.All, c.Op, v => _session.Edit(_ => c.Op = v), 70));
        row.Children.Add(LabUi.Line(c.Value, v => _session.Edit(_ => c.Value = v), 80, "What the field is compared with"));
        row.Children.Add(LabUi.IconButton("TbDelete", () => _session.EditStructure(_ => t.Constraints.Remove(c)), "Remove the constraint"));
        return row;
    }

    private UIElement EffectRow(StateTransitionDef t, StateEffectDef e)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 1, 4, 1) };
        var field = LabUi.Combo(FieldNames, e.Field, v => _session.Edit(_ => e.Field = v), 90);
        var value = LabUi.Line(e.Value, v => _session.Edit(_ => e.Value = v), 100, "The value to set or add; for Log the text, with {Field} for a field's value");
        row.Children.Add(LabUi.Combo(Enum.GetNames<StateEffectKind>(), e.Kind.ToString(), v =>
                                                                                          {
                                                                                              _session.Edit(_ => e.Kind = Enum.Parse<StateEffectKind>(v));
                                                                                              Show(e, field, value);
                                                                                          }, 70));
        row.Children.Add(field);
        row.Children.Add(value);
        row.Children.Add(LabUi.IconButton("TbDelete", () => _session.EditStructure(_ => t.Effects.Remove(e)), "Remove the effect"));
        Show(e, field, value);
        return row;
    }

    /// <summary>Only the boxes an effect of this kind uses.</summary>
    private static void Show(StateEffectDef e, UIElement field, UIElement value)
    {
        field.Visibility = e.Kind == StateEffectKind.Log ? Visibility.Collapsed : Visibility.Visible;
        value.Visibility = e.Kind == StateEffectKind.Toggle ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>The run: where the machine is, its fields, the transitions that leave the state (and why some cannot be taken), and the log.</summary>
internal sealed class StateTestInspector : LiveInspector
{
    private readonly StateLabSession                 _session;
    private readonly StackPanel                      _fields      = new();
    private readonly StackPanel                      _transitions = new();
    private readonly Dictionary<string, UIElement>   _editors     = new(StringComparer.OrdinalIgnoreCase);
    private          TextBlock?                      _where;
    private          TextBox?                        _log;
    private          string                          _fieldSignature      = "?";
    private          string                          _transitionSignature = "?";
    private          bool                            _syncing;

    public StateTestInspector(StateLabSession session) : base(session)
    {
        _session = session;
    }

    public override string Id => "test";

    public override string Title => "Test";

    public override string IconKey => "Check";

    protected override UIElement Create()
    {
        _where = LabUi.Label(string.Empty, bold: true);
        _where.FontSize = 15;
        _where.Margin   = new Thickness(6, 4, 6, 4);
        var bar = LabUi.Bar(LabUi.Button("Update", () => _session.Update(), "Take the transition that goes first of those that can be taken", icon: "Refresh"),
                            LabUi.Button("Run to Rest", _session.RunToRest, "Update until nothing can be taken (at most 50 steps)", icon: "Refresh"),
                            LabUi.Button("Reset", _session.ResetRun, "Start again from the initial state with the default values", icon: "Refresh"));
        var top = new StackPanel();
        top.Children.Add(_where);
        top.Children.Add(bar);

        var body = new StackPanel();
        body.Children.Add(LabUi.Heading("Fields"));
        body.Children.Add(_fields);
        body.Children.Add(LabUi.Heading("Transitions from here"));
        body.Children.Add(_transitions);

        _log = LabUi.Output();
        _log.Height = 150;
        var logBox = LabUi.Layout(LabUi.Heading("Log"), _log);
        return LabUi.Layout(top, new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, logBox);
    }

    protected override void Refresh()
    {
        if (_where is null || _log is null) return;

        var run = _session.Run;
        _where.Text = run.Current is null ? "Not in a state" : $"In {run.Current}";

        // the boxes are built again only when the fields are different: a box being typed in stays
        string fields = string.Join('|', _session.Doc.Fields.Select(f => $"{f.Name}:{f.Kind}"));
        if (fields != _fieldSignature)
        {
            _fieldSignature = fields;
            BuildFields();
        }

        _syncing = true;
        foreach (var (name, value) in run.Values)
        {
            if (!_editors.TryGetValue(name, out var editor)) continue;

            if (editor is CheckBox check) check.IsChecked = value is true;
            else if (editor is TextBox { IsKeyboardFocused: false } box && box.Text != StateValues.Show(value)) box.Text = StateValues.Show(value);
        }

        _syncing = false;

        var candidates = run.Candidates();
        string signature = string.Join('|', candidates.Select(c => $"{c.Transition.Id}:{c.Enabled}:{c.Reason}")) + "#" + run.Current;
        if (signature != _transitionSignature)
        {
            _transitionSignature = signature;
            BuildTransitions(candidates);
        }

        string log = string.Join(Environment.NewLine, run.Log);
        if (_log.Text != log)
        {
            _log.Text = log;
            _log.ScrollToEnd();
        }
    }

    private void BuildFields()
    {
        _fields.Children.Clear();
        _editors.Clear();
        if (_session.Doc.Fields.Count == 0)
        {
            _fields.Children.Add(LabUi.Label("The machine has no fields.", muted: true).Also(t => t.Margin = new Thickness(6, 0, 6, 4)));
            return;
        }

        foreach (var field in _session.Doc.Fields.Where(f => f.Name.Length > 0 && !_editors.ContainsKey(f.Name)))
        {
            string name = field.Name;
            UIElement editor;
            if (field.Kind == StateFieldKind.Bool)
            {
                var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2) };
                check.Checked   += (_, _) => Set(name, "true", null);
                check.Unchecked += (_, _) => Set(name, "false", null);
                editor           = check;
            }
            else
            {
                var box = LabUi.Line(string.Empty, _ => { }, 100);
                box.TextChanged += (_, _) => Set(name, box.Text, box);
                editor           = box;
            }

            _editors[name] = editor;
            var row = LabUi.Form([($"{name} ({field.Kind.ToString().ToLowerInvariant()})", editor)], 130);
            row.Margin = new Thickness(2, 0, 2, 0);
            _fields.Children.Add(row);
        }
    }

    private void Set(string name, string text, TextBox? box)
    {
        if (_syncing) return;

        string? error = _session.SetField(name, text);
        if (box is null) return;

        if (error is null) box.ClearValue(Control.BorderBrushProperty);
        else box.BorderBrush = LabUi.Red;
        box.ToolTip     = error;
    }

    private void BuildTransitions(IReadOnlyList<StateTransitionStatus> candidates)
    {
        _transitions.Children.Clear();
        if (candidates.Count == 0)
        {
            _transitions.Children.Add(LabUi.Label(_session.Run.Current is null ? "Choose an initial state first." : "Nothing leaves this state: it is an end state.", muted: true).Also(t => t.Margin = new Thickness(6, 0, 6, 4)));
            return;
        }

        bool first = true;
        foreach (var status in candidates)
        {
            var t   = status.Transition;
            var row = new DockPanel { Margin = new Thickness(4, 2, 4, 2), LastChildFill = true };
            var take = LabUi.Button("Take", () => _session.Fire(t), status.Enabled ? "Take this transition" : status.Reason, status.Enabled, "Check");
            DockPanel.SetDock(take, System.Windows.Controls.Dock.Left);
            row.Children.Add(take);

            var text = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
            string next = status.Enabled && first ? "  ← an update takes this" : string.Empty;
            text.Children.Add(LabUi.Label($"→ {t.To}{(t.Priority != 0 ? $"  (priority {t.Priority})" : string.Empty)}{next}", bold: status.Enabled));
            if (status.Reason is { } reason)
            {
                var why = LabUi.Label(reason, wrap: true);
                why.Foreground = LabUi.Amber;
                text.Children.Add(why);
            }

            row.Children.Add(text);
            _transitions.Children.Add(row);
            if (status.Enabled) first = false;
        }
    }
}

/// <summary>The machine as Telefrag.State code and as JSON.</summary>
internal sealed class StateCodeInspector : LiveInspector
{
    private readonly StateLabSession _session;
    private          TextBox?        _text;
    private          bool            _json;

    public StateCodeInspector(StateLabSession session) : base(session)
    {
        _session = session;
    }

    public override string Id => "code";

    public override string Title => "Code";

    public override string IconKey => "Coding";

    public override LabDock Dock => LabDock.Right;

    public override bool VisibleByDefault => false;

    protected override UIElement Create()
    {
        _text = LabUi.Output();
        var bar = LabUi.Bar(LabUi.Check("As JSON", false, on =>
                                                          {
                                                              _json = on;
                                                              Refresh();
                                                          }, "Show the machine as the JSON document the Silo keeps instead of C#"),
                            LabUi.Button("Copy", () => LabUi.Copy(_text.Text), "Put what is shown on the clipboard", icon: "TbCopy"));
        return LabUi.Layout(null, _text, bar);
    }

    protected override void Refresh()
    {
        if (_text is null) return;

        _text.Text = _json ? _session.Doc.ToJson() : StateCode.ToCSharp(_session.Doc);
    }
}
