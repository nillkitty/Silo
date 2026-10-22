using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Silo.Labs.Parser;

/// <summary>
///     The Parser Lab's main view: the rules of the spec as boxes inside boxes that things are dragged onto (tokens, other rules, combinators, and
///     the boxes themselves, to move them between rules and pieces), under a header that says what is being defined, what it returns, what it
///     ignores and what it ends on. In the tokenizer mode the rows are the matchers that cut text into tokens. It is rebuilt from the
///     <see cref="ParserWorkspace" /> whenever the spec changes, which is cheap at the size of a spec; a change of the selection only changes how
///     the boxes are marked (so that a click does not take away the box a drag is about to start from).
/// </summary>
internal sealed class SpecCanvasView : Grid
{
    private static readonly string[] ReturnTypes =
        ["", "string", "int", "long", "decimal", "bool", "guid", "byte", "byte[]", "byte[6]", "datetime", "object"];

    private readonly ParserWorkspace _ws;
    private readonly Action          _build;
    private readonly Action          _compile;
    private readonly Action          _test;
    private readonly Action<string>  _showPane;

    private readonly ContentControl _headerHost = new();
    private readonly ScrollViewer   _scroll     = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ContentControl _editorHost = new();
    private readonly TextBlock      _message    = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock      _problems   = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

    private readonly List<(SpecNode Node, Border Box)>       _boxes     = [];
    private readonly List<(TokenRule Rule, Border Row)>      _ruleRows  = [];
    private readonly Dictionary<string, FrameworkElement>    _ruleNames = new(StringComparer.OrdinalIgnoreCase);
    private          bool                                    _pending;

    public SpecCanvasView(ParserWorkspace workspace, Action build, Action compile, Action test, Action<string> showPane)
    {
        _ws       = workspace;
        _build    = build;
        _compile  = compile;
        _test     = test;
        _showPane = showPane;

        Focusable = true;
        this.SetResourceReference(Panel.BackgroundProperty, "Theme.Background");
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _scroll.SetResourceReference(Control.BackgroundProperty, "Theme.BackgroundAlt");
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");

        SetRow(_headerHost, 0);
        SetRow(_scroll, 1);
        SetRow(_editorHost, 2);
        Children.Add(_headerHost);
        Children.Add(_scroll);
        Children.Add(_editorHost);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
        footer.Children.Add(LabUi.Tool("Build", "Build", _build, "Make the parsers from the rules and report anything wrong with them"));
        footer.Children.Add(LabUi.Tool("Compile", "Compile", _compile, "Keep the parsers as a function and time it: what runs at full speed"));
        footer.Children.Add(LabUi.Tool("Run All Tests", "RunAll", _test, "Run all the tests"));
        footer.Children.Add(_problems);
        footer.Children.Add(_message);
        SetRow(footer, 3);
        Children.Add(footer);

        KeyDown += OnKeyDown;
        _ws.Changed += OnChanged;
        Unloaded += (_, _) => _ws.Changed -= OnChanged;
        Loaded += (_, _) =>
                  {
                      _ws.Changed -= OnChanged;
                      _ws.Changed += OnChanged;
                      Rebuild();
                  };
        Rebuild();
    }

    /// <summary>Shows a line of outcome next to the buttons (what Build, Compile or Test came to).</summary>
    public void Say(string message) => _message.Text = message;

    private void OnChanged(WorkspaceChange change)
    {
        if ((change & WorkspaceChange.TestsRan) != 0) Say(_ws.LastTestLine);

        const WorkspaceChange structure = WorkspaceChange.Header | WorkspaceChange.Rules | WorkspaceChange.Loaded;
        if ((change & (structure | WorkspaceChange.Selection)) == 0) return;

        if ((change & structure) == 0)
        {
            // only what is selected changed: mark it, now (the boxes stay where they are)
            UpdateSelection();
            return;
        }

        // not now: the change may come from a handler of an element that the rebuild takes away
        if (_pending) return;

        _pending = true;
        Dispatcher.InvokeAsync(() =>
                               {
                                   _pending = false;
                                   Rebuild();
                               }, DispatcherPriority.Normal);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox) return;

