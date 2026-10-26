using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Silo.Converters;
using Silo.Labs;
using Silo.Toolbar;
using Silo.Tools;
using Telefrag.DI;

namespace Silo.Ui.Panes;

// ---- the problems themselves --------------------------------------------------------------------

/// <summary>How bad a <see cref="Problem" /> is.</summary>
public enum ProblemSeverity
{
    /// <summary>Worth knowing.</summary>
    Info,

    /// <summary>Probably not what was meant.</summary>
    Warning,

    /// <summary>Wrong: the text cannot be read as it is.</summary>
    Error
}

/// <summary>One line of the Problems pane.</summary>
/// <param name="Severity">How bad it is.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Document">What it is in (the title of the document tab).</param>
/// <param name="Line">The line it is on (1-based); null when it is not about a place.</param>
/// <param name="Column">The column (1-based).</param>
/// <param name="Source">What found it ("JSON", "XML", "YAML").</param>
/// <param name="Go">
///     Shows the place (brings the document forward and puts the caret there); null when there is nowhere to
///     go.
/// </param>
public sealed record Problem(
    ProblemSeverity Severity,
    string          Message,
    string          Document,
    int?            Line,
    int?            Column,
    string          Source,
    Action?         Go = null);

/// <summary>
///     What the Problems pane lists: each owner (an open text editor, say) publishes its problems, replacing the ones it
///     published
///     before, and takes them back when it closes. Used on the UI thread.
/// </summary>
public static class ProblemList
{
    private static readonly Dictionary<object, IReadOnlyList<Problem>> ByOwner = new(ReferenceEqualityComparer.Instance);

    /// <summary>The owners in the order they first published (so the list does not jump around).</summary>
    private static readonly List<object> Order = [];

    /// <summary>Every problem, owner by owner.</summary>
    public static IReadOnlyList<Problem> All => Order.SelectMany(o => ByOwner[o]).ToList();

    /// <summary>The owners that have problems right now.</summary>
    public static IReadOnlyList<object> Owners => Order.Where(o => ByOwner[o].Count > 0).ToList();

    /// <summary>The problems changed (someone published or cleared theirs).</summary>
    public static event EventHandler? Changed;

    /// <summary>The problems of one owner (none when it has published none).</summary>
    public static IReadOnlyList<Problem> Of(object owner)
    {
        return ByOwner.TryGetValue(owner, out var list) ? list : [];
    }

    /// <summary>Replaces <paramref name="owner" />'s problems with <paramref name="problems" />.</summary>
    public static void Publish(object owner, IReadOnlyList<Problem> problems)
    {
        ArgumentNullException.ThrowIfNull(owner);

        bool had = ByOwner.TryGetValue(owner, out var before);
        if (had && before!.Count == 0 && problems.Count == 0) return;

        if (!had) Order.Add(owner);
        ByOwner[owner] = problems;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Takes back everything <paramref name="owner" /> published (its document closed).</summary>
    public static void Clear(object owner)
    {
        if (!ByOwner.Remove(owner)) return;

        Order.Remove(owner);
        Changed?.Invoke(null, EventArgs.Empty);
    }
}

// ---- the pane -----------------------------------------------------------------------------------

/// <summary>
///     The Problems pane: what is wrong in the open documents (a JSON file that does not parse, a key used twice in a YAML
///     mapping...), like the Error List of Visual Studio. It opens at the bottom of the window; double-click a line to go
///     to it.
///     View → Problems shows and hides it.
/// </summary>
[Singleton(typeof(ITool))]
public sealed class ProblemsTool : ToolBase
{
    /// <summary>The tool, asking the container for it if nothing has yet.</summary>
    public static ProblemsTool? Current => Instance ?? App.Resolve<IEnumerable<ITool>>()?.OfType<ProblemsTool>().FirstOrDefault();

