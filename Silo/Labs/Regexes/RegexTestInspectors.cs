using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Silo.Labs.Parser;

namespace Silo.Labs.Regexes;

/// <summary>
///     Fixed strings to try the regex on, in sets, as the Parser Lab's Samples pane does. A sample is only found or not found (shown in neutral
///     colors: it is not right or wrong); what is expected of samples is a test (see <see cref="RegexTestsInspector" />).
/// </summary>
internal sealed class RegexSamplesInspector(RegexLabSession session) : RegexInspector(session)
{
    private string?     _set;
    private StackPanel? _top;
    private ListBox?    _list;
    private TextBox?    _add;

    public override string Id => "samples";

    public override LabPaneKind Kind => LabPaneKind.Entity;

    public override string Title => "Samples";

    public override string IconKey => "Droplet";

    public override LabDock Dock => LabDock.Bottom;

    public override int Lane => 1;

    private SampleSet? Set => Session.Doc.Samples.FirstOrDefault(s => string.Equals(s.Name, _set, StringComparison.OrdinalIgnoreCase));

    protected override UIElement Create()
    {
        _top = new StackPanel();
        _add = LabUi.Field(string.Empty, _ => { }, 200, "A string to add to the set; Enter adds it", true);
        _add.Margin = new Thickness(6, 2, 6, 2);
        _add.KeyDown += (_, e) =>
                        {
                            if (e.Key == Key.Enter) AddSamples([_add.Text]);
                        };

        _list = LabUi.List();
        _list.KeyDown += (_, e) =>
                         {
                             if (e.Key != Key.Delete || _list.SelectedItem is not ListBoxItem { Tag: string sample }) return;

                             RemoveSample(sample);
                             e.Handled = true;
                         };

        var bottom = new StackPanel();
        bottom.Children.Add(_add);
        bottom.Children.Add(LabUi.Bar(LabUi.Tool("Add Sample", "Plus", () => AddSamples([_add.Text]), "Add the string above to the set"),
                                      LabUi.Tool("Paste Lines", "TbPaste", PasteLines, "Add each line on the clipboard as a sample"),
                                      LabUi.Tool("Clear Set", "Clear", ClearSet, "Take every sample out of the set")));
        return LabUi.Layout(_top, _list, bottom);
    }

    protected override void Refresh()
    {
        if (_top is null || _list is null) return;

        var doc = Session.Doc;
        if (Set is null) _set = doc.Samples.FirstOrDefault()?.Name;

        _top.Children.Clear();
        var bar = new WrapPanel { Margin = new Thickness(6, 4, 6, 4) };
        bar.Children.Add(LabUi.Combo(doc.Samples.Select(s => s.Name), _set, n =>
                                                                          {
                                                                              _set = n;
                                                                              Refresh();
                                                                          }, 130));
        bar.Children.Add(LabUi.IconButton("Plus", NewSet, "A new set of samples"));
        bar.Children.Add(LabUi.IconButton("TbDelete", DeleteSet, "Delete the set"));
        var current = Set;
        if (current is { } set) bar.Children.Add(LabUi.Tool("Make Test", "Flask", () => MakeTest(set), "Make a test of this set: say whether all of it should be found or none"));
        _top.Children.Add(bar);

        _list.Items.Clear();
        if (Set is null)
        {
            _list.Items.Add(Note("No sample sets yet: the plus button makes one."));
            return;
        }

        foreach (string sample in Set.Items.ToList())
        {
            var (found, value, error) = RegexTestRunner.Try(doc, sample, Session.Timeout);
            string kind   = error is not null ? "error" : found ? "found" : "not found";
            string detail = error ?? (found ? value : string.Empty);
            var    line   = LabUi.Row("Droplet", "\"" + sample + "\"", kind, detail, iconBrush: found ? ParserUi.Blue : ParserUi.Gray, mono: true);
            _list.Items.Add(Entry(line, sample, sample + (detail.Length > 0 ? "\n" + detail : string.Empty), SampleMenu(sample, current!)));
        }
    }

    private static ListBoxItem Entry(UIElement row, object? tag, string? tip, ContextMenu? menu = null)
    {
        return new ListBoxItem { Content = row, Tag = tag, ToolTip = tip, ContextMenu = menu, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(2, 1, 2, 1) };
    }

    private static ListBoxItem Note(string text)
    {
        return new ListBoxItem { Content = LabUi.Label(text, muted: true, wrap: true), IsHitTestVisible = false };
    }

