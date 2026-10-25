using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Silo.Commanding;
using Silo.Converters;
using Silo.Toolbar;

namespace Silo.Labs.Menus;

/// <summary>Makes a real menu of a <see cref="MenuDoc" />, as the program would show it, and reports what is done with it.</summary>
internal static class MenuTester
{
    public static Built Build(MenuDoc doc, Action<string> report)
    {
        var bindings = new List<KeyBinding>();
        var controls = doc.Items.Select(i => Make(i, report, bindings)).ToList();
        return new Built(controls, bindings);
    }

    private static Control Make(MenuItemDef def, Action<string> report, List<KeyBinding> bindings)
    {
        if (def.Separator) return new Separator();

        var item = new MenuItem
                   {
                       Header    = def.Header, ToolTip = string.IsNullOrWhiteSpace(def.ToolTip) ? null : def.ToolTip,
                       IsEnabled = def.Enabled
                   };

        // always set, even to nothing: inside the menu bar an implicit style would otherwise read an icon off the command
        item.Icon = ToolbarIcons.Find(def.IconKey) is { } geometry ? new AppIcon { Icon = geometry, Size = 16 } : null;
        if (MenuGesture.TryNormalize(def.Gesture, out var gesture, out _) && gesture.Length > 0) item.InputGestureText = gesture;
        if (def.Checkable)
        {
            item.IsCheckable = true;
            item.IsChecked   = def.Checked;
        }

        if (def.Default) item.FontWeight     = FontWeights.Bold;
        if (def.Destructive) item.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));

        string Name()
        {
            return (string.IsNullOrEmpty(def.Id) ? def.Display : def.Id) +
                   (def.Display.Length > 0 && !string.IsNullOrEmpty(def.Id) ? $" ({def.Display})" : string.Empty);
        }

        if (def.IsSubmenu)
        {
            foreach (var child in def.Children) item.Items.Add(Make(child, report, bindings));
        }
        else
        {
            item.Click += (_, _) => report($"clicked {Name()}" +
                                           (item.IsCheckable ? item.IsChecked ? " - now checked" : " - now unchecked" : string.Empty));
            if (gesture.Length > 0 && TryBinding(gesture, out var key, out var modifiers))
                bindings.Add(new KeyBinding(new DelegateCommand(_ =>
                                                                {
                                                                    if (!item.IsEnabled)
                                                                    {
                                                                        report($"{gesture} ignored: {Name()} is disabled");
                                                                        return;
                                                                    }

                                                                    if (item.IsCheckable) item.IsChecked = !item.IsChecked;
                                                                    report($"{gesture} ran {Name()}" +
                                                                           (item.IsCheckable
                                                                                ? item.IsChecked ? " - now checked" : " - now unchecked"
                                                                                : string.Empty));
                                                                }), key, modifiers));
        }

        return item;
    }

    /// <summary>The key and modifiers of a shortcut written the way <see cref="MenuGesture" /> writes them.</summary>
    public static bool TryBinding(string gesture, out Key key, out ModifierKeys modifiers)
    {
        key       = Key.None;
        modifiers = ModifierKeys.None;
        foreach (var part in gesture.Split('+'))
        {
            switch (part)
            {
                case "Ctrl":
                    modifiers |= ModifierKeys.Control;
                    continue;
                case "Alt":
                    modifiers |= ModifierKeys.Alt;
                    continue;
                case "Shift":
                    modifiers |= ModifierKeys.Shift;
                    continue;
                case "Win":
                    modifiers |= ModifierKeys.Windows;
                    continue;
            }

            string name = part.Length == 1 && char.IsAsciiDigit(part[0]) ? "D" + part : part;
            if (!Enum.TryParse(name, out key)) return false;
        }

        return key != Key.None;
    }

    /// <summary>Everything that was made: the menu, and for each entry that has a shortcut the key binding that runs it.</summary>
    public sealed record Built(IReadOnlyList<Control> Controls, IReadOnlyList<KeyBinding> Bindings);
}