    /// <summary>The pane tool (there is one; made by the container on first use).</summary>
    public static ProblemsTool? Instance { get; private set; }

    /// <inheritdoc />
    protected override double PaneHeight => 220;

    /// <inheritdoc />
    protected override bool UsesPane => true;

    /// <inheritdoc />
    protected override UIElement OnBuildContent()
    {
        return new ProblemsView();
    }

    /// <summary>Shows the pane (never hides it): what a click on an editor's error count does.</summary>
    public static void Reveal()
    {
        Current?.Show();
    }

    public ProblemsTool() : base("problems")
    {
        DisplayName = "Problems";
        GroupName   = "Text";
        Icon        = IconRef.FromKey("Checklist");
        Instance    = this;
    }
}

/// <summary>The content of the Problems pane.</summary>
internal sealed class ProblemsView : UserControl
{
    private readonly CheckBox         _currentOnly;
    private readonly TextBlock        _empty;
    private readonly ToggleButton     _errors;
    private readonly TextBox          _filter;
    private readonly ToggleButton     _infos;
    private readonly ListView         _list = new();
    private readonly List<ProblemRow> _rows = [];
    private readonly ToggleButton     _warnings;

    private bool _hooked;

    private string? _sortBy;
    private bool    _sortDown;

    private void Hook(bool on)
    {
        if (Application.Current?.MainWindow is not MainWindow main || on == _hooked) return;

        _hooked = on;
        if (on) main.DockHost.ActiveContentChanged += OnActiveContentChanged;
        else main.DockHost.ActiveContentChanged    -= OnActiveContentChanged;
    }

    private void OnActiveContentChanged(object? sender, EventArgs e)
    {
        if (_currentOnly.IsChecked == true) Refresh();
    }

    private void OnProblemsChanged(object? sender, EventArgs e)
    {
        Refresh();
    }

    private static ToggleButton Toggle(string text, string icon, Brush tint)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (LabUi.Icon(icon, 14) is { } glyph)
        {
            glyph.Foreground = tint;
            glyph.Margin     = new Thickness(0, 0, 5, 0);
            content.Children.Add(glyph);
        }

        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new ToggleButton
                     { Content = content, IsChecked = true, Padding = new Thickness(7, 2, 8, 2), Margin = new Thickness(2, 0, 2, 0) };
        return button;
    }

