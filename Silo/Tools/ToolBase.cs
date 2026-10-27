using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;
using Silo.Ui.Windows;
using Telefrag.DI;

namespace Silo.Tools;

[Transient(typeof(ITool))]
public abstract class ToolBase : ITool, ICommand, INotifyPropertyChanged
{
    private static readonly List<WeakReference<ToolBase>> _all = [];

    private UIElement?        _ctrl;
    private UIElement?        _paneContent; // what the pane shows: built once, whichever pane object currently holds it
    private bool              _isEnabled = true;
    private bool              _isVisible = true;
    private LayoutAnchorable? _pane;
    private Window?           _window;
    public  string?           DisplayName { get; set; }
    public  string?           GroupName   { get; set; }

    /// <summary>
    ///     Display text for the tool's Tools-menu entry. Declared concretely (not just
    ///     via the <see cref="ITool" /> default member) because WPF's reflection-based
    ///     data binding only sees members declared on the concrete runtime type.
    /// </summary>
    public string Header => DisplayName ?? Name;

    public IconRef? Icon { get; set; }

    /// <summary>
    ///     Reflects whether this tool's window is currently open.  There is exactly one
    ///     window per tool instance (tools are typically registered as singletons), so
    ///     this simply mirrors that window's visibility rather than tracking separate
    ///     state that could drift out of sync with it.
    /// </summary>
    public bool IsChecked
    {
        get
        {
            if (PaneHost is { } host && _pane is not null) return host.IsToolPaneOpen(_pane);
            return _window?.IsVisible ?? false;
        }
    }

    /// <summary>
    ///     True for a tool that opens as a pane in the main window's dock area (which can also be dragged out into a
    ///     floating window and docked back) instead of as a separate window.
    /// </summary>
    protected virtual bool UsesPane => false;

    /// <summary>How tall the pane is the first time it opens at the bottom of the dock area.</summary>
    protected virtual double PaneHeight => 360;

    /// <summary>The main window, when this tool opens as a pane in it.</summary>
    private MainWindow? PaneHost => UsesPane && Application.Current?.MainWindow is MainWindow w ? w : null;

