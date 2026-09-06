using Serilog;
using Silo.Commanding;
using Silo.Model;

namespace Silo.Commanding;

public record OpenSiloCommand() : SiloCommand("_Open Silo...", Enabler.Always)
{
    protected override Task OnExecute(object? parameter)
    {
        throw new NotImplementedException();
    }
}

public record NewSiloCommand() : SiloCommand("_New Silo...", Enabler.Always)
{
    protected override Task OnExecute(object? parameter)
    {
        throw new NotImplementedException();
    }
}

public record AppExitCommand() : SiloCommand("E_xit", Enabler.Always)
{
    protected override async Task OnExecute(object? parameter)
    {
        if (App.Instance is { } i)
        {
            foreach (var s in i.OpenSilos)
            {
                if (await s.Shutdown())
                {
                    Log.Debug("Successfully closed silo:  {name} ({path})",
                              s.Name, s.FilePath);
                }
            }
        }

        _exit(0);
    }

    private void _exit(int code)
    {
        if (App.Instance is { } i)
            i.Shutdown(code);
        else
            Environment.Exit(code);
    }
}

public record AppAboutCommand() : SiloCommand("_About Silo...", Enabler.Always)
{
    protected override Task OnExecute(object? parameter)
    {
        throw new NotImplementedException();
    }
}

public static class AppCommands
{
    public static ILogger Logger = Log.ForContext<App>();


    public static AppCommand NewConnection => new("New C_onnection",
                                                  o => OnNewConnection(),
                                                  Enabler.Always);

    public static AppCommand ExportAll =>
        new("Export A_ll", _ExportAllHandler, Enabler.IsSiloLoaded);

    public static AppCommand ImportData =>
        new("_Import Data...", _ImportDataHandler, Enabler.IsSiloLoaded);

    public static AppCommand ExportState =>
        new("Export _State...", _ExportStateHandler, Enabler.IsSiloLoaded);

    public static AppCommand ExportLogs =>
        new("Export _Logs...", _Unwritten, Enabler.IsSiloLoaded);

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
                                                  o => true);
}