    private ContextMenu SampleMenu(string sample, SampleSet set)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("Edit", "Property", () =>
                                                     {
                                                         RemoveSample(sample);
                                                         if (_add is null) return;

                                                         _add.Text = sample;
                                                         _add.Focus();
                                                     }));
        menu.Items.Add(MenuEntry("Copy", "TbCopy", () => LabUi.Copy(sample)));
        menu.Items.Add(MenuEntry("Remove", "TbDelete", () => RemoveSample(sample)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Test: This Should Be Found", "Flask", () => AddTest("Finds " + LabUi.Shorten(sample, 20), [], Expectation.Matches, [sample])));
        menu.Items.Add(MenuEntry("Test: This Should Not Be Found", "Flask", () => AddTest("Rejects " + LabUi.Shorten(sample, 20), [], Expectation.DoesNotMatch, [sample])));
        return menu;
    }

    private static MenuItem MenuEntry(string header, string icon, Action run)
    {
        var item = new MenuItem { Header = header, Icon = LabUi.Icon(icon) };
        item.Click += (_, _) => run();
        return item;
    }

    private void AddTest(string name, IEnumerable<string> sets, Expectation expect, IEnumerable<string>? extra = null)
    {
        Session.AddTest(name, sets, expect, extra);
    }

    private void MakeTest(SampleSet set)
    {
        int answer = Silo.Ui.Windows.PromptDialog.Show("Make a test", $"What should be true of the samples in '{set.Name}'?", ["All should be found", "None should be found", "Cancel"]).Button;
        if (answer is < 0 or > 1) return;

        AddTest(answer == 0 ? $"{set.Name} are found" : $"{set.Name} are rejected", [set.Name], answer == 0 ? Expectation.Matches : Expectation.DoesNotMatch);
    }

    private void NewSet()
    {
        var    sets = Session.Doc.Samples;
        string? name = LabUi.Ask("New sample set", "Name of the set:", sets.Count == 0 ? "Samples" : "Set " + (sets.Count + 1));
        if (string.IsNullOrWhiteSpace(name) || sets.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) return;

        _set = name.Trim();
        Session.EditNow(d => d.Samples.Add(new SampleSet { Name = _set }));
    }

    private void DeleteSet()
    {
        if (Set is not { } set || !LabUi.Confirm($"Delete the sample set '{set.Name}'?", "Regex Lab")) return;

        _set = null;
        Session.EditNow(d => d.Samples.RemoveAll(s => ReferenceEquals(s, set) || s.Name == set.Name));
    }

    private void AddSamples(IEnumerable<string> samples)
    {
        var wanted = samples.Where(s => s.Length > 0).ToList();
        if (wanted.Count == 0) return;

        if (Set is null)
        {
            NewSet();
            if (Set is null) return;
        }

        string name = Set.Name;
        Session.EditNow(d => d.Samples.First(s => s.Name == name).Items.AddRange(wanted));
        _add?.Clear();
    }

    private void RemoveSample(string sample)
    {
        if (Set is not { } set) return;

        string name = set.Name;
        Session.EditNow(d =>
                        {
                            var items = d.Samples.First(s => s.Name == name).Items;
                            int at    = items.IndexOf(sample);
                            if (at >= 0) items.RemoveAt(at);
                        });
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
        if (Set is not { Items.Count: > 0 } set || !LabUi.Confirm($"Take every sample out of '{set.Name}'?", "Regex Lab")) return;

        string name = set.Name;
        Session.EditNow(d => d.Samples.First(s => s.Name == name).Items.Clear());
    }
}

