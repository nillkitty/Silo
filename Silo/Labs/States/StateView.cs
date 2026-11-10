using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shapes = System.Windows.Shapes;

namespace Silo.Labs.States;

/// <summary>
///     The main view of the State Lab: the machine drawn as boxes (states) and arrows (transitions). A state is dragged to move it, double-clicked to
///     rename it; a double-click on the empty canvas adds a state; Connect (or Ctrl+click) makes a transition with a click on each of two states.
///     While the machine runs, the state it is in is green and the arrows that can be taken from it are green too.
/// </summary>
internal sealed class StateView : Grid
{
    private const double NodeWidth  = 124;
    private const double NodeHeight = 46;
    private const double Pad        = 80;

    private static readonly Brush Normal  = LabUi.Frozen(0x8A, 0x8F, 0x98);
    private static readonly Brush Current = LabUi.Frozen(0x3F, 0xA9, 0x5B, 0x3C);

    private readonly StateLabSession           _session;
    private readonly Canvas                    _surface = new() { Background = Brushes.Transparent };
    private readonly Canvas                    _edges   = new() { IsHitTestVisible = true };
    private readonly Canvas                    _nodes   = new();
    private readonly Dictionary<string, Border> _boxes   = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock                 _hint    = new() { Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
    private          string                    _signature = string.Empty;
    private          string?                   _connectFrom;
    private          string?                   _dragging;
    private          Point                     _dragOffset;
    private          bool                      _dragMoved;

    public StateView(StateLabSession session)
    {
        _session = session;
        Focusable = true;
        this.SetResourceReference(BackgroundProperty, "Theme.BackgroundAlt");
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _surface.Children.Add(_edges);
        _surface.Children.Add(_nodes);
        var scroll = new ScrollViewer { Content = _surface, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.SetResourceReference(Control.BackgroundProperty, "Theme.BackgroundAlt");
        SetRow(scroll, 0);
        Children.Add(scroll);

        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        SetRow(_hint, 1);
        Children.Add(_hint);

        _surface.MouseLeftButtonDown += OnSurfaceDown;
        KeyDown                      += OnKeyDown;
        session.Changed              += (_, _) => Refresh();
        Loaded                       += (_, _) => Refresh();
        Refresh();
    }

    // ---- drawing ---------------------------------------------------------------------------------

    private string Signature()
    {
        return string.Join('|', _session.Doc.States.Select(s => $"{s.Name}@{s.X:0},{s.Y:0}/{s.Priority}")) + "#" + _session.Doc.Initial;
    }

    private void Refresh()
    {
        string signature = Signature();
        if (signature != _signature)
        {
            _signature = signature;
            BuildNodes();
        }

        if (!_session.Connecting) _connectFrom = null;
        Style();
        DrawEdges();
        _hint.Text = _session.Connecting
                         ? _connectFrom is null ? "Click the state the transition leaves." : $"Click the state that {_connectFrom} leads to (Esc stops)."
                         : _session.Run.Current is { } now ? $"Running: in {now} after {_session.Run.Steps} step(s). Double-click empty space to add a state, drag to move, Ctrl+click two states to connect, Delete removes."
                                                           : "Double-click empty space to add a state, drag to move, Ctrl+click two states to connect, Delete removes.";
    }

    private void BuildNodes()
    {
        _nodes.Children.Clear();
        _boxes.Clear();
        var doc = _session.Doc;
        foreach (var state in doc.States.Where(s => s.Name.Length > 0 && !_boxes.ContainsKey(s.Name)))
        {
            var text = new TextBlock { Text = state.Name, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
            var detail = new TextBlock { TextAlignment = TextAlignment.Center, FontSize = 10, Visibility = Visibility.Collapsed };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(text);
            stack.Children.Add(detail);

            var box = new Border { Width = NodeWidth, Height = NodeHeight, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 2, 6, 2), Child = stack, Cursor = Cursors.Hand, Tag = state.Name };
            box.SetResourceReference(Border.BackgroundProperty, "Theme.Surface");
            Canvas.SetLeft(box, state.X);
            Canvas.SetTop(box, state.Y);
            box.MouseLeftButtonDown += OnNodeDown;
            box.MouseMove           += OnNodeMove;
            box.MouseLeftButtonUp   += OnNodeUp;
            _nodes.Children.Add(box);
            _boxes[state.Name] = box;
        }

        double width  = doc.States.Count == 0 ? 0 : doc.States.Max(s => s.X) + NodeWidth + Pad;
        double height = doc.States.Count == 0 ? 0 : doc.States.Max(s => s.Y) + NodeHeight + Pad;
        _surface.Width  = Math.Max(width, 400);
        _surface.Height = Math.Max(height, 300);
    }

    /// <summary>What a state box looks like now: selected, initial, where the run is.</summary>
    private void Style()
    {
        var doc = _session.Doc;
        foreach (var state in doc.States)
        {
            if (!_boxes.TryGetValue(state.Name, out var box)) continue;

            bool selected = string.Equals(_session.SelectedState, state.Name, StringComparison.OrdinalIgnoreCase);
            bool initial  = string.Equals(doc.Initial, state.Name, StringComparison.OrdinalIgnoreCase);
            bool here     = string.Equals(_session.Run.Current, state.Name, StringComparison.OrdinalIgnoreCase);
            bool linking  = _connectFrom is not null && string.Equals(_connectFrom, state.Name, StringComparison.OrdinalIgnoreCase);

            box.BorderThickness = new Thickness(selected || linking ? 3 : initial ? 2 : 1);
            if (selected || linking) box.SetResourceReference(Border.BorderBrushProperty, "Theme.Accent");
            else box.BorderBrush = initial ? LabUi.Blue : Normal;

            if (here) box.Background = Current;
            else box.SetResourceReference(Border.BackgroundProperty, "Theme.Surface");

            var stack  = (StackPanel)box.Child;
            var title  = (TextBlock)stack.Children[0];
            var detail = (TextBlock)stack.Children[1];
            title.Text = (initial ? "▶ " : string.Empty) + state.Name;
            string extra = string.Join(" · ", new[] { state.Priority != 0 ? $"priority {state.Priority}" : null, here ? "now" : null }.Where(x => x is not null));
            detail.Text       = extra;
            detail.Visibility = extra.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            box.ToolTip       = state.Description;
        }
    }

    private Rect RectOf(string name)
    {
        var state = _session.Doc.Find(name);
        return state is null ? Rect.Empty : new Rect(state.X, state.Y, NodeWidth, NodeHeight);
    }

    private void DrawEdges()
    {
        _edges.Children.Clear();
        var doc      = _session.Doc;
        var possible = _session.Run.Candidates().ToDictionary(c => c.Transition.Id);
        var selected = _session.SelectedTransition?.Id;

        foreach (var t in doc.Transitions)
        {
            var to = RectOf(t.To);
            if (to.IsEmpty) continue;

            Brush brush = Normal;
            double thickness = 1.6;
            bool dashed = false;
            if (possible.TryGetValue(t.Id, out var status))
            {
                brush     = status.Enabled ? LabUi.Green : LabUi.Amber;
                thickness = status.Enabled ? 2.6 : 1.6;
                dashed    = !status.Enabled;
            }

            if (t.Id == selected)
            {
                brush     = LabUi.Blue;
                thickness = 3;
            }

            var (path, arrow, mid) = t.FromAny ? FromAnywhere(t, to) : t.From.Equals(t.To, StringComparison.OrdinalIgnoreCase) ? Loop(to) : Straight(t, to);
            if (path is null) continue;

            string id    = t.Id;
            var    hit   = new Shapes.Path { Data = path, Stroke = Brushes.Transparent, StrokeThickness = 14, Cursor = Cursors.Hand, Fill = Brushes.Transparent };
            hit.MouseLeftButtonDown += (_, e) =>
                                       {
                                           e.Handled = true;
                                           Focus();
                                           _session.Select(null, id);
                                       };
            var line = new Shapes.Path { Data = path, Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false, StrokeDashArray = t.FromAny || dashed ? [4, 3] : null };
            var head = new Shapes.Path { Data = arrow, Fill = brush, Stroke = brush, StrokeThickness = 1, IsHitTestVisible = false };
            _edges.Children.Add(hit);
            _edges.Children.Add(line);
            _edges.Children.Add(head);

            string label = Label(t);
            if (label.Length == 0) continue;

            var text = new TextBlock { Text = label, FontSize = 11, Padding = new Thickness(3, 0, 3, 0), IsHitTestVisible = false };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
            text.SetResourceReference(TextBlock.BackgroundProperty, "Theme.BackgroundAlt");
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(text, mid.X - text.DesiredSize.Width / 2);
            Canvas.SetTop(text, mid.Y - text.DesiredSize.Height / 2);
            _edges.Children.Add(text);
        }
    }

    private static string Label(StateTransitionDef t)
    {
        var parts = new List<string>();
        if (t.Priority != 0) parts.Add($"#{t.Priority}");
        if (t.Constraints.Count > 0) parts.Add(LabUi.Shorten(string.Join(" & ", t.Constraints.Select(c => $"{c.Field} {c.Op} {c.Value}")), 30));
        if (t.Effects.Count > 0) parts.Add($"⚡{t.Effects.Count}");
        return string.Join(" ", parts);
    }

    private (Geometry? Path, Geometry Arrow, Point Mid) Straight(StateTransitionDef t, Rect to)
    {
        var from = RectOf(t.From);
        if (from.IsEmpty) return (null, Geometry.Empty, default);

        // transitions between the same two states run side by side
        var between = _session.Doc.Transitions.Where(x => !x.FromAny && Same(x, t)).ToList();
        int index   = between.IndexOf(t);
        bool forward = string.Compare(t.From, t.To, StringComparison.OrdinalIgnoreCase) <= 0;
        var a = Center(from);
        var b = Center(to);
        var dir = b - a;
        if (dir.Length < 1) return (null, Geometry.Empty, default);

        dir.Normalize();
        var canonical = forward ? dir : -dir;
        var perp      = new Vector(-canonical.Y, canonical.X);
        double shift  = (index - (between.Count - 1) / 2.0) * 22;
        var start     = Leave(from, b + perp * shift, a + perp * shift);
        var end       = Leave(to, a + perp * shift, b + perp * shift);
        return (new LineGeometry(start, end), Arrow(start, end), new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2 - 8));
    }

    private static bool Same(StateTransitionDef a, StateTransitionDef b)
    {
        bool direct  = a.From.Equals(b.From, StringComparison.OrdinalIgnoreCase) && a.To.Equals(b.To, StringComparison.OrdinalIgnoreCase);
        bool reverse = a.From.Equals(b.To, StringComparison.OrdinalIgnoreCase) && a.To.Equals(b.From, StringComparison.OrdinalIgnoreCase);
        return direct || reverse;
    }

    private static (Geometry? Path, Geometry Arrow, Point Mid) Loop(Rect box)
    {
        var start = new Point(box.X + box.Width * 0.3, box.Y);
        var end   = new Point(box.X + box.Width * 0.7, box.Y);
        var figure = new PathFigure { StartPoint = start };
        figure.Segments.Add(new BezierSegment(new Point(start.X - 14, box.Y - 46), new Point(end.X + 14, box.Y - 46), end, true));
        var path = new PathGeometry();
        path.Figures.Add(figure);
        return (path, Arrow(new Point(end.X + 10, end.Y - 26), end), new Point(box.X + box.Width / 2, box.Y - 40));
    }

    /// <summary>A transition from any state comes in from the corner, dashed.</summary>
    private static (Geometry? Path, Geometry Arrow, Point Mid) FromAnywhere(StateTransitionDef t, Rect to)
    {
        var from  = new Point(to.X - 70, to.Y - 56);
        var end   = Leave(to, from, Center(to));
        var label = new Point(from.X - 4, from.Y - 4);
        return (new LineGeometry(from, end), Arrow(from, end), new Point(label.X + 10, label.Y - 4));
    }

    private static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    /// <summary>Where the line from <paramref name="inside" /> (the middle of the box, possibly moved aside) toward <paramref name="toward" /> leaves the box.</summary>
    private static Point Leave(Rect box, Point toward, Point inside)
    {
        double dx = toward.X - inside.X;
        double dy = toward.Y - inside.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) return inside;

        double sx = Math.Abs(dx) < 0.001 ? double.PositiveInfinity : box.Width / 2 / Math.Abs(dx);
        double sy = Math.Abs(dy) < 0.001 ? double.PositiveInfinity : box.Height / 2 / Math.Abs(dy);
        double s  = Math.Min(sx, sy);
        return new Point(inside.X + dx * s, inside.Y + dy * s);
    }

