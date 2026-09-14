using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Markup;
using Silo.Commanding;
using Silo.Model;

namespace Silo;

public class SiloCommands(OpenSilo silo)
{
    public OpenSilo    Silo          { get; } = silo.Required();
    public SiloCommand ExportAll     => new ExportAllCommand(Silo);
    public SiloCommand ExportState   => new ExportStateCommand(Silo);
    public SiloCommand ExportLogs    => new ExportLogsCommand(Silo);
    public SiloCommand ImportData    => new ImportDataCommand(Silo);
    public SiloCommand NewConnection => new NewConnectionCommand(Silo);
    public SiloCommand NewCredential => new NewCredentialCommand(Silo);
    public SiloCommand SaveSilo      => new SaveSiloCommand(Silo);
    public SiloCommand SaveSiloAs    => new SaveSiloAsCommand(Silo);
}

public record AppCommand : ICommand, INotifyPropertyChanged
{
    private string               _header;
    private string?              _toolTip;
    private object?              _icon;
    private Checker.CheckerFunc? _checker;
    private bool                 _isCheckable;
    private IToggle?             _toggle;

    public object? DataContext { get; private set; }

    private readonly Func<object?, Task>? _executeHandler;
    private readonly Enabler.EnablerFunc? _itemEnabler;

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

    public string Header
    {
        get => _toggle?.Header ?? _header;
        set => SetField(ref _header, value);
    }

    public string? ToolTip
    {
        get => _toggle?.ToolTip ?? _toolTip;
        set => SetField(ref _toolTip, value);
    }

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
            if (_toggle is IToggle t)
            {
                t.IsChecked = value;
                return;
            }
        }
    }

    // --- ICommand ---
    public bool CanExecute(object? parameter) =>
        _toggle?.IsEnabled ?? _itemEnabler?.Invoke(parameter) ?? true;

    protected async virtual Task OnExecute(object? parameter)
    {
        if (_executeHandler is not null)
        {
            await _executeHandler.Invoke(parameter);
            return;
        }
    }

    public async void Execute(object? parameter)
    {
        if (_toggle is { } t)
        {
            await t.ToggleAsync(parameter);
            return;
        }

        if (IsCheckable)
        {
            IsChecked = !IsChecked;
        }

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

        return;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public void RaiseCanExecuteChanged() =>
        CommandManager.InvalidateRequerySuggested();

    // --- INotifyPropertyChanged ---
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual bool SetField<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}