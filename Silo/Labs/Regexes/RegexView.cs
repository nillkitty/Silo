using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Silo.Labs.Regexes;

/// <summary>The main view of the Regex Lab: the pattern with its options on top, and under them the test text next to the same text with the matches marked.</summary>
internal sealed class RegexView : Grid
{
    private const int MarkLimit = 200_000;

    private readonly RegexLabSession _session;
    private readonly TextBox         _pattern;
    private readonly TextBox         _text;
    private readonly TextBlock       _marked = new() { TextWrapping = TextWrapping.Wrap, FontFamily = LabUi.Mono, Padding = new Thickness(6) };
    private readonly TextBlock       _summary;
    private readonly TextBox         _name;
    private readonly Dictionary<char, CheckBox> _flags = [];
    private readonly Debouncer       _mark;
    private bool                     _loading;

    public RegexView(RegexLabSession session)
    {
        _session = session;
        _mark    = new Debouncer(TimeSpan.FromMilliseconds(30), DrawMarks);

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // the pattern
        _pattern = new TextBox
                   {
                       FontFamily = LabUi.Mono, FontSize = 15, Margin = new Thickness(4, 0, 4, 0), Padding = new Thickness(4, 3, 4, 3), AcceptsReturn = false,
                       ToolTip    = "The regular expression (.NET syntax)", VerticalContentAlignment = VerticalAlignment.Center
                   };
        _pattern.TextChanged += (_, _) =>
                                {
                                    if (!_loading) _session.Edit(d => d.Pattern = _pattern.Text);
                                };
        _name = new TextBox { MinWidth = 130, Margin = new Thickness(4, 0, 4, 0), ToolTip = "What it is called when it is kept in the Silo", VerticalContentAlignment = VerticalAlignment.Center };
        _name.TextChanged += (_, _) =>
                             {
                                 if (!_loading) _session.Edit(d => d.Name = _name.Text);
                             };

        var patternRow = new DockPanel { Margin = new Thickness(6, 6, 6, 2) };
        var slash      = LabUi.Label("/", bold: true);
        slash.FontSize = 18;
        var slash2     = LabUi.Label("/", bold: true);
        slash2.FontSize = 18;
        DockPanel.SetDock(slash, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(slash2, System.Windows.Controls.Dock.Right);
        var nameLabel = LabUi.Label("Name", muted: true);
        DockPanel.SetDock(nameLabel, System.Windows.Controls.Dock.Right);
        DockPanel.SetDock(_name, System.Windows.Controls.Dock.Right);
        patternRow.Children.Add(_name);
        patternRow.Children.Add(nameLabel);
        patternRow.Children.Add(slash2);
        patternRow.Children.Add(slash);
        patternRow.Children.Add(_pattern);
        SetRow(patternRow, 0);
        Children.Add(patternRow);

        // the options
        // the options in aligned columns (a wrap panel left them ragged)
        var boxes = new List<UIElement>();
        foreach (var f in RegexFlags.All)
        {
            var box = LabUi.Check($"{f.Name}", false, on => ToggleFlag(f.Letter, on), $"{f.Letter}: {f.Help}");
            _flags[f.Letter] = box;
            boxes.Add(box);
        }

        var flags = new DockPanel { Margin = new Thickness(6, 2, 6, 2), LastChildFill = true };
        var optionsLabel = LabUi.Label("Options", muted: true);
        optionsLabel.Margin            = new Thickness(0, 4, 6, 0);
        optionsLabel.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(optionsLabel, System.Windows.Controls.Dock.Left);
        flags.Children.Add(optionsLabel);
        flags.Children.Add(LabUi.Checks(boxes, 3));

        SetRow(flags, 1);
        Children.Add(flags);

        _summary = LabUi.Label(string.Empty, wrap: true);
        _summary.Margin = new Thickness(8, 2, 8, 4);
        SetRow(_summary, 2);
        Children.Add(_summary);

        // the text, and the text with the matches marked
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _text = LabUi.Memo(string.Empty, _ => { }, wrap: false);
        _text.TextChanged += (_, _) =>
                             {
                                 if (!_loading) _session.Edit(d => d.TestText = _text.Text);
                             };
        var left = LabUi.Layout(LabUi.Label("Test text", muted: true).Also(l => l.Margin = new Thickness(8, 2, 8, 2)), _text);
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        splitter.SetResourceReference(BackgroundProperty, "Theme.Border");
        Grid.SetColumn(splitter, 1);
        body.Children.Add(splitter);

        var scroll = new ScrollViewer { Content = _marked, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SetResourceReference(BackgroundProperty, "Theme.Background");
        _marked.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        var right = LabUi.Layout(LabUi.Label("Matches marked", muted: true).Also(l => l.Margin = new Thickness(8, 2, 8, 2)), scroll);
        Grid.SetColumn(right, 2);
        body.Children.Add(right);

        SetRow(body, 3);
        Children.Add(body);

        this.SetResourceReference(BackgroundProperty, "Theme.Background");

        _session.Changed         += (_, _) => OnChanged();
        _session.Loaded          += (_, _) => Load();
        _session.RevealRequested += Reveal;
        _session.InsertRequested += InsertIntoPattern;
        Load();
    }

    private void ToggleFlag(char letter, bool on)
    {
        if (_loading) return;

        _session.EditNow(d =>
                         {
                             var set = new HashSet<char>(d.Flags);
                             if (on) set.Add(letter);
                             else set.Remove(letter);
                             d.Flags = RegexFlags.Normalize(new string([.. set]));
                         });
    }

    /// <summary>Shows the regex of the session in the boxes (a different one was opened).</summary>
    private void Load()
    {
        _loading = true;
        try
        {
            var doc = _session.Doc;
            if (_pattern.Text != doc.Pattern) _pattern.Text = doc.Pattern;
            if (_text.Text != doc.TestText) _text.Text = doc.TestText;
            if (_name.Text != doc.Name) _name.Text = doc.Name;
            foreach (var (letter, box) in _flags) box.IsChecked = doc.Flags.Contains(letter);
        }
        finally
        {
            _loading = false;
        }

        OnChanged();
    }

    private void OnChanged()
    {
        var run = _session.Run;
        _summary.Text = run.Summary;
        _summary.Foreground = !run.IsValid || run.TimedOut ? LabUi.Red : run.Matches.Count > 0 ? LabUi.Green : LabUi.Gray;
        _pattern.BorderBrush = !run.IsValid ? LabUi.Red : null;
        _pattern.ToolTip = run.IsValid ? "The regular expression (.NET syntax)" : run.Error;
        _mark.Poke();
    }

    private void DrawMarks()
    {
        _marked.Inlines.Clear();
        string text = _session.Doc.TestText;
        if (text.Length == 0)
        {
            _marked.Inlines.Add(new Run("(nothing to try the regex on: type or paste some text on the left)") { FontStyle = FontStyles.Italic, Foreground = LabUi.Gray });
            return;
        }

        var run = _session.Run;
        if (text.Length > MarkLimit)
        {
            _marked.Inlines.Add(new Run($"The text is {text.Length:N0} characters long; matches are only marked in the first {MarkLimit:N0}.\n\n") { FontStyle = FontStyles.Italic, Foreground = LabUi.Gray });
            text = text[..MarkLimit];
        }

        if (!run.IsValid || run.Matches.Count == 0)
        {
            _marked.Inlines.Add(new Run(text));
            return;
        }

        foreach (var span in RegexEngine.Spans(text, run))
        {
            var piece = new Run(text.Substring(span.Start, span.Length));
            if (span.Match > 0)
            {
                piece.Background = LabUi.Marks[(span.Match - 1) % LabUi.Marks.Length];
                if (span.Group > 0)
                {
                    piece.FontWeight      = FontWeights.Bold;
                    piece.TextDecorations = TextDecorations.Underline;
                    piece.ToolTip         = $"Match {span.Match}, group {GroupName(run, span.Match, span.Group)}";
                }
                else piece.ToolTip = $"Match {span.Match}";
            }

            _marked.Inlines.Add(piece);
        }
    }

    private static string GroupName(RegexRun run, int match, int number)
    {
        var g = run.Matches[match - 1].Groups.FirstOrDefault(x => x.Number == number);
        return g is null ? number.ToString() : g.IsNamed ? $"{g.Name} ({g.Number})" : g.Number.ToString();
    }

    private void Reveal(int index, int length)
    {
        if (index < 0 || index > _text.Text.Length) return;

        _text.Focus();
        _text.Select(index, Math.Min(length, _text.Text.Length - index));
        _text.ScrollToLine(_text.GetLineIndexFromCharacterIndex(index));
    }

    private void InsertIntoPattern(string text)
    {
        _pattern.Focus();
        int at = _pattern.SelectionStart;
        _pattern.SelectedText = text;
        _pattern.SelectionStart = at + text.Length;
        _pattern.SelectionLength = 0;
    }
}
