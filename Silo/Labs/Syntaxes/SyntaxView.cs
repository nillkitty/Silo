using System.Windows.Controls;
using System.Windows.Media;
using Silo.Providers.Definitions;
using SyntaxKind = Silo.DbModel.Meta.SyntaxKind;

namespace Silo.Labs.Syntaxes;

/// <summary>The main view of the Syntax Lab: what the syntax is on top, under it the strings that have to be valid, the ones that must not be and the ones to try, and at the bottom what each came to.</summary>
internal sealed class SyntaxView : Grid
{
    private readonly SyntaxLabSession _session;
    private readonly TextBox          _name;
    private readonly TextBox          _alias;
    private readonly RadioButton      _patternKind;
    private readonly RadioButton      _parserKind;
    private readonly TextBox          _pattern;
    private readonly ComboBox         _parser;
    private readonly TextBlock        _parserInfo;
    private readonly Grid             _definition;
    private readonly StackPanel       _parserPanel;
    private readonly DockPanel        _patternPanel;
    private readonly TextBox          _example;
    private readonly TextBox          _description;
    private readonly TextBlock        _problems;
    private readonly TextBox          _valid;
    private readonly TextBox          _invalid;
    private readonly TextBox          _try;
    private readonly CheckBox         _escapes;
    private readonly ListBox          _results;
    private readonly TextBlock        _score;
    private bool                      _loading;
    private bool                      _following;

