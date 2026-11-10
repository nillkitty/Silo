using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Silo.Converters;
using Silo.Labs;
using Silo.Tools;
using Telefrag.DI;

namespace Silo.Tasks;

/// <summary>
///     The Tasks pane: what the app is working on, like the Recent Tasks of vCenter -- each task's name, source, status, how long it has been
///     going, how many results it produced and how big they are. A task that can be stopped has Cancel; a task can be marked so a toast
///     announces it when it ends. The pane opens at the bottom of the window by itself when the first task is queued; View -&gt; Tasks shows and
///     hides it.
/// </summary>
[Singleton(typeof(ITool))]
public sealed class TasksTool : ToolBase
{
    /// <summary>The tool, asking the container for it if nothing has yet.</summary>
    public static TasksTool? Current => Instance ?? App.Resolve<IEnumerable<ITool>>()?.OfType<TasksTool>().FirstOrDefault();

    /// <summary>The pane tool (there is one; made by the container on first use).</summary>
    public static TasksTool? Instance { get; private set; }

    /// <inheritdoc />
    protected override double PaneHeight => 220;

    /// <inheritdoc />
    protected override bool UsesPane => true;

    /// <inheritdoc />
    protected override UIElement OnBuildContent()
    {
        return new TasksView();
    }

    public TasksTool() : base("tasks")
    {
        DisplayName = "Tasks";
        GroupName   = "Diagnostics";
        Icon        = IconRef.FromKey("Tasks");
        Instance    = this;
        RevealPane.Request = RevealWhenReady;
    }

    /// <summary>
    ///     Shows the pane and brings it forward -- once the main window is there to hold it (a task queued while the app starts, such as opening
    ///     the Silo that was open last time, waits for it).
    /// </summary>
    private void RevealWhenReady()
    {
        if (Application.Current?.Dispatcher is not { } dispatcher) return;

        int tries = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
        void Try()
        {
            if (Application.Current?.MainWindow is MainWindow { IsLoaded: true, IsVisible: true } || ++tries > 50)
            {
                timer.Stop();
                if (tries <= 50) Show();
            }
        }

        timer.Tick += (_, _) => Try();
        dispatcher.BeginInvoke(new Action(() =>
                                          {
                                              Try();
                                              if (tries <= 50 && !(Application.Current?.MainWindow is MainWindow { IsLoaded: true, IsVisible: true })) timer.Start();
                                          }), DispatcherPriority.Background);
    }

    /// <summary>Shows the pane (never hides it).</summary>
    public static void Reveal()
    {
        Current?.Show();
    }
}

/// <summary>One line of the pane: a task as the grid shows it (and keeps showing it as the task changes).</summary>
internal sealed class TaskRow : INotifyPropertyChanged
{
    private static readonly Brush Queued    = Freeze(new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)));
    private static readonly Brush Running   = Freeze(new SolidColorBrush(Color.FromRgb(0x42, 0x8B, 0xE0)));
    private static readonly Brush Succeeded = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)));
    private static readonly Brush Failed    = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)));
    private static readonly Brush Cancelled = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00)));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    public TaskRow(ITrackedTask task)
    {
        Task = task;
        task.PropertyChanged += (_, _) => Refresh();
    }

    public ITrackedTask Task { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name => Task.Name;

    public IconRef Icon => TaskIcons.For(Task.Source);

    public string Source => Task.Source;

    public string StatusText => Task.State switch
                                {
                                    TaskState.Running when !string.IsNullOrWhiteSpace(Task.Message) => "Running: " + Task.Message,
                                    TaskState.Failed when !string.IsNullOrWhiteSpace(Task.Message)  => "Failed: " + Task.Message,
                                    _                                                               => Task.State.Describe()
                                };

    public Brush StateBrush => Task.State switch
                               {
                                   TaskState.Queued    => Queued,
                                   TaskState.Running   => Running,
                                   TaskState.Succeeded => Succeeded,
                                   TaskState.Failed    => Failed,
                                   _                   => Cancelled
                               };

    public string DurationText => Task.State == TaskState.Queued ? "" : TaskToast.Elapsed(Task.Duration);

    public TimeSpan Duration => Task.Duration;

    public string ResultsText => Task.ResultCount is { } n ? n.ToString("N0") : "";

    public int ResultCount => Task.ResultCount ?? -1;

    public string SizeText => Task.ResultBytes is { } b ? b.ToString("N0") : "";

    public long ResultBytes => Task.ResultBytes ?? -1;

    public int StateOrder => (int)Task.State;

    public bool CanNotify => !Task.State.IsFinished();

    public bool Notify
    {
        get => Task.NotifyOnCompletion;
        set => Task.NotifyOnCompletion = value;
    }

    public bool IsActive => !Task.State.IsFinished();

    /// <summary>Tells the grid everything may have changed (a task is one object whose parts change together).</summary>
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>The task as one line of text (what Copy puts on the clipboard).</summary>
    public string Describe()
    {
        return string.Join('\t', Name, Source, Task.State.Describe() + (Task.Message is { Length: > 0 } m ? ": " + m : ""), DurationText, ResultsText, SizeText);
    }
}

