using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Silo.Tools;

namespace Silo.ViewModel;

public class SiloToolsModel
{
    public DebuggerBreakCommand Break             { get; } = new();
    public bool                 HasToolsAuthoring => App.ShowToolsMenu;

    private ObservableCollection<ITool>? _tools;

    /// <summary>The tools in the person's order (<see cref="ToolSettings" />); the Tools menu follows when that changes.</summary>
    public ObservableCollection<ITool> Tools
    {
        get
        {
            if (_tools is null)
            {
                _tools = [];
                Refresh();
                ToolSettings.Changed += (_, _) => Refresh();
            }

            return _tools;
        }
    }

    private void Refresh()
    {
        _tools!.Clear();
        foreach (var tool in ToolSettings.Ordered(App.Resolve<IEnumerable<ITool>>() ?? []))
            _tools.Add(tool);
    }
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

    public event EventHandler? CanExecuteChanged { add { } remove { } }
}