        if (e.Key == Key.F2 || (e.Key == Key.Enter && _ws.SelectedNode is not null))
        {
            FocusEditor();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _ws.DeleteSelection())
        {
            e.Handled = true;
        }
        else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control && _ws.DuplicateSelection())
        {
            e.Handled = true;
        }
    }

    // ---- building --------------------------------------------------------------------------------

    private void Rebuild()
    {
        double offset = _scroll.VerticalOffset;
        bool   parser = _ws.Doc.Mode == SpecMode.Parser;

        _boxes.Clear();
        _ruleRows.Clear();
        _ruleNames.Clear();
        _headerHost.Content = parser ? ParserHeader() : TokenizerHeader();
        _scroll.Content     = parser ? RuleRows() : TokenizerRows();
        _editorHost.Content = NodeEditor();

        var problems = _ws.Problems;
        _problems.Text       = problems.Count == 0 ? string.Empty : $"{problems.Count} problem{(problems.Count == 1 ? string.Empty : "s")}";
        _problems.ToolTip    = problems.Count == 0 ? null : string.Join("\n", problems.Select(d => $"{d.Rule}: {d.Message}"));
        _problems.Foreground = ParserUi.Red;

        _scroll.Dispatcher.InvokeAsync(() => _scroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    /// <summary>Marks the selected box (and shows its editor) without making the boxes again.</summary>
    private void UpdateSelection()
    {
        foreach (var (node, box) in _boxes)
        {
            bool selected = ReferenceEquals(node, _ws.SelectedNode);
            box.BorderThickness = new Thickness(selected ? 2 : 1);
            box.SetResourceReference(Border.BorderBrushProperty, selected ? "Theme.Accent" : "Theme.Border");
        }

        foreach (var (rule, row) in _ruleRows)
        {
            bool selected = ReferenceEquals(rule, _ws.SelectedTokenRule);
            row.BorderThickness = new Thickness(selected ? 2 : 1);
            row.SetResourceReference(Border.BorderBrushProperty, selected ? "Theme.Accent" : "Theme.BackgroundAlt");
        }

        _editorHost.Content = NodeEditor();
    }

    private static StackPanel Row(string label)
    {
        var row  = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var text = LabUi.Label(label, bold: true);
        text.Width         = 96;
        text.TextAlignment = TextAlignment.Right;
        text.Margin        = new Thickness(0, 0, 6, 0);
        row.Children.Add(text);
        return row;
    }

    private UIElement ModeRow()
    {
        var row = Row("Mode:");
        row.Children.Add(ParserUi.Combo(["Parser", "Tokenizer"], _ws.Doc.Mode.ToString(), m => _ws.Edit(d => d.Mode = Enum.Parse<SpecMode>(m), WorkspaceChange.Header), false, 110));
        return row;
    }

    /// <summary>Runs something that may be refused (a name that is taken, an import that would make a circle) and says why when it is.</summary>
    private void Guard(Action act)
    {
        try
        {
            act();
        }
        catch (InvalidOperationException ex)
        {
            Say(ex.Message);
            MessageBox.Show(ex.Message, "Parser Lab");
        }
    }

    /// <summary>Does something that answers with what is wrong (null: nothing).</summary>
    private void Check(Func<string?> act)
    {
        if (act() is { } problem)
        {
            Say(problem);
            MessageBox.Show(problem, "Parser Lab");
        }
    }

    // ---- parser mode -----------------------------------------------------------------------------

    private UIElement ParserHeader()
    {
        var panel = new StackPanel { Margin = new Thickness(8, 8, 8, 6) };
        panel.Children.Add(ModeRow());

        var defining = Row("Defining:");
        defining.Children.Add(ParserUi.Combo(_ws.Doc.Rules.Select(r => r.Name), _ws.CurrentRule, n => _ws.CurrentRule = n, false, 200));
        defining.Children.Add(LabUi.IconButton("Plus", NewRule, "Add a rule"));
        defining.Children.Add(LabUi.IconButton("Property", RenameRule, "Rename the rule (and every reference to it)"));
        defining.Children.Add(LabUi.IconButton("TbDelete", DeleteRule, "Delete the rule"));
        if (_ws.Current is { } current)
        {
            var entry = LabUi.Check("Entry point", string.Equals(_ws.Doc.Root, current.Name, StringComparison.OrdinalIgnoreCase),
                                    on => _ws.Edit(d => d.Root = on ? current.Name : d.Root == current.Name ? null : d.Root, WorkspaceChange.Header),
                                    "What the spec parses text as when no rule is named");
            entry.Margin = new Thickness(10, 0, 0, 0);
            defining.Children.Add(entry);
        }

        panel.Children.Add(defining);

        if (_ws.Current is { } rule)
        {
            var returns = Row("Returns:");
            returns.Children.Add(ParserUi.Combo(["Syntax", "Token"], rule.Kind.ToString(), k => _ws.EditCurrent(r => r.Kind = Enum.Parse<RuleKind>(k)), false, 90));
            returns.Children.Add(ParserUi.Combo(["Tree", "Linear"], rule.Shape, s => _ws.EditCurrent(r => r.Shape = s), false, 90));
            returns.Children.Add(ParserUi.Combo(ReturnTypes, rule.Returns ?? string.Empty, t => _ws.EditCurrent(r => r.Returns = string.IsNullOrWhiteSpace(t) ? null : t.Trim()), true, 150));
            returns.ToolTip = "Tree: the values as nested lists. Linear: one flat list. The type: what the rule's value is made into (byte[6], int, string...).";
            panel.Children.Add(returns);

            var about = Row("About:");
            about.Children.Add(ParserUi.Field(rule.Description, t => _ws.EditCurrent(r => r.Description = string.IsNullOrWhiteSpace(t) ? null : t.Trim()), 360));
            panel.Children.Add(about);
        }

        panel.Children.Add(TokenChipRow("Ignoring:", d => d.Ignore, "Skipped between the items of a sequence", false));
        panel.Children.Add(TokenChipRow("Exiting On:", d => d.ExitOn, "Ends the text instead of being an error", true));
        return panel;
    }

    private StackPanel TokenChipRow(string label, Func<ParserSpecDoc, List<string>> pick, string tip, bool endOfText)
    {
        var row = Row(label);
        row.ToolTip = tip;
        foreach (string id in pick(_ws.Doc).ToList())
            row.Children.Add(Chip(id, () => _ws.Edit(d => pick(d).RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase)), WorkspaceChange.Header)));

        if (endOfText)
        {
            var eof = ParserUi.Box(ChipContent("EOF", null, muted: true));
            eof.ToolTip = "The end of the text always ends it";
            row.Children.Add(eof);
        }

        var add = LabUi.IconButton("Plus", () => { }, "Add a kind of character");
        add.Click += (_, _) =>
                     {
                         var menu = new ContextMenu();
                         foreach (string id in SpecCatalog.IgnorableTokenIds.Where(t => !pick(_ws.Doc).Contains(t, StringComparer.OrdinalIgnoreCase)))
                         {
                             string captured = id;
                             var    item     = new MenuItem { Header = id, ToolTip = SpecCatalog.Token(id)?.Description, Icon = LabUi.Icon("Token") };
                             item.Click += (_, _) => _ws.Edit(d => pick(d).Add(captured), WorkspaceChange.Header);
                             menu.Items.Add(item);
                         }

                         if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(all of them are there)", IsEnabled = false });
                         menu.PlacementTarget = add;
                         menu.IsOpen          = true;
                     };
        row.Children.Add(add);
        return row;
    }

    private static UIElement ChipContent(string text, Action? remove, bool muted = false)
    {
        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        if (LabUi.Icon("Token", 13) is { } glyph)
        {
            glyph.Margin = new Thickness(0, 0, 4, 0);
            inner.Children.Add(glyph);
        }

        var label = LabUi.Label(text, muted: muted, mono: true);
        label.FontStyle = FontStyles.Italic;
        inner.Children.Add(label);
        if (remove is not null)
        {
            var x = new TextBlock { Text = " ×", Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Remove" };
            x.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
            x.MouseLeftButtonUp += (_, _) => remove();
            inner.Children.Add(x);
        }

        return inner;
    }

    private static Border Chip(string text, Action? remove) => ParserUi.Box(ChipContent(text, remove));

    private void NewRule()
    {
        string? name = ParserUi.Ask("New rule", "Name of the rule:", _ws.UniqueRuleName("rule"));
        if (!string.IsNullOrWhiteSpace(name)) _ws.AddRule(name);
    }

    private void RenameRule()
    {
        if (_ws.CurrentRule is not { } current) return;

        string? name = ParserUi.Ask("Rename rule", "New name (references to it change too):", current);
        if (string.IsNullOrWhiteSpace(name)) return;

        Guard(() => _ws.RenameRule(current, name));
    }

    private void DeleteRule()
    {
        if (_ws.CurrentRule is not { } current) return;

        if (ParserUi.Confirm($"Delete the rule '{current}'?")) _ws.DeleteRule(current);
    }

    /// <summary>
    ///     Shows the rule (the current one when none is named) and puts the cursor in its settings: what Configure Rule of the Rules menu does. When a
    ///     piece of it is selected, the cursor goes to that piece's settings instead.
    /// </summary>
    public void ConfigureRule(string? rule = null)
    {
        if (rule is not null) _ws.CurrentRule = rule;

        _ws.SelectedNode = null;
        Dispatcher.InvokeAsync(() =>
                               {
                                   if (_ruleNames.TryGetValue(_ws.CurrentRule ?? string.Empty, out var box)) box.BringIntoView();
                                   FocusIn(_headerHost);
                               }, DispatcherPriority.Input);
    }

    /// <summary>Puts the cursor in the settings of the selected piece (the first box or list of its editor).</summary>
    public void FocusEditor()
    {
        if (_ws.SelectedNode is null)
        {
            Say("Select a piece of the rule first");
            return;
        }

        Dispatcher.InvokeAsync(() => FocusIn(_editorHost), DispatcherPriority.Input);
    }

    private static void FocusIn(DependencyObject host)
    {
        foreach (var control in Descendants(host))
        {
            if (control is TextBox { IsEnabled: true, IsVisible: true } or ComboBox { IsEnabled: true, IsVisible: true })
            {
                ((UIElement)control).Focus();
                if (control is TextBox box) box.SelectAll();
                return;
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    private UIElement RuleRows()
    {
        var stack = new StackPanel { Margin = new Thickness(8) };
        foreach (var rule in _ws.Doc.Rules) stack.Children.Add(RuleRow(rule));

        var start = ParserUi.Box(LabUi.Label("Drop a token, a rule or a combinator here to start a new rule (or click)", muted: true), false, "Theme.BackgroundAlt");
        start.Margin              = new Thickness(0, 12, 0, 0);
        start.HorizontalAlignment = HorizontalAlignment.Left;
        ParserUi.EnableDrop(start, (item, copy) => Guard(() =>
                                                         {
                                                             var rule = _ws.AddRule();
                                                             _ws.Drop(item, null, ParserWorkspace.RuleSlot(rule), copy);
                                                         }));
        start.MouseLeftButtonDown += (_, _) => NewRule();
        stack.Children.Add(start);

        // clicking the empty canvas clears the selection
        _scroll.MouseLeftButtonDown -= OnCanvasClick;
        _scroll.MouseLeftButtonDown += OnCanvasClick;
        return stack;
    }

    private void OnCanvasClick(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (_ws.SelectedNode is not null) _ws.SelectedNode = null;
        if (_ws.SelectedTokenRule is not null) _ws.SelectedTokenRule = null;
    }

    private UIElement RuleRow(RuleDef rule)
    {
        var slot = ParserWorkspace.RuleSlot(rule);
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        if (LabUi.Icon(rule.Kind == RuleKind.Token ? "Token" : "Rule", 15) is { } glyph)
        {
            glyph.Margin = new Thickness(0, 0, 5, 0);
            title.Children.Add(glyph);
        }

        title.Children.Add(LabUi.Label(rule.Name + " :=", bold: true, mono: true));
        var nameBox = ParserUi.Box(title, string.Equals(rule.Name, _ws.CurrentRule, StringComparison.Ordinal), "Theme.Surface", new Thickness(6, 3, 6, 3));
        nameBox.VerticalAlignment = VerticalAlignment.Top;
        nameBox.ToolTip           = (rule.Description is { Length: > 0 } d ? d + "\n" : string.Empty) + "Click to define it; double-click to change its settings. Drag it into another rule to use it there.";
        nameBox.MouseLeftButtonUp += (_, e) =>
                                     {
                                         Focus();
                                         _ws.SelectedNode = null;
                                         if (e.ClickCount >= 2) ConfigureRule(rule.Name);
                                         else _ws.CurrentRule = rule.Name;
                                         e.Handled = true;
                                     };
        ParserUi.EnableDrag(nameBox, () => new DragItem("rule", rule.Name));
        ParserUi.EnableDrop(nameBox, (item, copy) => Guard(() => _ws.Drop(item, null, slot, copy)));
        _ruleNames[rule.Name] = nameBox;
        SetColumn(nameBox, 0);
        grid.Children.Add(nameBox);

        UIElement body = rule.Body is null ? Placeholder(slot, null) : Render(rule.Body, slot);
        SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private Border Placeholder(SpecSlot slot, SpecNode? into)
    {
        var box = ParserUi.Box(LabUi.Label("drop here", muted: true), false, "Theme.BackgroundAlt");
        box.HorizontalAlignment = HorizontalAlignment.Left;
        ParserUi.EnableDrop(box, (item, copy) => Guard(() => _ws.Drop(item, into, slot, copy)));
        return box;
    }

    // ---- the boxes of a tree ---------------------------------------------------------------------

    private FrameworkElement Render(SpecNode node, SpecSlot slot)
    {
        bool selected = ReferenceEquals(node, _ws.SelectedNode);
        var  box      = ParserUi.Box(Content(node, slot), selected, node.Kind == "seq" ? "Theme.BackgroundAlt" : "Theme.Surface");
        box.HorizontalAlignment = HorizontalAlignment.Left;
        if (node.Ignore)
        {
            box.Opacity = 0.65;
            box.ToolTip = "Left out of the value";
        }

        _boxes.Add((node, box));

        // the drag is registered first: the box marks the click handled to stop its parent from taking it
        ParserUi.EnableNodeDrag(box, () =>
                                     {
                                         _ws.DraggedNode = node;
                                         return new DragItem("node", string.Empty);
                                     });
        box.MouseLeftButtonDown += (_, e) =>
                                   {
                                       Focus();
                                       _ws.SelectedNode = node;
                                       if (e.ClickCount == 2) FocusEditor();
                                       e.Handled = true;
                                   };
        ParserUi.EnableDrop(box, (item, copy) => Guard(() => _ws.Drop(item, node, slot, copy)));
        box.ContextMenu = NodeMenu(node);
        return box;
    }

    private UIElement Content(SpecNode node, SpecSlot slot)
    {
        switch (node.Kind)
        {
            case "token":
                return Leaf("Token", node.Text ?? "?", italic: true, error: SpecCatalog.Token(node.Text) is null && _ws.Doc.FindFragment(node.Text, FragmentKind.Token) is null);
            case "literal":
                return Leaf("Literal", "\"" + node.Text + "\"" + (node.IgnoreCase ? " (any case)" : string.Empty));
            case "chars":
                return Leaf("CharSet", (node.Negate ? "[^" : "[") + node.Text + "]");
            case "unicode":
                return Leaf("Unicode", (node.Negate ? "not " : string.Empty) + "unicode " + node.Text, error: !UnicodeClasses.TryParse(node.Text, out _, out _));
            case "ref":
                return Leaf("Rule", node.Spec is { Length: > 0 } spec ? node.Text + "  (" + spec + ")" : node.Text ?? "?", bold: true, error: node.Spec is null && _ws.Doc.Find(node.Text) is null);
            case "eof":
                return Leaf("Token", "EOF", italic: true);

            case "seq":
            {
                var sp = new StackPanel();
                if (node.Children.Count == 0) sp.Children.Add(LabUi.Label("(empty: drop something here)", muted: true));
                foreach (var child in node.Children) sp.Children.Add(Render(child, slot));
                return sp;
            }

            case "choice":
            {
                var sp = new StackPanel();
                sp.Children.Add(LabUi.Label("one of:", muted: true));
                for (int i = 0; i < node.Children.Count; i++)
                {
                    if (i > 0) sp.Children.Add(LabUi.Label("or", muted: true));
                    sp.Children.Add(Render(node.Children[i], slot));
                }

                return sp;
            }

            case "repeat":
                return Wrapped(node, slot, Quantifier(node));
            case "not":
                return Wrapped(node, slot, "not");
            case "try":
                return Wrapped(node, slot, "try");
            case "convert":
                return Wrapped(node, slot, "⇒ " + node.Text);
            case "where":
                return Wrapped(node, slot, "where " + node.Text);
            case "named":
                return Wrapped(node, slot, "# " + node.Name);

            case "between":
            {
                var sp = new StackPanel();
                string[] labels = ["open", "body", "close"];
                for (int i = 0; i < node.Children.Count; i++) sp.Children.Add(Labelled(i < 3 ? labels[i] : "…", Render(node.Children[i], slot)));
                return sp;
            }

            case "delimited":
            {
                var sp = new StackPanel();
                string[] labels = ["item", "separator"];
                for (int i = 0; i < node.Children.Count; i++) sp.Children.Add(Labelled(i < 2 ? labels[i] : "…", Render(node.Children[i], slot)));
                sp.Children.Insert(0, LabUi.Label(node.Min > 0 ? "list (at least one)" : "list (maybe none)", muted: true));
                return sp;
            }

            default:
                return LabUi.Label(node.Kind);
        }
    }

    private static UIElement Labelled(string label, UIElement element)
    {
        var row  = new StackPanel { Orientation = Orientation.Horizontal };
        var text = LabUi.Label(label, muted: true);
        text.Width  = 64;
        text.Margin = new Thickness(0, 0, 4, 0);
        row.Children.Add(text);
        row.Children.Add(element);
        return row;
    }

    private UIElement Wrapped(SpecNode node, SpecSlot slot, string badge)
    {
        var row   = new StackPanel { Orientation = Orientation.Horizontal };
        var inner = node.Children.Count > 0 ? Render(node.Children[0], slot) : Placeholder(slot, node);
        row.Children.Add(inner);
        var tag = ParserUi.Box(LabUi.Label(badge, mono: true), false, "Theme.BackgroundAlt");
        tag.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(tag);
        return row;
    }

    /// <summary>How a quantifier reads: [0..*], [1..*], [0..1], [n] for exactly n, [p..q], [p..*].</summary>
    private static string Quantifier(SpecNode n)
    {
        return (n.Min, n.Max) switch
               {
                   (0, null)                   => "[0..*]",
                   (1, null)                   => "[1..*]",
                   (0, 1)                      => "[0..1]",
                   ( { } a, { } b) when a == b => $"[{a}]",
                   ( { } a, null)              => $"[{a}..*]",
                   ( { } a, { } b)             => $"[{a}..{b}]"
               };
    }

    private static UIElement Leaf(string icon, string text, bool italic = false, bool bold = false, bool error = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (LabUi.Icon(icon, 14) is { } glyph)
        {
            glyph.Margin = new Thickness(0, 0, 5, 0);
            if (error) glyph.Foreground = ParserUi.Red;
            row.Children.Add(glyph);
        }

        var label = LabUi.Label(text, mono: true, bold: bold);
        if (italic) label.FontStyle = FontStyles.Italic;
        if (error) label.Foreground = ParserUi.Red;
        row.Children.Add(label);
        return row;
    }

    private ContextMenu NodeMenu(SpecNode node)
    {
        var menu = new ContextMenu();

        MenuItem Item(string header, string? icon, Action run, bool enabled = true, string? gesture = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, Icon = LabUi.Icon(icon) };
            if (gesture is not null) item.InputGestureText = gesture;
            item.Click += (_, _) => run();
            return item;
        }

        // the menu is built when the box is, but the clipboard changes: look again when it opens
        var paste = Item("Paste", "TbPaste", () =>
                                             {
                                                 _ws.SelectedNode = node;
                                                 Guard(() => _ws.Paste(node));
                                             }, gesture: "Ctrl+V");
        menu.Opened += (_, _) => paste.IsEnabled = _ws.CanPaste;

        menu.Items.Add(Item("Cut", "TbCut", () => _ws.CutNode(node), gesture: "Ctrl+X"));
        menu.Items.Add(Item("Copy", "TbCopy", () => _ws.CopyNode(node), gesture: "Ctrl+C"));
        menu.Items.Add(paste);
        menu.Items.Add(Item("Duplicate", "TbCopy", () => _ws.Duplicate(node), gesture: "Ctrl+D"));
        menu.Items.Add(Item("Delete", "TbDelete", () => _ws.Remove(node), gesture: "Del"));
        menu.Items.Add(new Separator());

        menu.Items.Add(Item(node.Ignore ? "Keep in the Value" : "Leave out of the Value", null, () => _ws.EditNode(node, n => n.Ignore = !n.Ignore)));

        var wrap = new MenuItem { Header = "Wrap in", Icon = LabUi.Icon("Wrap") };
        foreach (var comb in SpecCatalog.Combinators.Where(c => c.Id is "many" or "once" or "optional" or "one" or "not" or "try" or "where" or "named" or "apply" or "then" or "or"))
        {
            var captured = comb;
            wrap.Items.Add(Item(comb.Title, ParserUi.CombinatorIcon(comb.Id), () => _ws.Wrap(node, captured.Id)));
        }

        menu.Items.Add(wrap);
        menu.Items.Add(Item("Unwrap", null, () => _ws.Unwrap(node), node.Children.Count == 1));
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("Save Syntax...", "Extension", () => SaveAs(node, FragmentKind.Syntax)));
        menu.Items.Add(Item("Save as Token...", "Token", () => SaveAs(node, FragmentKind.Token), !node.SelfAndDescendants().Any(n => n.Kind == "ref")));
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("Move Up", "ArrowUp", () => _ws.Move(node, -1)));
        menu.Items.Add(Item("Move Down", "ArrowDown", () => _ws.Move(node, 1)));
        return menu;
    }

    /// <summary>Keeps the piece in the spec under a name: syntax (a copy is dropped in) or a token (used by name), listed in the Syntax or Tokens pane.</summary>
    private void SaveAs(SpecNode node, FragmentKind kind)
    {
        string what = kind == FragmentKind.Token ? "token" : "syntax";
        string? name = ParserUi.Ask(kind == FragmentKind.Token ? "Save as token" : "Save syntax", $"Name of the {what}:", _ws.UniqueFragmentName(node.Kind == "literal" ? node.Text ?? what : what));
        if (string.IsNullOrWhiteSpace(name)) return;

        Guard(() =>
              {
                  _ws.SaveFragment(node, name, kind);
                  Say($"Saved {what} '{name.Trim()}': it is in the {(kind == FragmentKind.Token ? "Tokens" : "Syntax")} pane");
                  _showPane(kind == FragmentKind.Token ? "tokens" : "syntax");
              });
    }

    // ---- what is selected ------------------------------------------------------------------------

    private UIElement? NodeEditor()
    {
        var node = _ws.SelectedNode;
        if (node is null) return null;

        var row = new WrapPanel { Margin = new Thickness(8, 4, 8, 4) };
        var kind = new StackPanel { Orientation = Orientation.Horizontal };
        if (LabUi.Icon(ParserUi.NodeIcon(node.Kind), 15) is { } glyph)
        {
            glyph.Margin = new Thickness(0, 0, 5, 0);
            kind.Children.Add(glyph);
        }

        kind.Children.Add(LabUi.Label(node.Kind, bold: true));
        row.Children.Add(kind);
        row.Children.Add(Spacer());

        switch (node.Kind)
        {
            case "token":
                row.Children.Add(ParserUi.Combo(SpecCatalog.Tokens.Select(t => t.Id).Concat(_ws.Doc.Fragments.Where(f => f.Kind == FragmentKind.Token).Select(f => f.Name)), node.Text, t => _ws.EditNode(node, n => n.Text = t), false, 150));
                break;
            case "literal":
                row.Children.Add(ParserUi.Field(node.Text, t => _ws.EditNode(node, n => n.Text = t), 140, "The text to match"));
                row.Children.Add(LabUi.Check("any case", node.IgnoreCase, v => _ws.EditNode(node, n => n.IgnoreCase = v)));
                break;
            case "chars":
                row.Children.Add(ParserUi.Field(node.Text, t => _ws.EditNode(node, n => n.Text = t), 140, "The characters, one of which is matched"));
                row.Children.Add(LabUi.Check("anything but these", node.Negate, v => _ws.EditNode(node, n => n.Negate = v)));
                break;
            case "unicode":
            {
                var classes = ParserUi.Field(node.Text, t => _ws.EditNode(node, n => n.Text = t), 140, "Unicode classes such as L, Lu or Nd; several are separated by commas");
                if (!UnicodeClasses.TryParse(node.Text, out _, out string? why))
                {
                    classes.Foreground = ParserUi.Red;
                    classes.ToolTip    = why;
                }

                row.Children.Add(classes);
                var pick = LabUi.Tool("Classes", "Unicode", () => { }, "Add a class of characters");
                pick.Click += (_, _) => ClassMenu(pick, node);
                row.Children.Add(pick);
                row.Children.Add(LabUi.Check("anything but these", node.Negate, v => _ws.EditNode(node, n => n.Negate = v)));
                break;
            }
            case "ref":
                row.Children.Add(ParserUi.Combo(node.Spec is { Length: > 0 } ? (_ws.LoadSpec(node.Spec)?.Rules.Select(r => r.Name) ?? []) : _ws.Doc.Rules.Select(r => r.Name), node.Text, t => _ws.EditNode(node, n => n.Text = t), false, 150));
                if (node.Spec is { Length: > 0 } from) row.Children.Add(LabUi.Label("from " + from, muted: true));
                break;
            case "repeat":
                row.Children.Add(LabUi.Label("least"));
                row.Children.Add(ParserUi.Field(node.Min.ToString(), t => _ws.EditNode(node, n => n.Min = int.TryParse(t, out int v) && v >= 0 ? v : n.Min), 40));
                row.Children.Add(LabUi.Label("most"));
                row.Children.Add(ParserUi.Field(node.Max?.ToString() ?? string.Empty, t => _ws.EditNode(node, n => n.Max = int.TryParse(t, out int v) && v >= 0 ? v : null), 40, "Empty: as many as there are"));
                break;
            case "not":
                row.Children.Add(LabUi.Label("matches only where its content does not, using up nothing", muted: true));
                break;
            case "delimited":
                row.Children.Add(LabUi.Check("at least one", node.Min > 0, v => _ws.EditNode(node, n => n.Min = v ? 1 : 0)));
                break;
            case "convert":
                row.Children.Add(ParserUi.Combo(SpecCatalog.Conversions, node.Text, t => _ws.EditNode(node, n => n.Text = t), false, 120));
                break;
            case "where":
                row.Children.Add(ParserUi.Field(node.Text, t => _ws.EditNode(node, n => n.Text = t), 160, "len:2-4, range:0..255 or regex:..."));
                break;
            case "named":
                row.Children.Add(ParserUi.Field(node.Name, t => _ws.EditNode(node, n => n.Name = t), 120, "The name of the value"));
                break;
        }

        row.Children.Add(Spacer());
        row.Children.Add(LabUi.Check("leave out of value", node.Ignore, v => _ws.EditNode(node, n => n.Ignore = v)));
        row.Children.Add(LabUi.IconButton("ArrowUp", () => _ws.Move(node, -1), "Move up"));
        row.Children.Add(LabUi.IconButton("ArrowDown", () => _ws.Move(node, 1), "Move down"));
        row.Children.Add(LabUi.IconButton("TbCopy", () => _ws.Duplicate(node), "Duplicate (Ctrl+D)"));
        row.Children.Add(LabUi.IconButton("TbDelete", () => _ws.Remove(node), "Delete (Del)"));

        var border = new Border { Child = row, BorderThickness = new Thickness(0, 2, 0, 0) };
        border.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        border.SetResourceReference(Border.BackgroundProperty, "Theme.Background");

        // where the settings are being changed is marked, so a change is noticed
        border.GotKeyboardFocus  += (_, _) => border.SetResourceReference(Border.BorderBrushProperty, "Theme.Accent");
        border.LostKeyboardFocus += (_, _) => border.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        return border;
    }

    private void ClassMenu(FrameworkElement anchor, SpecNode node)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };

        void Add(ItemsControl into, UnicodeClass cls)
        {
            var item = new MenuItem { Header = $"{cls.Code}   {cls.Title}", Icon = LabUi.Icon("Unicode") };
            item.Click += (_, _) => _ws.EditNode(node, n => n.Text = string.IsNullOrWhiteSpace(n.Text) ? cls.Code : n.Text + ", " + cls.Code);
            into.Items.Add(item);
        }

        foreach (var major in UnicodeClasses.All.Where(c => c.Code.Length == 1))
        {
            var group = new MenuItem { Header = $"{major.Code}   {major.Title}", Icon = LabUi.Icon("Unicode") };
            Add(group, major with { Title = major.Title + " (all)" });
            foreach (var cat in UnicodeClasses.All.Where(c => c.Code.Length == 2 && c.Code[0] == major.Code[0])) Add(group, cat);
            menu.Items.Add(group);
        }

        menu.IsOpen = true;
    }

    private static UIElement Spacer() => new Border { Width = 10 };

    // ---- tokenizer mode --------------------------------------------------------------------------

    private UIElement TokenizerHeader()
    {
        var tok   = _ws.Doc.Tokenizer;
        var panel = new StackPanel { Margin = new Thickness(8, 8, 8, 6) };
        panel.Children.Add(ModeRow());

        var defining = Row("Defining:");
        defining.Children.Add(ParserUi.Field(_ws.Doc.Name, t => _ws.Edit(d => d.Name = t.Trim(), WorkspaceChange.Header), 260, "The tokenizer's name"));
        panel.Children.Add(defining);

        // the token type: an enum of the Silo, or one this spec defines on the spot
        var type = Row("Token type:");
        var names = new List<string>();
        try
        {
            if (_ws.Env.Enums is { } store) names.AddRange(store.GetEnums().Select(e => e.Name));
        }
        catch (Exception)
        {
            // no Silo to ask: only a name that is typed
        }

        type.Children.Add(ParserUi.Combo(names, tok.TokenType, t =>
                                                              {
                                                                  if (!string.Equals(t.Trim(), tok.TokenType, StringComparison.Ordinal)) Check(() => _ws.SetTokenType(t));
                                                              }, true, 200));
        if (tok.ExistingEnum)
        {
            var existing = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), ToolTip = "An enum that is in the Silo's Enums: its values are the kinds of token" };
            if (LabUi.Icon("Enum", 14) is { } glyph)
            {
                glyph.Margin = new Thickness(0, 0, 4, 0);
                existing.Children.Add(glyph);
            }

            existing.Children.Add(LabUi.Label("enum of the Silo", muted: true));
            type.Children.Add(existing);
        }
        else
        {
            var include = LabUi.Check("Include in Enums", tok.IncludeInEnums, v => _ws.SetIncludeInEnums(v),
                                      "Add the enum these kinds make to the Silo's Enums (with this name) when the spec is saved, so that it can be used elsewhere");
            include.Margin = new Thickness(10, 0, 0, 0);
            type.Children.Add(include);
        }

        panel.Children.Add(type);

        var kinds = Row("Kinds:");
        foreach (string kind in tok.Kinds.ToList())
            kinds.Children.Add(Chip(kind, tok.ExistingEnum ? null : () => Check(() => _ws.RemoveKind(kind))));

        var addKind = LabUi.IconButton("Plus", () =>
                                               {
                                                   string? name = ParserUi.Ask("New kind of token", "Name of the kind (Word, Number...); each is different from the others:");
                                                   if (!string.IsNullOrWhiteSpace(name)) Check(() => _ws.AddKind(name));
                                               }, tok.ExistingEnum ? "The kinds are the values of an enum of the Silo: change them in its Enums" : "Add a kind of token (a value of the enum)", !tok.ExistingEnum);
        kinds.Children.Add(addKind);
        panel.Children.Add(kinds);
        return panel;
    }

    private UIElement TokenizerRows()
    {
        var stack = new StackPanel { Margin = new Thickness(8) };
        var rules = _ws.Doc.Tokenizer.Rules;
        for (int i = 0; i < rules.Count; i++) stack.Children.Add(TokenRuleRow(rules[i], i + 1));

        var add = LabUi.Tool("Add Tokenizer Rule", "Plus", () => _ws.AddTokenRule(), "Add a matcher; the first one that matches at a place makes the token there");
        add.Margin              = new Thickness(0, 10, 0, 0);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(add);

        _scroll.MouseLeftButtonDown -= OnCanvasClick;
        _scroll.MouseLeftButtonDown += OnCanvasClick;
        return stack;
    }

    private UIElement TokenRuleRow(TokenRule rule, int number)
    {
        var slot = ParserWorkspace.MatcherSlot(rule);
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 6, 0) };
        var num   = LabUi.Label(number + ".", muted: true);
        num.Margin = new Thickness(0, 0, 6, 0);
        tools.Children.Add(num);
        tools.Children.Add(LabUi.IconButton("ArrowUp", () => _ws.MoveTokenRule(rule, -1), "Try it earlier"));
        tools.Children.Add(LabUi.IconButton("ArrowDown", () => _ws.MoveTokenRule(rule, 1), "Try it later"));
        tools.Children.Add(LabUi.IconButton("TbDelete", () => _ws.RemoveTokenRule(rule), "Remove the rule"));
        SetColumn(tools, 0);
        grid.Children.Add(tools);

        UIElement matcher = rule.Matcher is null ? Placeholder(slot, null) : Render(rule.Matcher, slot);
        SetColumn(matcher, 1);
        grid.Children.Add(matcher);

        var kinds = new List<string> { "(skip)" };
        kinds.AddRange(_ws.Doc.Tokenizer.Kinds);
        var kind = ParserUi.Combo(kinds, rule.Kind ?? "(skip)", k => _ws.Edit(_ => rule.Kind = k == "(skip)" ? null : k), false, 110);
        kind.VerticalAlignment = VerticalAlignment.Top;
        kind.ToolTip           = "The kind of token it makes; (skip): the text is passed over";
        SetColumn(kind, 2);
        grid.Children.Add(kind);

        // the row as a whole can be selected, and copied, cut, pasted and duplicated
        var frame = new Border { Child = grid, BorderThickness = new Thickness(ReferenceEquals(rule, _ws.SelectedTokenRule) ? 2 : 1), CornerRadius = new CornerRadius(4), Padding = new Thickness(2) };
        frame.SetResourceReference(Border.BorderBrushProperty, ReferenceEquals(rule, _ws.SelectedTokenRule) ? "Theme.Accent" : "Theme.BackgroundAlt");
        frame.MouseLeftButtonDown += (_, e) =>
                                     {
                                         if (e.Handled) return;

                                         Focus();
                                         _ws.SelectedNode      = null;
                                         _ws.SelectedTokenRule = rule;
                                         e.Handled             = true;
                                     };
        frame.ContextMenu = TokenRuleMenu(rule);
        _ruleRows.Add((rule, frame));
        return frame;
    }

    private ContextMenu TokenRuleMenu(TokenRule rule)
    {
        var menu = new ContextMenu();

        MenuItem Item(string header, string icon, Action run, string? gesture = null)
        {
            var item = new MenuItem { Header = header, Icon = LabUi.Icon(icon) };
            if (gesture is not null) item.InputGestureText = gesture;
            item.Click += (_, _) => run();
            return item;
        }

        var paste = Item("Paste", "TbPaste", () => _ws.PasteTokenRules(rule), "Ctrl+V");
        menu.Opened += (_, _) => paste.IsEnabled = _ws.CanPaste;
        menu.Items.Add(Item("Cut", "TbCut", () => _ws.CutTokenRule(rule), "Ctrl+X"));
        menu.Items.Add(Item("Copy", "TbCopy", () => _ws.CopyTokenRule(rule), "Ctrl+C"));
        menu.Items.Add(paste);
        menu.Items.Add(Item("Duplicate", "TbCopy", () => _ws.DuplicateTokenRule(rule), "Ctrl+D"));
        menu.Items.Add(Item("Delete", "TbDelete", () => _ws.RemoveTokenRule(rule), "Del"));
        return menu;
    }
}
