using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Silo.Commanding;
using Silo.Model;

namespace Silo;

/// <summary>
///     Silo commands (Silo Menu)
/// </summary>
/// <param name="silo">The target Silo (which likely /was/ the active silo at the time, but not necessarily)/</param>
public class SiloCommands(OpenSilo silo)
{
    /// <summary>Silo -> Convert to Archive Format</summary>
    public SiloCommand ConvertToArchive => new ConvertToArchiveCommand(Silo);

    /// <summary>Silo -> Export All</summary>
    public SiloCommand ExportAll => new ExportAllCommand(Silo);

    /// <summary>Silo -> Export Logs</summary>
    public SiloCommand ExportLogs => new ExportLogsCommand(Silo);

    /// <summary>Silo -> Export State</summary>
    public SiloCommand ExportState => new ExportStateCommand(Silo);

    /// <summary>Silo -> Import Data</summary>
    public SiloCommand ImportData => new ImportDataCommand(Silo);

    /// <summary>Silo -> New Connectionl</summary>
    public SiloCommand NewConnection => new NewConnectionCommand(Silo);

    /// <summary>Silo -> New Credential</summary>
    public SiloCommand NewCredential => new NewCredentialCommand(Silo);

    /// <summary>Silo -> Save</summary>
    public SiloCommand SaveSilo => new SaveSiloCommand(Silo);

    /// <summary>Silo -> Save As</summary>
    public SiloCommand SaveSiloAs => new SaveSiloAsCommand(Silo);

    /// <summary>
    ///     Parent, open silo/
    /// </summary>
    public OpenSilo Silo { get; } = silo.Required();
}

/// <summary>
///     Application level command
/// </summary>
public record AppCommand : ICommand, INotifyPropertyChanged
{
    private readonly Checker.CheckerFunc? _checker;
    private readonly Func<object?, Task>? _executeHandler;
    private readonly Enabler.EnablerFunc? _itemEnabler;
    private readonly IToggle?             _toggle;
    private          string               _header;
    private          object?              _icon;
    private          bool                 _isCheckable;
    private          string?              _toolTip;

    /// <summary>
    ///     Gets or sets the model associated with the currentbiew/=.=
    /// </summary>
    public object? DataContext { get; private set; }

    /// <summary>
    ///     Gets or sets the command header
    /// </summary>
    public string Header
    {
        get => _toggle?.Header ?? _header;
        set => SetField(ref _header, value);
    }

    /// <summary>
    ///     Gets or sets the icon source
    /// </summary>
    public object? Icon
    {
        get => _toggle?.Icon ?? _icon;
        set => SetField(ref _icon, value);
    }

    public bool IsCheckable
    {
        get => _toggle?.IsCheckable ?? _isCheckable;
        set => SetField(ref _isCheckable, value);
    }

    public bool IsChecked
    {
        get => _toggle?.IsChecked ?? _checker?.Invoke(DataContext) ?? false;
        set
        {
            if (_toggle is IToggle t) t.IsChecked = value;
        }
    }

    public string? ToolTip
    {
        get => _toggle?.ToolTip ?? _toolTip;
        set => SetField(ref _toolTip, value);
    }

    protected virtual async Task OnExecute(object? parameter)
    {
        if (_executeHandler is not null) await _executeHandler.Invoke(parameter);
    }

    public void RaiseCanExecuteChanged()
    {
        CommandManager.InvalidateRequerySuggested();
    }

    protected virtual bool SetField<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public AppCommand(string header, IToggle toggle)
    {
        _header = header;
        _toggle = toggle.Required();
    }

    public AppCommand(string  header,      Func<object?, Task> execute, Enabler.EnablerFunc? enabler = null, string? toolTip = null,
                      object? icon = null, bool                isCheckable = false, Checker.CheckerFunc? checker = null)
    {
        _header         = header;
        _executeHandler = execute;
        _itemEnabler    = enabler;
        _toolTip        = toolTip;
        _icon           = icon;
        _isCheckable    = isCheckable;
        _checker        = checker;
    }

    public bool CanExecute(object? parameter)
    {
        return _toggle?.IsEnabled ?? _itemEnabler?.Invoke(parameter) ?? true;
    }

    public async void Execute(object? parameter)
    {
        if (_toggle is { } t)
        {
            await t.ToggleAsync(parameter);
            return;
        }

        if (IsCheckable) IsChecked = !IsChecked;

        try
        {
            await OnExecute(parameter);
        }
        catch (Exception ex)
        {
            if (App.Instance is { } i)
                i.Unhandled(ex);
            else
                Environment.FailFast("Unhandled exception in async handler", ex);
        }
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    // --- INotifyPropertyChanged ---
    public event PropertyChangedEventHandler? PropertyChanged;
}