    private static Geometry Arrow(Point from, Point tip)
    {
        var dir = tip - from;
        if (dir.Length < 1) return Geometry.Empty;

        dir.Normalize();
        var perp = new Vector(-dir.Y, dir.X);
        var figure = new PathFigure { StartPoint = tip, IsClosed = true };
        figure.Segments.Add(new LineSegment(tip - dir * 11 + perp * 5, true));
        figure.Segments.Add(new LineSegment(tip - dir * 11 - perp * 5, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    // ---- mouse and keys --------------------------------------------------------------------------

    private void OnNodeDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Focus();
        var box  = (Border)sender;
        string name = (string)box.Tag;

        if (e.ClickCount == 2)
        {
            Rename(name);
            return;
        }

        if (_session.Connecting || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Connect(name);
            return;
        }

        _session.Select(name, null);
        _dragging  = name;
        _dragMoved = false;
        _dragOffset = e.GetPosition(box);
        box.CaptureMouse();
    }

    private void OnNodeMove(object sender, MouseEventArgs e)
    {
        if (_dragging is null || e.LeftButton != MouseButtonState.Pressed || _session.Doc.Find(_dragging) is not { } state) return;

        var at = e.GetPosition(_nodes);
        state.X = Math.Max(0, Math.Round((at.X - _dragOffset.X) / 4) * 4);
        state.Y = Math.Max(0, Math.Round((at.Y - _dragOffset.Y) / 4) * 4);
        _dragMoved = true;
        var box = (Border)sender;
        Canvas.SetLeft(box, state.X);
        Canvas.SetTop(box, state.Y);
        DrawEdges();
    }

    private void OnNodeUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging is null) return;

        ((Border)sender).ReleaseMouseCapture();
        _dragging = null;
        if (!_dragMoved) return;

        _session.Moved();
        // the boxes are where the person left them: nothing to build again
        _signature = Signature();
        var doc = _session.Doc;
        double width  = doc.States.Max(s => s.X) + NodeWidth + Pad;
        double height = doc.States.Max(s => s.Y) + NodeHeight + Pad;
        _surface.Width  = Math.Max(width, 400);
        _surface.Height = Math.Max(height, 300);
    }

