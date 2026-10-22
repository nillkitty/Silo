using System.Windows.Controls;
using System.Windows.Input;
using Silo.Providers.Definitions;
using Silo.Labs.Web;

namespace Silo.Labs.Schemas;

/// <summary>The property editor of the Schema Lab: the form of the selected construct, built from what its kind has (see <see cref="SchemaProps" />).</summary>
internal sealed class SchemaEditor : ContentControl
{
    private readonly SchemaLabSession _session;

    public SchemaEditor(SchemaLabSession session)
    {
        _session                  = session;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment   = VerticalAlignment.Stretch;
        Rebuild();
    }

    /// <summary>The names a reference of the kind can take: the draft's, then the Silo's.</summary>
    private IReadOnlyList<string> NamesOf(ConstructKind kind)
    {
        var draft = _session.Draft.Of(kind).Select(c => c.Name);
        var silo  = _session.Snapshot;
        IEnumerable<string> more = kind switch
                                   {
                                       ConstructKind.Class        => silo.Classes,
                                       ConstructKind.Attribute    => silo.Attributes,
                                       ConstructKind.Enum         => silo.Enums.Select(e => e.Name),
                                       ConstructKind.Syntax       => silo.Syntaxes.Select(s => s.Name),
                                       ConstructKind.Unit         => silo.Units,
                                       ConstructKind.UnitRelation => silo.UnitRelations,
                                       _                          => []
                                   };
        return draft.Concat(more).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Rebuild()
    {
        var panel = new StackPanel { Margin = new Thickness(4) };
        if (_session.Selected is { } item) Fill(panel, item);
        else FillDraft(panel);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = panel };
        Content = scroll;
    }

    private void Again() => Rebuild();

    // ---- nothing selected: the draft itself -------------------------------------------------------

    private void FillDraft(StackPanel panel)
    {
        var d = _session.Draft;
        panel.Children.Add(LabUi.Heading("The draft"));
        var name   = LabUi.Field(d.Name, _session.SetDraftName, 200, "What the draft is called");
        var desc   = LabUi.Field(d.Description, _session.SetDescription, 200, "What the draft is for");
        var target = LabUi.Combo(["Metaverse", "Local world"], d.Target == PromoteTarget.Meta ? "Metaverse" : "Local world", t => _session.SetTarget(t == "Metaverse" ? PromoteTarget.Meta : PromoteTarget.World), 200);
        target.ToolTip = "Where promoting puts the draft: the metaverse (classes, enums, syntaxes) or the local world (as types with fields)";
        panel.Children.Add(LabUi.Form([("Name", name), ("Description", desc), ("Promote to", target)], 90));

        string tally = d.IsEmpty ? "The draft is empty." : string.Join(", ", d.Tally().Select(t => $"{t.Count} {(t.Count == 1 ? t.Kind.Title() : t.Kind.Plural()).ToLowerInvariant()}"));
        var info = LabUi.Label(tally, muted: true, wrap: true);
        info.Margin = new Thickness(8, 6, 8, 2);
        panel.Children.Add(info);

        var hint = LabUi.Label("Select a box in the diagram to edit it, or add one with the buttons above the diagram. Connect joins two classes: the first inherits the second (hold Ctrl on the second to include an auxiliary class instead).", muted: true, wrap: true);
        hint.Margin = new Thickness(8, 6, 8, 2);
        panel.Children.Add(hint);
    }

    // ---- a construct --------------------------------------------------------------------------------