    /// <inheritdoc cref="ITool.IsEnabled" />
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <inheritdoc cref="ITool.IsVisible" />
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged();
        }
    }

    public bool ModalOnly { get; set; }

    public string Name     { get; }
    public int    Priority { get; set; }

    protected ToolBase(string name)
    {
        Name = name.Required();
        lock (_all) _all.Add(new WeakReference<ToolBase>(this));
    }

    /// <summary>The tools that open as a pane in the dock area (the main window asks, to put their panes back where they were left).</summary>
    internal static IReadOnlyList<ToolBase> PaneTools()
    {
        lock (_all)
            return _all.Select(r => r.TryGetTarget(out var t) ? t : null).OfType<ToolBase>().Where(t => t.UsesPane).DistinctBy(t => t.Name).ToList();
    }

    /// <summary>What the tool's pane shows (built now if it never was): the same object whichever pane object holds it.</summary>
    internal UIElement BuildPaneContent()
    {
        return _paneContent ??= OnBuildContent();
    }

    /// <summary>
    ///     A saved dock layout was put back and has this tool's pane in it: the tool takes that pane object as its own (the
    ///     content was handed to it with <see cref="BuildPaneContent" />).
    /// </summary>
    internal void AdoptPane(LayoutAnchorable pane)
    {
        _pane = pane;
        HookPane(pane);
        OnPropertyChanged(nameof(IsChecked));
    }

    /// <summary>The pane left the layout for good (the layout that was put back does not have it): it is made again, empty, when next opened.</summary>
    internal void ResetPane()
    {
        _pane = null;
        OnPropertyChanged(nameof(IsChecked));
    }

    protected abstract UIElement OnBuildContent();

    // --- extra panes ---
    // A pane tool can let parts of its content be undocked into panes of their own (ContentId "tool:<name>/<key>"). The tool
    // makes and places them itself; the main window only needs these three to save them with the dock layout and put them back.

    /// <summary>The keys of the parts of this tool that can be panes of their own.</summary>
    internal virtual IReadOnlyList<string> ExtraPaneKeys => [];

    /// <summary>The ContentId of an extra pane.</summary>
    internal string ExtraPaneId(string key) => "tool:" + Name + "/" + key;

    /// <summary>A saved layout has the extra pane <paramref name="key" />: the part, taken out of wherever it is now, for the pane to show (null: leave the pane out).</summary>
    internal virtual UIElement? TakeExtraPaneContent(string key) => null;

    /// <summary>
    ///     After a layout was put back: the pane object that now shows the part <paramref name="key" />, or null when the layout had none (the
    ///     part then goes back into the tool's own content).
    /// </summary>
    internal virtual void AdoptExtraPane(string key, LayoutAnchorable? pane) { }

    private Window WindowWrap(UIElement ctrl)
    {
        ctrl.Required();
        if (_window is null)
        {
            _window = new Window
                      {
                          Title   = DisplayName ?? Name,
                          Content = ctrl
                      };

            // Tools are singletons and are meant to be shown/hidden repeatedly rather
            // than rebuilt, so closing the window via the titlebar (or Alt+F4, etc.)
            // hides it instead of destroying it - the same Window/content instance is
            // reused the next time the tool is toggled back open.
            _window.Closing += (_, e) =>
                               {
                                   e.Cancel = true;
                                   _window.Hide();
                                   OnPropertyChanged(nameof(IsChecked));
                               };
        }

        return _window;
    }

    private LayoutAnchorable? PaneWrap(UIElement content)
    {
        var pane = new LayoutAnchorable
               {
                   Title          = DisplayName,
                   ContentId      = "tool:" + Name,
                   Content        = content.Required(),
                   CanFloat       = true,
                   FloatingWidth  = 900,
                   FloatingHeight = 640
               };

        HookPane(pane);
        return pane;
    }

    private void HookPane(LayoutAnchorable pane)
    {
        // closing the pane with its own X must update the Tools menu check mark too
        pane.PropertyChanged += (_, e) =>
                                {
                                    if (e.PropertyName is "IsVisible" or "Parent") OnPropertyChanged(nameof(IsChecked));
                                };
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public LayoutAnchorable? GetPane()
    {
        if (ModalOnly) return null;

        _pane ??= PaneWrap(_paneContent ??= OnBuildContent());
        return _pane;
    }

    /// <inheritdoc />
    public Window? Toggle()
    {
        var d = App.Instance!.Dispatcher;
        if (!d.CheckAccess())
            return d.Invoke(Toggle);

        // a pane tool is shown or hidden in the dock area (it may be floating there; that is still "open")
        if (PaneHost is { } host && GetPane() is { } pane)
        {
            if (host.IsToolPaneOpen(pane)) host.HideToolPane(pane);
            else host.ShowToolPane(pane, PaneHeight);

            OnPropertyChanged(nameof(IsChecked));
            return null;
        }

        // Executing the command while the window is open closes it, rather than
        // re-showing (or, worse, rebuilding) an already-open window.
        if (_window is { IsVisible: true })
        {
            _window.Hide();
            OnPropertyChanged(nameof(IsChecked));
            return null;
        }

        // Was `ModalOnly ? WindowWrap(_ctrl) : WindowWrap(_pane ??= PaneWrap(_ctrl))`: under
        // Telerik, RadPane was itself a UIElement, so a non-modal tool's floating window
        // could host the RadPane (chrome and all) directly. AvalonDock's LayoutAnchorable
        // is a layout-model object, not a UIElement/Visual, so it can't be used as a
        // Window's Content - only GetPane() (for docking into MainWindow's DockingManager)
        // needs a LayoutAnchorable now. The floating window always just hosts the raw
        // content; ModalOnly no longer changes which window-wrapping path is taken, since
        // there's only one now, but the field/property is left in place in case it starts
        // gating something else later.
        _ctrl ??= OnBuildContent();
        Window window = WindowWrap(_ctrl);
        WindowPlacements.Track(window, App.Instance?.ActiveSilo, WindowPlacements.Tool, "silo:tool/" + Name);

        window.Show();
        window.Activate();
        OnPropertyChanged(nameof(IsChecked));
        return window;
    }

    /// <summary>Opens the tool (a pane is shown and selected, a window is shown and activated) without ever closing it.</summary>
    public void Show()
    {
        var d = App.Instance!.Dispatcher;
        if (!d.CheckAccess())
        {
            d.Invoke(Show);
            return;
        }

        if (PaneHost is { } host && GetPane() is { } pane)
        {
            host.ShowToolPane(pane, PaneHeight);
            OnPropertyChanged(nameof(IsChecked));
            return;
        }

        if (!IsChecked) Toggle();
        else _window?.Activate();
    }

    public UIElement? GetContent()
    {
        _ctrl = OnBuildContent();
        return _ctrl;
    }

    // --- ICommand ---
    // Lets a ToolBase instance be bound directly as a Tools-menu item's Command:
    // IsEnabled drives CanExecute (with CommandManager notified on change above so the
    // menu item's enabled state stays live), and clicking the item calls Toggle() -
    // the same single entry point used for opening/closing the tool's window elsewhere.
    bool ICommand.CanExecute(object? parameter)
    {
        return IsEnabled;
    }

    void ICommand.Execute(object? parameter)
    {
        Toggle();
    }

    event EventHandler? ICommand.CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}