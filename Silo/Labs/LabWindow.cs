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
public sealed class LabWindow : Window, IWindowLayout, ILabHost
{
    /// <summary>The kind of window it is for <see cref="WindowPlacements" />.</summary>
    public const string PlacementType = "Lab";

    private const string InspectorPrefix = "inspector:";
    private const string MainId          = "main";
    private const int    LogLimit        = 500;

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

    private UIElement _main = null!;
    private int       _historyIndex;
    private string?   _completionBase;
    private string?   _lastCompletion;
    private int       _completionIndex;
    private bool      _closed;
    private bool      _restoredLayout;

    public LabWindow(ILabSession session)
    {
        _session  = session ?? throw new ArgumentNullException(nameof(session));
        _terminal = new LabTerminal(this, session);

        Width     = 1320;
        Height    = 820;
        MinWidth  = 640;
        MinHeight = 400;
        Title     = session.Title;

        SetResourceReference(BackgroundProperty, "Theme.Background");
        SetResourceReference(ForegroundProperty, "Theme.Text");

        session.Attach(this);
        _main = session.BuildMainView();

        foreach (var inspector in session.Inspectors) _inspectors[inspector.Id] = inspector;
        if (session.Lab.Parameters.Count > 0) _inspectors[LabParametersInspector.InspectorId] = new LabParametersInspector(session.Lab.Parameters);

        Content = BuildUi();
        BuildDefaultLayout();
        RebuildActions();

        _session.PropertyChanged += OnSessionPropertyChanged;
        _session.ValueChanged    += (_, _) => Dispatcher.InvokeAsync(ShowValue);
        _session.ActionsChanged  += (_, _) => Dispatcher.InvokeAsync(RebuildActions);
        _session.Lab.Parameters.Changed += OnParametersChanged;
        AppTheme.Instance.PropertyChanged += OnThemeChanged;

        ShowValue();
        ApplyDockTheme();
        Print($"{session.Lab.Name}. Type help for the commands.");

        // the place the Lab's windows were left in; this window is the layout it keeps and restores
        WindowPlacements.Track(this, App.Instance?.ActiveSilo, PlacementType, $"silo:lab/{session.Lab.Id}", this);

        Loaded  += (_, _) => SyncShown();
        Closed  += OnClosed;
    }

    /// <summary>What the session is called by; the window title follows it.</summary>
    public ILabSession Session => _session;

    Window ILabHost.Window => this;

    internal IEnumerable<string> InspectorIds => _inspectors.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

    // ---- building ---------------------------------------------------------------------------

    private UIElement BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var menu = BuildMenu();
        Grid.SetRow(menu, 0);
        root.Children.Add(menu);

