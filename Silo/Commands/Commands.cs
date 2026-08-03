using System;
using System.Reflection.Metadata;
using System.Windows.Input;
using Serilog;
using Silo.Connectors;
using Silo.Model;

namespace Silo;

public static class AppCommands
{
    public static ILogger Logger = Log.ForContext<App>();

    public static AppCommand NewConnection => new("New C_onnection",
                                                  o => App.NewConnection(),
                                                  o => true,
                                                  o => false);

    public static AppCommand NewCredential => new("New _Credential",
                                                  o =>
                                                      App
                                                         .NewCredential(o as
                                                                            OpenSilo),
                                                  o => true,
                                                  o => false);
}

public record AppCommand(
    string               Title,
    Func<object?, Task>  Handler,
    Func<object?, bool>? Enabler = null,
    Func<object?, bool>? Checker = null) : ICommand
{
    public bool CanExecute(object? parameter) =>
        OnCanExecute(parameter);

    public void Execute(object? parameter) =>
        OnExecute(parameter);

    protected virtual bool OnCanExecute(object? parameter)
    {
        var e = Enabler?.Invoke(parameter) ?? false;
        if (e != IsExecutable)
            CanExecuteChanged?.Invoke(parameter, EventArgs.Empty);
        return IsExecutable;
    }

    public bool IsExecutable { get; set; }

    protected virtual void OnExecute(object? parameter)
    {
        Handler?.Invoke(parameter);
    }

    public event EventHandler? CanExecuteChanged;
}