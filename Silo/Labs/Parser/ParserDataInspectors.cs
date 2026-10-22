using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Silo.Labs.Parser;

/// <summary>
///     Fixed strings to try the spec on, in sets. A sample only ever <i>matches</i> or <i>does not match</i> (shown in neutral colors: a sample is
///     not right or wrong); what is expected of samples is a test (see <see cref="TestsInspector" />), which passes or fails.
/// </summary>
internal sealed class SamplesInspector : ParserInspector
{
    private sealed record Row(string Sample, bool Matched, string Detail);

    private string?     _set;
    private StackPanel? _top;
    private ListBox?    _list;
    private TextBox?    _add;

    public SamplesInspector(ParserWorkspace workspace) : base(workspace)
    {
    }

    public override string Id => "samples";

    public override LabPaneKind Kind => LabPaneKind.Entity;

    public override string Title => "Samples";

    public override string IconKey => "Droplet";

    public override LabDock Dock => LabDock.Bottom;

    public override int Lane => 1;

    protected override UIElement Create()
    {
        _top = new StackPanel();
        _add = new TextBox { Margin = new Thickness(6, 2, 6, 2), FontFamily = LabUi.Mono, ToolTip = "A string to add to the set; Enter adds it" };
        _add.KeyDown += (_, e) =>
                        {
                            if (e.Key == Key.Enter) AddSample(_add.Text);
                        };

        _list = List();
        _list.KeyDown += (_, e) =>
                         {
                             if (e.Key != Key.Delete || _list.SelectedItem is not ListBoxItem { Tag: Row row }) return;

                             RemoveSample(row.Sample);
                             e.Handled = true;
                         };

        var bottom = Bar(LabUi.Tool("Add Sample", "Plus", () => AddSample(_add.Text), "Add the string above to the set"),
                         LabUi.Tool("Paste Lines", "TbPaste", PasteLines, "Add each line on the clipboard as a sample"),
                         LabUi.Tool("Clear Set", "Clear", ClearSet, "Take every sample out of the set"));
        var bottomPanel = new StackPanel();
        bottomPanel.Children.Add(_add);
        bottomPanel.Children.Add(bottom);
        return Layout(_top, _list, bottomPanel);
    }

    private SampleSet? Set => Ws.Book.Samples.FirstOrDefault(s => string.Equals(s.Name, _set, StringComparison.OrdinalIgnoreCase));

    protected override void Refresh()
    {
        if (_top is null || _list is null) return;

        if (Set is null) _set = Ws.Book.Samples.FirstOrDefault()?.Name;

        _top.Children.Clear();
        var bar = new WrapPanel { Margin = new Thickness(6, 4, 6, 4) };
        bar.Children.Add(ParserUi.Combo(Ws.Book.Samples.Select(s => s.Name), _set, n =>
                                                                                    {
                                                                                        _set = n;
                                                                                        Refresh();
                                                                                    }, false, 130));
        bar.Children.Add(LabUi.IconButton("Plus", NewSet, "A new set of samples"));
        bar.Children.Add(LabUi.IconButton("TbDelete", DeleteSet, "Delete the set"));
        if (Set is { } set)
        {
            bar.Children.Add(LabUi.Label("Show against", muted: true));
            var rules = new List<string> { "(entry point)" };
            rules.AddRange(Ws.Doc.Rules.Select(r => r.Name));
            bar.Children.Add(ParserUi.Combo(rules, set.Rule ?? "(entry point)", r =>
                                                                                  {
                                                                                      set.Rule = r == "(entry point)" ? null : r;
                                                                                      Ws.BookChanged();
                                                                                  }, false, 120));
            bar.Children.Add(LabUi.Tool("Make Test", "Flask", () => MakeTest(set), "Make a test of this set: say whether all of it should match or none"));
        }

        _top.Children.Add(bar);

        _list.Items.Clear();
        if (Set is null)
        {
            Message(_list, "No sample sets yet: the plus button makes one.");
            return;
        }

        var compiled = Ws.Compiled;
        foreach (string sample in Set.Items.ToList())
        {
            var outcome = SpecEval.Eval(compiled, sample, Set.Rule);
            _list.Items.Add(RowItem(new Row(sample, outcome.Success, outcome.Summary), Set));
        }
    }