        var bar = BuildToolbar();
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);

        Grid.SetRow(_dock, 2);
        root.Children.Add(_dock);

        var status = BuildStatusBar();
        Grid.SetRow(status, 3);
        root.Children.Add(status);

        InputBindings.Add(new KeyBinding(new DelegateCommand(_ => { _valueBox.Focus(); _valueBox.SelectAll(); }), Key.L, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new DelegateCommand(_ => _input.Focus()), Key.OemTilde, ModifierKeys.Control));
        return root;
    }

    private Menu BuildMenu()
    {
        var menu = new Menu();

        // an empty menu item cannot open, so each holds a stand-in that is replaced when it opens
        AddMenu(menu, "_Lab",   FillLabMenu);
        AddMenu(menu, "_Value", FillValueMenu);
        AddMenu(menu, "Vie_w",  FillViewMenu);
        return menu;

        static void AddMenu(Menu parent, string header, Action<MenuItem> fill)
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
        }
    }

    private void FillLabMenu(MenuItem menu)
    {
        AddCommands(menu, _session.GlobalActions);
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        var newWindow = new MenuItem { Header = $"_New {_session.Lab.Name} Window" };
        newWindow.Click += (_, _) => LabHost.Open(_session.Lab);
        menu.Items.Add(newWindow);

        var close = new MenuItem { Header = "_Close" };
        close.Click += (_, _) => Close();
        menu.Items.Add(close);
    }

    private void FillValueMenu(MenuItem menu)
    {
        AddCommands(menu, _session.ValueActions);
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(no actions)", IsEnabled = false });
    }

    private void FillViewMenu(MenuItem menu)
    {
        foreach (var id in InspectorIds)
        {
            var inspector = _inspectors[id];
            var item = new MenuItem { Header = inspector.Title.Replace("_", "__"), IsCheckable = true, IsChecked = IsInspectorOpen(id) };
            item.Icon = ToolbarIcons.Find(inspector.IconKey) is { } geometry ? new AppIcon { Icon = geometry, Size = 16 } : null;
            item.Click += (_, _) =>
                          {
                              // IsCheckable has already flipped the mark: it says what the person asked for
                              if (item.IsChecked) ShowInspector(id);
                              else HideInspector(id);
                          };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "_Reset Layout", ToolTip = "Put the panes back where a new window has them" };
        reset.Click += (_, _) => ResetLayout();
        menu.Items.Add(reset);

        var terminal = new MenuItem { Header = "Terminal _Log", InputGestureText = "Ctrl+`" };
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

        FillBar(_contextBar, SafeActions(() => _session.ContextActions), 4);
        FillBar(_valueBar,   SafeActions(() => _session.ValueActions),   0);

        foreach (var action in SafeActions(() => _session.GlobalActions).Concat(SafeActions(() => _session.ContextActions)).Concat(SafeActions(() => _session.ValueActions)))
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
                             Padding          = new Thickness(7, 3, 7, 3),
                             Margin           = new Thickness(0, 0, 3, 0),
                             MinWidth         = 28,
                             ToolTip          = string.IsNullOrEmpty(action.ToolTip) ? header : $"{header}: {action.ToolTip}"
                         };

            button.Content = ToolbarIcons.Find(action.IconKey) is { } geometry ? (object)new AppIcon { Icon = geometry, Size = 16 } : header;
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

    // ---- inspectors and the dock layout ------------------------------------------------------

    private void ApplyDockTheme()
    {
        _dock.Theme = AppTheme.Instance.IsEffectivelyDark ? new Vs2013DarkTheme() : new Vs2013LightTheme();
    }

    private void OnThemeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppTheme.IsEffectivelyDark)) Dispatcher.InvokeAsync(ApplyDockTheme);
    }

    /// <summary>The arrangement of a new window: the main view in the middle, each inspector that starts open tabbed on its side.</summary>
    private void BuildDefaultLayout()
    {
        foreach (var pane in _panes.Values) pane.Content = null;
        _panes.Clear();
        foreach (var document in _dock.Layout.Descendents().OfType<LayoutDocument>()) document.Content = null;

        var main = new LayoutDocument { Title = _session.Lab.Name, ContentId = MainId, Content = _main, CanClose = false, CanFloat = false };
        var docs = new LayoutDocumentPane();
        docs.Children.Add(main);

        var left  = new LayoutAnchorablePane { DockWidth = new GridLength(300) };
        var right = new LayoutAnchorablePane { DockWidth = new GridLength(380) };
        foreach (var inspector in _inspectors.Values.Where(i => i.VisibleByDefault))
            (inspector.Dock == LabDock.Left ? left : right).Children.Add(MakePane(inspector));

        var panel = new LayoutPanel { Orientation = Orientation.Horizontal };
        if (left.Children.Count > 0) panel.Children.Add(left);
        panel.Children.Add(docs);
        if (right.Children.Count > 0) panel.Children.Add(right);

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

    public void ShowInspector(string id)
    {
        if (!_inspectors.TryGetValue(id, out var inspector)) return;

        if (!_panes.TryGetValue(id, out var pane)) pane = MakePane(inspector);

        if (pane.Root is null)
        {
            // tab it with an open inspector of the same side, else give it a column of its own
            var sibling = _inspectors.Values.Where(i => i.Dock == inspector.Dock && !ReferenceEquals(i, inspector))
                                     .Select(i => _panes.GetValueOrDefault(i.Id))
                                     .FirstOrDefault(p => p is { Root: not null } && p.Parent is LayoutAnchorablePane);

            if (sibling?.Parent is LayoutAnchorablePane group)
            {
                group.Children.Add(pane);
            }
            else
            {
                pane.AddToLayout(_dock, inspector.Dock == LabDock.Left ? AnchorableShowStrategy.Left : AnchorableShowStrategy.Right);
                if (pane.Parent is LayoutAnchorablePane own) own.DockWidth = new GridLength(inspector.Dock == LabDock.Left ? 300 : 380);
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

        _restoredLayout = true;
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