    public SyntaxView(SyntaxLabSession session)
    {
        _session = session;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 90 });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star), MinHeight = 90 });

        // what the syntax is
        _name  = LabUi.Line(string.Empty, v => Edit(d => d.Name = v), 160, "What the syntax is called; types refer to it as syntax:Name");
        _alias = LabUi.Line(string.Empty, v => Edit(d => d.Alias = string.IsNullOrWhiteSpace(v) ? null : v), 100, "An optional second name");
        var nameRow = new DockPanel();
        var aliasLabel = LabUi.Label("Alias", muted: true).Also(l => l.Margin = new Thickness(8, 0, 4, 0));
        DockPanel.SetDock(_alias, System.Windows.Controls.Dock.Right);
        DockPanel.SetDock(aliasLabel, System.Windows.Controls.Dock.Right);
        nameRow.Children.Add(_alias);
        nameRow.Children.Add(aliasLabel);
        nameRow.Children.Add(_name);

        _patternKind = new RadioButton { Content = "Pattern", GroupName = "syntaxkind", Margin = new Thickness(0, 0, 10, 0), ToolTip = "A regular expression that all of a string has to match; the value is the string" }.Themed(null);
        _parserKind  = new RadioButton { Content = "Parser", GroupName = "syntaxkind", ToolTip = "A built-in parser that reads the string into a value (a date, an address, a number...)" }.Themed(null);
        _patternKind.Checked += (_, _) => KindChosen(SyntaxKind.Pattern);
        _parserKind.Checked  += (_, _) => KindChosen(SyntaxKind.Parser);
        var kindRow = new StackPanel { Orientation = Orientation.Horizontal };
        kindRow.Children.Add(_patternKind);
        kindRow.Children.Add(_parserKind);

        _pattern = new TextBox { FontFamily = LabUi.Mono, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "The regular expression (.NET syntax); all of the string has to match" };
        _pattern.TextChanged += (_, _) => Edit(d => d.Pattern = _pattern.Text);
        _pattern.SelectionChanged += (_, _) =>
                                     {
                                         // the caret is somewhere in a group: that group is the active part
                                         if (!_loading && !_following && _pattern.IsKeyboardFocusWithin) _session.NotePatternCaret(_pattern.CaretIndex);
                                     };
        var toRegex = LabUi.Button("Regex Lab", () => RunAction("to-regex"), "Edit the pattern in the Regex Lab with the strings below as its text");
        DockPanel.SetDock(toRegex, System.Windows.Controls.Dock.Right);
        _patternPanel = new DockPanel();
        _patternPanel.Children.Add(toRegex);
        _patternPanel.Children.Add(_pattern);

        _parser = LabUi.Combo(SyntaxParsers.All.Select(p => p.Key), null, key =>
                                                                         {
                                                                             if (!_loading) Edit(d => d.Parser = key, now: true);
                                                                         }, 160);
        _parserInfo = LabUi.Label(string.Empty, muted: true, wrap: true);
        var useExample = LabUi.Button("Use its example", UseParserExample, "Fill in the example with the parser's own");
        var parserTop = new StackPanel { Orientation = Orientation.Horizontal };
        parserTop.Children.Add(_parser);
        parserTop.Children.Add(useExample);
        _parserPanel = new StackPanel();
        _parserPanel.Children.Add(parserTop);
        _parserPanel.Children.Add(_parserInfo);

        _definition = new Grid();
        _definition.Children.Add(_patternPanel);
        _definition.Children.Add(_parserPanel);

        _example     = LabUi.Line(string.Empty, v => Edit(d => d.Example = string.IsNullOrWhiteSpace(v) ? null : v), 200, "A sample of a valid string; the Silo checks it is valid when the syntax is saved", true);
        _description = LabUi.Line(string.Empty, v => Edit(d => d.Description = string.IsNullOrWhiteSpace(v) ? null : v), 200, "What the syntax is for");

        var form = LabUi.Form([("Name", nameRow), ("Kind", kindRow), ("Definition", _definition), ("Example", _example), ("Description", _description)], 80);
        SetRow(form, 0);
        Children.Add(form);

        _problems = LabUi.Label(string.Empty, wrap: true);
        _problems.Margin = new Thickness(8, 2, 8, 4);
        SetRow(_problems, 1);
        Children.Add(_problems);

        // the strings
        _valid   = LabUi.Memo(string.Empty, v => _session.EditBook(b => b.Valid = v));
        _invalid = LabUi.Memo(string.Empty, v => _session.EditBook(b => b.Invalid = v));
        _try     = LabUi.Memo(string.Empty, v => _session.EditBook(b => b.Try = v));
        _valid.GotKeyboardFocus   += (_, _) => _session.NoteSet("valid");
        _invalid.GotKeyboardFocus += (_, _) => _session.NoteSet("invalid");
        _try.GotKeyboardFocus     += (_, _) => _session.NoteSet("try");
        _escapes = LabUi.Check("Read \\n \\t \\\\ in a string", true, on =>
                                                                      {
                                                                          if (!_loading) _session.EditBookNow(b => b.Escapes = on);
                                                                      }, "A string is a line: \\n stands for a new line, \\t for a tab and \\\\ for a backslash, so a string can have several lines");

        var samples = new Grid();
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) samples.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            samples.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        AddSample(samples, 0, "Has to be valid", LabUi.Green, _valid);
        AddSplitter(samples, 1);
        AddSample(samples, 2, "Must not be valid", LabUi.Red, _invalid);
        AddSplitter(samples, 3);
        AddSample(samples, 4, "To try", LabUi.Gray, _try);
        var samplesTop = LabUi.Bar(LabUi.Label("Strings, one to a line", muted: true).Also(l => l.Margin = new Thickness(2, 0, 12, 0)), _escapes);
        var samplesBox = LabUi.Layout(samplesTop, samples);
        SetRow(samplesBox, 2);
        Children.Add(samplesBox);

        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        splitter.SetResourceReference(BackgroundProperty, "Theme.Border");
        SetRow(splitter, 3);
        Children.Add(splitter);

        // what each came to
        _score   = LabUi.Label(string.Empty, muted: true);
        _score.Margin = new Thickness(8, 2, 8, 2);
        _results = LabUi.List();
        _results.SelectionChanged += (_, _) =>
                                     {
                                         if (!_following && _results.SelectedIndex >= 0) _session.NoteResult(_results.SelectedIndex);
                                     };
        var resultsBox = LabUi.Layout(_score, _results);
        SetRow(resultsBox, 4);
        Children.Add(resultsBox);

        this.SetResourceReference(BackgroundProperty, "Theme.Background");

        _session.Changed += (_, _) => OnChanged();
        _session.Loaded  += (_, _) => Load();
        _session.ProductionFocusRequested += index => Following(() =>
                                                               {
                                                                   if (index >= 0 && index < _results.Items.Count) _results.SelectedIndex = index;
                                                                   _results.ScrollIntoView(_results.SelectedItem);
                                                               });
        _session.PatternRangeRequested += (start, length) => Following(() =>
                                                                      {
                                                                          if (_session.Draft.Kind != SyntaxKind.Pattern || start < 0 || start + length > _pattern.Text.Length) return;
                                                                          _pattern.Select(start, length);
                                                                      });
        _session.SetFocusRequested += set => Following(() => (set switch { "valid" => _valid, "invalid" => _invalid, _ => _try }).Focus());
        Load();
    }

    /// <summary>Does something to the boxes because the active part changed (and so does not say, in turn, that the box changed it).</summary>
    private void Following(Action action)
    {
        _following = true;
        try
        {
            action();
        }
        finally
        {
            _following = false;
        }
    }

    private static void AddSample(Grid grid, int column, string title, Brush color, TextBox memo)
    {
        var head = LabUi.Label(title, bold: true);
        head.Foreground = color;
        head.Margin     = new Thickness(8, 2, 8, 2);
        var panel = LabUi.Layout(head, memo);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private static void AddSplitter(Grid grid, int column)
    {
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        splitter.SetResourceReference(BackgroundProperty, "Theme.Border");
        Grid.SetColumn(splitter, column);
        grid.Children.Add(splitter);
    }

    private void Edit(Action<SyntaxDraft> change, bool now = false)
    {
        if (_loading) return;

        if (now) _session.EditNow(change);
        else _session.Edit(change);
    }

    private void KindChosen(SyntaxKind kind)
    {
        if (_loading) return;

        _session.EditNow(d =>
                         {
                             d.Kind = kind;
                             if (kind == SyntaxKind.Parser && string.IsNullOrEmpty(d.Parser)) d.Parser = SyntaxParsers.All.FirstOrDefault()?.Key;
                         });
        Load();
    }

    private void UseParserExample()
    {
        if (SyntaxParsers.Find(_session.Draft.Parser) is not { } parser) return;

        _session.EditNow(d => d.Example = parser.Example);
        Load();
    }

    private void RunAction(string id)
    {
        var action = _session.ValueActions.FirstOrDefault(a => a.Id == id);
        if (action is not null && action.Command.CanExecute(null)) action.Command.Execute(null);
    }

    /// <summary>Shows the syntax of the session in the boxes (a different one was opened).</summary>
    private void Load()
    {
        _loading = true;
        try
        {
            var d = _session.Draft;
            Set(_name, d.Name);
            Set(_alias, d.Alias ?? string.Empty);
            Set(_pattern, d.Pattern ?? string.Empty);
            Set(_example, d.Example ?? string.Empty);
            Set(_description, d.Description ?? string.Empty);
            _patternKind.IsChecked = d.Kind == SyntaxKind.Pattern;
            _parserKind.IsChecked  = d.Kind == SyntaxKind.Parser;
            _parser.SelectedItem   = d.Parser;
            Set(_valid, _session.Book.Valid);
            Set(_invalid, _session.Book.Invalid);
            Set(_try, _session.Book.Try);
            _escapes.IsChecked = _session.Book.Escapes;
        }
        finally
        {
            _loading = false;
        }

        OnChanged();
    }

    private static void Set(TextBox box, string text)
    {
        if (box.Text != text) box.Text = text;
    }

    private void OnChanged()
    {
        var d = _session.Draft;
        bool isParser = d.Kind == SyntaxKind.Parser;
        _patternPanel.Visibility = isParser ? Visibility.Collapsed : Visibility.Visible;
        _parserPanel.Visibility  = isParser ? Visibility.Visible : Visibility.Collapsed;

        var parser = SyntaxParsers.Find(d.Parser);
        _parserInfo.Text = parser is null
            ? "Choose the built-in parser that reads a string into a value."
            : $"{parser.Title}: {parser.Description} Makes a {parser.ValueType.Name}. Example: {LabUi.Shorten(parser.Example, 60)}";

        var build = _session.Build;
        if (build.Problems.Count == 0)
        {
            _problems.Text       = _session.Summary();
            _problems.Foreground = LabUi.Green;
        }
        else
        {
            _problems.Text       = string.Join("  ", build.Problems);
            _problems.Foreground = build.IsBuilt ? LabUi.Amber : LabUi.Red;
        }

        _pattern.BorderBrush = !isParser && !build.IsBuilt ? LabUi.Red : null;

        var (passed, expected) = _session.Score;
        _score.Text       = expected == 0 ? "Put strings in the lists above to see what the syntax makes of them." : $"{passed} of {expected} as expected";
        _score.Foreground = expected == 0 ? LabUi.Gray : passed == expected ? LabUi.Green : LabUi.Red;
        DrawResults();
    }

    private void DrawResults()
    {
        int selected = _results.SelectedIndex;
        Following(() =>
                  {
                      _results.Items.Clear();
                      foreach (var r in _session.Results.Take(500)) _results.Items.Add(Row(r));
                      if (_session.Results.Count > 500) _results.Items.Add(new ListBoxItem { Content = LabUi.Label($"… and {_session.Results.Count - 500:N0} more", muted: true), IsEnabled = false });
                      if (selected >= 0 && selected < _results.Items.Count) _results.SelectedIndex = selected;
                  });
    }

    private static UIElement Row(TrialResult r)
    {
        var panel = new StackPanel { Margin = new Thickness(2) };

        var first = new StackPanel { Orientation = Orientation.Horizontal };
        var mark  = LabUi.Label(r.Passed ? (r.IsValid ? "valid" : "invalid") : r.IsValid ? "valid!" : "invalid!", bold: true);
        mark.Foreground = r.Expect == TrialExpect.Nothing ? LabUi.Gray : r.Passed ? LabUi.Green : LabUi.Red;
        mark.Width      = 56;
        first.Children.Add(mark);
        first.Children.Add(LabUi.Label("\"" + LabUi.Shorten(r.Text, 80) + "\"", mono: true));
        if (r.Expect != TrialExpect.Nothing && !r.Passed)
            first.Children.Add(LabUi.Label(r.Expect == TrialExpect.Valid ? "  (it has to be valid)" : "  (it must not be valid)", muted: true));
        panel.Children.Add(first);

        string detail;
        Brush? color = null;
        if (r.IsValid)
        {
            detail = $"{r.TypeName}: {LabUi.Shorten(r.Value ?? string.Empty, 90)}";
            if (r.Canonical is null) detail += "  - cannot be written back";
            else if (!r.RoundTrips) { detail += $"  - written back as \"{LabUi.Shorten(r.Canonical, 40)}\", which is not the same value"; color = LabUi.Amber; }
            else if (r.Canonical != r.Text) detail += $"  - written back as \"{LabUi.Shorten(r.Canonical, 40)}\"";
        }
        else
        {
            detail = r.Error ?? "Not valid.";
            color  = r.Expect == TrialExpect.Invalid ? LabUi.Gray : LabUi.Red;
        }

        var line = LabUi.Label(detail, muted: color is null, mono: true, wrap: true);
        line.Margin = new Thickness(56, 0, 0, 0);
        if (color is not null) line.Foreground = color;
        panel.Children.Add(line);

        return new ListBoxItem { Content = panel };
    }
}
