using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Silo.Labs.Web;

namespace Silo.Labs.Regexes;

/// <summary>The base of the Regex Lab's inspectors: they redraw a moment after the regex or its result changed.</summary>
internal abstract class RegexInspector : LabInspector
{
    private bool _pending;
    private bool _ready;

    protected RegexInspector(RegexLabSession session)
    {
        Session         = session;
        session.Changed += (_, _) => Poke();
        session.Loaded  += (_, _) => Poke();
    }

    protected RegexLabSession Session { get; }

    protected sealed override UIElement BuildView()
    {
        var view = Create();
        _ready = true;
        Refresh();
        return view;
    }

    protected abstract UIElement Create();

    protected abstract void Refresh();

    public override void OnShown() => Refresh();

    protected void Poke()
    {
        if (!_ready || _pending) return;

        _pending = true;
        (Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher).InvokeAsync(() =>
                                                                                      {
                                                                                          _pending = false;
                                                                                          Refresh();
                                                                                      }, DispatcherPriority.Background);
    }
}

/// <summary>Every match with its groups; choosing one shows it in the test text.</summary>
internal sealed class RegexMatchesInspector(RegexLabSession session) : RegexInspector(session)
{
    private TreeView?  _tree;
    private TextBlock? _head;

    public override string Id => "matches";

    public override string Title => "Matches";

    public override string IconKey => "Checklist";

    protected override UIElement Create()
    {
        _head = LabUi.Label(string.Empty, muted: true);
        _head.Margin = new Thickness(8, 6, 8, 4);
        _tree = new TreeView { BorderThickness = new Thickness(0) }.Themed();
        _tree.SelectedItemChanged += (_, _) =>
                                     {
                                         if (_tree.SelectedItem is TreeViewItem { Tag: (int index, int length) }) Session.Reveal(index, length);
                                     };
        return LabUi.Layout(_head, _tree);
    }

    protected override void Refresh()
    {
        if (_tree is null || _head is null) return;

        var run = Session.Run;
        _head.Text = run.Summary;
        _tree.Items.Clear();
        foreach (var m in run.Matches.Take(500))
        {
            var item = new TreeViewItem { Header = Row($"{m.Number}", $"[{m.Index}..{m.Index + m.Length})", m.Value, LabUi.Marks[(m.Number - 1) % LabUi.Marks.Length]), Tag = (m.Index, m.Length) };
            foreach (var g in m.Groups.Where(g => g.Number != 0))
            {
                var node = new TreeViewItem
                           {
                               Header = Row(g.IsNamed ? g.Name : $"${g.Number}", g.Success ? $"[{g.Index}..{g.Index + g.Length})" : "no match", g.Success ? g.Value : "(did not take part)", null),
                               Tag    = g.Success ? (g.Index, g.Length) : ((int, int)?)null
                           };
                // a group in a loop captured several times
                if (g.Captures.Count > 1)
                    foreach (var c in g.Captures)
                        node.Items.Add(new TreeViewItem { Header = Row("capture", $"[{c.Index}..{c.Index + c.Length})", c.Value, null), Tag = (c.Index, c.Length) });
                item.Items.Add(node);
            }

            _tree.Items.Add(item);
        }

        if (run.Matches.Count > 500) _tree.Items.Add(new TreeViewItem { Header = LabUi.Label($"… and {run.Matches.Count - 500:N0} more", muted: true), IsEnabled = false });
    }

    private static UIElement Row(string label, string place, string value, Brush? mark)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (mark is not null) row.Children.Add(new Border { Width = 8, Height = 14, Background = mark, Margin = new Thickness(0, 0, 4, 0), CornerRadius = new CornerRadius(2) });
        row.Children.Add(LabUi.Label(label, bold: true).Also(l => l.Margin = new Thickness(0, 0, 6, 0)));
        row.Children.Add(LabUi.Label(place, muted: true, mono: true).Also(l => l.Margin = new Thickness(0, 0, 6, 0)));
        row.Children.Add(LabUi.Label(LabUi.Shorten(value, 60), mono: true));
        return row;
    }
}

/// <summary>What the replacement makes of the text, and what splitting it at the matches leaves.</summary>
internal sealed class RegexReplaceInspector(RegexLabSession session) : RegexInspector(session)
{
    private TextBox?  _replacement;
    private TextBox?  _result;
    private ListBox?  _parts;
    private bool      _loading;

    public override string Id => "replace";

    public override string Title => "Replace && Split";

    public override string IconKey => "Transfer1";

    protected override UIElement Create()
    {
        _replacement = LabUi.Field(Session.Doc.Replacement, _ => { }, 200, "What a match is replaced with: $1, ${name}, $& (the whole match), $$ for a dollar sign", true);
        _replacement.TextChanged += (_, _) =>
                                    {
                                        if (!_loading) Session.Edit(d => d.Replacement = _replacement.Text);
                                    };
        _result = LabUi.Output(wrap: true);
        _parts  = LabUi.List();

        var top = new StackPanel();
        top.Children.Add(LabUi.Heading("Replace with"));
        top.Children.Add(_replacement);
        top.Children.Add(LabUi.Heading("Result"));

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_result, 0);
        var splitHead = LabUi.Heading("Split at the matches");
        Grid.SetRow(splitHead, 1);
        Grid.SetRow(_parts, 2);
        grid.Children.Add(_result);
        grid.Children.Add(splitHead);
        grid.Children.Add(_parts);