    private ListBoxItem RowItem(Row row, SampleSet set)
    {
        // neutral: a sample matches or it does not; there is no wrong answer (a test decides that)
        var line = LabUi.Row("Droplet", "\"" + row.Sample + "\"", row.Matched ? "matches" : "does not match", row.Detail, iconBrush: row.Matched ? ParserUi.Blue : ParserUi.Gray, mono: true);
        var item = Entry(line, row, row.Sample + "\n" + row.Detail);

        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("Edit", "Property", () =>
                                                    {
                                                        RemoveSample(row.Sample);
                                                        if (_add is not null)
                                                        {
                                                            _add.Text = row.Sample;
                                                            _add.Focus();
                                                        }
                                                    }));
        menu.Items.Add(MenuEntry("Copy", "TbCopy", () => LabUi.Copy(row.Sample)));
        menu.Items.Add(MenuEntry("Remove", "TbDelete", () => RemoveSample(row.Sample)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Test: This Should Match", "Flask", () => Ws.AddTest("Matches " + LabUi.Shorten(row.Sample, 20), [], Expectation.Matches, set.Rule, [row.Sample])));
        menu.Items.Add(MenuEntry("Test: This Should Not Match", "Flask", () => Ws.AddTest("Rejects " + LabUi.Shorten(row.Sample, 20), [], Expectation.DoesNotMatch, set.Rule, [row.Sample])));
        item.ContextMenu = menu;
        return item;
    }

    private static MenuItem MenuEntry(string header, string icon, Action run)
    {
        var item = new MenuItem { Header = header, Icon = LabUi.Icon(icon) };
        item.Click += (_, _) => run();
        return item;
    }

    private void MakeTest(SampleSet set)
    {
        int answer = Silo.Ui.Windows.PromptDialog.Show("Make a test", $"What should be true of the samples in '{set.Name}'?", ["All should match", "None should match", "Cancel"]).Button;
        if (answer is < 0 or > 1) return;

        var test = Ws.AddTest(answer == 0 ? $"{set.Name} match" : $"{set.Name} are rejected", [set.Name], answer == 0 ? Expectation.Matches : Expectation.DoesNotMatch, set.Rule);
        Ws.RunTest(test);
    }

    private void NewSet()
    {
        string? name = Ask("New sample set", "Name of the set:", Ws.Book.Samples.Count == 0 ? "Samples" : "Set " + (Ws.Book.Samples.Count + 1));
        if (string.IsNullOrWhiteSpace(name)) return;
        if (Ws.Book.Samples.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) return;

        Ws.Book.Samples.Add(new SampleSet { Name = name });
        _set = name;
        Ws.BookChanged();
    }

    private void DeleteSet()
    {
        if (Set is not { } set || !ParserUi.Confirm($"Delete the sample set '{set.Name}'?")) return;

        Ws.Book.Samples.Remove(set);
        _set = null;
        Ws.BookChanged();
    }

    private void AddSample(string text)
    {
        if (text.Length == 0) return;

        if (Set is null)
        {
            NewSet();
            if (Set is null) return;
        }

        Set.Items.Add(text);
        if (_add is not null) _add.Clear();
        Ws.BookChanged();
    }

    /// <summary>Adds text to a set (the one being shown; made when there is none): the Tests menu's Add Sample and Paste to Samples.</summary>
    public void AddSamples(IEnumerable<string> samples)
    {
        if (Set is null)
        {
            NewSet();
            if (Set is null) return;
        }

        foreach (string sample in samples.Where(s => s.Length > 0)) Set.Items.Add(sample);
        Ws.BookChanged();
    }

    private void RemoveSample(string sample)
    {
        if (Set is not { } set) return;

        int at = set.Items.IndexOf(sample);
        if (at < 0) return;

        set.Items.RemoveAt(at);
        Ws.BookChanged();
    }

    private void PasteLines()
    {
        try
        {
            AddSamples(Clipboard.GetText().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // the clipboard is in use by another program
        }
    }

    private void ClearSet()
    {
        if (Set is not { Items.Count: > 0 } set || !ParserUi.Confirm($"Take every sample out of '{set.Name}'?")) return;

        set.Items.Clear();
        Ws.BookChanged();
    }

    /// <summary>Puts the cursor in the box for a new sample (the Tests menu's Add Sample).</summary>
    public void FocusAdd()
    {
        _add?.Focus();
    }
}

/// <summary>Samples and what is expected of them, as pass or fail, run as a Test Explorer runs tests.</summary>
internal sealed class TestsInspector : ParserInspector
{
    private ListBox?        _list;
    private ContentControl? _editor;
    private string?         _selected;
    private bool            _autoRun;
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);

    public TestsInspector(ParserWorkspace workspace) : base(workspace)
    {
    }

    public override string Id => "tests";

    public override LabPaneKind Kind => LabPaneKind.Entity;

    public override string Title => "Tests";

    public override string IconKey => "Flask";

    public override LabDock Dock => LabDock.Bottom;

    public override int Lane => 0;

    /// <summary>The name of the selected test, if any.</summary>
    public string? SelectedName => _selected;

    protected override void Noticed(WorkspaceChange change)
    {
        if (_autoRun && (change & (WorkspaceChange.Rules | WorkspaceChange.Loaded)) != 0 && Ws.Book.Tests.Count > 0)
            Application.Current?.Dispatcher.InvokeAsync(() => Ws.RunTests(), DispatcherPriority.Background);
    }

    protected override UIElement Create()
    {
        _list = List();
        _list.SelectionChanged += (_, _) =>
                                  {
                                      if ((_list.SelectedItem as ListBoxItem)?.Tag is TestCase test && !string.Equals(_selected, test.Name, StringComparison.OrdinalIgnoreCase))
                                      {
                                          _selected = test.Name;
                                          ShowEditor();
                                      }
                                  };
        _list.KeyDown += (_, e) =>
                         {
                             if (e.Key != Key.Delete) return;

                             DeleteTest();
                             e.Handled = true;
                         };

        var auto = LabUi.Check("Run on change", _autoRun, v => _autoRun = v, "Run every test again whenever the spec changes");

        var bar = Bar(LabUi.Tool("Run All", "RunAll", () => Ws.RunTests(), "Run every test"),
                      LabUi.Tool("Run Failing", "RunFailing", () => Ws.RunFailingTests(), "Run again the tests that failed"),
                      LabUi.Tool("Run Test", "Run", RunSelected, "Run the selected test"),
                      LabUi.Tool("Add Test", "Plus", NewTest, "A new test, from the sample sets"),
                      LabUi.Tool("Delete", "TbDelete", DeleteTest, "Delete the selected test"),
                      auto);

        _editor = new ContentControl();
        return Layout(bar, _list, _editor);
    }

    private TestCase? Selected => Ws.Book.Tests.FirstOrDefault(t => string.Equals(t.Name, _selected, StringComparison.OrdinalIgnoreCase));

    protected override void Refresh()
    {
        if (_list is null) return;

        _list.Items.Clear();
        string root = Ws.Doc.Root ?? Ws.Doc.Rules.FirstOrDefault()?.Name ?? "(no rule)";
        foreach (var group in Ws.Book.Tests.GroupBy(t => t.Rule ?? root).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var results = group.Select(t => Ws.Results.TryGetValue(t.Name, out var r) ? r : null).ToList();
            var state   = Aggregate(results);
            _list.Items.Add(Entry(LabUi.Row("Rule", group.Key, "rule", Count(results), tree: true, iconBrush: Tint(state), bold: true), null, null, selectable: false));

            foreach (var test in group)
            {
                Ws.Results.TryGetValue(test.Name, out var result);
                bool open  = _open.Contains(test.Name) || result is { State: TestState.Failed };
                var  tstate = result?.State ?? TestState.NotRun;
                string name = test.Name;
                var row = LabUi.Row("Flask", test.Name, ExpectText(test.Expect), result?.Summary, tree: true, level: 1, expanded: result is { Outcomes.Count: > 0 } ? open : null,
                                    toggle: () =>
                                            {
                                                if (!_open.Remove(name)) _open.Add(name);
                                                Refresh();
                                            }, iconBrush: Tint(tstate));
                var item = Entry(row, test, StateText(tstate));
                item.IsSelected = string.Equals(test.Name, _selected, StringComparison.OrdinalIgnoreCase);
                item.ContextMenu = TestMenu(test);
                _list.Items.Add(item);

                if (open && result is not null)
                    foreach (var o in result.Outcomes)
                        _list.Items.Add(Entry(LabUi.Row("Droplet", "\"" + o.Sample + "\"", o.Passed ? "passed" : "failed", o.Detail, tree: true, level: 2, iconBrush: o.Passed ? ParserUi.Green : ParserUi.Red, mono: true), null, o.Detail, selectable: false));
            }
        }

        if (Ws.Book.Tests.Count == 0) Message(_list, "No tests yet: Add Test makes one from the sample sets.");

        ShowEditor();
    }

    private ContextMenu TestMenu(TestCase test)
    {
        var menu = new ContextMenu();

        MenuItem Item(string header, string icon, Action run)
        {
            var item = new MenuItem { Header = header, Icon = LabUi.Icon(icon) };
            item.Click += (_, _) => run();
            return item;
        }

        menu.Items.Add(Item("Run Test", "Run", () => Ws.RunTest(test)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete", "TbDelete", () =>
                                                  {
                                                      _selected = test.Name;
                                                      DeleteTest();
                                                  }));
        return menu;
    }

    private static string Count(List<TestResult?> results)
    {
        int ran = results.Count(r => r is not null);
        return ran == 0 ? $"{results.Count} not run" : $"{results.Count(r => r?.State == TestState.Passed)}/{results.Count} passed";
    }

    private static string ExpectText(Expectation e) => e switch { Expectation.Matches => "should match", Expectation.DoesNotMatch => "should not match", _ => "value equals" };

    private static string StateText(TestState state) => state switch { TestState.Passed => "Passed", TestState.Failed => "Failed", _ => "Not run" };

    private static Brush Tint(TestState state) => state switch { TestState.Passed => ParserUi.Green, TestState.Failed => ParserUi.Red, _ => ParserUi.Gray };

    private static TestState Aggregate(List<TestResult?> results)
    {
        if (results.Any(r => r?.State == TestState.Failed)) return TestState.Failed;
        return results.Count > 0 && results.All(r => r?.State == TestState.Passed) ? TestState.Passed : TestState.NotRun;
    }

    /// <summary>Runs the selected test (the Tests menu's Run Test).</summary>
    public void RunSelected()
    {
        if (Selected is { } test) Ws.RunTest(test);
        else Warn("Select a test in the Tests pane first.");
    }

    /// <summary>Makes a test (asks for its name) from all the sample sets that there are.</summary>
    public void NewTest()
    {
        string? asked = Ask("New test", "Name of the test:", Ws.UniqueTestName(Ws.Book.Tests.Count == 0 ? "Matches" : "Test"));
        if (string.IsNullOrWhiteSpace(asked)) return;

        var set = Ws.Book.Samples.FirstOrDefault();
        var test = Ws.AddTest(asked, set is null ? [] : [set.Name], Expectation.Matches);
        _selected = test.Name;
        Ws.BookChanged();
    }

    private void DeleteTest()
    {
        if (Selected is not { } test) return;

        Ws.Book.Tests.Remove(test);
        Ws.Results.Remove(test.Name);
        _selected = null;
        Ws.BookChanged();
    }

    private void ShowEditor()
    {
        if (_editor is null) return;

        if (Selected is not { } test)
        {
            _editor.Content = null;
            return;
        }

        var panel = new StackPanel { Margin = new Thickness(6, 4, 6, 6) };

        void Changed(Action change)
        {
            change();
            Ws.Results.Remove(test.Name);
            Ws.BookChanged();
        }

        var name = new WrapPanel();
        name.Children.Add(LabUi.Label("Test", muted: true));
        name.Children.Add(ParserUi.Field(test.Name, t =>
                                                    {
                                                        string old = test.Name;
                                                        if (string.IsNullOrWhiteSpace(t) || Ws.Book.Tests.Any(x => !ReferenceEquals(x, test) && string.Equals(x.Name, t.Trim(), StringComparison.OrdinalIgnoreCase))) return;

                                                        Changed(() => test.Name = t.Trim());
                                                        Ws.Results.Remove(old);
                                                        _selected = test.Name;
                                                    }, 150));
        var rules = new List<string> { "(entry point)" };
        rules.AddRange(Ws.Doc.Rules.Select(r => r.Name));
        name.Children.Add(LabUi.Label("Rule", muted: true));
        name.Children.Add(ParserUi.Combo(rules, test.Rule ?? "(entry point)", r => Changed(() => test.Rule = r == "(entry point)" ? null : r), false, 120));
        name.Children.Add(LabUi.Label("Expect", muted: true));
        name.Children.Add(ParserUi.Combo(["Matches", "DoesNotMatch", "ValueEquals"], test.Expect.ToString(), e => Changed(() => test.Expect = Enum.Parse<Expectation>(e)), false, 120));
        panel.Children.Add(name);

        if (test.Expect == Expectation.ValueEquals)
        {
            var value = new WrapPanel();
            value.Children.Add(LabUi.Label("Value", muted: true));
            value.Children.Add(ParserUi.Field(test.ExpectedValue, t => Changed(() => test.ExpectedValue = t), 260, "What the value reads as in the Terminal (byte[6] { 20 9F ... })"));
            panel.Children.Add(value);
        }

        if (Ws.Book.Samples.Count > 0)
        {
            panel.Children.Add(LabUi.Label("Uses the samples of", muted: true));
            var boxes = Ws.Book.Samples.Select(set =>
                                               {
                                                   string setName = set.Name;
                                                   return (UIElement)LabUi.Check(set.Name, test.Samples.Contains(setName, StringComparer.OrdinalIgnoreCase), on => Changed(() =>
                                                                                                                                                                       {
                                                                                                                                                                           test.Samples.RemoveAll(s => string.Equals(s, setName, StringComparison.OrdinalIgnoreCase));
                                                                                                                                                                           if (on) test.Samples.Add(setName);
                                                                                                                                                                       }));
                                               });
            panel.Children.Add(LabUi.Checks(boxes));
        }

        var extra = new TextBox { AcceptsReturn = true, Height = 54, FontFamily = LabUi.Mono, Text = string.Join("\n", test.Extra), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "Strings of its own, one per line" };
        string before = extra.Text;
        extra.LostKeyboardFocus += (_, _) =>
                                   {
                                       if (extra.Text == before) return;

                                       Changed(() => test.Extra = extra.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList());
                                   };
        panel.Children.Add(LabUi.Label("Own samples (one per line)", muted: true));
        panel.Children.Add(extra);

        var border = new Border { Child = panel, BorderThickness = new Thickness(0, 1, 0, 0) };
        border.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        _editor.Content = border;
    }
}

/// <summary>A REPL: <c>"text" as &lt;rule&gt;</c> tried on the spec as it is now (see <see cref="ParserRepl" />).</summary>
internal sealed class TerminalInspector : ParserInspector
{
    private readonly List<string> _history = [];
    private          int          _historyAt;
    private          TextBox?     _output;
    private          TextBox?     _input;

