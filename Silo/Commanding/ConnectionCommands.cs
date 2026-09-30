using Serilog;
using Silo.Connectors;
using Silo.Model;
using Silo.Ui.Windows;

namespace Silo.Commanding;

public record NewConnectionCommand(OpenSilo Silo) : SiloCommand("New _Connection...", Enabler.IsSiloAuthorable)
{
    protected override async Task OnExecute(object? parameter)
    {
        if (ConnectionWindow.Modal() is IConnection c)
        {
            Silo.Data.AddItem(c);
            Silo.Connections.Add(c);
            Log.Debug("Added new connection:  {connection}", c);
            return;
        }

        Log.Debug("New connection window cancelled or failed.");
    }
}

public record NewCredentialCommand(OpenSilo Silo) : SiloCommand("New Cre_dential...", Enabler.IsSiloAuthorable)
{
    protected override async Task OnExecute(object? parameter)
    {
        var c = FormWindow.PromptFor<Credential>(Silo, "New credential...", true, o => { o.Encrypt(); });

        if (c != null)
            Log.Debug("Added new credential:  {credential}", c);
        else
            Log.Debug("New credential window cancelled or failed.");
    }
}