        var copy = LabUi.Button("Copy result", () => LabUi.Copy(_result.Text), "Put the result on the clipboard");
        var bar  = LabUi.Bar(copy);
        return LabUi.Layout(top, grid, bar);
    }

    protected override void Refresh()
    {
        if (_replacement is null || _result is null || _parts is null) return;

        _loading = true;
        if (_replacement.Text != Session.Doc.Replacement) _replacement.Text = Session.Doc.Replacement;
        _loading = false;

        var (text, error) = RegexEngine.Replace(Session.Doc, Session.Timeout);
        _result.Text       = error ?? text ?? string.Empty;
        _result.Foreground = error is null ? null : LabUi.Red;
        if (error is null) _result.SetResourceReference(Control.ForegroundProperty, "Theme.Text");

        _parts.Items.Clear();
        var (parts, splitError) = RegexEngine.Split(Session.Doc, Session.Timeout);
        if (parts is null) _parts.Items.Add(LabUi.Label(splitError ?? string.Empty, muted: true));
        else
            for (int i = 0; i < parts.Count && i < 500; i++)
                _parts.Items.Add(LabUi.Label($"{i + 1,3}  \"{LabUi.Shorten(parts[i], 80)}\"", mono: true));
    }
}

/// <summary>The pattern explained piece by piece, indented as far as it is inside groups.</summary>
internal sealed class RegexExplainInspector(RegexLabSession session) : RegexInspector(session)
{
    private StackPanel? _lines;

    public override string Id => "explain";

    public override string Title => "Explain";

    public override string IconKey => "Analysis";

    protected override UIElement Create()
    {
        _lines = new StackPanel { Margin = new Thickness(6) };
        var scroll = new ScrollViewer { Content = _lines, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        var copy = LabUi.Button("Copy explanation", () => LabUi.Copy(RegexExplainer.ToText(RegexExplainer.Explain(Session.Doc.Pattern, Session.Doc.Flags))), "Put the explanation on the clipboard as text");
        return LabUi.Layout(null, scroll, LabUi.Bar(copy));
    }

    protected override void Refresh()
    {
        if (_lines is null) return;

        _lines.Children.Clear();
        foreach (var line in RegexExplainer.Explain(Session.Doc.Pattern, Session.Doc.Flags))
        {
            var row = new Grid { Margin = new Thickness(line.Depth * 18, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var token = LabUi.Label(line.Token, mono: true);
            token.Foreground = LabUi.Blue;
            token.TextWrapping = TextWrapping.NoWrap;
            token.TextTrimming = TextTrimming.CharacterEllipsis;
            token.ToolTip = line.Token;
            var meaning = LabUi.Label(line.Meaning, wrap: true);
            Grid.SetColumn(meaning, 1);
            row.Children.Add(token);
            row.Children.Add(meaning);
            _lines.Children.Add(row);
        }
    }
}

/// <summary>The syntax of .NET regular expressions in short: double-click a line to put it in the pattern.</summary>
internal sealed class RegexReferenceInspector(RegexLabSession session) : RegexInspector(session)
{
    private ListBox? _list;

    public override string Id => "reference";

    public override string Title => "Reference";

    public override string IconKey => "Document";

    public override LabDock Dock => LabDock.Left;

    public override bool VisibleByDefault => false;

    protected override UIElement Create()
    {
        _list = LabUi.List();
        string? group = null;
        foreach (var r in RegexLibrary.Reference)
        {
            if (r.Group != group)
            {
                group = r.Group;
                _list.Items.Add(LabUi.Section(group));
            }

            string token = r.Token.Split("  ", 2)[0];
            _list.Items.Add(LabUi.Item(token, LabUi.Row("Token", token, null, r.Meaning, template: true, mono: true), "Double-click to put it in the pattern"));
        }

        _list.MouseDoubleClick += (_, _) =>
                                  {
                                      if (_list.SelectedItem is ListBoxItem { Tag: string token }) Session.Insert(token);
                                  };
        return LabUi.Layout(LabUi.TemplateBanner("Reference: built in to the app"), _list);
    }

    protected override void Refresh()
    {
    }
}

/// <summary>The regex as code in another language.</summary>
internal sealed class RegexCodeInspector(RegexLabSession session) : RegexInspector(session)
{
    private TextBox? _code;
    private string   _language = "C#";

    public override string Id => "code";

    public override string Title => "Code";

    public override string IconKey => "Code";

    public override bool VisibleByDefault => false;

    protected override UIElement Create()
    {
        _code = LabUi.Output();
        var combo = LabUi.Combo(RegexCode.Languages, _language, l =>
                                                                {
                                                                    _language = l;
                                                                    Refresh();
                                                                }, 130);
        var copy = LabUi.Button("Copy", () => LabUi.Copy(_code.Text), "Put the code on the clipboard");
        return LabUi.Layout(LabUi.Bar(combo, copy), _code);
    }

    protected override void Refresh()
    {
        if (_code is not null) _code.Text = RegexCode.Generate(_language, Session.Doc);
    }
}
