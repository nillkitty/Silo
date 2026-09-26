using System.Diagnostics;
using System.Windows.Input;

namespace Silo.ViewModel;

public class SiloToolsModel
{
    public DebuggerBreakCommand Break             { get; } = new();
    public bool                 HasToolsAuthoring => App.ShowToolsMenu;
    public List<ITool>          Tools             => App.Resolve<IEnumerable<ITool>>()?.ToList() ?? [];
}

public class DebuggerBreakCommand : ICommand
{
    public bool CanExecute(object? parameter)
    {
        return Debugger.IsAttached;
    }

    public void Execute(object? parameter)
    {
        Debugger.Break();
    }

    public event EventHandler? CanExecuteChanged;
}