/// <summary>
///     The main view of the Menu Lab: on the left the menu as a tree with the buttons to change it, on the right the form
///     for the selected entry and, under it, the menu itself to try out,
///     with a log of what was clicked.
/// </summary>
internal sealed class MenuView : Grid
{
    private readonly CheckBox        _checkable, _checked, _enabled, _default, _destructive;
    private readonly Grid            _fields;
    private readonly StackPanel      _form = new();
    private readonly TextBlock       _formNote;
    private readonly TextBlock       _gestureNote;
    private readonly TextBox         _header, _id, _gesture, _icon, _tip;
    private readonly ComboBox        _kind;
    private readonly HashSet<string> _known = [];
    private readonly ListBox         _log;
    private readonly TextBox         _name;
    private readonly HashSet<string> _open = [];
    private readonly MenuLabSession  _session;
    private readonly TreeView        _tree;
    private          bool            _loading;
    private          bool            _rebuilding;

    private void Edit(Action<MenuItemDef> change, bool now = false)
    {
        if (!_loading) _session.EditItem(change, now);
    }

    private void ShowGestureNote(string text)
    {
        if (!MenuGesture.TryNormalize(text, out var normal, out var problem))
        {
            _gestureNote.Text       = problem;
            _gestureNote.Foreground = LabUi.Red;
        }
        else
        {
            _gestureNote.Text = normal.Length > 0 && normal != text.Trim() ? "Shown as " + normal : string.Empty;
            _gestureNote.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        }
    }

    private void LoadAll()
    {
        _loading = true;
        try
        {
            _name.Text         = _session.Doc.Name;
            _kind.SelectedItem = _session.Doc.Kind == MenuKind.Bar ? "Menu bar" : "Context menu";
        }
        finally
        {
            _loading = false;
        }

        Redraw();
        LoadForm();
        DrawLog();
    }

    private void Redraw()
    {
        DrawTree();
        DrawStage();
    }

    private static string Key(int[] path)
    {
        return string.Join('.', path);
    }

    private void DrawTree()
    {
        _rebuilding = true;
        try
        {
            // what is open is remembered by address so that a change does not fold the tree up; entries that were not there before start open
            foreach (var node in All(_tree.Items))
                if (node.IsExpanded) _open.Add(Key((int[])node.Tag));
                else _open.Remove(Key((int[])node.Tag));

            _tree.Items.Clear();
            var           problems = _session.Problems;
            TreeViewItem? selected = null;

            void Fill(ItemCollection into, List<MenuItemDef> items, int[] parent)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    int[] path = [.. parent, i];
                    var   def  = items[i];
                    var   node = new TreeViewItem { Tag = path, IsExpanded = _open.Contains(Key(path)) || !_known.Contains(Key(path)) };
                    var   row  = new StackPanel { Orientation = Orientation.Horizontal };
                    // what the entry is: a line, a submenu, or a command (with its own icon when it names one the app has)
                    string kindIcon = def.Separator
                                          ? "Separator"
                                          :
                                          def.IsSubmenu
                                              ? "Submenu"
                                              :
                                              !string.IsNullOrWhiteSpace(def.IconKey) && ToolbarIcons.Find(def.IconKey) is not null
                                                  ?
                                                  def.IconKey!
                                                  : "Command";
                    if (LabUi.Icon(kindIcon, 14) is { } kindGlyph)
                    {
                        kindGlyph.Margin  = new Thickness(0, 0, 6, 0);
                        kindGlyph.Opacity = def.Separator || def.IsSubmenu ? 0.6 : 1;
                        row.Children.Add(kindGlyph);
                    }

                    if (def.Separator)
                    {
                        row.Children.Add(LabUi.Label("────────────", true));
                    }
                    else
                    {
                        var label = LabUi.Label(def.Display.Length == 0 ? "(no text)" : def.Display, bold: def.Default);
                        if (!def.Enabled) label.Muted();
                        if (def.Destructive) label.Foreground = LabUi.Red;
                        row.Children.Add(label);
                        if (def.IsSubmenu) row.Children.Add(LabUi.Label(" ▸",                                     true));
                        if (!string.IsNullOrEmpty(def.Gesture)) row.Children.Add(LabUi.Label("   " + def.Gesture, true));
                        if (def.Checkable) row.Children.Add(LabUi.Label(def.Checked ? "  ☑" : "  ☐",              true));
                    }

                    var mine = problems.Where(p => p.Path.SequenceEqual(path)).ToList();
                    if (mine.Count > 0)
                    {
                        var mark = LabUi.Label("  ●");
                        mark.Foreground = mine.Any(p => p.Severity == MenuSeverity.Error) ? LabUi.Red : LabUi.Amber;
                        mark.ToolTip    = string.Join("\n", mine.Select(p => p.Text));
                        row.Children.Add(mark);
                    }

                    node.Header = row;
                    into.Add(node);
                    if (_session.SelectedPath is { } sel && sel.SequenceEqual(path)) selected = node;
                    Fill(node.Items, def.Children, path);
                }
            }

