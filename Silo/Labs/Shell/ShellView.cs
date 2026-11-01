using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Silo.Shell;

namespace Silo.Labs.Shell;

/// <summary>
///     The main view of the Shell Lab: on the left the package and its entries as a tree with the buttons to change it, on the right the form for
///     the selected entry and, under it, the menus the package makes as the shell will show them (click an item to select its entry).
/// </summary>
internal sealed class ShellView : Grid
{
    private enum Field { Name, Target, Label, Description, Command, Arguments, Icon, Position, Extended, MakeDefault, Extensions, Content, Key, ValueName, ValueKind, Data }

    private static readonly Dictionary<ShellEntryKind, string> KindNames = new()
                                                                          {
                                                                              [ShellEntryKind.Protocol]    = "Protocol handler",
                                                                              [ShellEntryKind.FileType]    = "File type (ProgID)",
                                                                              [ShellEntryKind.Verb]        = "Right-click action",
                                                                              [ShellEntryKind.Submenu]     = "Right-click submenu",
                                                                              [ShellEntryKind.ClassIcon]   = "Class icon",
                                                                              [ShellEntryKind.DefaultVerb] = "Default action",
                                                                              [ShellEntryKind.SendTo]      = "Send To item",
                                                                              [ShellEntryKind.NewFile]     = "New menu item",
                                                                              [ShellEntryKind.AppPath]     = "Program name (App Paths)",
                                                                              [ShellEntryKind.Application] = "Application registration",
                                                                              [ShellEntryKind.Registry]    = "Registry value"
                                                                          };

