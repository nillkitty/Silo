using System.Windows.Controls;
using System.Windows.Input;
using Silo.DbModel.Meta;
using Silo.Labs.Web;

namespace Silo.Labs.Schemas;

/// <summary>What promoting the draft would write, and where it goes; the button that does it; and what happened the last time.</summary>
internal sealed class SchemaPromoteInspector(SchemaLabSession session) : LiveInspector(session)
{
    private StackPanel? _panel;

    public override string Id => "promote";

    public override LabPaneKind Kind => LabPaneKind.Entity;

    public override string Title => "Promote";

    public override string IconKey => "Checklist";

    protected override UIElement Create()
    {
        _panel = new StackPanel { Margin = new Thickness(4) };
        return new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    protected override void Refresh()
    {
        if (_panel is null) return;

        var plan = session.Plan;
        _panel.Children.Clear();

        _panel.Children.Add(LabUi.Heading("Where"));
        var target = LabUi.Combo(["Metaverse", "Local world"], session.Draft.Target == PromoteTarget.Meta ? "Metaverse" : "Local world", t => session.SetTarget(t == "Metaverse" ? PromoteTarget.Meta : PromoteTarget.World), 160);
        _panel.Children.Add(LabUi.Bar(target));

        var support = SchemaSilo.Support;
        string Has(bool yes) => yes ? "yes" : "no";
        var what = LabUi.Label(SchemaSilo.IsOpen
                                   ? $"This Silo keeps enums: {Has(support.Enums)}; syntaxes: {Has(support.Syntaxes)}; classes, attributes and type relations: {Has(support.Schema)}" + (support.Schema ? string.Empty : " (its database has no schema store yet)")
                                   : "No Silo is open, so nothing can be promoted.", muted: true, wrap: true);
        what.Margin = new Thickness(8, 0, 8, 4);
        _panel.Children.Add(what);

        _panel.Children.Add(LabUi.Heading("A name the Silo already has"));
        var skip    = new RadioButton { Content = "Leave it as it is (skip)", GroupName = "conflict", IsChecked = !session.ReplaceExisting && !session.KeepBoth, Margin = new Thickness(8, 1, 8, 1) };
        var replace = new RadioButton { Content = "Replace it", GroupName = "conflict", IsChecked = session.ReplaceExisting, Margin = new Thickness(8, 1, 8, 1), ToolTip = "The enum, syntax or class of that name is overwritten with the draft's" };
        var both    = new RadioButton { Content = "Keep both (the draft's is named Copy_of_...)", GroupName = "conflict", IsChecked = session.KeepBoth, Margin = new Thickness(8, 1, 8, 1) };
        void Choose() => session.SetPromoteOptions(replace.IsChecked == true, both.IsChecked == true);
        skip.Checked    += (_, _) => Choose();
        replace.Checked += (_, _) => Choose();
        both.Checked    += (_, _) => Choose();
        foreach (var r in new[] { skip, replace, both })
        {
            r.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
            _panel.Children.Add(r);
        }

        _panel.Children.Add(LabUi.Heading("What would be written"));
        if (plan.Steps.Count == 0) _panel.Children.Add(Note("The draft is empty."));
        foreach (var s in plan.Steps)
        {
            var row = new StackPanel { Margin = new Thickness(8, 2, 8, 2) };
            var head = LabUi.Label($"{s.Action}  {s.Kind.Title()} {s.Name}" + (string.Equals(s.Name, s.StoredAs, StringComparison.Ordinal) ? string.Empty : $"  as {s.StoredAs}"), bold: true);
            head.Foreground = s.Action switch { StepAction.Add or StepAction.Replace or StepAction.Copy => LabUi.Green, StepAction.Blocked => LabUi.Red, _ => LabUi.Gray };
            row.Children.Add(head);
            row.Children.Add(LabUi.Label(s.Why, muted: true, wrap: true));
            _panel.Children.Add(row);
        }

        foreach (var p in plan.Problems.Where(p => p.Severity == SchemaSeverity.Error))
        {
            var line = LabUi.Label($"Error - {p.Where}: {p.Text}", wrap: true);
            line.Foreground = LabUi.Red;
            line.Margin     = new Thickness(8, 2, 8, 2);
            _panel.Children.Add(line);
        }

        var go = LabUi.Button("Promote", () => session.Promote(), plan.HasErrors ? "The draft has errors" : "Write it into the Silo", plan.CanApply, "TbExport");
        _panel.Children.Add(LabUi.Bar(go, LabUi.Button("Refresh from the Silo", () =>
                                                                                {
                                                                                    session.RefreshSnapshot();
                                                                                    session.Say("Looked at the Silo again.");
                                                                                    Refresh();
                                                                                }, "Look at what the Silo holds again")));

        if (session.LastOutcome is { } outcome)
        {
            _panel.Children.Add(LabUi.Heading("Last promotion"));
            foreach (string done in outcome.Done)
            {
                var l = LabUi.Label("Wrote " + done, wrap: true);
                l.Foreground = LabUi.Green;
                l.Margin     = new Thickness(8, 1, 8, 1);
                _panel.Children.Add(l);
            }

            foreach (string failed in outcome.Failed)
            {
                var l = LabUi.Label("Failed: " + failed, wrap: true);
                l.Foreground = LabUi.Red;
                l.Margin     = new Thickness(8, 1, 8, 1);
                _panel.Children.Add(l);
            }
        }
    }

    private static TextBlock Note(string text)
    {
        var t = LabUi.Label(text, muted: true, wrap: true);
        t.Margin = new Thickness(8, 2, 8, 2);
        return t;
    }
}

/// <summary>The draft as an outline or as a C# sketch, to read or copy.</summary>
internal sealed class SchemaTextInspector(SchemaLabSession session) : LiveInspector(session)
{
    private TextBox? _text;
    private string   _mode = "Outline";