            Fill(_tree.Items, _session.Doc.Items, []);
            _known.Clear();
            foreach (var n in All(_tree.Items)) _known.Add(Key((int[])n.Tag));

            if (selected is not null)
            {
                for (var parent = ItemsControl.ItemsControlFromItemContainer(selected) as TreeViewItem;
                     parent is not null;
                     parent = ItemsControl.ItemsControlFromItemContainer(parent) as TreeViewItem)
                    parent.IsExpanded = true;
                selected.IsSelected = true;
            }
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private static IEnumerable<TreeViewItem> All(ItemCollection items)
    {
        foreach (TreeViewItem node in items)
        {
            yield return node;
            foreach (var inner in All(node.Items)) yield return inner;
        }
    }

    private void LoadForm()
    {
        // the tree follows the selection, whatever moved it
        var current = (_tree.SelectedItem as TreeViewItem)?.Tag as int[];
        var wanted  = _session.SelectedPath;
        if (!(current is null && wanted is null) && (current is null || wanted is null || !current.SequenceEqual(wanted))) DrawTree();

        var item = _session.Selected;
        _loading = true;
        try
        {
            bool has = item is { Separator: false };
            _fields.IsEnabled = has;
            foreach (var c in new UIElement[] { _checkable, _checked, _enabled, _default, _destructive }) c.IsEnabled = has;
            _formNote.Text = item is null   ? "Nothing is selected. Add an entry with + Entry." :
                             item.Separator ? "A line between two groups of entries." :
                             item.IsSubmenu ? "A submenu: choosing it opens the entries inside it, and it is not run itself." :
                                              string.Empty;
            if (item is null || item.Separator)
            {
                _header.Text      = _id.Text = _gesture.Text = _icon.Text = _tip.Text = string.Empty;
                _gestureNote.Text = string.Empty;
                return;
            }

            _header.Text  = item.Header;
            _id.Text      = item.Id      ?? string.Empty;
            _gesture.Text = item.Gesture ?? string.Empty;
            ShowGestureNote(_gesture.Text);
            _icon.Text             = item.IconKey ?? string.Empty;
            _tip.Text              = item.ToolTip ?? string.Empty;
            _checkable.IsChecked   = item.Checkable;
            _checked.IsChecked     = item.Checked;
            _enabled.IsChecked     = item.Enabled;
            _default.IsChecked     = item.Default;
            _destructive.IsChecked = item.Destructive;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    ///     The menu as it will be goes in the preview bar under the window's own menu bar (see
    ///     <see cref="MenuPreviewBar" />).
    /// </summary>
    private void DrawStage()
    {
        _session.Preview.Show(MenuTester.Build(_session.Doc, _session.Report), _session.Doc.Kind);
    }

    private void DrawLog()
    {
        _log.Items.Clear();
        foreach (string line in _session.Log) _log.Items.Add(new ListBoxItem { Content = LabUi.Label(line, mono: true) });
        if (_log.Items.Count > 0) _log.ScrollIntoView(_log.Items[^1]);
    }

    public MenuView(MenuLabSession session)
    {
        _session = session;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MinWidth = 220 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star), MinWidth = 260 });

        // ---- the menu as a tree -----------------------------------------------------------------------
        _name = LabUi.Field(string.Empty, v =>
                                          {
                                              if (!_loading) _session.EditDoc(d => d.Name = v.Trim());
                                          }, 140, "The name of the menu, and the key of its XAML resource");
        _kind = LabUi.Combo(["Context menu", "Menu bar"], "Context menu", v =>
                                                                          {
                                                                              if (!_loading)
                                                                                  _session.EditDoc(d => d.Kind = v == "Menu bar"
                                                                                                                     ? MenuKind.Bar
                                                                                                                     : MenuKind.Context);
                                                                          }, 110);
        _kind.ToolTip = "A context menu opens with the right button; a menu bar runs across the top of a window";
        var heading = LabUi.Bar(LabUi.Label("Name", true), _name, _kind);

