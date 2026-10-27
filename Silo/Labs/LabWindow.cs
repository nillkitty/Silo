using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using AvalonDock.Themes;
using Serilog;
using Silo.Commanding;
using Silo.Converters;
using Silo.Theming;
using Silo.Toolbar;
using Silo.Ui.Windows;

namespace Silo.Labs;

/// <summary>
///     The window every Lab shares: a menu (the Lab's global actions, the value's actions, the view), a toolbar (the actions of the moment,
///     the value box), the Lab's main view in the middle with its inspectors docked left and right, and a status bar with a terminal.
///     What is specific to the kind of value comes from the <see cref="ILabSession" />.
/// </summary>
/// <remarks>
///     The window's place, size and the arrangement of its panes are kept in the Silo with the other windows (see
///     <see cref="WindowPlacements" />), one arrangement per Lab: the next window of the Lab opens just so.
/// </remarks>
public sealed class LabWindow : Window, IWindowLayout, ILabHost, ILabCreator
{
    /// <summary>The kind of window it is for <see cref="WindowPlacements" />.</summary>
    public const string PlacementType = "Lab";

    private const string InspectorPrefix = "inspector:";
    private const string MainId          = "main";
    private const int    LogLimit        = 500;
    private const double LaneWidth       = 270;
    private const double MainMinWidth    = 520;
    private const double BottomHeight    = 250;

    private readonly List<KeyBinding>                      _bindings   = [];
    private readonly Dictionary<string, LabInspector>      _inspectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LayoutAnchorable>  _panes      = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string>                          _history    = [];
    private readonly Queue<string>                         _log        = new();
    private readonly ILabSession                           _session;
    private readonly LabTerminal                           _terminal;