    private void BuildList()
    {
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = string.Empty, Width = 30, CellTemplate = IconTemplate() });
        view.Columns.Add(Column("Description", nameof(ProblemRow.Message),    520));
        view.Columns.Add(Column("Document",    nameof(ProblemRow.Document),   200));
        view.Columns.Add(Column("Line",        nameof(ProblemRow.LineText),   56));
        view.Columns.Add(Column("Col",         nameof(ProblemRow.ColumnText), 48));
        view.Columns.Add(Column("Source",      nameof(ProblemRow.Source),     70));

        _list.View            = view;
        _list.BorderThickness = new Thickness(0);
        _list.SelectionMode   = SelectionMode.Extended;
        _list.SetResourceReference(BackgroundProperty, "Theme.Background");
        _list.SetResourceReference(ForegroundProperty, "Theme.Text");
        _list.MouseDoubleClick += (_, e) =>
                                  {
                                      if (e.OriginalSource is DependencyObject d &&
                                          ItemsControl.ContainerFromElement(_list, d) is ListViewItem) GoToSelected();
                                  };
        _list.KeyDown += (_, e) =>
                         {
                             if (e.Key == Key.Enter)
                             {
                                 e.Handled = true;
                                 GoToSelected();
                             }
                             else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
                             {
                                 e.Handled = true;
                                 LabUi.Copy(string.Join(Environment.NewLine,
                                                        _list.SelectedItems.OfType<ProblemRow>().Select(r => r.Describe())));
                             }
                         };

        // clicking a column header sorts by it (again: the other way)
        _list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column }) return;

        string? property = (column.DisplayMemberBinding as Binding)?.Path.Path ?? nameof(ProblemRow.Severity);
        if (property == nameof(ProblemRow.LineText)) property = nameof(ProblemRow.Line);
        if (property == nameof(ProblemRow.ColumnText)) property = nameof(ProblemRow.Column);

        _sortDown = _sortBy == property && !_sortDown;
        _sortBy   = property;
        Refresh();
    }

    private static GridViewColumn Column(string header, string property, double width)
    {
        return new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new Binding(property) };
    }

    private static DataTemplate IconTemplate()
    {
        var icon = new FrameworkElementFactory(typeof(AppIcon));
        icon.SetBinding(AppIcon.IconProperty, new Binding(nameof(ProblemRow.Icon)));
        icon.SetBinding(ForegroundProperty,   new Binding(nameof(ProblemRow.Tint)));
        icon.SetValue(AppIcon.SizeProperty,        14d);
        icon.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        return new DataTemplate { VisualTree = icon };
    }

    private void GoToSelected()
    {
        if (_list.SelectedItem is ProblemRow { Problem.Go: { } go }) go();
    }

    private void CopyShown()
    {
        LabUi.Copy(string.Join(Environment.NewLine, _rows.Select(r => r.Describe())));
        App.Status($"Copied {_rows.Count} problem(s)");
    }

    /// <summary>The owner of the selected document tab, when "current document only" is on.</summary>
    private static object? CurrentDocument()
    {
        return (Application.Current?.MainWindow as MainWindow)?.Documents.SelectedContent;
    }

    private void Refresh()
    {
        var all                                                                     = ProblemList.All;
        if (_currentOnly.IsChecked == true && CurrentDocument() is { } current) all = ProblemList.Of(current);

        int errors   = all.Count(p => p.Severity == ProblemSeverity.Error);
        int warnings = all.Count(p => p.Severity == ProblemSeverity.Warning);
        int infos    = all.Count - errors - warnings;
        SetCount(_errors,   errors,   errors   == 1 ? "Error" : "Errors");
        SetCount(_warnings, warnings, warnings == 1 ? "Warning" : "Warnings");
        SetCount(_infos,    infos,    infos    == 1 ? "Message" : "Messages");

        string filter = _filter.Text.Trim();
        IEnumerable<Problem> shown = all.Where(p => p.Severity switch
                                                    {
                                                        ProblemSeverity.Error   => _errors.IsChecked   == true,
                                                        ProblemSeverity.Warning => _warnings.IsChecked == true,
                                                        _                       => _infos.IsChecked    == true
                                                    });
        if (filter.Length > 0)
            shown = shown.Where(p => p.Message.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                                     p.Document.Contains(filter, StringComparison.CurrentCultureIgnoreCase));

        var rows = shown.Select(p => new ProblemRow(p)).ToList();
        rows = _sortBy switch
               {
                   null => rows.OrderByDescending(r => r.Problem.Severity).ToList(), // errors first, each document's in order of place
                   nameof(ProblemRow.Line) => Sort(rows, r => r.Problem.Line ?? 0),
                   nameof(ProblemRow.Column) => Sort(rows, r => r.Problem.Column ?? 0),
                   nameof(ProblemRow.Message) => Sort(rows, r => r.Message),
                   nameof(ProblemRow.Document) => Sort(rows, r => r.Document),
                   nameof(ProblemRow.Source) => Sort(rows, r => r.Source),
                   _ => Sort(rows, r => (int)r.Problem.Severity)
               };

        _rows.Clear();
        _rows.AddRange(rows);
        _list.ItemsSource = rows;
        _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = all.Count == 0
                          ? _currentOnly.IsChecked == true
                                ? "Nothing is wrong in this document."
                                : "Nothing is wrong in the open documents."
                          : "No problems match what is shown.";
    }

    private List<ProblemRow> Sort<TKey>(List<ProblemRow> rows, Func<ProblemRow, TKey> key)
    {
        return (_sortDown ? rows.OrderByDescending(key) : rows.OrderBy(key)).ToList();
    }

    private void SetCount(ToggleButton button, int count, string word)
    {
        if (button.Content is StackPanel { Children.Count: > 0 } panel && panel.Children[^1] is TextBlock text)
            text.Text = $"{count} {word}";
    }

    public ProblemsView()
    {
        _errors   = Toggle("Errors",   "StateFailed", LabUi.Red);
        _warnings = Toggle("Warnings", "Feedback",    LabUi.Amber);
        _infos    = Toggle("Messages", "Document",    LabUi.Gray);
        foreach (var toggle in new[] { _errors, _warnings, _infos })
        {
            toggle.Checked   += (_, _) => Refresh();
            toggle.Unchecked += (_, _) => Refresh();
        }

        _currentOnly = new CheckBox
                       {
                           Content           = "Current document only", Margin = new Thickness(10, 0, 6, 0),
                           VerticalAlignment = VerticalAlignment.Center
                       };
        _currentOnly.SetResourceReference(ForegroundProperty, "Theme.Text");
        _currentOnly.Checked   += (_, _) => Refresh();
        _currentOnly.Unchecked += (_, _) => Refresh();

        _filter = new TextBox
                  {
                      MinWidth = 160, Margin = new Thickness(6, 0, 2, 0), VerticalContentAlignment = VerticalAlignment.Center,
                      ToolTip  = "Show only the problems whose text contains this"
                  };
        _filter.TextChanged += (_, _) => Refresh();
        var filterLabel = LabUi.Label("Filter:", true);
        filterLabel.Margin = new Thickness(8, 0, 0, 0);

        var bar = LabUi.Bar(_errors, _warnings, _infos, _currentOnly, filterLabel, _filter,
                            LabUi.Tool("Copy", "TbCopy", CopyShown, "Copy the problems shown, as text"));

        BuildList();
        _empty        = LabUi.Label("Nothing is wrong in the open documents.", true);
        _empty.Margin = new Thickness(10, 6, 10, 6);

        var body = new Grid();
        body.Children.Add(_list);
        body.Children.Add(_empty);

        var root = LabUi.Layout(bar, body);
        root.SetResourceReference(Panel.BackgroundProperty, "Theme.Background");
        Content = root;

        // the list follows the published problems, and "current document only" follows the selected tab
        ProblemList.Changed += OnProblemsChanged;
        Loaded              += (_, _) => Hook(true);
        Unloaded            += (_, _) => Hook(false);
        Refresh();
    }

    /// <summary>A problem as the list shows it.</summary>
    private sealed class ProblemRow(Problem problem)
    {
        public int    Column     => Problem.Column             ?? 0;
        public string ColumnText => Problem.Column?.ToString() ?? string.Empty;
        public string Document   => Problem.Document;

        public Geometry? Icon => ToolbarIcons.Find(Problem.Severity switch
                                                   {
                                                       ProblemSeverity.Error   => "StateFailed",
                                                       ProblemSeverity.Warning => "Feedback",
                                                       _                       => "Document"
                                                   });

        public int    Line     => Problem.Line             ?? 0;
        public string LineText => Problem.Line?.ToString() ?? string.Empty;

        public string  Message  => Problem.Message;
        public Problem Problem  { get; } = problem;
        public int     Severity => (int)Problem.Severity;
        public string  Source   => Problem.Source;

        public Brush Tint => Problem.Severity switch
                             {
                                 ProblemSeverity.Error   => LabUi.Red,
                                 ProblemSeverity.Warning => LabUi.Amber,
                                 _                       => LabUi.Gray
                             };

        public string Describe()
        {
            return $"{Problem.Severity}\t{Document}\t{LineText}\t{ColumnText}\t{Source}\t{Message}";
        }
    }
}