/// <summary>The content of the Tasks pane.</summary>
internal sealed class TasksView : UserControl, IDisposable
{
    private readonly ObservableCollection<TaskRow> _rows = [];
    private readonly ListView                      _list = new();
    private readonly Button                        _cancel;
    private readonly ToggleButton                  _notify;
    private readonly Button                        _clear;
    private readonly TextBlock                     _empty;
    private readonly TextBlock                     _detail;
    private readonly DispatcherTimer               _tick;

    private string?            _sortBy;
    private ListSortDirection  _sortDirection;
    private bool               _updating;

    public TasksView()
    {
        var root = new DockPanel();

        // the bar: cancel the selected tasks, be told when they end, clear what is over
        var bar = new WrapPanel { Margin = new Thickness(6, 4, 6, 4) };
        _cancel = LabUi.Tool("Cancel", "CrawlStop", CancelSelected, "Stop the selected tasks that can be stopped", false);
        _notify = new ToggleButton
                  {
                      Content = Labelled("Notify when done", "Tasks"), Padding = new Thickness(7, 2, 8, 2), Margin = new Thickness(2, 0, 2, 0), IsEnabled = false,
                      ToolTip = "Show a toast when the selected tasks end"
                  };
        _notify.Click += (_, _) => SetNotify(_notify.IsChecked == true);
        _clear = LabUi.Tool("Clear finished", "TbDelete", () => TaskCenter.ClearFinished(), "Take the tasks that are over out of the list");
        bar.Children.Add(_cancel);
        bar.Children.Add(_notify);
        bar.Children.Add(_clear);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        _detail = new TextBlock { Margin = new Thickness(8, 2, 8, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        DockPanel.SetDock(_detail, Dock.Bottom);
        root.Children.Add(_detail);

        BuildList();
        _empty = new TextBlock
                 {
                     Text = "No tasks yet. Saving, loading, importing, exporting and syncing show up here.", HorizontalAlignment = HorizontalAlignment.Center,
                     VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 20, 0, 0)
                 };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        root.Children.Add(new Grid { Children = { _list, _empty } });
        Content = root;

        foreach (var task in TaskCenter.Tasks) AddRow(task);
        TaskCenter.Tasks.CollectionChanged += OnTasksChanged;
        Unloaded += (_, _) => _tick?.Stop();
        Loaded   += (_, _) => _tick?.Start();

        // a running task's duration moves on
        _tick = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _tick.Tick += (_, _) =>
                      {
                          foreach (var row in _rows.Where(r => r.IsActive)) row.Refresh();
                      };
        _tick.Start();
        UpdateState();
    }

    /// <summary>Lets go of the center (the pane lives as long as the app does, but a view made for a test must not stay subscribed to it).</summary>
    public void Dispose()
    {
        TaskCenter.Tasks.CollectionChanged -= OnTasksChanged;
        _tick.Stop();
    }

    private void AddRow(ITrackedTask task)
    {
        var row = new TaskRow(task);
        row.PropertyChanged += (_, _) =>
                               {
                                   if (_list.SelectedItems.Contains(row)) UpdateState(); // the buttons follow the selected task as it ends
                               };
        _rows.Add(row);
    }

    private static StackPanel Labelled(string text, string icon)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (LabUi.Icon(icon, 14) is { } glyph)
        {
            glyph.Margin = new Thickness(0, 0, 5, 0);
            content.Children.Add(glyph);
        }

        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    private void BuildList()
    {
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Notify", Width = 54, CellTemplate = NotifyTemplate() });
        view.Columns.Add(new GridViewColumn { Header = "Name", Width = 260, CellTemplate = NameTemplate() });
        view.Columns.Add(Column("Source",             nameof(TaskRow.Source),      130));
        view.Columns.Add(new GridViewColumn { Header = "Status", Width = 280, CellTemplate = StatusTemplate() });
        view.Columns.Add(Column("Duration",           nameof(TaskRow.DurationText), 80, right: true));
        view.Columns.Add(Column("Results",            nameof(TaskRow.ResultsText),  70, right: true));
        view.Columns.Add(Column("Result Size (bytes)", nameof(TaskRow.SizeText),   120, right: true));

        _list.View            = view;
        _list.ItemsSource     = _rows;
        _list.BorderThickness = new Thickness(0);
        _list.SelectionMode   = SelectionMode.Extended;
        _list.SetResourceReference(BackgroundProperty, "Theme.Background");
        _list.SetResourceReference(ForegroundProperty, "Theme.Text");
        _list.SelectionChanged += (_, _) => UpdateState();
        _list.KeyDown += (_, e) =>
                         {
                             if (e.Key == Key.Delete) CancelSelected();
                             else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) CopySelected();
                         };
        _list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));

        var menu = new ContextMenu();
        var cancel = new MenuItem { Header = "_Cancel" };
        cancel.Click += (_, _) => CancelSelected();
        var notify = new MenuItem { Header = "_Notify when done", IsCheckable = true };
        notify.Click += (_, _) => SetNotify(notify.IsChecked);
        var copy = new MenuItem { Header = "C_opy" };
        copy.Click += (_, _) => CopySelected();
        var clear = new MenuItem { Header = "Clear _finished" };
        clear.Click += (_, _) => TaskCenter.ClearFinished();
        menu.Opened += (_, _) =>
                       {
                           var sel = Selected();
                           cancel.IsEnabled = sel.Any(CanCancel);
                           notify.IsEnabled = sel.Any(r => r.CanNotify);
                           notify.IsChecked = sel.Count > 0 && sel.All(r => r.Notify);
                       };
        menu.Items.Add(cancel);
        menu.Items.Add(notify);
        menu.Items.Add(new Separator());
        menu.Items.Add(copy);
        menu.Items.Add(clear);
        _list.ContextMenu = menu;
    }

    private static GridViewColumn Column(string header, string property, double width, bool right = false)
    {
        var column = new GridViewColumn { Header = header, Width = width };
        if (!right)
        {
            column.DisplayMemberBinding = new Binding(property);
            return column;
        }

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(property));
        text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
        text.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
        column.CellTemplate = new DataTemplate { VisualTree = text };
        return column;
    }

    private static DataTemplate NotifyTemplate()
    {
        var box = new FrameworkElementFactory(typeof(CheckBox));
        box.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(TaskRow.Notify)) { Mode = BindingMode.TwoWay });
        box.SetBinding(IsEnabledProperty, new Binding(nameof(TaskRow.CanNotify)));
        box.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        box.SetValue(ToolTipProperty, "Show a toast when this task ends");
        return new DataTemplate { VisualTree = box };
    }

    /// <summary>The task's name, led by the icon of the kind of work it is.</summary>
    private static DataTemplate NameTemplate()
    {
        var icon = new FrameworkElementFactory(typeof(AppIcon));
        icon.SetValue(AppIcon.SizeProperty, 14d);
        icon.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
        icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetBinding(AppIcon.SourceProperty, new Binding(nameof(TaskRow.Icon)));

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(TaskRow.Name)));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.AppendChild(icon);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    private static DataTemplate StatusTemplate()
    {
        var dot = new FrameworkElementFactory(typeof(Ellipse));
        dot.SetValue(WidthProperty, 9d);
        dot.SetValue(HeightProperty, 9d);
        dot.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
        dot.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        dot.SetBinding(Shape.FillProperty, new Binding(nameof(TaskRow.StateBrush)));

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(TaskRow.StatusText)));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.AppendChild(dot);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    // ---- the list --------------------------------------------------------------------------------

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _rows.Clear();
        }
        else
        {
            if (e.OldItems is not null)
                foreach (ITrackedTask task in e.OldItems)
                    if (_rows.FirstOrDefault(r => ReferenceEquals(r.Task, task)) is { } gone)
                        _rows.Remove(gone);

            if (e.NewItems is not null)
                foreach (ITrackedTask task in e.NewItems)
                    AddRow(task);
        }

        UpdateState();
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column }) return;

        string? header = column.Header as string;
        string? sort = header switch
                       {
                           "Name"               => nameof(TaskRow.Name),
                           "Source"             => nameof(TaskRow.Source),
                           "Duration"           => nameof(TaskRow.Duration),
                           "Results"            => nameof(TaskRow.ResultCount),
                           "Result Size (bytes)" => nameof(TaskRow.ResultBytes),
                           "Status"             => nameof(TaskRow.StateOrder),
                           _                    => null
                       };
        if (sort is null) return;

        _sortDirection = _sortBy == sort && _sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        _sortBy        = sort;

        var viewOfRows = CollectionViewSource.GetDefaultView(_rows);
        viewOfRows.SortDescriptions.Clear();
        viewOfRows.SortDescriptions.Add(new SortDescription(sort, _sortDirection));
    }

    private List<TaskRow> Selected()
    {
        return _list.SelectedItems.OfType<TaskRow>().ToList();
    }

    private static bool CanCancel(TaskRow row)
    {
        return row.Task.CanCancel && !row.Task.State.IsFinished();
    }

    private void UpdateState()
    {
        if (_updating) return;

        _updating = true;
        try
        {
            var sel = Selected();
            _cancel.IsEnabled = sel.Any(CanCancel);
            _notify.IsEnabled = sel.Any(r => r.CanNotify);
            _notify.IsChecked = sel.Count > 0 && sel.Where(r => r.CanNotify).All(r => r.Notify) && sel.Any(r => r.CanNotify);
            _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            int active = _rows.Count(r => r.IsActive);
            _detail.Text = sel.Count == 1 ? Detail(sel[0]) : _rows.Count == 0 ? "" : $"{_rows.Count} task{(_rows.Count == 1 ? "" : "s")}, {active} running or queued";
        }
        finally
        {
            _updating = false;
        }
    }

    private static string Detail(TaskRow row)
    {
        var t = row.Task;
        string when = $"Queued {t.QueuedUtc.ToLocalTime():T}" + (t.StartedUtc is { } s ? $", started {s.ToLocalTime():T}" : "") + (t.EndedUtc is { } e ? $", ended {e.ToLocalTime():T}" : "");
        return string.IsNullOrWhiteSpace(t.Message) ? when : $"{t.Message}  ·  {when}";
    }

    private void CancelSelected()
    {
        foreach (var row in Selected().Where(CanCancel)) row.Task.Cancel();
        UpdateState();
    }

    private void SetNotify(bool on)
    {
        foreach (var row in Selected().Where(r => r.CanNotify)) row.Notify = on;
        UpdateState();
    }

    private void CopySelected()
    {
        var rows = Selected();
        if (rows.Count == 0) rows = [.. _rows];
        LabUi.Copy(string.Join(Environment.NewLine, rows.Select(r => r.Describe())));
        App.Status($"Copied {rows.Count} task(s)");
    }
}
