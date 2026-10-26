using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Silo.Labs.Schemas;

/// <summary>The colors the Schema Lab draws each kind of construct in.</summary>
internal static class SchemaColors
{
    private static readonly Brush Purple = LabUi.Frozen(0xB0, 0x5D, 0xD6);
    private static readonly Brush Teal   = LabUi.Frozen(0x2D, 0xB8, 0xC4);

    public static Brush Of(ConstructKind kind)
    {
        return kind switch
               {
                   ConstructKind.Class        => LabUi.Blue,
                   ConstructKind.Attribute    => LabUi.Green,
                   ConstructKind.Enum         => LabUi.Amber,
                   ConstructKind.Syntax       => Purple,
                   ConstructKind.Unit         => Teal,
                   ConstructKind.UnitRelation => LabUi.Gray,
                   _                          => LabUi.Gray
               };
    }
}

/// <summary>The Schema Lab's main view: buttons to add constructs, the diagram of the draft, and the property editor of the selected one.</summary>
internal sealed class SchemaView : Grid
{
    private readonly SchemaLabSession _session;
    private readonly Canvas           _canvas = new();
    private readonly SchemaEditor     _editor;
    private readonly TextBlock        _summary;
    private readonly Button           _promote;
    private readonly System.Windows.Controls.Primitives.ToggleButton _connect;
    private readonly ComboBox         _target;
    private          string?          _connectFrom;
    private          bool             _loading;

    /// <summary>An icon and its text, as <see cref="LabUi.Tool" /> lays them out, for a button that is not a plain one.</summary>
    private static StackPanel ToggleContent(string text, string icon)
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