    private readonly Dictionary<Field, (TextBlock Title, FrameworkElement Control)> _rows = [];
    private readonly ShellLabSession _session;
    private readonly TreeView        _tree;
    private readonly StackPanel      _preview = new();
    private readonly ComboBox        _previewClass = new() { MinWidth = 200, Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock       _note;
    private readonly TextBox         _pkgName, _pkgVersion, _pkgAuthor, _pkgDescription;
    private readonly ComboBox        _pkgScope, _kind;
    private readonly CheckBox        _enabled;
    private          bool            _loading, _rebuilding, _previewing;

    public ShellView(ShellLabSession session)
    {
        _session = session;

        // ---- left: the package, the buttons, the tree
        _pkgName        = LabUi.Line(null, t => EditPackage(p => p.Name = t.Trim()), 120, "The package's name: also the name of its record when installed");
        _pkgVersion     = LabUi.Line(null, t => EditPackage(p => p.Version = t.Trim()), 60);
        _pkgAuthor      = LabUi.Line(null, t => EditPackage(p => p.Author = t), 120);
        _pkgDescription = LabUi.Line(null, t => EditPackage(p => p.Description = t), 120);
        _pkgScope       = LabUi.Combo(["Current user", "All users (needs an administrator)"], null, s => EditPackage(p => p.Scope = s.StartsWith("All") ? ShellScope.AllUsers : ShellScope.CurrentUser), 120);

        var head = LabUi.Form([("Name", _pkgName), ("Version", _pkgVersion), ("For", _pkgScope), ("Author", _pkgAuthor), ("Description", _pkgDescription)], 80);

        var add = LabUi.Button("Add", () => { }, "Add an entry after the selected one", icon: "TbNew");
        add.Click += (_, _) => ShowAddMenu(add);
        var bar = LabUi.Bar(add, LabUi.Button("Up", () => _session.Move(-1), "Move the entry up"), LabUi.Button("Down", () => _session.Move(1), "Move the entry down"),
                            LabUi.Button("Duplicate", _session.Duplicate, "Copy the entry after itself"), LabUi.Button("Delete", _session.Delete, "Take the entry out of the package"));

        _tree = new TreeView { BorderThickness = new Thickness(0) }.Themed();
        _tree.SelectedItemChanged += (_, _) =>
                                     {
                                         if (!_rebuilding && (_tree.SelectedItem as TreeViewItem)?.Tag is int[] path) _session.Select(path);
                                     };

        var top = new StackPanel();
        top.Children.Add(head);
        top.Children.Add(bar);
        var left = LabUi.Layout(top, _tree);

        // ---- right: the entry form and the preview
        _kind    = LabUi.Combo(KindNames.Values, null, s => ChangeKind(KindNames.First(k => k.Value == s).Key), 220);
        _enabled = LabUi.Check("Install this entry", true, on => Edit(e => e.Enabled = on, true), "Switch off to keep the entry in the package without installing it");
        _note    = LabUi.Label(string.Empty, muted: true, wrap: true);

        var form = new StackPanel { Margin = new Thickness(6) };
        form.Children.Add(LabUi.Heading("Entry"));
        var rows = new List<(string, UIElement)> { ("Kind", _kind) };
        foreach (var field in Enum.GetValues<Field>()) rows.Add((field.ToString(), MakeControl(field)));
        var grid = LabUi.Form(rows, 120);
        // the form made a label for each row; remember the labels and controls so each kind shows the ones it needs
        foreach (var field in Enum.GetValues<Field>())
        {
            var control = _controls[field];
            var title   = grid.Children.OfType<TextBlock>().First(t => Grid.GetRow(t) == Grid.GetRow(control));
            _rows[field] = (title, control);
        }

        form.Children.Add(grid);
        form.Children.Add(_enabled);
        form.Children.Add(_note);
        form.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 6) });
        form.Children.Add(LabUi.Heading("As the shell will show it"));
        form.Children.Add(_previewClass);
        form.Children.Add(_preview);
        _previewClass.SelectionChanged += (_, _) =>
                                          {
                                              if (!_previewing) RefreshPreview();
                                          };

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340), MinWidth = 240 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        SetColumn(left, 0);
        Children.Add(left);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        SetColumn(splitter, 1);
        Children.Add(splitter);
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetColumn(scroll, 2);
        Children.Add(scroll);

        session.Changed          += (_, _) => Dispatcher.InvokeAsync(OnChanged);
        session.SelectionChanged += (_, _) => Dispatcher.InvokeAsync(OnSelection);
        session.Loaded           += (_, _) => Dispatcher.InvokeAsync(() => { LoadPackage(); OnChanged(); OnSelection(); });
        LoadPackage();
        OnChanged();
        OnSelection();
    }

    // ---- the form ----------------------------------------------------------------------------------

    private readonly Dictionary<Field, FrameworkElement> _controls = [];

    private FrameworkElement MakeControl(Field field)
    {
        FrameworkElement control;
        switch (field)
        {
            case Field.Name:        control = LabUi.Line(null, t => Edit(e => e.Name = t.Trim()), 240); break;
            case Field.Target:      control = LabUi.Line(null, t => Edit(e => e.Target = Nullable(t)), 240, @"A class: * (all files), .txt, Directory, Directory\Background, Folder, Drive, AllFilesystemObjects, or a ProgID"); break;
            case Field.Label:       control = LabUi.Line(null, t => Edit(e => e.Label = Nullable(t)), 240, "The text in the menu; & marks the key that chooses it"); break;
            case Field.Description: control = LabUi.Line(null, t => Edit(e => e.Description = Nullable(t)), 240); break;
            case Field.Command:     control = LabUi.Line(null, t => Edit(e => e.Command = Nullable(t)), 240, "%1 is the item, %V the folder", true); break;
            case Field.Arguments:   control = LabUi.Line(null, t => Edit(e => e.Arguments = Nullable(t)), 240, null, true); break;
            case Field.Icon:        control = LabUi.Line(null, t => Edit(e => e.Icon = Nullable(t)), 240, @"A path to an .ico, .exe or .dll, optionally followed by ,index", true); break;
            case Field.Position:    control = LabUi.Combo(Enum.GetNames<ShellPosition>(), null, s => Edit(e => e.Position = Enum.Parse<ShellPosition>(s), true), 100); break;
            case Field.Extended:    control = LabUi.Check("Only when Shift is held", false, on => Edit(e => e.Extended = on, true)); break;
            case Field.MakeDefault: control = LabUi.Check("Make it the default", false, on => Edit(e => e.MakeDefault = on, true)); break;
            case Field.Extensions:  control = LabUi.Line(null, t => Edit(e => e.Extensions = t.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()), 240, "Separated by commas: .md, .markdown"); break;
            case Field.Content:
                var memo = LabUi.Memo(null, t => Edit(e => e.Content = Nullable(t)));
                memo.Height = 90;
                control     = memo;
                break;
            case Field.Key:         control = LabUi.Line(null, t => Edit(e => e.Key = Nullable(t)), 240, @"From the hive: HKCU\Software\Vendor\Product", true); break;
            case Field.ValueName:   control = LabUi.Line(null, t => Edit(e => e.ValueName = Nullable(t)), 240, "Empty is the key's default value"); break;
            case Field.ValueKind:   control = LabUi.Combo(Enum.GetNames<ShellValueKind>(), null, s => Edit(e => e.ValueKind = Enum.Parse<ShellValueKind>(s), true), 120); break;
            default:                control = LabUi.Line(null, t => Edit(e => e.Data = t), 240, null, true); break;
        }

        _controls[field] = control;
        return control;
    }

    private static string? Nullable(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private void Edit(Action<ShellEntry> change, bool now = false)
    {
        if (!_loading) _session.EditEntry(change, now);
    }

    private void EditPackage(Action<ShellPackage> change)
    {
        if (!_loading) _session.EditPackage(change);
    }

    private void ChangeKind(ShellEntryKind kind)
    {
        if (_loading || _session.Selected is not { } e || e.Kind == kind) return;

        _session.EditEntry(x => x.Kind = kind, true);
        OnSelection();
    }

    private static Dictionary<Field, string> Titles(ShellEntryKind kind, bool inSubmenu)
    {
        var t = kind switch
                {
                    ShellEntryKind.Protocol    => new Dictionary<Field, string> { [Field.Name] = "Scheme", [Field.Description] = "Description", [Field.Command] = "Command", [Field.Icon] = "Icon" },
                    ShellEntryKind.FileType    => new Dictionary<Field, string> { [Field.Name] = "ProgID", [Field.Description] = "Description", [Field.Command] = "Open command", [Field.Icon] = "Icon", [Field.Extensions] = "Extensions", [Field.MakeDefault] = "Default" },
                    ShellEntryKind.Verb        => new Dictionary<Field, string> { [Field.Target] = "Class", [Field.Name] = "Verb key", [Field.Label] = "Menu text", [Field.Command] = "Command", [Field.Icon] = "Icon", [Field.Position] = "Position", [Field.Extended] = "Extended", [Field.MakeDefault] = "Default" },
                    ShellEntryKind.Submenu     => new Dictionary<Field, string> { [Field.Target] = "Class", [Field.Name] = "Key", [Field.Label] = "Menu text", [Field.Icon] = "Icon", [Field.Position] = "Position", [Field.Extended] = "Extended" },
                    ShellEntryKind.ClassIcon   => new Dictionary<Field, string> { [Field.Target] = "Class", [Field.Icon] = "Icon" },
                    ShellEntryKind.DefaultVerb => new Dictionary<Field, string> { [Field.Target] = "Class", [Field.Name] = "Verb key" },
                    ShellEntryKind.SendTo      => new Dictionary<Field, string> { [Field.Name] = "Shortcut name", [Field.Command] = "Program", [Field.Arguments] = "Arguments" },
                    ShellEntryKind.NewFile     => new Dictionary<Field, string> { [Field.Name] = "Extension", [Field.Label] = "File name", [Field.Content] = "Content" },
                    ShellEntryKind.AppPath     => new Dictionary<Field, string> { [Field.Name] = "Program name", [Field.Command] = "Path" },
                    ShellEntryKind.Application => new Dictionary<Field, string> { [Field.Name] = "Program name", [Field.Label] = "Friendly name", [Field.Command] = "Open command", [Field.Icon] = "Icon", [Field.Extensions] = "Opens" },
                    _                          => new Dictionary<Field, string> { [Field.Key] = "Key", [Field.ValueName] = "Value name", [Field.ValueKind] = "Type", [Field.Data] = "Value" }
                };
        if (inSubmenu)
        {
            t.Remove(Field.Target);
            t.Remove(Field.MakeDefault);
        }

        return t;
    }

    private static string NoteFor(ShellEntryKind kind) => kind switch
    {
        ShellEntryKind.Protocol    => "Addresses like scheme://anything open the command; %1 is the address.",
        ShellEntryKind.FileType    => "Describes a kind of file. Its extensions offer it in Open with; Default makes it what a double-click runs.",
        ShellEntryKind.Verb        => "An action in the right-click menu of the class. An extension (.txt) works whatever program owns it.",
        ShellEntryKind.Submenu     => "A cascading menu. Select it and Add > Right-click action to put verbs inside.",
        ShellEntryKind.ClassIcon   => "The icon of a class (an extension with a ProgID takes the ProgID's icon first).",
        ShellEntryKind.DefaultVerb => "Which verb of the class a double-click runs.",
        ShellEntryKind.SendTo      => "A shortcut in the person's Send To folder.",
        ShellEntryKind.NewFile     => "An item in the New menu. Empty content makes an empty file.",
        ShellEntryKind.AppPath     => "Lets Run and the shell find the program by name alone.",
        ShellEntryKind.Application => "Registers the program as an application (its friendly name, icon and the types it opens).",
        _                          => "Any registry value, for what the other kinds do not cover."
    };

    private void LoadPackage()
    {
        _loading = true;
        var p = _session.Package;
        Put(_pkgName, p.Name);
        Put(_pkgVersion, p.Version);
        Put(_pkgAuthor, p.Author);
        Put(_pkgDescription, p.Description);
        _pkgScope.SelectedIndex = p.Scope == ShellScope.AllUsers ? 1 : 0;
        _loading = false;
    }

    private static void Put(TextBox box, string? text)
    {
        if (box.Text != (text ?? string.Empty) && !box.IsKeyboardFocusWithin) box.Text = text ?? string.Empty;
    }

    private void OnSelection()
    {
        _loading = true;
        var e = _session.Selected;
        var path = _session.SelectedPath;
        bool inSubmenu = path is { Length: 2 };
        IsEnabledOfForm(e is not null);
        if (e is not null)
        {
            _kind.SelectedItem = KindNames[e.Kind];
            _kind.IsEnabled    = !inSubmenu;
            var titles = Titles(e.Kind, inSubmenu);
            foreach (var (field, (title, control)) in _rows)
            {
                bool shown = titles.ContainsKey(field);
                title.Visibility   = shown ? Visibility.Visible : Visibility.Collapsed;
                control.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                if (shown) title.Text = titles[field];
            }

            Set(Field.Name, e.Name);
            Set(Field.Target, e.Target);
            Set(Field.Label, e.Label);
            Set(Field.Description, e.Description);
            Set(Field.Command, e.Command);
            Set(Field.Arguments, e.Arguments);
            Set(Field.Icon, e.Icon);
            Set(Field.Extensions, string.Join(", ", e.Extensions));
            Set(Field.Content, e.Content);
            Set(Field.Key, e.Key);
            Set(Field.ValueName, e.ValueName);
            Set(Field.Data, e.Data);
            ((ComboBox)_controls[Field.Position]).SelectedItem  = e.Position.ToString();
            ((ComboBox)_controls[Field.ValueKind]).SelectedItem = e.ValueKind.ToString();
            ((CheckBox)_controls[Field.Extended]).IsChecked     = e.Extended;
            ((CheckBox)_controls[Field.MakeDefault]).IsChecked  = e.MakeDefault;
            _enabled.IsChecked = e.Enabled;
            _note.Text         = NoteFor(e.Kind);
        }
        else
        {
            _note.Text = "Add an entry to begin.";
        }

        _loading = false;
        SelectInTree();
        RefreshPreview();
    }

    private void IsEnabledOfForm(bool on)
    {
        foreach (var (_, (title, control)) in _rows)
        {
            control.IsEnabled = on;
            if (!on)
            {
                title.Visibility   = Visibility.Collapsed;
                control.Visibility = Visibility.Collapsed;
            }
        }

        _kind.IsEnabled    = on;
        _enabled.IsEnabled = on;
    }

    private void Set(Field field, string? text)
    {
        if (_controls[field] is TextBox box && box.Text != (text ?? string.Empty)) box.Text = text ?? string.Empty;
    }

    // ---- the tree ----------------------------------------------------------------------------------

    private void ShowAddMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (kind, name) in KindNames)
        {
            var k    = kind;
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => _session.AddEntry(k);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void OnChanged()
    {
        RebuildTree();
        RefreshPreview();
    }

    private void RebuildTree()
    {
        _rebuilding = true;
        _tree.Items.Clear();
        var pkg = _session.Package;
        for (int i = 0; i < pkg.Entries.Count; i++)
        {
            var e    = pkg.Entries[i];
            var node = Node(e, [i]);
            node.IsExpanded = true;
            for (int c = 0; c < e.Children.Count; c++) node.Items.Add(Node(e.Children[c], [i, c]));
            _tree.Items.Add(node);
        }

        _rebuilding = false;
        SelectInTree();
    }

    private TreeViewItem Node(ShellEntry e, int[] path)
    {
        var text = new TextBlock { Text = $"{e.Display}   ·  {KindNames[e.Kind]}" };
        if (!e.Enabled) text.TextDecorations = TextDecorations.Strikethrough;
        bool bad = _session.Problems.Any(p => p.Severity == ShellSeverity.Error && p.Path.SequenceEqual(path));
        if (bad) text.Foreground = LabUi.Red;
        return new TreeViewItem { Header = text, Tag = path };
    }

    private void SelectInTree()
    {
        if (_session.SelectedPath is not { } path) return;

        _rebuilding = true;
        try
        {
            foreach (var item in AllNodes(_tree.Items))
                if (((int[])item.Tag).SequenceEqual(path))
                {
                    item.IsSelected = true;
                    return;
                }
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private static IEnumerable<TreeViewItem> AllNodes(ItemCollection items)
    {
        foreach (TreeViewItem item in items)
        {
            yield return item;
            foreach (var child in AllNodes(item.Items)) yield return child;
        }
    }

    // ---- the preview -------------------------------------------------------------------------------

    private void RefreshPreview()
    {
        _previewing = true;
        var pkg = _session.Package;
        var classes = pkg.Entries.Where(e => e.Enabled && e.Kind is ShellEntryKind.Verb or ShellEntryKind.Submenu && !string.IsNullOrWhiteSpace(e.Target))
                         .Select(e => e.Target!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? want = _previewClass.SelectedItem as string;
        if (_session.Selected is { Kind: ShellEntryKind.Verb or ShellEntryKind.Submenu, Target: { } t } && classes.Contains(t.Trim(), StringComparer.OrdinalIgnoreCase)) want = t.Trim();
        if (!_previewClass.Items.Cast<string>().SequenceEqual(classes))
        {
            _previewClass.Items.Clear();
            foreach (string c in classes) _previewClass.Items.Add(c);
        }

        _previewClass.SelectedItem = want is not null && classes.Contains(want) ? want : classes.FirstOrDefault();
        _previewClass.Visibility   = classes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _previewing                = false;

        _preview.Children.Clear();
        if (_previewClass.SelectedItem is string cls) AddMenuMock(cls, pkg);

        var sendTo = pkg.Entries.Select((e, i) => (e, i)).Where(x => x.e.Enabled && x.e.Kind == ShellEntryKind.SendTo).ToList();
        if (sendTo.Count > 0)
        {
            AddCaption("Send to");
            var panel = NewMenuPanel();
            foreach (string std in new[] { "Bluetooth device", "Compressed (zipped) folder", "Desktop (create shortcut)", "Documents" }) panel.Children.Add(Row(std, dim: true));
            foreach (var (e, i) in sendTo) panel.Children.Add(Row(string.IsNullOrWhiteSpace(e.Name) ? "(unnamed)" : e.Name, path: [i]));
            _preview.Children.Add(panel);
        }

        var newFiles = pkg.Entries.Select((e, i) => (e, i)).Where(x => x.e.Enabled && x.e.Kind == ShellEntryKind.NewFile).ToList();
        if (newFiles.Count > 0)
        {
            AddCaption("New");
            var panel = NewMenuPanel();
            foreach (string std in new[] { "Folder", "Shortcut" }) panel.Children.Add(Row(std, dim: true));
            foreach (var (e, i) in newFiles)
                panel.Children.Add(Row(string.IsNullOrWhiteSpace(e.Label) ? "New File" + e.Name : ShellText.PlainLabel(e.Label) + "  (" + e.Name + ")", path: [i]));
            _preview.Children.Add(panel);
        }
    }

    private void AddCaption(string text)
    {
        var caption = LabUi.Label(text, muted: true);
        caption.Margin = new Thickness(0, 8, 0, 2);
        _preview.Children.Add(caption);
    }

    private static StackPanel NewMenuPanel() => new();

    private void AddMenuMock(string cls, ShellPackage pkg)
    {
        var mine = pkg.Entries.Select((e, i) => (e, i)).Where(x => x.e.Enabled && x.e.Kind is ShellEntryKind.Verb or ShellEntryKind.Submenu && string.Equals(x.e.Target?.Trim(), cls, StringComparison.OrdinalIgnoreCase)).ToList();
        string? defaultVerb = pkg.Entries.FirstOrDefault(e => e.Enabled && e.Kind == ShellEntryKind.DefaultVerb && string.Equals(e.Target?.Trim(), cls, StringComparison.OrdinalIgnoreCase))?.Name;

        var panel = new StackPanel();
        void Entries(IEnumerable<(ShellEntry e, int i)> list)
        {
            foreach (var (e, i) in list)
            {
                bool isDefault = e.MakeDefault || (defaultVerb is not null && string.Equals(defaultVerb, e.Name, StringComparison.OrdinalIgnoreCase));
                panel.Children.Add(Row(ShellText.PlainLabel(e.Label ?? e.Name), bold: isDefault, arrow: e.Kind == ShellEntryKind.Submenu, hint: e.Extended ? "Shift" : null, path: [i], hasIcon: !string.IsNullOrWhiteSpace(e.Icon)));
                if (e.Kind == ShellEntryKind.Submenu)
                    for (int c = 0; c < e.Children.Count; c++)
                        if (e.Children[c].Enabled)
                            panel.Children.Add(Row(ShellText.PlainLabel(e.Children[c].Label ?? e.Children[c].Name), indent: 1, path: [i, c], hasIcon: !string.IsNullOrWhiteSpace(e.Children[c].Icon)));
            }
        }

        bool background = cls.EndsWith("Background", StringComparison.OrdinalIgnoreCase);
        Entries(mine.Where(x => x.e.Position == ShellPosition.Top));
        if (background)
        {
            foreach (string std in new[] { "View", "Sort by", "Refresh", "Paste", "New" }) panel.Children.Add(Row(std, dim: true, arrow: std is "View" or "Sort by" or "New"));
        }
        else
        {
            foreach (string std in new[] { "Open", "Edit" }) panel.Children.Add(Row(std, dim: true));
        }

        Entries(mine.Where(x => x.e.Position == ShellPosition.None));
        foreach (string std in background ? [] : new[] { "Send to", "Cut", "Copy", "Delete", "Rename" }) panel.Children.Add(Row(std, dim: true, arrow: std == "Send to"));
        panel.Children.Add(Row("Properties", dim: true));
        Entries(mine.Where(x => x.e.Position == ShellPosition.Bottom));

        AddCaption($"Right-click menu of {cls}");
        _preview.Children.Add(panel);
    }

    private Border Row(string text, bool dim = false, bool bold = false, bool arrow = false, string? hint = null, int indent = 0, int[]? path = null, bool hasIcon = false)
    {
        bool selected = path is not null && _session.SelectedPath is { } sel && sel.SequenceEqual(path);
        var line = new DockPanel { LastChildFill = true };
        line.Children.Add(new TextBlock { Text = hasIcon ? "▣" : " ", Width = 22, Margin = new Thickness(indent * 20, 0, 0, 0), Foreground = LabUi.Gray });
        if (arrow)
        {
            var a = new TextBlock { Text = "›", Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(a, Dock.Right);
            line.Children.Add(a);
        }

        if (hint is not null)
        {
            var h = new TextBlock { Text = "(" + hint + ")", Margin = new Thickness(8, 0, 0, 0), Foreground = LabUi.Gray, FontStyle = FontStyles.Italic };
            DockPanel.SetDock(h, Dock.Right);
            line.Children.Add(h);
        }

        var label = new TextBlock { Text = text, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
        if (dim) label.Foreground = LabUi.Gray;
        else label.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        line.Children.Add(label);

        var border = new Border { Child = line, Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(3), Cursor = path is null ? Cursors.Arrow : Cursors.Hand };
        if (selected) border.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceHover");
        if (path is not null) border.MouseLeftButtonDown += (_, _) => _session.Select(path);
        return border;
    }
}
