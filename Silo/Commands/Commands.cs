using System;
using System.CodeDom;
using System.Reflection.Metadata;
using System.Windows.Input;
using System.Windows.Markup;
using Serilog;
using Silo.Connectors;
using Silo.Model;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace Silo;

[MarkupExtensionReturnType(typeof(ICommand))]
public class SiloCommand : MarkupExtension
{
    [ConstructorArgument("command")]
    public AppCommand Command { get; set; }

    public SiloCommand()
    {
    }

    public SiloCommand(AppCommand command)
    {
        Command = command.Required();
    }

    public override object? ProvideValue(IServiceProvider serviceProvider)
    {
        return Command;
    }
}

public static class AppCommands
{
    public static ILogger Logger = Log.ForContext<App>();


    internal static Func<object, bool> Always => o => true;
    internal static Func<object, bool> Never  => o => false;

    internal static Func<object, bool> IsSiloLoaded =>
        _ => App.Instance?.ActiveSilo is { Data: { } };

    public static AppCommand NewConnection => new("New C_onnection",
                                                  o => OnNewConnection(),
                                                  Always, o => false);

    public static AppCommand ExportAll =>
        new("Export A_ll", _ExportAllHandler, IsSiloLoaded);

    public static AppCommand ImportData =>
        new("_Import Data...", _ImportDataHandler, IsSiloLoaded);

    public static AppCommand ExportState =>
        new("Export _State...", _ExportStateHandler, IsSiloLoaded);

    public static AppCommand ExportLogs =>
        new("Export _Logs...", _Unwritten, IsSiloLoaded);

    private async static Task? _ExportAllHandler(object o)
    {
        throw new NotImplementedException();
    }

    private async static Task? _ExportStateHandler(object o)
    {
        throw new NotImplementedException();
    }

    private async static Task? _Unwritten(object o)
    {
        throw new NotImplementedException();
    }

    private async static Task? _ImportDataHandler(object o)
    {
        throw new NotImplementedException();
    }

    private async static Task OnNewConnection()
    {
        if (App.Instance?.NewConnection() is { } c)
        {
            if (App.Instance?.ActiveSilo is { } s)
            {
                s.Data.AddItem(c);
            }
        }
    }

    public static AppCommand NewCredential => new("New _Credential",
                                                  o =>
                                                      App
                                                         .NewCredential(o as
                                                                            OpenSilo),
                                                  o => true, o => false);
}

public record AppCommand(
    string               Title,
    Func<object, Task?>  Handler,
    Func<object, bool>   Enabler = null,
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