    private void Connect(string name)
    {
        if (_connectFrom is null)
        {
            _connectFrom = name;
            if (!_session.Connecting) _session.SetConnecting(true);
            Refresh();
            return;
        }

        string from = _connectFrom;
        _connectFrom = null;
        _session.SetConnecting(false);
        _session.AddTransition(from, name);
        _session.Say($"Added {from} → {name}: choose its priority, constraints and effects in the Design pane");
        _session.ShowDesign();
    }

    private void Rename(string name)
    {
        string? answer = LabUi.Ask("Rename state", "What is the state called?", name);
        if (answer is null || answer.Trim() == name) return;

        if (_session.RenameState(name, answer) is { } problem) _session.Say(problem);
    }

    private void OnSurfaceDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != _surface && e.OriginalSource != _edges && e.OriginalSource != _nodes) return;

        Focus();
        if (e.ClickCount == 2 && !_session.Connecting)
        {
            var at = e.GetPosition(_surface);
            _session.AddState(Math.Max(0, Math.Round((at.X - NodeWidth / 2) / 4) * 4), Math.Max(0, Math.Round((at.Y - NodeHeight / 2) / 4) * 4));
            return;
        }

        _connectFrom = null;
        if (_session.Connecting) _session.SetConnecting(false);
        _session.Select(null, null);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            _session.DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _session.Connecting)
        {
            _connectFrom = null;
            _session.SetConnecting(false);
            e.Handled = true;
        }
    }
}
