using System.ComponentModel;
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
///     The window every Lab shares: a menu (the Lab's global actions, the value's actions, the view), a toolbar (the
///     actions of the moment,
///     the value box), the Lab's main view in the middle with its inspectors docked left and right, and a status bar with
///     a terminal.
///     What is specific to the kind of value comes from the <see cref="ILabSession" />.
/// </summary>
/// <remarks>
///     The window's place, size and the arrangement of its panes are kept in the Silo with the other windows (see
///     <see cref="WindowPlacements" />), one arrangement per Lab: the next window of the Lab opens just so.
/// </remarks>
public sealed class LabWindow : Window, IWindowLayout, ILabHost
{
    /// <summary>The kind of window it is for <see cref="WindowPlacements" />.</summary>
    public const string PlacementType = "Lab";

    private const string InspectorPrefix = "inspector:";
    private const string MainId          = "main";
    private const int    LogLimit        = 500;
    private const double LaneWidth       = 270;
    private const double MainMinWidth    = 520;
    private const double BottomHeight    = 250;

    private readonly List<KeyBinding> _bindings   = [];
    private readonly StackPanel       _contextBar = new() { Orientation = Orientation.Horizontal };

    private readonly DockingManager _dock    = new();
    private readonly List<string>   _history = [];

    private readonly TextBox _input = new()
                                      {
                                          BorderThickness          = new Thickness(0), Background = Brushes.Transparent,
                                          VerticalContentAlignment = VerticalAlignment.Center,
                                          FontFamily               = new FontFamily("Cascadia Mono, Consolas, Courier New")
                                      };

    private readonly Dictionary<string, LabInspector> _inspectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _log = new();
    private readonly Popup _logPopup = new() { StaysOpen = false, Placement = PlacementMode.Top, AllowsTransparency = false };

    private readonly TextBox _logText = new()
                                        {
                                            IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                                            TextWrapping = TextWrapping.NoWrap
                                        };

    private readonly TextBlock _outputText = new()
                                             {
                                                 VerticalAlignment = VerticalAlignment.Center,
                                                 TextTrimming      = TextTrimming.CharacterEllipsis, MaxWidth = 460,
                                                 Margin            = new Thickness(8, 0, 8, 0), Cursor        = Cursors.Hand
                                             };

    private readonly Dictionary<string, LayoutAnchorable> _panes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Button _problemsButton = new()
                                              {
                                                  Visibility      = Visibility.Collapsed, Background = Brushes.Transparent,
                                                  BorderThickness = new Thickness(0), Padding        = new Thickness(8, 0, 8, 0),
                                                  Cursor          = Cursors.Hand
                                              };

    private readonly List<SharedPane> _shared = [];

    private readonly TextBlock _statusText = new()
                                             {
                                                 VerticalAlignment = VerticalAlignment.Center,
                                                 TextTrimming      = TextTrimming.CharacterEllipsis, MaxWidth = 380
                                             };

    private readonly LabTerminal _terminal;
    private readonly StackPanel  _valueBar = new() { Orientation = Orientation.Horizontal };