        // one row of icon buttons (every one has a tooltip): all icons, none half text
        var tools = LabUi.Bar(LabUi.IconButton("Plus",      () => _session.AddItem(), "Add an entry after the selected one"),
                              LabUi.IconButton("Separator", _session.AddSeparator,    "Add a line between groups of entries"),
                              LabUi.IconButton("Submenu",   _session.AddSubmenu,      "Add an entry that opens a menu"),
                              LabUi.IconButton("TbDelete",  _session.Delete,          "Take the selected entry out"),
                              LabUi.IconButton("ArrowUp",   () => _session.Move(-1),  "Move up (Alt+Up)"),
                              LabUi.IconButton("ArrowDown", () => _session.Move(1),   "Move down (Alt+Down)"),
                              LabUi.IconButton("Indent",    _session.Indent,          "Make it a subentry of the entry above (Alt+Right)"),
                              LabUi.IconButton("Outdent",   _session.Outdent,         "Take it out of its submenu (Alt+Left)"),
                              LabUi.IconButton("TbCopy",    _session.Duplicate,       "Copy the entry after itself"));

        _tree = new TreeView { BorderThickness = new Thickness(0) }.Themed();
        _tree.SelectedItemChanged += (_, _) =>
                                     {
                                         if (_rebuilding) return;

                                         _session.Select((_tree.SelectedItem as TreeViewItem)?.Tag as int[]);
                                     };
        var top = new StackPanel();
        top.Children.Add(heading);
        top.Children.Add(tools);
        var left = LabUi.Layout(top, _tree);
        SetColumn(left, 0);
        Children.Add(left);

        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        splitter.SetResourceReference(BackgroundProperty, "Theme.Border");
        SetColumn(splitter, 1);
        Children.Add(splitter);

        // ---- the form of the selected entry -------------------------------------------------------------
        _header = LabUi.Line(string.Empty, v => Edit(i => i.Header = v), 180,
                             "The text. An underscore before a letter makes it the key that chooses the entry once the menu is open: _Open");
        _id = LabUi.Line(string.Empty, v => Edit(i => i.Id = string.IsNullOrWhiteSpace(v) ? null : v.Trim()), 180,
                         "What the command is called in the program (file.open); it is the Tag of the entry in the XAML", true);
        // the shortcut is pressed, not typed: the box takes the key or combination as it is struck
        _gesture = LabUi.KeyBox(string.Empty, v =>
                                              {
                                                  if (_loading) return;

                                                  Edit(i => i.Gesture = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), true);
                                                  ShowGestureNote(v);
                                              }, 140,
                                "Click here and press the shortcut (Ctrl+Shift+O, F5, Alt+Enter); Backspace clears it");
        _gestureNote = LabUi.Label(string.Empty, true, wrap: true);
        _icon = LabUi.Line(string.Empty, v => Edit(i => i.IconKey = string.IsNullOrWhiteSpace(v) ? null : v.Trim()), 140,
                           "The key of one of the Silo's icons: TbSave, TbOpen, TbDelete...", true);
        _tip = LabUi.Line(string.Empty, v => Edit(i => i.ToolTip = string.IsNullOrWhiteSpace(v) ? null : v), 180,
                          "What is shown when the pointer rests on the entry");
        _checkable = LabUi.Check("Checkable", false, on => Edit(i => i.Checkable = on, true),
                                 "Choosing the entry turns a check mark on or off");
        _checked = LabUi.Check("Checked", false, on => Edit(i => i.Checked = on, true), "The check mark is on to begin with");
        _enabled = LabUi.Check("Enabled", true, on => Edit(i => i.Enabled = on, true),
                               "A disabled entry is grayed out and cannot be chosen");
        _default = LabUi.Check("Default (bold)", false, on => Edit(i => i.Default = on, true), "The command a double-click would run");
        _destructive = LabUi.Check("Destructive (red)", false, on => Edit(i => i.Destructive = on, true), "Deletes or discards something");
        _formNote = LabUi.Label(string.Empty, true, wrap: true);
        _formNote.Margin = new Thickness(8, 4, 8, 4);

        var gestureRow = new StackPanel();
        gestureRow.Children.Add(_gesture);
        gestureRow.Children.Add(_gestureNote);
        _fields = LabUi.Form([("Text", _header), ("Id", _id), ("Shortcut", gestureRow), ("Icon", _icon), ("Tooltip", _tip)], 70);
        var checks = LabUi.Checks(new UIElement[] { _checkable, _checked, _enabled, _default, _destructive }, 2);
        _form.Children.Add(_formNote);
        _form.Children.Add(_fields);
        _form.Children.Add(checks);

        // ---- the stage: the menu as it will be ------------------------------------------------------------
        _log            = LabUi.List();
        _log.FontFamily = LabUi.Mono;
        var logBar = LabUi.Bar(LabUi.Label("Test",                                                            bold: true),
                               LabUi.Label("click the entries, press their shortcuts (click the area first)", true),
                               LabUi.Button("Clear", _session.ClearLog, "Empty the log"));
        var formScroll = new ScrollViewer
                         {
                             Content                       = _form, VerticalScrollBarVisibility      = ScrollBarVisibility.Auto,
                             HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 330
                         };
        var right = LabUi.Layout(new StackPanel().Also(p =>
                                                       {
                                                           p.Children.Add(LabUi.Heading("Entry"));
                                                           p.Children.Add(formScroll);
                                                           p.Children.Add(logBar);
                                                       }), _log);
        SetColumn(right, 2);
        Children.Add(right);

        SetResourceReference(BackgroundProperty, "Theme.Background");
        _session.Changed          += (_, _) => Redraw();
        _session.Loaded           += (_, _) => LoadAll();
        _session.SelectionChanged += (_, _) => LoadForm();
        _session.LogChanged       += (_, _) => DrawLog();
        LoadAll();
    }
}