    public TerminalInspector(ParserWorkspace workspace) : base(workspace)
    {
    }

    public override string Id => "terminal";

    public override string Title => "Terminal";

    public override string IconKey => "Terminal";

    public override LabDock Dock => LabDock.Bottom;

    public override int Lane => 2;

    protected override WorkspaceChange Interesting => WorkspaceChange.None;

    protected override UIElement Create()
    {
        _output = WebUi.Output();
        _output.AppendText("Try text on the spec:  \"209F.CA9F.19CC\" as [mac-addr]   (no quotes or `as`: the entry point)\n"
                           + "More  as  convert:  \"1234ABCD99A0\" as mac-addr as hex-colon.   Targets: rule, syntax:x, enum:X, type:int, tokens\n");

        _input = new TextBox { FontFamily = LabUi.Mono, Margin = new Thickness(6, 4, 6, 6), ToolTip = "Enter runs it; Up and Down go through what was typed" };
        _input.PreviewKeyDown += OnKey;
        return Layout(null, _output, _input);
    }

    protected override void Refresh()
    {
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_input is null || _output is null) return;

        switch (e.Key)
        {
            case Key.Enter when _input.Text.Trim().Length > 0:
            {
                string line = _input.Text.Trim();
                _input.Clear();
                _history.Add(line);
                _historyAt = _history.Count;
                _output.AppendText("> " + line + "\n");
                _output.AppendText(Run(line) + "\n");
                _output.ScrollToEnd();
                e.Handled = true;
                break;
            }
            case Key.Up when _historyAt > 0:
                _input.Text       = _history[--_historyAt];
                _input.CaretIndex = _input.Text.Length;
                e.Handled         = true;
                break;
            case Key.Down when _historyAt < _history.Count:
                _historyAt++;
                _input.Text       = _historyAt < _history.Count ? _history[_historyAt] : string.Empty;
                _input.CaretIndex = _input.Text.Length;
                e.Handled         = true;
                break;
        }
    }

    private string Run(string line)
    {
        if (string.Equals(line, "clear", StringComparison.OrdinalIgnoreCase))
        {
            _output?.Clear();
            return string.Empty;
        }

        try
        {
            return ParserRepl.Run(Ws, line);
        }
        catch (Exception ex)
        {
            return "error: " + ex.Message;
        }
    }
}