    private readonly TextBox _valueBox = new()
                                         {
                                             VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0),
                                             MinHeight                = 26
                                         };

    private          bool      _closed;
    private          string?   _completionBase;
    private          int       _completionIndex;
    private          int       _historyIndex;
    private          bool      _lanes;
    private          string?   _lastCompletion;
    private readonly UIElement _main = null!;

    private          ILabProblems? _problems;
    private          bool          _problemsAutoShown;
    private          bool          _problemsPending;
    private          bool          _restoredLayout;
    private readonly UIElement?    _topBar;

    /// <summary>What the session is called by; the window title follows it.</summary>
    public ILabSession Session { get; }

    internal IEnumerable<string> InspectorIds => _inspectors.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

    Window ILabHost.Window => this;

    // ---- the panes every window has ---------------------------------------------------------------

    private void AddLibrary()
    {
        if (Session is not ILabLibrary library) return;

        var pane = new LabLibraryInspector(library);
        _shared.Add(pane);
        _inspectors[pane.Id] = pane;
    }

    private void AddCommonPanes()
    {
        var recent = new LabRecentInspector(Session, OpenRecent);
        _shared.Add(recent);
        _inspectors[recent.Id] = recent;

        if (Session is ILabPresets { PresetKinds.Count: > 0 } presets)
        {
            var pane = new LabPresetsInspector(Session, presets);
            _shared.Add(pane);
            _inspectors[pane.Id] = pane;
        }

        if (Session is ILabProblems problems)
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
            if (Session is ILabRecent recent) await recent.OpenRecentAsync(text);
            else if (Session.TryParseValue(text, out var value)) await Session.OpenAsync(value);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening {Text} again in {Lab} failed", text, Session.Lab.Name);
            SetOutput(ex.Message, true);
            (Session as ILabProblems)?.ReportProblem(LabProblemSeverity.Error, "Recent", ex.Message, text);
        }
    }

    // ---- problems -------------------------------------------------------------------------------

    private void WireProblems()
    {
        if (Session is not ILabProblems problems) return;

        _problems                =  problems;
        problems.ProblemsChanged += (_, _) => Dispatcher.InvokeAsync(UpdateProblems);

        // a Lab that checks its value as it is edited says it changed
        if (Session is ILabLive live) live.Changed += (_, _) => Dispatcher.InvokeAsync(UpdateProblems);
        _problemsButton.Click += (_, _) => ShowInspector(LabProblemsInspector.InspectorId);
    }

    /// <summary>
    ///     Counts the problems for the status bar and the pane; the pane opens the first time there is one (and stays
    ///     closed if the person closes it).
    /// </summary>
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
                                   var text = new TextBlock
                                              {
                                                  Text   = list.Count.ToString(), VerticalAlignment = VerticalAlignment.Center,
                                                  Margin = new Thickness(4, 0, 0, 0)
                                              };
                                   var row = new StackPanel { Orientation = Orientation.Horizontal };
                                   if (LabUi.Icon(errors > 0 ? "StateFailed" : "Check", 13) is { } glyph)
                                   {
                                       glyph.Foreground = errors > 0 ? LabUi.Red : LabUi.Amber;
                                       row.Children.Add(glyph);
                                   }

                                   row.Children.Add(text);
                                   _problemsButton.Content = row;
                                   _problemsButton.ToolTip =
                                       list.Count == 0
                                           ? null
                                           : $"{errors} error(s), {warnings} warning(s), {list.Count - errors - warnings} note(s): click to see them";

                                   if (list.Count > 0 && !_problemsAutoShown && IsLoaded)
                                   {
                                       _problemsAutoShown = true;
                                       ShowInspector(LabProblemsInspector.InspectorId);
                                   }

                                   if (_inspectors.TryGetValue(LabProblemsInspector.InspectorId, out var pane) && pane is SharedPane shared)
                                       shared.Poke();
                               }, DispatcherPriority.Background);
    }

    // ---- building ---------------------------------------------------------------------------

    private UIElement BuildUi()
    {
        var root = new Grid();
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

        var bar = BuildToolbar();
        Grid.SetRow(bar, 2);
        root.Children.Add(bar);

        Grid.SetRow(_dock, 3);
        root.Children.Add(_dock);

        var status = BuildStatusBar();
        Grid.SetRow(status, 4);
        root.Children.Add(status);

        InputBindings.Add(new KeyBinding(new DelegateCommand(_ =>
                                                             {
                                                                 _valueBox.Focus();
                                                                 _valueBox.SelectAll();
                                                             }), Key.L, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new DelegateCommand(_ => _input.Focus()), Key.OemTilde, ModifierKeys.Control));
        return root;
    }

    private Menu BuildMenu()
    {
        var menu = new Menu();

        // an empty menu item cannot open, so each holds a stand-in that is replaced when it opens
        AddMenu(menu, "_File", FillFileMenu);
        if (OwnMenu("edit") is not null || Sorted(LabMenuPlace.Edit).Count > 0) AddMenu(menu, "_Edit", FillEditMenu);
        foreach (var own in LabMenus.SafeMenus(Session).Where(m => !IsMenu(m, "file") && !IsMenu(m, "edit")))
        {
            var captured = own;
            AddMenu(menu, captured.Header, item => LabMenus.Fill(item, captured.Items));
        }

        AddMenu(menu, "_Lab", FillLabMenu);
        // a Lab whose value actions all live in its own menus (the Parser Lab) has nothing for a Value menu
        if (Remaining(LabMenus.Safe(() => Session.ValueActions).OfType<ProvidedCommand>()).Any()) AddMenu(menu, "_Value", FillValueMenu);
        AddMenu(menu, "Vie_w", FillViewMenu);
        return menu;

        static void AddMenu(Menu parent, string header, Action<MenuItem> fill)
        {
            var item = new MenuItem { Header     = header };
            item.Items.Add(new MenuItem { Header = "...", IsEnabled = false });
            item.SubmenuOpened += (_, e) =>
                                  {
                                      if (!ReferenceEquals(e.OriginalSource, item)) return;

                                      item.Items.Clear();
                                      fill(item);
                                  };
            parent.Items.Add(item);
        }
    }

    private static bool IsMenu(LabMenu menu, string name)
    {
        return string.Equals(menu.Header.Replace("_", string.Empty), name, StringComparison.OrdinalIgnoreCase);
    }

    private LabMenu? OwnMenu(string name)
    {
        return LabMenus.SafeMenus(Session).FirstOrDefault(m => IsMenu(m, name));
    }

    /// <summary>
    ///     The global and value actions that go in this menu by what they are, in order with a separator between the
    ///     kinds (when the Lab makes no menu of its own for it).
    /// </summary>
    private List<ProvidedCommand?> Sorted(LabMenuPlace place)
    {
        var all = LabMenus.Safe(() => Session.GlobalActions).Concat(LabMenus.Safe(() => Session.ValueActions)).OfType<ProvidedCommand>();
        return LabMenus.Ranked(all.Select(c => (Command: c, Class: LabMenus.Classify(c)))
                                  .Where(t => t.Class.Place == place)
                                  .Select(t => (t.Command, t.Class.Rank)));
    }

    /// <summary>The actions of the Lab menu or the Value menu: the ones the File and Edit menus did not take.</summary>
    private IEnumerable<ProvidedCommand> Remaining(IEnumerable<ProvidedCommand> actions)
    {
        bool ownFile = OwnMenu("file") is not null;
        bool ownEdit = OwnMenu("edit") is not null;
        var inMenus = new HashSet<string>(LabMenus.SafeMenus(Session).SelectMany(m => m.Items).OfType<ProvidedCommand>().Select(c => c.Id),
                                          StringComparer.OrdinalIgnoreCase);
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

    private void FillFileMenu(MenuItem menu)
    {
        LabMenus.Fill(menu, OwnMenu("file")?.Items ?? Sorted(LabMenuPlace.File));
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        var newWindow = new MenuItem { Header = $"_New {Session.Lab.Name} Window", Icon = LabUi.Icon("TbNew") };
        newWindow.Click += (_, _) => LabHost.Open(Session.Lab);
        menu.Items.Add(newWindow);

        menu.Items.Add(new Separator());
        var close = new MenuItem { Header = "_Close", Icon = LabUi.Icon("TbCloseSilo") };
        close.Click += (_, _) => Close();
        menu.Items.Add(close);
    }

    private void FillEditMenu(MenuItem menu)
    {
        LabMenus.Fill(menu, OwnMenu("edit")?.Items ?? Sorted(LabMenuPlace.Edit));
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(nothing to edit)", IsEnabled = false });
    }

    private void FillLabMenu(MenuItem menu)
    {
        AddCommands(menu, Remaining(LabMenus.Safe(() => Session.GlobalActions).OfType<ProvidedCommand>()));
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(no actions)", IsEnabled = false });
    }

    private void FillValueMenu(MenuItem menu)
    {
        AddCommands(menu, Remaining(LabMenus.Safe(() => Session.ValueActions).OfType<ProvidedCommand>()));
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(no actions)", IsEnabled = false });
    }

    private void FillViewMenu(MenuItem menu)
    {
        // the panes of the things the Lab works with and keeps come first, then (after a line) the views of what is going on
        int problems = _problems?.ProblemList.Count ?? 0;
        var listed = _inspectors.Values.Where(i => i.Id != LabProblemsInspector.InspectorId || problems > 0 || IsInspectorOpen(i.Id))
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
        var reset = new MenuItem
                    {
                        Header = "_Reset Layout", ToolTip = "Put the panes back where a new window has them",
                        Icon   = LabUi.Icon("Refresh")
                    };
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

    private UIElement BuildToolbar()
    {
        var panel = new DockPanel { Margin = new Thickness(6, 4, 6, 4), LastChildFill = true };

        DockPanel.SetDock(_contextBar, Dock.Left);
        DockPanel.SetDock(_valueBar,   Dock.Right);
        panel.Children.Add(_contextBar);
        panel.Children.Add(_valueBar);

        _valueBox.ToolTip = "The value being studied. Type another and press Enter to study it (Ctrl+L)";
        _valueBox.KeyDown += async (_, e) =>
                             {
                                 if (e.Key == Key.Enter)
                                 {
                                     await OpenTypedValueAsync();
                                 }
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
            if (!Session.TryParseValue(text, out var value))
            {
                SetOutput($"'{text}' is not something {Session.Lab.Name} can open.", true);
                return;
            }

            await Session.OpenAsync(value);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening {Text} in {Lab} failed", text, Session.Lab.Name);
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

        var prompt = new TextBlock
                     {
                         Text              = "›", FontSize = 16, Margin = new Thickness(0, 0, 6, 0),
                         VerticalAlignment = VerticalAlignment.Center
                     };
        prompt.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        DockPanel.SetDock(prompt, Dock.Left);

        _input.ToolTip = "Terminal: help lists the commands; every action is a command too. Up and Down recall, Tab completes (Ctrl+`)";
        _input.SetResourceReference(ForegroundProperty, "Theme.Text");
        _input.PreviewKeyDown += OnTerminalKey;

        _outputText.ToolTip           =  "Click to open the terminal log";
        _outputText.MouseLeftButtonUp += (_, _) => ShowLog();

        DockPanel.SetDock(_problemsButton, Dock.Right);
        _problemsButton.SetResourceReference(ForegroundProperty, "Theme.Text");
        dock.Children.Add(_problemsButton);
        dock.Children.Add(_statusText);
        dock.Children.Add(_outputText);
        dock.Children.Add(prompt);
        dock.Children.Add(_input);
        bar.Child = dock;

        _logText.SetResourceReference(BackgroundProperty, "Theme.Background");
        _logText.SetResourceReference(ForegroundProperty, "Theme.Text");
        _logPopup.Child           = new Border { Width = 760, Height = 280, BorderThickness = new Thickness(1), Child = _logText };
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

        FillBar(_contextBar, SafeActions(() => Session.ContextActions), 4);
        FillBar(_valueBar,   SafeActions(() => Session.ValueActions),   0);

        foreach (var action in LabMenus.All(Session))
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

        if (!_valueBox.IsKeyboardFocused) _valueBox.Text = Session.ValueText;
        Title = Session.Title;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
                               {
                                   if (_closed) return;

                                   if (e.PropertyName      == nameof(ILabSession.Title)) Title             = Session.Title;
                                   else if (e.PropertyName == nameof(ILabSession.Status)) _statusText.Text = Session.Status;
                               });
    }

    private void OnParametersChanged(LabParameter? changed)
    {
        // the session listens for itself; the window only keeps its toolbar honest (a parameter can decide which actions there are)
        Dispatcher.InvokeAsync(RebuildActions);
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
    private int LaneCount(LabDock dock)
    {
        return _inspectors.Values.Where(i => i.Dock == dock && i.VisibleByDefault).Select(i => i.Lane).Distinct().Count();
    }

    /// <summary>How wide the window needs to be for every lane to have a column of its own.</summary>
    private double WidthForLanes()
    {
        return (LaneCount(LabDock.Left) + LaneCount(LabDock.Right)) * LaneWidth + MainMinWidth;
    }

    /// <summary>A Lab with several lanes opens wider (as far as the screen allows) so that they can sit side by side.</summary>
    private void EnsureWidth()
    {
        double need = WidthForLanes();
        if (LaneCount(LabDock.Left) + LaneCount(LabDock.Right) <= 2) return;

        double room             = SystemParameters.WorkArea.Width - 24;
        if (Width < need) Width = Math.Min(need + 40, room);
    }

    /// <summary>The lane a pane is in now: its own when the window is wide enough for the lanes, else all of a side share one.</summary>
    private int LaneOf(LabInspector inspector)
    {
        return _lanes ? inspector.Lane : 0;
    }

    /// <summary>
    ///     The arrangement of a new window: the main view in the middle; the left and right inspectors in a column per lane
    ///     (all of a
    ///     side tabbed together when the window is too narrow for that); the bottom ones side by side under the main view.
    /// </summary>
    private void BuildDefaultLayout()
    {
        foreach (var pane in _panes.Values) pane.Content = null;
        _panes.Clear();
        foreach (var document in _dock.Layout.Descendents().OfType<LayoutDocument>()) document.Content = null;

        _lanes = Width >= WidthForLanes() - 1;

        var main = new LayoutDocument { Title = Session.Lab.Name, ContentId = MainId, Content = _main, CanClose = false, CanFloat = false };
        var docs = new LayoutDocumentPane();
        docs.Children.Add(main);

        var shown = _inspectors.Values.Where(i => i.VisibleByDefault).ToList();

        List<LayoutAnchorablePane> Panes(LabDock dock, double single, double many)
        {
            var groups = shown.Where(i => i.Dock == dock).GroupBy(LaneOf).OrderBy(g => g.Key).ToList();
            var panes  = new List<LayoutAnchorablePane>();
            foreach (var group in groups)
            {
                var pane = new LayoutAnchorablePane();
                if (dock == LabDock.Bottom) pane.DockWidth = new GridLength(1, GridUnitType.Star);
                else pane.DockWidth                        = new GridLength(groups.Count > 1 ? many : single);
                foreach (var inspector in group) pane.Children.Add(MakePane(inspector));
                panes.Add(pane);
            }

            return panes;
        }

        var left   = Panes(LabDock.Left,   300, LaneWidth);
        var right  = Panes(LabDock.Right,  380, LaneWidth + 30);
        var bottom = Panes(LabDock.Bottom, 0,   0);

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
                                    if (e.PropertyName is "IsVisible" or "Parent")
                                        Dispatcher.InvokeAsync(() => SetShown(inspector, IsOpen(pane)));
                                };
    }

    private static bool IsOpen(LayoutAnchorable pane)
    {
        return pane.Root is not null && pane.IsVisible;
    }

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

    internal bool IsInspectorOpen(string id)
    {
        return _panes.TryGetValue(id, out var pane) && IsOpen(pane);
    }

    internal void HideInspector(string id)
    {
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
                e.Handled         = true;
                _historyIndex     = Math.Max(0, _historyIndex - 1);
                _input.Text       = _history[_historyIndex];
                _input.CaretIndex = _input.Text.Length;
                break;

            case Key.Down when _history.Count > 0:
                e.Handled         = true;
                _historyIndex     = Math.Min(_history.Count, _historyIndex + 1);
                _input.Text       = _historyIndex >= _history.Count ? string.Empty : _history[_historyIndex];
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

        _completionIndex  = (_completionIndex + 1) % matches.Count;
        _lastCompletion   = matches[_completionIndex];
        _input.Text       = _lastCompletion;
        _input.CaretIndex = _input.Text.Length;
    }

    private async Task RunAsync(string line)
    {
        Print("> " + line);
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
            (Session as ILabProblems)?.ReportProblem(LabProblemSeverity.Error, "Terminal", ex.Message, line);
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
        _closed                           =  true;
        Session.PropertyChanged           -= OnSessionPropertyChanged;
        Session.Lab.Parameters.Changed    -= OnParametersChanged;
        AppTheme.Instance.PropertyChanged -= OnThemeChanged;
        _logPopup.IsOpen                  =  false;

        try
        {
            Session.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Closing a Lab session failed");
        }
    }

    public LabWindow(ILabSession session)
    {
        Session   = session ?? throw new ArgumentNullException(nameof(session));
        _terminal = new LabTerminal(this, session);

        Width     = 1320;
        Height    = 820;
        MinWidth  = 640;
        MinHeight = 400;
        Title     = session.Title;
        if (LabWindowIcon.For(session.Lab.IconKey) is { } icon) Icon = icon;

        SetResourceReference(BackgroundProperty, "Theme.Background");
        SetResourceReference(ForegroundProperty, "Theme.Text");

        session.Attach(this);
        _main   = session.BuildMainView();
        _topBar = session.BuildTopBar();

        // the library leads its side; the Lab's own panes follow; then what every window has
        AddLibrary();
        foreach (var inspector in session.Inspectors) _inspectors[inspector.Id] = inspector;
        AddCommonPanes();
        if (session.Lab.Parameters.Count > 0)
            _inspectors[LabParametersInspector.InspectorId] = new LabParametersInspector(session.Lab.Parameters);

        EnsureWidth();
        Content = BuildUi();
        BuildDefaultLayout();
        RebuildActions();

        Session.PropertyChanged += OnSessionPropertyChanged;
        Session.ValueChanged += (_, _) => Dispatcher.InvokeAsync(() =>
                                                                 {
                                                                     ShowValue();
                                                                     PokeLibrary();
                                                                 });
        Session.ActionsChanged += (_, _) => Dispatcher.InvokeAsync(RebuildActions);
        WireProblems();
        if (Session is ILabLive live) live.Changed += (_, _) => Dispatcher.InvokeAsync(PokeLibrary);
        Session.Lab.Parameters.Changed    += OnParametersChanged;
        AppTheme.Instance.PropertyChanged += OnThemeChanged;

        ShowValue();
        ApplyDockTheme();
        Print($"{session.Lab.Name}. Type help for the commands.");

        // the place the Lab's windows were left in; this window is the layout it keeps and restores
        WindowPlacements.Track(this, App.Instance?.ActiveSilo, PlacementType,
                               session.LayoutVersion > 1
                                   ? $"silo:lab/{session.Lab.Id}/v{session.LayoutVersion}"
                                   : $"silo:lab/{session.Lab.Id}", this);

        Loaded += (_, _) =>
                  {
                      SyncShown();
                      UpdateProblems();
                  };
        Closed += OnClosed;
    }

    public void ShowInspector(string id)
    {
        if (!_inspectors.TryGetValue(id, out var inspector)) return;

        if (!_panes.TryGetValue(id, out var pane)) pane = MakePane(inspector);

        if (pane.Root is null)
        {
            // tab it with an open inspector of the same side, else give it a column of its own
            var sibling = _inspectors.Values
                                     .Where(i => i.Dock == inspector.Dock && LaneOf(i) == LaneOf(inspector) &&
                                                 !ReferenceEquals(i, inspector))
                                     .Select(i => _panes.GetValueOrDefault(i.Id))
                                     .FirstOrDefault(p => p is { Root: not null } && p.Parent is LayoutAnchorablePane);

            if (sibling?.Parent is LayoutAnchorablePane group)
            {
                group.Children.Add(pane);
            }
            else
            {
                pane.AddToLayout(_dock,
                                 inspector.Dock switch
                                 {
                                     LabDock.Left => AnchorableShowStrategy.Left, LabDock.Bottom => AnchorableShowStrategy.Bottom,
                                     _            => AnchorableShowStrategy.Right
                                 });
                if (pane.Parent is LayoutAnchorablePane own)
                {
                    if (inspector.Dock == LabDock.Bottom) own.DockHeight = new GridLength(BottomHeight);
                    else own.DockWidth                                   = new GridLength(inspector.Dock == LabDock.Left ? 300 : 380);
                }
            }
        }
        else if (!pane.IsVisible)
        {
            pane.Show();
        }

        pane.IsSelected = true;
        pane.IsActive   = true;
        SetShown(inspector, true);
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
        foreach (var pane in _panes.Values) pane.Content                                               = null;
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
                                                                  when id.StartsWith(InspectorPrefix, StringComparison.Ordinal) &&
                                                                       _inspectors.TryGetValue(id[InspectorPrefix.Length..],
                                                                                               out var inspector):
                                                                  e.Content            = inspector.View;
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

        _restoredLayout = true;
        if (IsLoaded) SyncShown();
    }
}

/// <summary>A small window that shows text and nothing else: a page's source, a long result.</summary>
public sealed class LabTextWindow : Window
{
    /// <summary>Shows the text in a window of its own.</summary>
    public static void ShowText(string title, string text)
    {
        new LabTextWindow(title, text).Show();
    }

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
}