    public SchemaView(SchemaLabSession session)
    {
        _session = session;
        _editor  = new SchemaEditor(session);

        var bar = new WrapPanel { Margin = new Thickness(6, 4, 6, 4) };
        foreach (var kind in new[] { ConstructKind.Class, ConstructKind.Attribute, ConstructKind.Enum, ConstructKind.Syntax, ConstructKind.Unit, ConstructKind.UnitRelation })
        {
            var k = kind;
            var b = LabUi.Button("+ " + kind.Title(), () => _session.AddConstruct(k), $"Add a {kind.Title().ToLowerInvariant()} to the draft", icon: kind.IconKey());
            b.BorderBrush     = SchemaColors.Of(kind);
            b.BorderThickness = new Thickness(0, 0, 0, 2);
            bar.Children.Add(b);
        }

        _connect = new System.Windows.Controls.Primitives.ToggleButton { Content = ToggleContent("Connect", "Link"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 2, 0), ToolTip = "Join two classes: click the one that inherits, then its base class (hold Ctrl for an auxiliary class it includes)" };
        _connect.Checked   += (_, _) => StartConnect();
        _connect.Unchecked += (_, _) => StopConnect();
        bar.Children.Add(_connect);

        var show = LabUi.Button("Show ▾", () => { }, "Choose which kinds the diagram draws", icon: "Layers");
        show.Click += (_, _) => ShowMenu(show);
        bar.Children.Add(show);

        _target = LabUi.Combo(["Metaverse", "Local world"], "Metaverse", t =>
                                                                         {
                                                                             if (!_loading) _session.SetTarget(t == "Metaverse" ? PromoteTarget.Meta : PromoteTarget.World);
                                                                         }, 110);
        _target.ToolTip = "Where promoting puts the draft";
        _target.Margin  = new Thickness(10, 0, 2, 0);
        bar.Children.Add(_target);
        _promote = LabUi.Button("Promote", () => _session.Promote(), "Write the draft into the Silo as the Promote pane says", icon: "TbExport");
        bar.Children.Add(_promote);
        _summary = LabUi.Label(string.Empty, muted: true);
        _summary.Margin = new Thickness(8, 0, 0, 0);
        bar.Children.Add(_summary);

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360), MinWidth = 240 });

        SetColumnSpan(bar, 3);
        Children.Add(bar);

        var scroll = new ScrollViewer { Content = _canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        scroll.SetResourceReference(BackgroundProperty, "Theme.Background");
        SetRow(scroll, 1);
        Children.Add(scroll);

        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns };
        SetRow(splitter, 1);
        SetColumn(splitter, 1);
        Children.Add(splitter);

        SetRow(_editor, 1);
        SetColumn(_editor, 2);
        Children.Add(_editor);

        session.Changed          += (_, _) => Dispatcher.InvokeAsync(Redraw);
        session.SelectionChanged += (_, _) => Dispatcher.InvokeAsync(() =>
                                                                     {
                                                                         _editor.Rebuild();
                                                                         Redraw();
                                                                     });
        session.Loaded += (_, _) => Dispatcher.InvokeAsync(() =>
                                                           {
                                                               _editor.Rebuild();
                                                               Redraw();
                                                           });
        Focusable = true;
        KeyDown += (_, e) =>
                   {
                       if (e.Key == Key.Escape && _connect.IsChecked == true) _connect.IsChecked = false;
                   };
        Redraw();
    }

    private void ShowMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var kind in ConstructKinds.All.Where(k => k != ConstructKind.Relation))
        {
            var k    = kind;
            var item = new MenuItem { Header = kind.Plural(), IsCheckable = true, IsChecked = !_session.Hidden.Contains(kind) };
            item.Click += (_, _) => _session.ToggleHidden(k, !item.IsChecked);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    // ---- joining two classes ------------------------------------------------------------------------

    private void StartConnect()
    {
        _connectFrom = null;
        _session.Say("Click the class that inherits (Esc to stop).");
    }

    private void StopConnect()
    {
        _connectFrom = null;
        _session.Say(_session.Draft.Count + " construct(s)");
    }

    private void Clicked(SchemaConstruct item)
    {
        if (_connect.IsChecked != true)
        {
            _session.Select(item);
            return;
        }

        if (item.Kind != ConstructKind.Class)
        {
            _session.Say("Connect joins two classes: click a class.");
            return;
        }

        if (_connectFrom is null)
        {
            _connectFrom = item.Name;
            _session.Say($"Now click the class {item.Name} inherits (hold Ctrl for an auxiliary class it includes).");
            return;
        }

        string? problem = _session.Connect(_connectFrom, item.Name, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        _connect.IsChecked = false;
        if (problem is not null) _session.Say(problem);
    }

    // ---- the diagram -------------------------------------------------------------------------------

    private void Redraw()
    {
        _loading = true;
        _target.SelectedItem = _session.Draft.Target == PromoteTarget.Meta ? "Metaverse" : "Local world";
        _loading = false;

        var plan = _session.Plan;
        int errors = _session.Problems.Count(p => p.Severity == SchemaSeverity.Error);
        _summary.Text = $"{plan.Writes} to write, {plan.Steps.Count(s => s.Action == StepAction.Blocked)} blocked" + (errors > 0 ? $", {errors} error(s)" : string.Empty);
        _promote.IsEnabled = plan.CanApply;

        var layout = _session.Layout;
        _canvas.Children.Clear();
        _canvas.Width  = Math.Max(layout.Width, 200);
        _canvas.Height = Math.Max(layout.Height, 120);

        if (layout.Nodes.Count == 0)
        {
            var empty = LabUi.Label("The draft is empty. Add a class, attribute, enum, syntax or unit with the buttons above, or start from a template (Lab menu).", muted: true, wrap: true);
            empty.Width = 420;
            Canvas.SetLeft(empty, 20);
            Canvas.SetTop(empty, 20);
            _canvas.Children.Add(empty);
            return;
        }

        foreach (var edge in layout.Edges)
            if (layout.Node(edge.From) is { } from && layout.Node(edge.To) is { } to)
                DrawEdge(from, to, edge.Kind);

        var problems = _session.Problems.Where(p => p.Kind is not null).ToLookup(p => $"{p.Kind}:{p.Name}", StringComparer.OrdinalIgnoreCase);
        foreach (var node in layout.Nodes) DrawNode(node, problems[node.Ref].ToList());
    }

    private void DrawNode(LayoutNode node, IReadOnlyList<SchemaProblem> problems)
    {
        bool selected = string.Equals(_session.Selected?.Ref, node.Ref, StringComparison.OrdinalIgnoreCase);
        bool error    = problems.Any(p => p.Severity == SchemaSeverity.Error);
        bool warning  = problems.Any(p => p.Severity == SchemaSeverity.Warning);

        var stack = new StackPanel();
        var title = LabUi.Label(node.Title, bold: true);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        stack.Children.Add(title);
        if (node.Subtitle.Length > 0)
        {
            var sub = LabUi.Label(node.Subtitle, muted: true);
            sub.FontSize     = 11;
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(sub);
        }

        foreach (string line in node.Lines)
        {
            var l = LabUi.Label(line, mono: true);
            l.FontSize     = 11;
            l.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(l);
        }

        var box = new Border
                  {
                      Width           = node.Width,
                      Height          = node.Height,
                      Padding         = new Thickness(8, 4, 8, 4),
                      CornerRadius    = new CornerRadius(5),
                      BorderThickness = new Thickness(selected ? 3 : 1.5),
                      BorderBrush     = error ? LabUi.Red : warning ? LabUi.Amber : SchemaColors.Of(node.Kind),
                      Child           = stack,
                      Cursor          = Cursors.Hand,
                      ClipToBounds    = true,
                      Tag             = node.Ref
                  };
        box.SetResourceReference(Border.BackgroundProperty, "Theme.Surface");
        if (problems.Count > 0) box.ToolTip = string.Join("\n", problems.Select(p => p.Text));
        else box.ToolTip = node.Kind.Title() + " " + node.Title;

        box.MouseLeftButtonDown += (_, e) =>
                                   {
                                       if (_session.Draft.FindRef(node.Ref) is { } item) Clicked(item);
                                       e.Handled = true;
                                   };
        Canvas.SetLeft(box, node.X);
        Canvas.SetTop(box, node.Y);
        _canvas.Children.Add(box);
    }

    /// <summary>Where a line from the center of <paramref name="from" /> toward the center of <paramref name="to" /> leaves the box of <paramref name="from" />.</summary>
    internal static Point Leave(LayoutNode from, LayoutNode to)
    {
        double dx = to.CenterX - from.CenterX, dy = to.CenterY - from.CenterY;
        if (dx == 0 && dy == 0) return new Point(from.CenterX, from.CenterY);

        double sx = dx == 0 ? double.MaxValue : from.Width / 2 / Math.Abs(dx);
        double sy = dy == 0 ? double.MaxValue : from.Height / 2 / Math.Abs(dy);
        double s  = Math.Min(sx, sy);
        return new Point(from.CenterX + dx * s, from.CenterY + dy * s);
    }

    private void DrawEdge(LayoutNode from, LayoutNode to, EdgeKind kind)
    {
        var a = Leave(from, to);
        var b = Leave(to, from);
        var stroke = kind switch
                     {
                         EdgeKind.Inherits or EdgeKind.Includes => (Brush)LabUi.Blue,
                         EdgeKind.Typed                         => LabUi.Amber,
                         EdgeKind.Syntax                        => SchemaColors.Of(ConstructKind.Syntax),
                         _                                      => LabUi.Gray
                     };
        var line = new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = stroke, StrokeThickness = kind is EdgeKind.Inherits or EdgeKind.Includes ? 1.6 : 1.1, IsHitTestVisible = false };
        if (kind is EdgeKind.Includes or EdgeKind.Typed or EdgeKind.Syntax) line.StrokeDashArray = kind == EdgeKind.Includes ? [5, 3] : [2, 3];
        if (kind is EdgeKind.Target or EdgeKind.Next) line.StrokeDashArray = [4, 3];
        _canvas.Children.Add(line);

        // the head of the arrow, at the end that was pointed to: a hollow triangle for inheritance, an open one for the rest
        double angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
        double size  = kind == EdgeKind.Inherits ? 11 : 8;
        var    left  = new Point(b.X - size * Math.Cos(angle - 0.45), b.Y - size * Math.Sin(angle - 0.45));
        var    right = new Point(b.X - size * Math.Cos(angle + 0.45), b.Y - size * Math.Sin(angle + 0.45));
        if (kind == EdgeKind.Inherits)
        {
            var head = new Polygon { Points = [b, left, right], Stroke = stroke, StrokeThickness = 1.6, IsHitTestVisible = false };
            head.SetResourceReference(Shape.FillProperty, "Theme.Background");
            _canvas.Children.Add(head);
        }
        else
        {
            _canvas.Children.Add(new Polyline { Points = [left, b, right], Stroke = stroke, StrokeThickness = 1.2, IsHitTestVisible = false });
        }
    }
}