/// <summary>
///     The strip under the window's menu bar that shows the menu being made as it will be: the menu bar itself, or (for a
///     context menu) an area to
///     right-click. Shortcuts work while it has the keyboard focus (click it first).
/// </summary>
internal sealed class MenuPreviewBar : Border
{
    private readonly TextBlock _hint;
    private readonly DockPanel _host = new() { LastChildFill = true };

    /// <summary>Shows the menu that was built (the old one is replaced).</summary>
    public void Show(MenuTester.Built built, MenuKind kind)
    {
        _host.Children.Clear();
        InputBindings.Clear();
        foreach (var binding in built.Bindings) InputBindings.Add(binding);

        if (_hint.Parent is Panel old) old.Children.Remove(_hint);
        DockPanel.SetDock(_hint, Dock.Right);
        _host.Children.Add(_hint);

        if (kind == MenuKind.Bar)
        {
            var bar = new Menu { VerticalAlignment = VerticalAlignment.Center };
            foreach (var c in built.Controls) bar.Items.Add(c);
            _host.Children.Add(bar);
            ContextMenu = null;
            _hint.Text  = "Preview of the menu bar. Click here, then press a shortcut.";
        }
        else
        {
            var menu = new ContextMenu();
            foreach (var c in built.Controls) menu.Items.Add(c);
            ContextMenu = menu;
            _host.Children.Add(new TextBlock
                               {
                                   Text              = "Right-click here to open the menu", Margin = new Thickness(10, 6, 10, 6),
                                   VerticalAlignment = VerticalAlignment.Center, FontStyle         = FontStyles.Italic
                               });
            _hint.Text = "Preview of the context menu. Shortcuts work after a click here.";
        }
    }

    public MenuPreviewBar()
    {
        Focusable       = true;
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding         = new Thickness(0);
        Child           = _host;
        SetResourceReference(BackgroundProperty,  "Theme.Surface");
        SetResourceReference(BorderBrushProperty, "Theme.Border");
        MouseDown += (_, _) => Focus();

        _hint                   = LabUi.Label(string.Empty, true);
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _hint.Margin            = new Thickness(10, 0, 10, 0);
    }
}