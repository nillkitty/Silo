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
                    Log.Debug("Successfully closed silo:  {name} ({path})", s.Name, s.FilePath);
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