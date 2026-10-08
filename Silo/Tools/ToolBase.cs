using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;
using Telefrag.DI;

namespace Silo.Tools;

[Transient(typeof(ITool))]
public abstract class ToolBase(string name) : ITool, ICommand, INotifyPropertyChanged
{
    private UIElement?        _ctrl;
    private bool              _isEnabled = true;
    private bool              _isVisible = true;
    private LayoutAnchorable? _pane;
    private Window?           _window;
    public  string            DisplayName { get; set; }
    public  string            GroupName   { get; set; }

    /// <summary>
    ///     Display text for the tool's Tools-menu entry. Declared concretely (not just
    ///     via the <see cref="ITool" /> default member) because WPF's reflection-based
    ///     data binding only sees members declared on the concrete runtime type.
    /// </summary>
    public string Header => DisplayName ?? Name;

    public ImageSource Icon { get; set; }

    /// <summary>
    ///     Reflects whether this tool's window is currently open.  There is exactly one
    ///     window per tool instance (tools are typically registered as singletons), so
    ///     this simply mirrors that window's visibility rather than tracking separate
    ///     state that could drift out of sync with it.
    /// </summary>
    public bool IsChecked => _window?.IsVisible ?? false;

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

    public string Name     { get; } = name.Required();
    public int    Priority { get; set; }

    protected abstract UIElement OnBuildContent();

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
        return new LayoutAnchorable
               {
                   Title   = DisplayName,
                   Content = content.Required()
               };
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public LayoutAnchorable? GetPane()
    {
        if (ModalOnly) return null;

        _pane ??= PaneWrap(OnBuildContent());
        return _pane;
    }

    /// <inheritdoc />
    public Window? Toggle()
    {
        var d = App.Instance!.Dispatcher;
        if (!d.CheckAccess())
            return d.Invoke(Toggle);

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

        window.Show();
        window.Activate();
        OnPropertyChanged(nameof(IsChecked));
        return window;
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