/// <summary>Samples and what is expected of them, as pass or fail, run as a Test Explorer runs tests (every test is run again with each change).</summary>
internal sealed class RegexTestsInspector(RegexLabSession session) : RegexInspector(session)
{
    private ListBox?        _list;
    private ContentControl? _editor;
    private TextBlock?      _head;
    private string?         _selected;
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);

    public override string Id => "tests";

    public override LabPaneKind Kind => LabPaneKind.Entity;

    public override string Title => "Tests";

    public override string IconKey => "Flask";

    public override LabDock Dock => LabDock.Bottom;

    public override int Lane => 0;

    private TestCase? Selected => Session.Doc.Tests.FirstOrDefault(t => string.Equals(t.Name, _selected, StringComparison.OrdinalIgnoreCase));

    protected override UIElement Create()
    {
        _head = LabUi.Label(string.Empty, muted: true);
        _head.Margin = new Thickness(8, 4, 8, 2);
        _list = LabUi.List();
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

        var top = new StackPanel();
        top.Children.Add(LabUi.Bar(LabUi.Tool("Add Test", "Plus", NewTest, "A new test, from the sample sets"),
                                   LabUi.Tool("Delete", "TbDelete", DeleteTest, "Delete the selected test")));
        top.Children.Add(_head);

        _editor = new ContentControl();
        return LabUi.Layout(top, _list, _editor);
    }

    protected override void Refresh()
    {
        if (_list is null || _head is null) return;

        var tests   = Session.Doc.Tests;
        var results = Session.Results;
        int passed  = tests.Count(t => results.TryGetValue(t.Name, out var r) && r.State == TestState.Passed);
        int failed  = tests.Count(t => results.TryGetValue(t.Name, out var r) && r.State == TestState.Failed);
        _head.Text = tests.Count == 0 ? string.Empty : failed > 0 ? $"{failed} of {tests.Count} failed" : passed == tests.Count ? $"All {tests.Count} passed" : $"{tests.Count} not run (the pattern is not valid)";

        _list.Items.Clear();
        foreach (var test in tests)
        {
            results.TryGetValue(test.Name, out var result);
            var    state = result?.State ?? TestState.NotRun;
            string name  = test.Name;
            bool   open  = _open.Contains(name) || result is { State: TestState.Failed };
            var row = LabUi.Row("Flask", test.Name, ExpectText(test.Expect), result?.Summary, tree: true, expanded: result is { Outcomes.Count: > 0 } ? open : null,
                                toggle: () =>
                                        {
                                            if (!_open.Remove(name)) _open.Add(name);
                                            Refresh();
                                        }, iconBrush: Tint(state));
            _list.Items.Add(new ListBoxItem
                            {
                                Content = row, Tag = test, ToolTip = state.ToString(), IsSelected = string.Equals(test.Name, _selected, StringComparison.OrdinalIgnoreCase),
                                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(2, 1, 2, 1)
                            });

            if (open && result is not null)
                foreach (var o in result.Outcomes)
                    _list.Items.Add(new ListBoxItem
                                    {
                                        Content = LabUi.Row("Droplet", "\"" + o.Sample + "\"", o.Passed ? "passed" : "failed", o.Detail, tree: true, level: 1, iconBrush: o.Passed ? ParserUi.Green : ParserUi.Red, mono: true),
                                        ToolTip = o.Detail, IsHitTestVisible = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(2, 1, 2, 1)
                                    });
        }

        if (tests.Count == 0) _list.Items.Add(new ListBoxItem { Content = LabUi.Label("No tests yet: Add Test makes one from the sample sets.", muted: true, wrap: true), IsHitTestVisible = false });

        ShowEditor();
    }

    private static string ExpectText(Expectation e) => e switch { Expectation.Matches => "should be found", Expectation.DoesNotMatch => "should not be found", _ => "first match equals" };

    private static Brush Tint(TestState state) => state switch { TestState.Passed => ParserUi.Green, TestState.Failed => ParserUi.Red, _ => ParserUi.Gray };

    private void NewTest()
    {
        string? asked = LabUi.Ask("New test", "Name of the test:", Session.UniqueTestName(Session.Doc.Tests.Count == 0 ? "Finds" : "Test"));
        if (string.IsNullOrWhiteSpace(asked)) return;

        var set  = Session.Doc.Samples.FirstOrDefault();
        var test = Session.AddTest(asked, set is null ? [] : [set.Name], Expectation.Matches);
        _selected = test.Name;
        Refresh();
    }

    private void DeleteTest()
    {
        if (Selected is not { } test) return;

        string name = test.Name;
        _selected = null;
        Session.EditNow(d => d.Tests.RemoveAll(t => t.Name == name));
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

        void Changed(Action change) => Session.EditNow(_ => change());

        var head = new WrapPanel();
        head.Children.Add(LabUi.Label("Test", muted: true));
        head.Children.Add(LabUi.Field(test.Name, t =>
                                                 {
                                                     if (string.IsNullOrWhiteSpace(t) || Session.Doc.Tests.Any(x => !ReferenceEquals(x, test) && string.Equals(x.Name, t.Trim(), StringComparison.OrdinalIgnoreCase))) return;

                                                     Changed(() => test.Name = t.Trim());
                                                     _selected = test.Name;
                                                 }, 150));
        head.Children.Add(LabUi.Label("Expect", muted: true));
        head.Children.Add(LabUi.Combo(["Matches", "DoesNotMatch", "ValueEquals"], test.Expect.ToString(), e => Changed(() => test.Expect = Enum.Parse<Expectation>(e)), 120));
        panel.Children.Add(head);

        if (test.Expect == Expectation.ValueEquals)
        {
            var value = new WrapPanel();
            value.Children.Add(LabUi.Label("Value", muted: true));
            value.Children.Add(LabUi.Field(test.ExpectedValue, t => Changed(() => test.ExpectedValue = t), 260, "What the first match of each sample should be", true));
            panel.Children.Add(value);
        }

        if (Session.Doc.Samples.Count > 0)
        {
            panel.Children.Add(LabUi.Label("Uses the samples of", muted: true));
            var boxes = Session.Doc.Samples.Select(set =>
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