    public override string Id => "outline";

    public override string Title => "Outline";

    public override string IconKey => "Document";

    protected override UIElement Create()
    {
        _text = LabUi.Output();
        var mode = LabUi.Combo(["Outline", "C# sketch"], _mode, m =>
                                                                {
                                                                    _mode = m;
                                                                    Refresh();
                                                                }, 110);
        var bar = LabUi.Bar(mode, LabUi.Button("Copy", () => LabUi.Copy(_text.Text), "Put the text on the clipboard"));
        return LabUi.Layout(bar, _text);
    }

    protected override void Refresh()
    {
        if (_text is not null) _text.Text = _mode == "Outline" ? session.Outline : session.CSharp;
    }
}

/// <summary>The draft as JSON, which is what it is kept as: change it here and press Apply (or Ctrl+Enter) to change the draft.</summary>
internal sealed class SchemaJsonInspector(SchemaLabSession session) : LiveInspector(session)
{
    private TextBox?   _text;
    private TextBlock? _error;

    public override string Id => "json";

    public override string Title => "JSON";

    public override string IconKey => "Coding";

    protected override UIElement Create()
    {
        _text = LabUi.Memo(string.Empty, _ => { });
        _text.KeyDown += (_, e) =>
                         {
                             if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) Apply();
                         };
        _error = LabUi.Label(string.Empty, wrap: true);
        _error.Foreground = LabUi.Red;
        _error.Margin     = new Thickness(8, 0, 8, 4);
        var bar = LabUi.Bar(LabUi.Button("Apply", Apply, "Make the draft of this JSON (Ctrl+Enter)"), LabUi.Button("Revert", () =>
                                                                                                                             {
                                                                                                                                 _text.Text  = session.Json;
                                                                                                                                 _error.Text = string.Empty;
                                                                                                                             }, "Go back to the JSON of the draft as it is", icon: "Undo"),
                            LabUi.Button("Copy", () => LabUi.Copy(session.Json), "Put the draft's JSON on the clipboard"));
        var head = new StackPanel();
        head.Children.Add(bar);
        head.Children.Add(_error);
        return LabUi.Layout(head, _text);
    }

    private void Apply()
    {
        if (_text is null || _error is null) return;

        _error.Text = session.ApplyJson(_text.Text) ?? string.Empty;
    }

    protected override void Refresh()
    {
        // what is typed here is not overwritten while the box has the keyboard
        if (_text is null || _text.IsKeyboardFocusWithin) return;

        string json = session.Json;
        if (_text.Text != json)
        {
            _text.Text = json;
            if (_error is not null) _error.Text = string.Empty;
        }
    }
}