    private readonly DockingManager _dock = new();
    private readonly StackPanel     _contextBar = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel     _valueBar   = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox        _valueBox   = new() { VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), MinHeight = 26 };
    private readonly TextBlock      _statusText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 };
    private readonly TextBlock      _outputText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460, Margin = new Thickness(8, 0, 8, 0), Cursor = Cursors.Hand };
    private readonly TextBox        _input = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New") };
    private readonly TextBox        _logText = new() { IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
    private readonly Popup          _logPopup = new() { StaysOpen = false, Placement = PlacementMode.Top, AllowsTransparency = false };

    private readonly List<SharedPane> _shared = [];
    private readonly Button           _problemsButton = new() { Visibility = Visibility.Collapsed, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8, 0, 8, 0), Cursor = Cursors.Hand };

    private readonly ILabModel?                          _model;
    private readonly WrapPanel                           _commandBar   = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel                          _selectors    = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox                            _partCombo    = new() { MinWidth = 150, MaxWidth = 280, Margin = new Thickness(4, 0, 10, 0), ToolTip = "The active part of the subject: what part menu and part parameters are about" };
    private readonly ComboBox                            _toolCombo    = new() { MinWidth = 120, MaxWidth = 220, Margin = new Thickness(4, 0, 0, 0), ToolTip = "The active tool" };
    private readonly List<(LabAspect Aspect, MenuItem Item)> _aspectMenus = [];
    private readonly HashSet<string>                     _autoHidden   = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string>                        _recorded     = [];
    private readonly Button                              _recordButton = new() { Visibility = Visibility.Collapsed, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8, 0, 8, 0), Cursor = Cursors.Hand };

    private bool          _syncing;
    private bool          _modelPending;
    private string?       _recordingName;
    private bool          _replaying;
    private ILabProblems? _problems;
    private bool          _problemsAutoShown;
    private bool          _problemsPending;
    private UIElement     _main = null!;
    private int       _historyIndex;
    private string?   _completionBase;
    private string?   _lastCompletion;
    private int       _completionIndex;
    private bool      _closed;
    private bool      _lanes;
    private UIElement? _topBar;

    public LabWindow(ILabSession session)
    {
        _session  = session ?? throw new ArgumentNullException(nameof(session));
        _terminal = new LabTerminal(this, session);

        Width     = 1320;
        Height    = 820;
        MinWidth  = 640;
        MinHeight = 400;
        Title     = session.Title;
        if (LabWindowIcon.For(session.Lab.IconKey) is { } icon) Icon = icon;

        SetResourceReference(BackgroundProperty, "Theme.Background");
        SetResourceReference(ForegroundProperty, "Theme.Text");

        _model = session as ILabModel;
        session.Attach(this);
        _main   = session.BuildMainView();
        _topBar = session.BuildTopBar();

        // the library leads its side; the Lab's own panes follow; then what every window has
        AddLibrary();
        foreach (var inspector in session.Inspectors) _inspectors[inspector.Id] = inspector;
        AddCommonPanes();
        AddParameterPanes();

        EnsureWidth();
        Content = BuildUi();
        BuildDefaultLayout();
        RebuildActions();

        _session.PropertyChanged += OnSessionPropertyChanged;
        _session.ValueChanged    += (_, _) => Dispatcher.InvokeAsync(() =>
                                                                      {
                                                                          ShowValue();
                                                                          PokeLibrary();
                                                                      });
        _session.ActionsChanged  += (_, _) => Dispatcher.InvokeAsync(RebuildActions);
        if (_model is not null) _model.ModelChanged += OnModelChanged;
        WireProblems();
        if (_session is ILabLive live) live.Changed += (_, _) => Dispatcher.InvokeAsync(PokeLibrary);
        _session.Lab.Parameters.Changed += OnParametersChanged;
        AppTheme.Instance.PropertyChanged += OnThemeChanged;

        ShowValue();
        ApplyDockTheme();
        Print($"{session.Lab.Name}. Type help for the commands.");

        // the place the Lab's windows were left in; this window is the layout it keeps and restores
        WindowPlacements.Track(this, App.Instance?.ActiveSilo, PlacementType, session.LayoutVersion > 1 ? $"silo:lab/{session.Lab.Id}/v{session.LayoutVersion}" : $"silo:lab/{session.Lab.Id}", this);

        Loaded  += (_, _) =>
                   {
                       SyncShown();
                       UpdateProblems();
                       RefreshModelUi();
                   };
        Closed  += OnClosed;
    }

    /// <summary>What the session is called by; the window title follows it.</summary>
    public ILabSession Session => _session;

    Window ILabHost.Window => this;

    internal IEnumerable<string> InspectorIds => _inspectors.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

    // ---- the panes every window has ---------------------------------------------------------------

    private void AddLibrary()
    {
        var own     = _session as ILabLibrary;
        var library = _model is null ? own : new LabModelLibrary(own, _model, this);
        if (library is null) return;

        var pane = new LabLibraryInspector(library);
        _shared.Add(pane);
        _inspectors[pane.Id] = pane;
    }

    private void AddCommonPanes()
    {
        var recent = new LabRecentInspector(_session, OpenRecent);
        _shared.Add(recent);
        _inspectors[recent.Id] = recent;

        if (_session is ILabPresets { PresetKinds.Count: > 0 } presets)
        {
            var pane = new LabPresetsInspector(_session, presets);
            _shared.Add(pane);
            _inspectors[pane.Id] = pane;
        }

        if (_session is ILabProblems problems)
        {
            var pane = new LabProblemsInspector(problems);
            _shared.Add(pane);
            _inspectors[pane.Id] = pane;
        }
    }

    private void PokeLibrary()
    {
        foreach (var pane in _shared.OfType<LabLibraryInspector>()) pane.Poke();
    }

    private async void OpenRecent(string text)
    {
        try
        {
            if (_session is ILabRecent recent) await recent.OpenRecentAsync(text);
            else if (_session.TryParseValue(text, out var value)) await _session.OpenAsync(value);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening {Text} again in {Lab} failed", text, _session.Lab.Name);
            SetOutput(ex.Message, true);
            (_session as ILabProblems)?.ReportProblem(LabProblemSeverity.Error, "Recent", ex.Message, text);
        }
    }

    // ---- problems -------------------------------------------------------------------------------

    private void WireProblems()
    {
        if (_session is not ILabProblems problems) return;

        _problems = problems;
        problems.ProblemsChanged += (_, _) => Dispatcher.InvokeAsync(UpdateProblems);

        // a Lab that checks its value as it is edited says it changed
        if (_session is ILabLive live) live.Changed += (_, _) => Dispatcher.InvokeAsync(UpdateProblems);
        _problemsButton.Click += (_, _) => ShowInspector(LabProblemsInspector.InspectorId);
    }

    /// <summary>Counts the problems for the status bar and the pane; the pane opens the first time there is one (and stays closed if the person closes it).</summary>
    private void UpdateProblems()
    {
        if (_closed || _problems is null || _problemsPending) return;

        _problemsPending = true;
        Dispatcher.InvokeAsync(() =>
                               {
                                   _problemsPending = false;
                                   if (_closed || _problems is null) return;

                                   var list     = _problems.ProblemList;
                                   int errors   = list.Count(p => p.Severity == LabProblemSeverity.Error);
                                   int warnings = list.Count(p => p.Severity == LabProblemSeverity.Warning);

                                   _problemsButton.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                                   var text = new TextBlock { Text = list.Count.ToString(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
                                   var row  = new StackPanel { Orientation = Orientation.Horizontal };
                                   if (LabUi.Icon(errors > 0 ? "StateFailed" : "Check", 13) is { } glyph)
                                   {
                                       glyph.Foreground = errors > 0 ? LabUi.Red : LabUi.Amber;
                                       row.Children.Add(glyph);
                                   }

                                   row.Children.Add(text);
                                   _problemsButton.Content = row;
                                   _problemsButton.ToolTip = list.Count == 0 ? null : $"{errors} error(s), {warnings} warning(s), {list.Count - errors - warnings} note(s): click to see them";

                                   if (list.Count > 0 && !_problemsAutoShown && IsLoaded)
                                   {
                                       _problemsAutoShown = true;
                                       ShowInspector(LabProblemsInspector.InspectorId);
                                   }

                                   if (_inspectors.TryGetValue(LabProblemsInspector.InspectorId, out var pane) && pane is SharedPane shared) shared.Poke();
                               }, DispatcherPriority.Background);
    }

    // ---- building ---------------------------------------------------------------------------

    private UIElement BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var menu = BuildMenu();
        Grid.SetRow(menu, 0);
        root.Children.Add(menu);

        if (_topBar is not null)
        {
            Grid.SetRow(_topBar, 1);
            root.Children.Add(_topBar);
        }

        var commands = BuildCommandBar();
        Grid.SetRow(commands, 2);
        root.Children.Add(commands);

        var bar = BuildToolbar();
        Grid.SetRow(bar, 3);
        root.Children.Add(bar);

        Grid.SetRow(_dock, 4);
        root.Children.Add(_dock);

        var status = BuildStatusBar();
        Grid.SetRow(status, 5);
        root.Children.Add(status);

        InputBindings.Add(new KeyBinding(new DelegateCommand(_ => { _valueBox.Focus(); _valueBox.SelectAll(); }), Key.L, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new DelegateCommand(_ => _input.Focus()), Key.OemTilde, ModifierKeys.Control));
        return root;
    }

    private Menu BuildMenu()
    {
        var menu = new Menu();

        // an empty menu item cannot open, so each holds a stand-in that is replaced when it opens
        AddMenu(menu, "_File", FillFileMenu);
        if (OwnMenu("edit") is not null || Sorted(LabMenuPlace.Edit).Count > 0) AddMenu(menu, "_Edit", FillEditMenu);
        foreach (var own in LabMenus.SafeMenus(_session).Where(m => !IsMenu(m, "file") && !IsMenu(m, "edit")))
        {
            var captured = own;
            AddMenu(menu, captured.Header, item => LabMenus.Fill(item, captured.Items, Wrap));
        }

        // an aspect with commands has a menu while it is active
        foreach (var aspect in _model?.Aspects.Where(a => a.Commands.Count > 0) ?? [])
        {
            var captured = aspect;
            var item     = AddMenu(menu, aspect.Title.Replace("_", "__"), m => FillMenu(m, [.. captured.Commands.Select(Wrap)]));
            _aspectMenus.Add((aspect, item));
        }

        AddMenu(menu, "_Lab", FillLabMenu);
        AddMenu(menu, "_Subject", FillSubjectMenu);
        if (_model is { } model && (model.PartTypes.Count > 0 || model.Parts.Count > 0)) AddMenu(menu, "_Part", FillPartMenu);
        if (_model is { Tools.Count: > 0 }) AddMenu(menu, "_Tool", FillToolMenu);
        AddMenu(menu, "Vie_w", FillViewMenu);
        return menu;

        static MenuItem AddMenu(Menu parent, string header, Action<MenuItem> fill)
        {
            var item = new MenuItem { Header = header };
            item.Items.Add(new MenuItem { Header = "...", IsEnabled = false });
            item.SubmenuOpened += (_, e) =>
                                  {
                                      if (!ReferenceEquals(e.OriginalSource, item)) return;

                                      item.Items.Clear();
                                      fill(item);
                                  };
            parent.Items.Add(item);
            return item;
        }
    }

    private static bool IsMenu(LabMenu menu, string name) => string.Equals(menu.Header.Replace("_", string.Empty), name, StringComparison.OrdinalIgnoreCase);

    private LabMenu? OwnMenu(string name) => LabMenus.SafeMenus(_session).FirstOrDefault(m => IsMenu(m, name));

    /// <summary>The global and value actions that go in this menu by what they are, in order with a separator between the kinds (when the Lab makes no menu of its own for it).</summary>
    private List<ProvidedCommand?> Sorted(LabMenuPlace place)
    {
        var all = LabMenus.Safe(() => _session.GlobalActions).Concat(LabMenus.Safe(() => _session.ValueActions)).OfType<ProvidedCommand>();
        return LabMenus.Ranked(all.Select(c => (Command: c, Class: LabMenus.Classify(c))).Where(t => t.Class.Place == place).Select(t => (t.Command, t.Class.Rank)));
    }

    /// <summary>The actions of the Lab menu or the Value menu: the ones the File and Edit menus did not take.</summary>
    private IEnumerable<ProvidedCommand> Remaining(IEnumerable<ProvidedCommand> actions)
    {
        bool ownFile = OwnMenu("file") is not null;
        bool ownEdit = OwnMenu("edit") is not null;
        var  inMenus = new HashSet<string>(LabMenus.SafeMenus(_session).SelectMany(m => m.Items).OfType<ProvidedCommand>().Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
        {
            if (inMenus.Contains(action.Id)) continue;

            // what the File and Edit menus took (by what it is) is not listed again; a Lab that makes its own File or Edit lists what it wants there
            var place = LabMenus.Classify(action).Place;
            if (place == LabMenuPlace.File && !ownFile) continue;
            if (place == LabMenuPlace.Edit && !ownEdit) continue;
            yield return action;
        }
    }

    // ---- what each menu holds: lists of commands, a null being a separator (the menu bar and the command bar are made from the same) -----

    /// <summary>A command of the session, noted by the macro recorder when it runs.</summary>
    private ProvidedCommand Wrap(ProvidedCommand command) => LabMenus.Wrapped(command, c =>
                                                                                      {
                                                                                          if (c.Id.IndexOfAny([' ', '\t']) < 0) NoteLine(c.Id);
                                                                                      });

    /// <summary>A command of the window's own: runs <paramref name="run" />, and the macro records <paramref name="line" /> for it.</summary>
    private ProvidedCommand Own(string id, string header, string line, Action run, string? icon = null, Func<bool>? canRun = null, string? tip = null, Func<bool>? isChecked = null)
    {
        var command = ProvidedCommand.From(id, header, () =>
                                                       {
                                                           NoteLine(line);
                                                           run();
                                                       }, icon, canRun, tip);
        return isChecked is null ? command : command with { IsChecked = isChecked };
    }

    private List<ProvidedCommand?> FileItems()
    {
        var list = (OwnMenu("file")?.Items ?? Sorted(LabMenuPlace.File)).Select(c => c is null ? null : Wrap(c)).ToList();
        if (list.Count > 0) list.Add(null);
        list.Add(ProvidedCommand.From("new-window", $"_New {_session.Lab.Name} Window", () => LabHost.Open(_session.Lab), "TbNew", toolTip: "Open another window of this Lab"));
        list.Add(null);
        list.Add(ProvidedCommand.From("close-window", "_Close", Close, "TbCloseSilo", toolTip: "Close this window"));
        return list;
    }

    private List<ProvidedCommand?> EditItems() => (OwnMenu("edit")?.Items ?? Sorted(LabMenuPlace.Edit)).Select(c => c is null ? null : Wrap(c)).ToList();

    private List<ProvidedCommand?> LabItems()
    {
        var list = new List<ProvidedCommand?>();
        list.AddRange(Remaining(LabMenus.Safe(() => _session.GlobalActions).OfType<ProvidedCommand>()).Select(Wrap));
        if (_model is not null)
        {
            var own = LabModelRules.ActiveCommands(_model).Where(e => e.Owner.Kind is LabOwnerKind.Lab or LabOwnerKind.Instance).Select(e => Wrap(e.Command)).ToList();
            if (own.Count > 0)
            {
                if (list.Count > 0) list.Add(null);
                list.AddRange(own);
            }
        }

        if (list.Count > 0) list.Add(null);
        list.Add(MacrosMenu());
        return list;
    }

    private List<ProvidedCommand?> SubjectItems()
    {
        var list = new List<ProvidedCommand?>();
        list.AddRange(Remaining(LabMenus.Safe(() => _session.ValueActions).OfType<ProvidedCommand>()).Select(Wrap));
        if (_model is null) return list;

        var own = LabModelRules.ActiveCommands(_model).Where(e => e.Owner.Kind is LabOwnerKind.Subject or LabOwnerKind.SubjectType).Select(e => Wrap(e.Command)).ToList();
        if (own.Count > 0)
        {
            if (list.Count > 0) list.Add(null);
            list.AddRange(own);
        }

        var fresh = new List<ProvidedCommand>();
        foreach (var type in _model.SubjectTypes)
        {
            var children = CreationItems(type.PrefabList, type.TemplateList, p => CreateAsync(type, null, p, null), t => CreateAsync(type, null, null, t));
            if (children.Count == 0) continue;

            fresh.Add(_model.SubjectTypes.Count == 1
                          ? ProvidedCommand.Submenu("new-from-" + type.Id, $"_New {type.Name} From", children, iconKey: "Template", toolTip: "Start a new subject from a prefab or a template")
                          : ProvidedCommand.Submenu("new-from-" + type.Id, $"New {type.Name} _From", children, iconKey: "Template"));
        }

        if (fresh.Count > 0)
        {
            if (list.Count > 0) list.Add(null);
            list.AddRange(fresh);
        }

        return list;
    }

    private List<ProvidedCommand?> PartItems()
    {
        var list = new List<ProvidedCommand?>();
        if (_model is not { } model) return list;

        var active = model.ActivePart;
        var parts  = LabModelRules.AllParts(model).ToList();
        int at     = active is null ? -1 : parts.FindIndex(p => ReferenceEquals(p, active) || p.Key == active.Key);

        list.Add(Own("part-parent", "Select _Parent", "part parent", () => model.ActivatePart(active?.Parent), "ArrowUp", () => active?.Parent is not null, "Make the part this one is inside the active part"));
        list.Add(Own("part-child", "Select _First Child", "part child", () => model.ActivatePart(active!.Children[0]), "ArrowDown", () => active is { Children.Count: > 0 }, "Make the first part inside the active part the active one"));
        list.Add(Own("part-previous", "Select Pre_vious", "part previous", () => model.ActivatePart(parts[at - 1]), "Undo", () => at > 0, "The part before this one"));
        list.Add(Own("part-next", "Select _Next", "part next", () => model.ActivatePart(parts[at + 1]), "TbRedo", () => at >= 0 && at < parts.Count - 1, "The part after this one"));
        list.Add(Own("part-none", "_No Part", "part none", () => model.ActivatePart(null), null, () => active is not null, "Leave no part active"));

        var own = LabModelRules.ActiveCommands(model).Where(e => e.Owner.Kind is LabOwnerKind.Part or LabOwnerKind.PartType).Select(e => Wrap(e.Command)).ToList();
        if (own.Count > 0)
        {
            list.Add(null);
            list.AddRange(own);
        }

        var adds = new List<ProvidedCommand>();
        foreach (var type in LabModelRules.AddableTypes(model, active))
        {
            var children = CreationItems(type.PrefabList, type.TemplateList, p => CreateAsync(null, type, p, null), t => CreateAsync(null, type, null, t));
            if (children.Count > 0) adds.Add(ProvidedCommand.Submenu("add-" + type.Id, $"Add {type.Name} _From", children, iconKey: type.IconKey ?? "Template"));
        }

        if (adds.Count > 0)
        {
            list.Add(null);
            list.AddRange(adds);
        }

        return list;
    }

    private List<ProvidedCommand?> ToolItems()
    {
        var list = new List<ProvidedCommand?>();
        if (_model is not { } model) return list;

        var active = model.ActiveTool;
        list.Add(Own("tool-none", "No _Tool", "tool none", () => model.ActivateTool(null), null, null, "Put the current tool down", () => model.ActiveTool is null));
        foreach (var tool in LabModelRules.AvailableTools(model))
        {
            var captured = tool;
            list.Add(Own("tool-" + tool.Id, tool.Name.Replace("_", "__"), "tool " + (tool.Id.IndexOf(' ') < 0 ? tool.Id : '"' + tool.Id + '"'), () => model.ActivateTool(captured), tool.IconKey, null, tool.Description, () => ReferenceEquals(model.ActiveTool, captured)));
        }

        var own = LabModelRules.ActiveCommands(model).Where(e => e.Owner.Kind == LabOwnerKind.Tool).Select(e => Wrap(e.Command)).ToList();
        if (own.Count > 0)
        {
            list.Add(null);
            list.AddRange(own);
        }

        return list;
    }

    /// <summary>The entries that add a prefab or a template: prefabs, then templates (their names end in "..." when they ask something); a Group makes a submenu.</summary>
    private List<ProvidedCommand> CreationItems(IReadOnlyList<LabPrefab> prefabs, IReadOnlyList<LabTemplate> templates, Func<LabPrefab, Task> usePrefab, Func<LabTemplate, Task> useTemplate)
    {
        var entries = new List<(string? Group, ProvidedCommand Command)>();
        foreach (var p in prefabs)
        {
            var captured = p;
            entries.Add((p.Group, ProvidedCommand.From("prefab-" + p.Id, p.Name.Replace("_", "__"), () => _ = usePrefab(captured), p.IconKey, null, p.Description)));
        }

        foreach (var t in templates)
        {
            var captured = t;
            bool asks    = t.NeedsAnswers(_model);
            entries.Add((t.Group, ProvidedCommand.From("template-" + t.Id, t.Name.Replace("_", "__") + (asks ? "..." : string.Empty), () => _ = useTemplate(captured), t.IconKey ?? "Template", null, t.Description)));
        }

        var result = new List<ProvidedCommand>();
        result.AddRange(entries.Where(e => e.Group is null).Select(e => e.Command));
        foreach (var group in entries.Where(e => e.Group is not null).GroupBy(e => e.Group!))
            result.Add(ProvidedCommand.Submenu("group-" + group.Key, group.Key.Replace("_", "__"), group.Select(e => e.Command), iconKey: "Folder17"));

        return result;
    }

    // ---- macros ------------------------------------------------------------------------------------

    private ProvidedCommand MacrosMenu()
    {
        var children = new List<ProvidedCommand>();
        if (_recordingName is null) children.Add(ProvidedCommand.From("macro-record", "_Record Macro...", AskAndRecord, "Record", toolTip: "Keep what you do in this window, step by step, under a name", group: "1record"));
        else children.Add(ProvidedCommand.From("macro-stop", $"_Stop Recording '{_recordingName}'", () => StopRecording(), "Stop", toolTip: "Keep the steps recorded so far", group: "1record"));

        var names = LabProfile.MacroNames(_session.Lab.Id);
        foreach (string name in names)
        {
            string captured = name;
            children.Add(ProvidedCommand.From("macro-run-" + name, name.Replace("_", "__"), () => _ = RunMacroSafeAsync(captured), "Run", () => _recordingName is null, "Run the steps of this macro", "2run"));
        }

        if (names.Count > 0)
        {
            var delete = names.Select(name => ProvidedCommand.From("macro-delete-" + name, name.Replace("_", "__"), () =>
                                                                                                                       {
                                                                                                                           if (LabUi.Confirm($"Forget the macro '{name}'?", "Macros")) LabProfile.DeleteMacro(_session.Lab.Id, name);
                                                                                                                       }, "TbDelete")).ToList();
            children.Add(ProvidedCommand.Submenu("macro-delete", "_Delete Macro", delete, "3delete", "TbDelete"));
        }

        return ProvidedCommand.Submenu("macros", "_Macros", children, iconKey: "Record", toolTip: "Steps recorded under a name, to run again");
    }

    private void AskAndRecord()
    {
        string? name = LabUi.Ask("Record macro", "Name the macro, then do what it should repeat (menus, buttons, terminal). Stop Recording keeps it:");
        if (!string.IsNullOrWhiteSpace(name)) StartRecording(name);
    }

    internal string? RecordingName => _recordingName;

    /// <summary>Starts keeping the commands and terminal lines run in this window under a name.</summary>
    internal void StartRecording(string name)
    {
        if (_recordingName is not null) throw new InvalidOperationException($"Already recording '{_recordingName}'. macro stop keeps it.");

        _recorded.Clear();
        _recordingName = name.Trim();
        UpdateRecordButton();
    }

    /// <summary>Stops recording and keeps the steps; returns how many there were.</summary>
    internal int StopRecording()
    {
        if (_recordingName is not { } name) throw new InvalidOperationException("Not recording.");

        _recordingName = null;
        int count = _recorded.Count;
        LabProfile.SaveMacro(_session.Lab.Id, name, _recorded);
        _recorded.Clear();
        UpdateRecordButton();
        SetOutput($"Saved macro '{name}' ({count} step(s))", false);
        return count;
    }

    private void UpdateRecordButton()
    {
        _recordButton.Visibility = _recordingName is null ? Visibility.Collapsed : Visibility.Visible;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (LabUi.Icon("Record", 13) is { } glyph)
        {
            glyph.Foreground = LabUi.Red;
            row.Children.Add(glyph);
        }

        row.Children.Add(new TextBlock { Text = $"Recording '{_recordingName}' ({_recorded.Count})", Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        _recordButton.Content = row;
        _recordButton.ToolTip = "Click to stop recording and keep the macro";
    }

    /// <summary>Notes a step for the macro being recorded: a command's id or a terminal line (nothing while a macro is replayed).</summary>
    internal void NoteLine(string line)
    {
        if (_recordingName is null || _replaying || string.IsNullOrWhiteSpace(line)) return;

        _recorded.Add(line.Trim());
        UpdateRecordButton();
    }

    /// <summary>The steps of a macro (<see cref="LabProfile.GetMacro" />), null when there is none by that name.</summary>
    internal IReadOnlyList<string>? MacroLines(string name) => LabProfile.GetMacro(_session.Lab.Id, name);

    /// <summary>Runs the steps of a macro one after another; stops at the first that fails.</summary>
    internal async Task RunMacroAsync(string name)
    {
        var lines = MacroLines(name) ?? throw new InvalidOperationException($"There is no macro '{name}'.");
        if (_recordingName is not null) throw new InvalidOperationException("Stop recording first.");

        _replaying = true;
        try
        {
            foreach (string line in lines)
                await _terminal.ExecuteAsync(line);
        }
        finally
        {
            _replaying = false;
        }
    }

    private async Task RunMacroSafeAsync(string name)
    {
        try
        {
            await RunMacroAsync(name);
            SetOutput($"Ran macro '{name}'", false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Macro {Name} failed", name);
            SetOutput($"Macro '{name}': {ex.Message}", true);
            (_session as ILabProblems)?.ReportProblem(LabProblemSeverity.Error, "Macro", ex.Message, name);
        }
    }

    // ---- prefabs and templates ------------------------------------------------------------------------

    async Task ILabCreator.CreateAsync(LabSubjectType? subject, LabPartType? part, LabPrefab? prefab, LabTemplate? template) => await CreateAsync(subject, part, prefab, template);

    /// <summary>Adds a new subject or part from a prefab, or from a template once its placeholders have values.</summary>
    private async Task CreateAsync(LabSubjectType? subject, LabPartType? part, LabPrefab? prefab, LabTemplate? template)
    {
        if (_model is null) return;

        try
        {
            string content;
            if (template is not null)
            {
                var values = template.Initial(_model, out var bound);
                if (template.Placeholders.Any(p => !bound.Contains(p.Name)) && !LabTemplateDialog.Ask(this, template, values, bound)) return;

                content = template.Render(values);
            }
            else
            {
                content = prefab?.Content ?? string.Empty;
            }

            NoteLine(template is not null ? $"# template {template.Id}" : $"# prefab {prefab?.Id}");
            if (subject is not null)
            {
                await _model.NewSubjectAsync(subject, content);
            }
            else if (part is not null)
            {
                await _model.AddPartAsync(part, ParentFor(part), content);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Adding from a prefab or template in {Lab} failed", _session.Lab.Name);
            SetOutput(ex.Message, true);
            (_session as ILabProblems)?.ReportProblem(LabProblemSeverity.Error, "New", ex.Message);
        }
    }

    /// <summary>The part a new part of this type goes in: the active part, or the nearest part around it of the type the new one must go in.</summary>
    private LabPart? ParentFor(LabPartType type)
    {
        var active = _model?.ActivePart;
        if (type.ParentTypeId is null) return active;

        for (var p = active; p is not null; p = p.Parent)
            if (string.Equals(p.Type.Id, type.ParentTypeId, StringComparison.OrdinalIgnoreCase))
                return p;

        throw new InvalidOperationException($"Select a {type.ParentTypeId} part first: a {type.Name.ToLowerInvariant()} goes inside one.");
    }

    // ---- the menus ----------------------------------------------------------------------------------

    /// <summary>Fills a menu from a list of commands: runs between the nulls are laid out together (by group), a separator between them.</summary>
    private static void FillMenu(MenuItem menu, List<ProvidedCommand?> items)
    {
        var run = new List<ProvidedCommand>();

        void Flush()
        {
            if (run.Count == 0) return;

            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            AddCommands(menu, run);
            run.Clear();
        }

        foreach (var item in items)
        {
            if (item is null) Flush();
            else run.Add(item);
        }

        Flush();
        while (menu.Items.Count > 0 && menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(no actions)", IsEnabled = false });
    }

    private void FillFileMenu(MenuItem menu)
    {
        var items = FileItems();
        // a Lab's own File content keeps its order and separators; the window's New Window and Close follow
        LabMenus.Fill(menu, items);
    }

    private void FillEditMenu(MenuItem menu)
    {
        LabMenus.Fill(menu, EditItems());
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(nothing to edit)", IsEnabled = false });
    }

    private void FillLabMenu(MenuItem menu) => FillMenu(menu, LabItems());

    private void FillSubjectMenu(MenuItem menu) => FillMenu(menu, SubjectItems());

    private void FillPartMenu(MenuItem menu) => FillMenu(menu, PartItems());

    private void FillToolMenu(MenuItem menu) => FillMenu(menu, ToolItems());

    private void FillViewMenu(MenuItem menu)
    {
        // the panes of the things the Lab works with and keeps come first, then (after a line) the views of what is going on
        int problems = _problems?.ProblemList.Count ?? 0;
        var listed = _inspectors.Values.Where(i => i.Id != LabProblemsInspector.InspectorId || problems > 0 || IsInspectorOpen(i.Id))
                                .Where(i => IsInspectorOpen(i.Id) || Applies(i))
                                .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
                                .ToList();

        void AddPanes(IEnumerable<LabInspector> panes)
        {
            foreach (var inspector in panes)
            {
                string id    = inspector.Id;
                string title = id == LabProblemsInspector.InspectorId && problems > 0 ? $"{inspector.Title} ({problems})" : inspector.Title;
                var    item  = new MenuItem { Header = title.Replace("_", "__"), IsCheckable = true, IsChecked = IsInspectorOpen(id) };
                item.Icon = ToolbarIcons.Find(inspector.IconKey) is { } geometry ? new AppIcon { Icon = geometry, Size = 16 } : null;
                item.Click += (_, _) =>
                              {
                                  // IsCheckable has already flipped the mark: it says what the person asked for
                                  if (item.IsChecked) ShowInspector(id);
                                  else HideInspector(id);
                              };
                menu.Items.Add(item);
            }
        }

        var entities = listed.Where(i => i.Kind == LabPaneKind.Entity).ToList();
        var others   = listed.Where(i => i.Kind != LabPaneKind.Entity).ToList();
        AddPanes(entities);
        if (entities.Count > 0 && others.Count > 0) menu.Items.Add(new Separator());
        AddPanes(others);

        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "_Reset Layout", ToolTip = "Put the panes back where a new window has them", Icon = LabUi.Icon("Refresh") };
        reset.Click += (_, _) => ResetLayout();
        menu.Items.Add(reset);

        menu.Items.Add(new Separator());
        var terminal = new MenuItem { Header = "Terminal _Log", InputGestureText = "Ctrl+`", Icon = LabUi.Icon("Terminal") };
        terminal.Click += (_, _) => ShowLog();
        menu.Items.Add(terminal);
    }

    private static void AddCommands(MenuItem menu, IEnumerable<ProvidedCommand> commands)
    {
        try
        {
            foreach (var entry in CommandMenu.Items(commands.Select(NodeCommand.From))) menu.Items.Add(entry);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Building a Lab menu failed");
        }
    }

    // ---- the command bar: a button for every menu entry, then the part and tool selectors --------------

    private UIElement BuildCommandBar()
    {
        var panel = new DockPanel { Margin = new Thickness(6, 2, 6, 0), LastChildFill = true };

        if (_model is not null)
        {
            _selectors.Children.Add(LabUi.Label("Part", muted: true));
            _selectors.Children.Add(_partCombo);
            _selectors.Children.Add(LabUi.Label("Tool", muted: true));
            _selectors.Children.Add(_toolCombo);
            DockPanel.SetDock(_selectors, Dock.Right);
            panel.Children.Add(_selectors);

            _partCombo.SelectionChanged += (_, _) =>
                                           {
                                               if (_syncing || _partCombo.SelectedItem is not ComboBoxItem item) return;

                                               NoteLine(item.Tag is LabPart p ? $"part \"{p.Key}\"" : "part none");
                                               _model!.ActivatePart(item.Tag as LabPart);
                                           };
            _toolCombo.SelectionChanged += (_, _) =>
                                           {
                                               if (_syncing || _toolCombo.SelectedItem is not ComboBoxItem item) return;

                                               NoteLine(item.Tag is LabTool t ? $"tool \"{t.Id}\"" : "tool none");
                                               _model!.ActivateTool(item.Tag as LabTool);
                                           };
        }

        panel.Children.Add(_commandBar);
        return panel;
    }

    /// <summary>One icon button for each command of every menu (a submenu's is a button that opens it), a little gap between the menus.</summary>
    private void RebuildCommandBar()
    {
        if (_closed) return;

        _commandBar.Children.Clear();

        var groups = new List<List<ProvidedCommand?>> { FileItems() };
        if (OwnMenu("edit") is not null || Sorted(LabMenuPlace.Edit).Count > 0) groups.Add(EditItems());
        foreach (var own in LabMenus.SafeMenus(_session).Where(m => !IsMenu(m, "file") && !IsMenu(m, "edit")))
            groups.Add(own.Items.Select(c => c is null ? null : Wrap(c)).ToList());

        if (_model is not null)
            foreach (var aspect in LabModelRules.ActiveAspects(_model).Where(a => a.Commands.Count > 0))
                groups.Add(aspect.Commands.Select(c => (ProvidedCommand?)Wrap(c)).ToList());

        groups.Add(LabItems());
        groups.Add(SubjectItems());
        if (_model is not null)
        {
            if (_model.PartTypes.Count > 0 || _model.Parts.Count > 0) groups.Add(PartItems());
            if (_model.Tools.Count > 0) groups.Add(ToolItems());
        }

        foreach (var group in groups)
        {
            bool any = false;
            foreach (var command in group)
            {
                if (command is null)
                {
                    continue;
                }

                _commandBar.Children.Add(CommandButton(command));
                any = true;
            }

            if (any) _commandBar.Children.Add(new Border { Width = 1, Height = 18, Margin = new Thickness(5, 0, 7, 3), Opacity = 0.5 }.Also(b => b.SetResourceReference(Border.BackgroundProperty, "Theme.TextMuted")));
        }

        var panes = new Button { Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 3, 3), ToolTip = "Panes: show or hide the Lab's panes" };
        panes.Content = IconContent("Layers", true);
        panes.Click += (_, _) => DropDown(panes, FillViewMenu);
        _commandBar.Children.Add(panes);
    }

    private static UIElement IconContent(string? icon, bool dropdown = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (LabUi.Icon(icon, 15) is { } glyph) row.Children.Add(glyph);
        if (dropdown) row.Children.Add(new TextBlock { Text = "?", Margin = new Thickness(3, 0, 0, 0), FontSize = 9, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private UIElement CommandButton(ProvidedCommand command)
    {
        string header = LabTerminal.Strip(command.Header);
        string tip    = string.IsNullOrEmpty(command.ToolTip) ? header : $"{header}: {command.ToolTip}";
        if (!string.IsNullOrWhiteSpace(command.Gesture)) tip += $" ({command.Gesture})";

        if (command.Children is { Count: > 0 } children)
        {
            var drop = new Button { Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 3, 3), ToolTip = tip, Content = IconContent(LabMenus.IconFor(command), true) };
            drop.Click += (_, _) => DropDown(drop, menu => FillMenu(menu, [.. children]));
            return drop;
        }

        if (command.IsChecked is not null)
        {
            var toggle = new System.Windows.Controls.Primitives.ToggleButton
                         {
                             Padding   = new Thickness(6, 3, 6, 3),
                             Margin    = new Thickness(0, 0, 3, 3),
                             ToolTip   = tip,
                             Content   = IconContent(LabMenus.IconFor(command)),
                             IsChecked = SafeChecked(command)
                         };
            toggle.Click += (_, _) =>
                            {
                                if (command.Command.CanExecute(command.Parameter)) command.Command.Execute(command.Parameter);
                                toggle.IsChecked = SafeChecked(command);
                            };
            toggle.IsEnabled = command.Command.CanExecute(command.Parameter);
            return toggle;
        }

        return new Button
               {
                   Command          = command.Command,
                   CommandParameter = command.Parameter,
                   Padding          = new Thickness(6, 3, 6, 3),
                   Margin           = new Thickness(0, 0, 3, 3),
                   ToolTip          = tip,
                   Content          = IconContent(LabMenus.IconFor(command))
               };
    }

    private static bool SafeChecked(ProvidedCommand command)
    {
        try
        {
            return command.IsChecked?.Invoke() ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Opens a menu under a button, filled by <paramref name="fill" /> (which fills a menu item).</summary>
    private static void DropDown(Button anchor, Action<MenuItem> fill)
    {
        var holder = new MenuItem();
        fill(holder);
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var item in holder.Items.Cast<object>().ToList())
        {
            holder.Items.Remove(item);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>Brings the Part and Tool boxes in line with the model.</summary>
    private void SyncSelectors()
    {
        if (_model is null || _closed) return;

        _syncing = true;
        try
        {
            var parts = LabModelRules.AllParts(_model).Take(600).ToList();
            _partCombo.Items.Clear();
            _partCombo.Items.Add(new ComboBoxItem { Content = "(none)", Tag = null });
            var active = _model.ActivePart;
            foreach (var part in parts)
            {
                var item = new ComboBoxItem { Content = new string(' ', Math.Min(part.Depth, 8) * 2) + part.Name, Tag = part, ToolTip = part.Type.Name + (string.IsNullOrEmpty(part.Detail) ? string.Empty : ": " + part.Detail) };
                _partCombo.Items.Add(item);
                if (active is not null && part.Key == active.Key) _partCombo.SelectedItem = item;
            }

            if (_partCombo.SelectedItem is null) _partCombo.SelectedIndex = 0;
            _partCombo.Visibility = parts.Count > 0 || _model.PartTypes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_selectors.Children.Count > 1) _selectors.Children[0].Visibility = _partCombo.Visibility;

            var tools = LabModelRules.AvailableTools(_model).ToList();
            _toolCombo.Items.Clear();
            _toolCombo.Items.Add(new ComboBoxItem { Content = "(none)", Tag = null });
            var activeTool = _model.ActiveTool;
            foreach (var tool in tools)
            {
                var item = new ComboBoxItem { Content = tool.Name.Replace("_", string.Empty), Tag = tool, ToolTip = tool.Description };
                _toolCombo.Items.Add(item);
                if (ReferenceEquals(tool, activeTool)) _toolCombo.SelectedItem = item;
            }

            if (_toolCombo.SelectedItem is null) _toolCombo.SelectedIndex = 0;
            var toolVisibility = _model.Tools.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _toolCombo.Visibility = toolVisibility;
            if (_selectors.Children.Count > 3) _selectors.Children[2].Visibility = toolVisibility;
        }
        finally
        {
            _syncing = false;
        }
    }

    private UIElement BuildToolbar()
    {
        var panel = new DockPanel { Margin = new Thickness(6, 4, 6, 4), LastChildFill = true };

        DockPanel.SetDock(_contextBar, Dock.Left);
        DockPanel.SetDock(_valueBar, Dock.Right);
        panel.Children.Add(_contextBar);
        panel.Children.Add(_valueBar);

        _valueBox.ToolTip = "The value being studied. Type another and press Enter to study it (Ctrl+L)";
        _valueBox.KeyDown += async (_, e) =>
                             {
                                 if (e.Key == Key.Enter) await OpenTypedValueAsync();
                                 else if (e.Key == Key.Escape)
                                 {
                                     ShowValue();
                                     Keyboard.ClearFocus();
                                 }
                             };
        panel.Children.Add(_valueBox);
        return panel;
    }

    private async Task OpenTypedValueAsync()
    {
        string text = _valueBox.Text.Trim();
        if (text.Length == 0) return;

        try
        {
            if (!_session.TryParseValue(text, out var value))
            {
                SetOutput($"'{text}' is not something {_session.Lab.Name} can open.", true);
                return;
            }

            await _session.OpenAsync(value);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening {Text} in {Lab} failed", text, _session.Lab.Name);
            SetOutput(ex.Message, true);
        }
    }

    private UIElement BuildStatusBar()
    {
        var bar = new Border { Padding = new Thickness(8, 3, 8, 3), BorderThickness = new Thickness(0, 1, 0, 0) };
        bar.SetResourceReference(Border.BorderBrushProperty, "Theme.TextMuted");

        var dock = new DockPanel { LastChildFill = true };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        _outputText.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        DockPanel.SetDock(_statusText, Dock.Right);
        DockPanel.SetDock(_outputText, Dock.Right);

        var prompt = new TextBlock { Text = "›", FontSize = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        prompt.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        DockPanel.SetDock(prompt, Dock.Left);

        _input.ToolTip = "Terminal: help lists the commands; every action is a command too. Up and Down recall, Tab completes (Ctrl+`)";
        _input.SetResourceReference(ForegroundProperty, "Theme.Text");
        _input.PreviewKeyDown += OnTerminalKey;

        _outputText.ToolTip     = "Click to open the terminal log";
        _outputText.MouseLeftButtonUp += (_, _) => ShowLog();

        DockPanel.SetDock(_problemsButton, Dock.Right);
        _problemsButton.SetResourceReference(ForegroundProperty, "Theme.Text");
        dock.Children.Add(_problemsButton);

        DockPanel.SetDock(_recordButton, Dock.Right);
        _recordButton.SetResourceReference(ForegroundProperty, "Theme.Text");
        _recordButton.Click += (_, _) =>
                               {
                                   try
                                   {
                                       StopRecording();
                                   }
                                   catch (Exception ex)
                                   {
                                       SetOutput(ex.Message, true);
                                   }
                               };
        dock.Children.Add(_recordButton);
        dock.Children.Add(_statusText);
        dock.Children.Add(_outputText);
        dock.Children.Add(prompt);
        dock.Children.Add(_input);
        bar.Child = dock;

        _logText.SetResourceReference(BackgroundProperty, "Theme.Background");
        _logText.SetResourceReference(ForegroundProperty, "Theme.Text");
        _logPopup.Child = new Border { Width = 760, Height = 280, BorderThickness = new Thickness(1), Child = _logText };
        _logPopup.PlacementTarget = bar;
        return bar;
    }

    // ---- actions and toolbar -----------------------------------------------------------------

    /// <summary>Rebuilds the toolbar buttons and the keys of the actions (the context ones change with what is going on).</summary>
    private void RebuildActions()
    {
        if (_closed) return;

        foreach (var binding in _bindings) InputBindings.Remove(binding);
        _bindings.Clear();

        FillBar(_contextBar, SafeActions(() => _session.ContextActions).Select(Wrap).ToList(), 4);
        FillBar(_valueBar,   SafeActions(() => _session.ValueActions).Select(Wrap).ToList(),   0);

        foreach (var action in LabMenus.All(_session))
        {
            if (string.IsNullOrWhiteSpace(action.Gesture)) continue;

            try
            {
                if (new KeyGestureConverter().ConvertFromString(action.Gesture) is KeyGesture gesture)
                {
                    var binding = new KeyBinding(action.Command, gesture) { CommandParameter = action.Parameter };
                    InputBindings.Add(binding);
                    _bindings.Add(binding);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "The gesture {Gesture} of action {Id} is not a key", action.Gesture, action.Id);
            }
        }
    }

    private static List<ProvidedCommand> SafeActions(Func<IEnumerable<ProvidedCommand>> get)
    {
        try
        {
            return get().ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Asking a Lab for its actions failed");
            return [];
        }
    }

    private static void FillBar(StackPanel bar, IEnumerable<ProvidedCommand> actions, double gap)
    {
        bar.Children.Clear();
        foreach (var action in actions)
        {
            // only the ones with an icon or a short header earn a button; the menu has them all
            string header = LabTerminal.Strip(action.Header);
            var button = new Button
                         {
                             Command          = action.Command,
                             CommandParameter = action.Parameter,
                             Padding          = new Thickness(7, 3, 8, 3),
                             Margin           = new Thickness(0, 0, 3, 0),
                             MinWidth         = 28,
                             ToolTip          = string.IsNullOrEmpty(action.ToolTip) ? header : $"{header}: {action.ToolTip}"
                         };

            // every button has an icon and its text, never one without the other
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (LabUi.Icon(LabMenus.IconFor(action), 14) is { } glyph)
            {
                glyph.Margin = new Thickness(0, 0, 5, 0);
                content.Children.Add(glyph);
            }

            content.Children.Add(new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center });
            button.Content = content;
            bar.Children.Add(button);
        }

        bar.Margin = new Thickness(0, 0, gap, 0);
    }

    // ---- the value and the title -------------------------------------------------------------

    private void ShowValue()
    {
        if (_closed) return;

        if (!_valueBox.IsKeyboardFocused) _valueBox.Text = _session.ValueText;
        Title = _session.Title;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
                               {
                                   if (_closed) return;

                                   if (e.PropertyName == nameof(ILabSession.Title)) Title = _session.Title;
                                   else if (e.PropertyName == nameof(ILabSession.Status)) _statusText.Text = _session.Status;
                               });
    }

    private void OnParametersChanged(LabParameter? changed)
    {
        // the session listens for itself; the window only keeps its toolbar honest (a parameter can decide which actions there are)
        Dispatcher.InvokeAsync(RebuildActions);
    }

    // ---- the model: what applies now ----------------------------------------------------------

    private bool Applies(LabInspector inspector) => _model is null || LabModelRules.Applies(_model, inspector);

    /// <summary>The parameter panes: the general one, and one for each aspect and tool that has parameters or commands of its own.</summary>
    private void AddParameterPanes()
    {
        if (_model is null) return;

        _inspectors[LabParametersInspector.InspectorId] = new LabParametersInspector(_model);

        foreach (var aspect in _model.Aspects.Where(a => a.Parameters is { Count: > 0 } || a.Commands.Count > 0))
        {
            // the settings of an aspect of a part, a subject type or a tool only matter while that is at hand; the others are always there
            var type = aspect.Owner.Kind is LabOwnerKind.SubjectType or LabOwnerKind.PartType or LabOwnerKind.Tool ? LabPaneType.Instance : LabPaneType.Static;
            string id = "aspect:" + aspect.Id;
            _inspectors[id] = new LabParametersInspector(_model, id, aspect.Title + " parameters", aspect.IconKey ?? "Settings", LabOwner.OfAspect(aspect.Id), type);
        }

        foreach (var tool in _model.Tools.Where(t => t.Parameters is { Count: > 0 } || t.Commands.Count > 0))
        {
            string id = "tool:" + tool.Id;
            _inspectors[id] = new LabParametersInspector(_model, id, tool.Name.Replace("_", string.Empty) + " parameters", tool.IconKey ?? "Settings", LabOwner.OfTool(tool.Id), LabPaneType.Instance);
        }
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (_closed || _modelPending) return;

        _modelPending = true;
        Dispatcher.InvokeAsync(() =>
                               {
                                   _modelPending = false;
                                   if (!_closed) RefreshModelUi();
                               }, DispatcherPriority.Background);
    }

    /// <summary>The part or tool or the parts changed: the selectors, the command bar, the toolbar, the panes and the aspect menus follow.</summary>
    private void RefreshModelUi()
    {
        if (_closed || _model is null) return;

        SyncSelectors();
        RebuildCommandBar();
        RebuildActions();
        ApplyApplicability();
        PokeLibrary();
        foreach (var pane in _shared.OfType<LabPresetsInspector>()) pane.Poke();

        foreach (var (aspect, item) in _aspectMenus)
            item.Visibility = LabModelRules.IsActive(_model, aspect.Owner) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Closes the instance panes whose owner stopped applying (remembering to open them again) and opens them again when it applies.</summary>
    private void ApplyApplicability()
    {
        if (_model is null || !IsLoaded) return;

        foreach (var inspector in _inspectors.Values.ToList())
        {
            if (inspector.PaneType == LabPaneType.Static) continue;

            string id      = inspector.Id;
            bool   applies = Applies(inspector);
            bool   open    = IsInspectorOpen(id);
            if (!applies && open)
            {
                _autoHidden.Add(id);
                if (_panes.TryGetValue(id, out var pane))
                {
                    pane.Hide();
                    SetShown(inspector, false);
                }
            }
            else if (applies && _autoHidden.Contains(id))
            {
                ShowInspector(id, false);
            }
        }
    }

    // ---- inspectors and the dock layout ------------------------------------------------------

    private void ApplyDockTheme()
    {
        _dock.Theme = AppTheme.Instance.IsEffectivelyDark ? new Vs2013DarkTheme() : new Vs2013LightTheme();
    }

    private void OnThemeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppTheme.IsEffectivelyDark)) Dispatcher.InvokeAsync(ApplyDockTheme);
    }

    /// <summary>How many lanes (columns, or sections along the bottom) the visible-by-default inspectors have on a side.</summary>
    private int LaneCount(LabDock dock) => _inspectors.Values.Where(i => i.Dock == dock && i.VisibleByDefault).Select(i => i.Lane).Distinct().Count();

    /// <summary>How wide the window needs to be for every lane to have a column of its own.</summary>
    private double WidthForLanes() => (LaneCount(LabDock.Left) + LaneCount(LabDock.Right)) * LaneWidth + MainMinWidth;

    /// <summary>A Lab with several lanes opens wider (as far as the screen allows) so that they can sit side by side.</summary>
    private void EnsureWidth()
    {
        double need = WidthForLanes();
        if (LaneCount(LabDock.Left) + LaneCount(LabDock.Right) <= 2) return;

        double room = SystemParameters.WorkArea.Width - 24;
        if (Width < need) Width = Math.Min(need + 40, room);
    }

    /// <summary>The lane a pane is in now: its own when the window is wide enough for the lanes, else all of a side share one.</summary>
    private int LaneOf(LabInspector inspector) => _lanes ? inspector.Lane : 0;

    /// <summary>
    ///     The arrangement of a new window: the main view in the middle; the left and right inspectors in a column per lane (all of a
    ///     side tabbed together when the window is too narrow for that); the bottom ones side by side under the main view.
    /// </summary>
    private void BuildDefaultLayout()
    {
        foreach (var pane in _panes.Values) pane.Content = null;
        _panes.Clear();
        foreach (var document in _dock.Layout.Descendents().OfType<LayoutDocument>()) document.Content = null;

        _lanes = Width >= WidthForLanes() - 1;

        var main = new LayoutDocument { Title = _session.Lab.Name, ContentId = MainId, Content = _main, CanClose = false, CanFloat = false };
        var docs = new LayoutDocumentPane();
        docs.Children.Add(main);

        // a pane that is open by default but does not apply right now (an instance pane of another part type) waits until it does
        var shown = _inspectors.Values.Where(i => i.VisibleByDefault && Applies(i)).ToList();
        _autoHidden.Clear();
        foreach (var waiting in _inspectors.Values.Where(i => i.VisibleByDefault && !Applies(i))) _autoHidden.Add(waiting.Id);

        List<LayoutAnchorablePane> Panes(LabDock dock, double single, double many)
        {
            var groups = shown.Where(i => i.Dock == dock).GroupBy(LaneOf).OrderBy(g => g.Key).ToList();
            var panes  = new List<LayoutAnchorablePane>();
            foreach (var group in groups)
            {
                var pane = new LayoutAnchorablePane();
                if (dock == LabDock.Bottom) pane.DockWidth = new GridLength(1, GridUnitType.Star);
                else pane.DockWidth = new GridLength(groups.Count > 1 ? many : single);
                foreach (var inspector in group) pane.Children.Add(MakePane(inspector));
                panes.Add(pane);
            }

            return panes;
        }

        var left   = Panes(LabDock.Left, 300, LaneWidth);
        var right  = Panes(LabDock.Right, 380, LaneWidth + 30);
        var bottom = Panes(LabDock.Bottom, 0, 0);

        // the middle: the main view, with the bottom panes side by side under it
        ILayoutPanelElement middle = docs;
        if (bottom.Count > 0)
        {
            var below = new LayoutPanel { Orientation = Orientation.Horizontal, DockHeight = new GridLength(BottomHeight) };
            foreach (var pane in bottom) below.Children.Add(pane);

            var column = new LayoutPanel { Orientation = Orientation.Vertical };
            column.Children.Add(docs);
            column.Children.Add(below);
            middle = column;
        }

        var panel = new LayoutPanel { Orientation = Orientation.Horizontal };
        foreach (var pane in left) panel.Children.Add(pane);
        panel.Children.Add(middle);
        foreach (var pane in right) panel.Children.Add(pane);

        _dock.Layout = new LayoutRoot { RootPanel = panel };
    }

    private LayoutAnchorable MakePane(LabInspector inspector)
    {
        var pane = new LayoutAnchorable
                   {
                       Title          = inspector.Title,
                       ContentId      = InspectorPrefix + inspector.Id,
                       Content        = inspector.View,
                       CanFloat       = true,
                       FloatingWidth  = 460,
                       FloatingHeight = 560
                   };

        HookPane(pane, inspector);
        _panes[inspector.Id] = pane;
        return pane;
    }

    private void HookPane(LayoutAnchorable pane, LabInspector inspector)
    {
        // closing a pane with its own X must tell the inspector (and the View menu reads the state when it opens)
        pane.PropertyChanged += (_, e) =>
                                {
                                    if (e.PropertyName is "IsVisible" or "Parent") Dispatcher.InvokeAsync(() => SetShown(inspector, IsOpen(pane)));
                                };
    }

    private static bool IsOpen(LayoutAnchorable pane) => pane.Root is not null && pane.IsVisible;

    private static void SetShown(LabInspector inspector, bool shown)
    {
        if (inspector.IsShown == shown) return;

        inspector.IsShown = shown;
        try
        {
            if (shown) inspector.OnShown();
            else inspector.OnHidden();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Inspector {Id} failed", inspector.Id);
        }
    }

    /// <summary>Tells each inspector whether its pane is open (once the window is up, and after a layout is put back).</summary>
    private void SyncShown()
    {
        foreach (var (id, inspector) in _inspectors)
            SetShown(inspector, _panes.TryGetValue(id, out var pane) && IsOpen(pane));
    }

    internal bool IsInspectorOpen(string id) => _panes.TryGetValue(id, out var pane) && IsOpen(pane);

    public void ShowInspector(string id) => ShowInspector(id, true);

    /// <summary>Opens a pane; one that does not apply now (an instance pane whose owner is not active) is opened as soon as it does. <paramref name="activate" />: also bring it to the front.</summary>
    internal void ShowInspector(string id, bool activate)
    {
        if (!_inspectors.TryGetValue(id, out var inspector)) return;

        if (!Applies(inspector))
        {
            _autoHidden.Add(id);
            return;
        }

        _autoHidden.Remove(id);
        if (!_panes.TryGetValue(id, out var pane)) pane = MakePane(inspector);

        if (pane.Root is null)
        {
            // tab it with an open inspector of the same side, else give it a column of its own
            var sibling = _inspectors.Values.Where(i => i.Dock == inspector.Dock && LaneOf(i) == LaneOf(inspector) && !ReferenceEquals(i, inspector))
                                     .Select(i => _panes.GetValueOrDefault(i.Id))
                                     .FirstOrDefault(p => p is { Root: not null } && p.Parent is LayoutAnchorablePane);

            if (sibling?.Parent is LayoutAnchorablePane group)
            {
                group.Children.Add(pane);
            }
            else
            {
                pane.AddToLayout(_dock, inspector.Dock switch { LabDock.Left => AnchorableShowStrategy.Left, LabDock.Bottom => AnchorableShowStrategy.Bottom, _ => AnchorableShowStrategy.Right });
                if (pane.Parent is LayoutAnchorablePane own)
                {
                    if (inspector.Dock == LabDock.Bottom) own.DockHeight = new GridLength(BottomHeight);
                    else own.DockWidth = new GridLength(inspector.Dock == LabDock.Left ? 300 : 380);
                }
            }
        }
        else if (!pane.IsVisible)
        {
            pane.Show();
        }

        if (activate)
        {
            pane.IsSelected = true;
            pane.IsActive   = true;
        }

        SetShown(inspector, true);
    }

    internal void HideInspector(string id)
    {
        _autoHidden.Remove(id);
        if (_panes.TryGetValue(id, out var pane) && IsOpen(pane))
        {
            pane.Hide();
            if (_inspectors.TryGetValue(id, out var inspector)) SetShown(inspector, false);
        }
    }

    /// <summary>Puts the panes back where a new window has them.</summary>
    internal void ResetLayout()
    {
        BuildDefaultLayout();
        SyncShown();
    }

    // ---- IWindowLayout -----------------------------------------------------------------------

    public string? CaptureLayout()
    {
        try
        {
            var xml = new StringWriter();
            new XmlLayoutSerializer(_dock).Serialize(xml);
            return xml.ToString();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not capture the layout of a Lab window");
            return null;
        }
    }

    public void RestoreLayout(string layout)
    {
        // the views are handed over to the panes of the new layout: let go of them in the old one
        foreach (var pane in _panes.Values) pane.Content = null;
        foreach (var document in _dock.Layout.Descendents().OfType<LayoutDocument>()) document.Content = null;

        _lanes = Width >= WidthForLanes() - 1;
        var placed = new Dictionary<string, LayoutAnchorable>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var serializer = new XmlLayoutSerializer(_dock);
            serializer.LayoutSerializationCallback += (_, e) =>
                                                      {
                                                          switch (e.Model)
                                                          {
                                                              case LayoutAnchorable { ContentId: { } id } anchorable
                                                                  when id.StartsWith(InspectorPrefix, StringComparison.Ordinal)
                                                                       && _inspectors.TryGetValue(id[InspectorPrefix.Length..], out var inspector):
                                                                  e.Content        = inspector.View;
                                                                  placed[inspector.Id] = anchorable;
                                                                  break;
                                                              case LayoutDocument { ContentId: MainId }:
                                                                  e.Content = _main;
                                                                  break;
                                                              default:
                                                                  e.Cancel = true; // an inspector this version does not have
                                                                  break;
                                                          }
                                                      };

            using var reader = new StringReader(layout);
            serializer.Deserialize(reader);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not put back the layout of a Lab window; using the default");
            BuildDefaultLayout();
            return;
        }

        _panes.Clear();
        foreach (var (id, anchorable) in placed)
        {
            _panes[id] = anchorable;
            HookPane(anchorable, _inspectors[id]);
        }

        if (IsLoaded) SyncShown();
    }

    // ---- terminal ----------------------------------------------------------------------------

    private async void OnTerminalKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                string line = _input.Text.Trim();
                _input.Clear();
                _completionBase = null;
                _lastCompletion = null;
                if (line.Length > 0)
                {
                    if (_history.Count == 0 || _history[^1] != line) _history.Add(line);
                    _historyIndex = _history.Count;
                    await RunAsync(line);
                }

                break;

            case Key.Up when _history.Count > 0:
                e.Handled     = true;
                _historyIndex = Math.Max(0, _historyIndex - 1);
                _input.Text   = _history[_historyIndex];
                _input.CaretIndex = _input.Text.Length;
                break;

            case Key.Down when _history.Count > 0:
                e.Handled     = true;
                _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                _input.Text   = _historyIndex >= _history.Count ? string.Empty : _history[_historyIndex];
                _input.CaretIndex = _input.Text.Length;
                break;

            case Key.Tab:
                e.Handled = true;
                Complete();
                break;

            case Key.Escape:
                e.Handled = true;
                _input.Clear();
                Keyboard.ClearFocus();
                break;
        }
    }

    private void Complete()
    {
        // only the first word completes; repeating Tab cycles through the matches
        string text = _input.Text;
        if (text.Contains(' ')) return;

        if (_lastCompletion is null || text != _lastCompletion)
        {
            _completionBase  = text;
            _completionIndex = -1;
        }

        var matches = _terminal.Completions(_completionBase ?? string.Empty);
        if (matches.Count == 0) return;

        _completionIndex = (_completionIndex + 1) % matches.Count;
        _lastCompletion  = matches[_completionIndex];
        _input.Text      = _lastCompletion;
        _input.CaretIndex = _input.Text.Length;
    }

    private async Task RunAsync(string line)
    {
        Print("> " + line);
        if (!line.StartsWith("macro", StringComparison.OrdinalIgnoreCase)) NoteLine(line);
        try
        {
            string? result = await _terminal.ExecuteAsync(line);
            if (!string.IsNullOrEmpty(result))
            {
                Print(result);
                SetOutput(result.Split('\n')[0].TrimEnd('\r'), false);
                if (result.Contains('\n')) ShowLog();
            }
            else
            {
                SetOutput("ok", false);
            }
        }
        catch (Exception ex)
        {
            Print("error: " + ex.Message);
            SetOutput(ex.Message, true);
            (_session as ILabProblems)?.ReportProblem(LabProblemSeverity.Error, "Terminal", ex.Message, line);
        }
    }

    private void SetOutput(string text, bool error)
    {
        _outputText.Text = text;
        if (error) _outputText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
        else _outputText.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
    }

    private void Print(string text)
    {
        foreach (string line in text.Replace("\r", string.Empty).Split('\n'))
        {
            _log.Enqueue(line);
            while (_log.Count > LogLimit) _log.Dequeue();
        }

        _logText.Text = string.Join(Environment.NewLine, _log);
        _logText.ScrollToEnd();
    }

    internal void ClearLog()
    {
        _log.Clear();
        _logText.Clear();
        SetOutput(string.Empty, false);
    }

    internal void ShowLog()
    {
        _logPopup.IsOpen = true;
        _logText.ScrollToEnd();
    }

    // ---- closing -----------------------------------------------------------------------------

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        if (_model is not null) _model.ModelChanged -= OnModelChanged;
        _session.Lab.Parameters.Changed -= OnParametersChanged;
        AppTheme.Instance.PropertyChanged -= OnThemeChanged;
        _logPopup.IsOpen = false;

        try
        {
            _session.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Closing a Lab session failed");
        }
    }
}

/// <summary>A small window that shows text and nothing else: a page's source, a long result.</summary>
public sealed class LabTextWindow : Window
{
    public LabTextWindow(string title, string text)
    {
        Title  = title;
        Width  = 900;
        Height = 640;
        SetResourceReference(BackgroundProperty, "Theme.Background");

        var box = new TextBox
                  {
                      Text                          = text,
                      IsReadOnly                    = true,
                      FontFamily                    = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                      VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                      HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                      TextWrapping                  = TextWrapping.NoWrap,
                      BorderThickness               = new Thickness(0),
                      Padding                       = new Thickness(8)
                  };
        box.SetResourceReference(BackgroundProperty, "Theme.Background");
        box.SetResourceReference(ForegroundProperty, "Theme.Text");
        Content = box;
    }

    /// <summary>Shows the text in a window of its own.</summary>
    public static void ShowText(string title, string text)
    {
        new LabTextWindow(title, text).Show();
    }
}