    private void Fill(StackPanel panel, SchemaConstruct item)
    {
        panel.Children.Add(LabUi.Heading(item.Kind.Title()));

        var rows = new List<(string, UIElement)>();
        if (item.Kind == ConstructKind.Relation)
        {
            rows.Add(("Name", LabUi.Label(item.Name, mono: true)));
        }
        else
        {
            var name = LabUi.Field(item.Name, text =>
                                              {
                                                  if (_session.RenameSelected(text) is { } refusal)
                                                  {
                                                      _session.Say(refusal);
                                                      Again();
                                                  }
                                              }, 200, "Press Enter to rename; whatever refers to it follows");
            rows.Add(("Name", name));
        }

        var error = LabUi.Label(string.Empty, wrap: true);
        error.Foreground = LabUi.Red;
        error.Margin     = new Thickness(8, 0, 8, 4);

        foreach (var prop in SchemaProps.For(item.Kind)) rows.Add((prop.Title, ControlFor(item, prop, error)));
        panel.Children.Add(LabUi.Form(rows, 96));
        panel.Children.Add(error);

        switch (item.Kind)
        {
            case ConstructKind.Class:
                ClassParts(panel, item);
                break;
            case ConstructKind.Enum:
                EnumParts(panel, item);
                break;
            case ConstructKind.Attribute:
                AttributeParts(panel, item);
                break;
            case ConstructKind.Syntax:
                SyntaxParts(panel, item);
                break;
        }

        // what is wrong with this one
        var own = _session.Problems.Where(p => p.Kind == item.Kind && string.Equals(p.Name, item.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (own.Count > 0)
        {
            panel.Children.Add(LabUi.Heading("Problems"));
            foreach (var p in own)
            {
                var line = LabUi.Label((p.Severity == SchemaSeverity.Error ? "Error: " : p.Severity == SchemaSeverity.Warning ? "Warning: " : "Note: ") + p.Text, wrap: true);
                line.Margin = new Thickness(8, 1, 8, 1);
                if (p.Severity == SchemaSeverity.Error) line.Foreground = LabUi.Red;
                else if (p.Severity == SchemaSeverity.Warning) line.Foreground = LabUi.Amber;
                else line.Muted();
                panel.Children.Add(line);
            }
        }
    }

    private UIElement ControlFor(SchemaConstruct item, SchemaProp prop, TextBlock error)
    {
        string current = item.Get(prop.Key);

        // sets the property; a refusal is shown and the box goes back to what is set
        bool Set(string? text)
        {
            if (_session.SetProp(prop.Key, text) is { } problem)
            {
                error.Text = problem;
                return false;
            }

            error.Text = string.Empty;
            return true;
        }

        switch (prop.Type)
        {
            case PropType.Bool:
                return LabUi.Check(string.Empty, item.Flag(prop.Key), on => Set(on ? "true" : string.Empty), prop.Hint.Length > 0 ? prop.Hint : null);
            case PropType.Lines:
            {
                var box = new TextBox { Text = current, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = prop.Hint.Length > 0 ? prop.Hint : null };
                string before = current;
                box.LostKeyboardFocus += (_, _) =>
                                         {
                                             if (box.Text == before) return;

                                             before = box.Text;
                                             Set(box.Text);
                                         };
                return box;
            }
            case PropType.Flags:
            {
                var set   = item.List(prop.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var boxes = new List<UIElement>();
                foreach (string flag in prop.Choices ?? [])
                    boxes.Add(LabUi.Check(flag, set.Contains(flag), on =>
                                                                    {
                                                                        if (on) set.Add(flag);
                                                                        else set.Remove(flag);
                                                                        Set(string.Join(", ", (prop.Choices ?? []).Where(set.Contains)));
                                                                    }));
                return LabUi.Checks(boxes, 2);
            }
            case PropType.Choice:
            case PropType.Ref:
            {
                bool   attributeType = item.Kind == ConstructKind.Attribute && prop.Key == "type";
                bool   editable      = prop.Type == PropType.Ref || attributeType;
                var    items         = new List<string>();
                if (prop.Type == PropType.Ref) items.Add(string.Empty);
                if (attributeType) items.AddRange(SchemaProps.ClrTypes.Concat(NamesOf(ConstructKind.Enum).Select(n => "enum:" + n)));
                else if (prop.Type == PropType.Choice) items.AddRange(prop.Choices ?? []);
                else if (prop.RefKind is { } kind) items.AddRange(NamesOf(kind).Where(n => !(kind == item.Kind && string.Equals(n, item.Name, StringComparison.OrdinalIgnoreCase))));

                var combo = new ComboBox { IsEditable = editable, MinWidth = 180, ToolTip = prop.Hint.Length > 0 ? prop.Hint : null };
                foreach (string s in items) combo.Items.Add(s);
                string shown = current.Length > 0 ? current : prop.Fallback;
                if (editable) combo.Text = shown;
                else combo.SelectedItem = items.FirstOrDefault(s => string.Equals(s, shown, StringComparison.OrdinalIgnoreCase));

                bool loading = true;
                string last  = shown;
                void Commit(string text)
                {
                    if (loading || text == last) return;

                    last = text;
                    if (!Set(text)) Again();
                }

                if (editable)
                {
                    combo.LostKeyboardFocus += (_, _) => Commit(combo.Text);
                    combo.KeyDown += (_, e) =>
                                     {
                                         if (e.Key == Key.Enter) Commit(combo.Text);
                                     };
                    combo.DropDownClosed += (_, _) => Commit(combo.Text);
                }
                else
                {
                    combo.SelectionChanged += (_, _) =>
                                              {
                                                  if (combo.SelectedItem is string s) Commit(s);
                                              };
                }

                loading = false;
                return combo;
            }
            default:
            {
                var field = LabUi.Field(current, text =>
                                                 {
                                                     if (!Set(text)) Again();
                                                 }, 180, prop.Hint.Length > 0 ? prop.Hint : null);
                return field;
            }
        }
    }

    // ---- class: attributes and relations ------------------------------------------------------------

    private void ClassParts(StackPanel panel, SchemaConstruct c)
    {
        panel.Children.Add(LabUi.Heading("Attributes"));
        if (c.Members.Count == 0)
        {
            var none = LabUi.Label("None yet.", muted: true);
            none.Margin = new Thickness(8, 0, 8, 2);
            panel.Children.Add(none);
        }

        for (int i = 0; i < c.Members.Count; i++)
        {
            int    at    = i;
            string name  = c.Members[i];
            var    attr  = _session.Draft.Find(ConstructKind.Attribute, name);
            var    row   = new DockPanel { Margin = new Thickness(6, 1, 6, 1) };
            var    tools = new StackPanel { Orientation = Orientation.Horizontal };
            tools.Children.Add(Small("▲", () => Move(() => _session.MoveMember(at, -1)), "Move up"));
            tools.Children.Add(Small("▼", () => Move(() => _session.MoveMember(at, 1)), "Move down"));
            tools.Children.Add(Small("✕", () => Move(() => _session.RemoveMember(at)), "Take the attribute out of the class (it stays in the draft)"));
            DockPanel.SetDock(tools, Dock.Right);
            row.Children.Add(tools);
            var label = LabUi.Label(attr is null ? $"{name}  (in the Silo)" : $"{name} : {attr.Get("type", "String")}", mono: true);
            if (attr is not null)
            {
                label.Cursor    = Cursors.Hand;
                label.ToolTip   = "Select this attribute";
                label.MouseLeftButtonUp += (_, _) => _session.Select(attr);
            }

            row.Children.Add(label);
            panel.Children.Add(row);
        }

        var add = new ComboBox { IsEditable = true, MinWidth = 140, ToolTip = "An attribute of the draft or of the Silo" };
        foreach (string n in NamesOf(ConstructKind.Attribute).Where(n => !c.Members.Contains(n, StringComparer.OrdinalIgnoreCase))) add.Items.Add(n);
        var bar = LabUi.Bar(add, LabUi.Button("Add", () =>
                                                     {
                                                         _session.AddMember(add.Text);
                                                         Again();
                                                     }, "Give the class the attribute named in the box"),
                            LabUi.Button("New attribute", () => _session.NewMember(), "Make a new attribute and give it to the class"));
        panel.Children.Add(bar);

        var all = SchemaCheck.AllMembers(_session.Draft, c.Name);
        if (all.Count > c.Members.Count)
        {
            var inherited = LabUi.Label("With what it inherits and includes: " + string.Join(", ", all), muted: true, wrap: true);
            inherited.Margin = new Thickness(8, 2, 8, 2);
            panel.Children.Add(inherited);
        }

        panel.Children.Add(LabUi.Heading("Relations"));
        var relations = _session.Draft.Of(ConstructKind.Relation).Where(r => string.Equals(r.Get("subject"), c.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Get("target"), c.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (relations.Count == 0)
        {
            var none = LabUi.Label("None.", muted: true);
            none.Margin = new Thickness(8, 0, 8, 2);
            panel.Children.Add(none);
        }

        foreach (var r in relations)
        {
            var row  = new DockPanel { Margin = new Thickness(6, 1, 6, 1) };
            var drop = Small("✕", () =>
                                  {
                                      _session.RemoveRelation(r.Name);
                                      Again();
                                  }, "Take the relation away");
            DockPanel.SetDock(drop, Dock.Right);
            row.Children.Add(drop);
            var label = LabUi.Label(r.Name, mono: true);
            row.Children.Add(label);
            panel.Children.Add(row);
        }

        var other = new ComboBox { MinWidth = 140, ToolTip = "Another class of the draft" };
        foreach (string n in _session.Draft.Of(ConstructKind.Class).Select(k => k.Name).Where(n => !string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase))) other.Items.Add(n);
        void Join(bool aux)
        {
            if (other.SelectedItem is not string target) { _session.Say("Choose the class first."); return; }

            if (_session.Connect(c.Name, target, aux) is { } problem) _session.Say(problem);
            Again();
        }

        panel.Children.Add(LabUi.Bar(LabUi.Label(c.Name + " "), other, LabUi.Button("inherits", () => Join(false), "This class inherits the chosen class"), LabUi.Button("includes", () => Join(true), "This class includes the chosen auxiliary class")));
    }

    private static Button Small(string text, Action click, string tip)
    {
        var b = LabUi.Button(text, click, tip);
        b.Padding = new Thickness(4, 0, 4, 0);
        b.MinWidth = 22;
        return b;
    }

    /// <summary>Does a change that moves things around in the lists, then shows the form afresh.</summary>
    private void Move(Action change)
    {
        change();
        Again();
    }

    // ---- enum: values ---------------------------------------------------------------------------------

    private void EnumParts(StackPanel panel, SchemaConstruct e)
    {
        panel.Children.Add(LabUi.Heading("Values"));
        if (e.Values.Count == 0)
        {
            var none = LabUi.Label("None yet.", muted: true);
            none.Margin = new Thickness(8, 0, 8, 2);
            panel.Children.Add(none);
        }

        var grid = new Grid { Margin = new Thickness(6, 0, 6, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < e.Values.Count; i++)
        {
            int at = i;
            var v  = e.Values[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name  = new TextBox { Text = v.Name, ToolTip = "The label", Margin = new Thickness(1) };
            var value = new TextBox { Text = v.Value ?? string.Empty, ToolTip = "The value (left empty, it is numbered when promoted)", FontFamily = LabUi.Mono, Margin = new Thickness(1) };
            var desc  = new TextBox { Text = v.Description ?? string.Empty, ToolTip = "What it means", Margin = new Thickness(1) };
            void Changed() => _session.SetValue(at, name.Text, value.Text, desc.Text);
            name.TextChanged  += (_, _) => Changed();
            value.TextChanged += (_, _) => Changed();
            desc.TextChanged  += (_, _) => Changed();

            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            tools.Children.Add(Small("▲", () => Move(() => _session.MoveValue(at, -1)), "Move up"));
            tools.Children.Add(Small("▼", () => Move(() => _session.MoveValue(at, 1)), "Move down"));
            tools.Children.Add(Small("✕", () => Move(() => _session.RemoveValue(at)), "Take the value out"));

            Grid.SetRow(name, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            Grid.SetRow(desc, i);
            Grid.SetColumn(desc, 2);
            Grid.SetRow(tools, i);
            Grid.SetColumn(tools, 3);
            grid.Children.Add(name);
            grid.Children.Add(value);
            grid.Children.Add(desc);
            grid.Children.Add(tools);
        }

        panel.Children.Add(grid);
        panel.Children.Add(LabUi.Bar(LabUi.Button("Add value", () => Move(_session.AddValue), "Add a value, numbered as the Enums window would")));
    }

    // ---- attribute: who has it -------------------------------------------------------------------------

    private void AttributeParts(StackPanel panel, SchemaConstruct a)
    {
        var users = _session.Draft.Of(ConstructKind.Class).Where(c => c.Members.Contains(a.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        panel.Children.Add(LabUi.Heading("Used by"));
        if (users.Count == 0)
        {
            var none = LabUi.Label("No class of the draft has it.", muted: true);
            none.Margin = new Thickness(8, 0, 8, 2);
            panel.Children.Add(none);
            return;
        }

        var wrap = new WrapPanel { Margin = new Thickness(6, 0, 6, 0) };
        foreach (var c in users)
        {
            var link = LabUi.Button(c.Name, () => _session.Select(c), "Select the class");
            wrap.Children.Add(link);
        }

        panel.Children.Add(wrap);
    }

    // ---- syntax: try a value ---------------------------------------------------------------------------

    private void SyntaxParts(StackPanel panel, SchemaConstruct s)
    {
        panel.Children.Add(LabUi.Heading("Try a value"));
        var result = LabUi.Label(string.Empty, wrap: true);
        result.Margin = new Thickness(8, 2, 8, 2);
        var sample = new TextBox { Margin = new Thickness(6, 0, 6, 0), FontFamily = LabUi.Mono };

        void Test()
        {
            if (!StoredSyntax.TryCreate(SchemaRecords.ToSyntax(s), out var built, out string? why))
            {
                result.Text       = "The syntax itself cannot be built: " + why;
                result.Foreground = LabUi.Red;
                return;
            }

            if (sample.Text.Length == 0)
            {
                result.Text = "Type a value to see whether the syntax accepts it.";
                result.Muted();
                return;
            }

            bool ok = built!.TryParse(sample.Text, out _, out string? error);
            result.Text       = ok ? "Valid." : "Not valid: " + error;
            result.Foreground = ok ? LabUi.Green : LabUi.Red;
        }

        sample.TextChanged += (_, _) => Test();
        panel.Children.Add(sample);
        panel.Children.Add(result);
        Test();
    }
}
