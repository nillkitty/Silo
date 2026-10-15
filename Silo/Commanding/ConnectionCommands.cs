using Serilog;
using Silo.Model;
using Silo.Ui.Windows;

namespace Silo.Commanding;

/// <summary>
///     Silo -> New Connection
/// </summary>
/// <param name="Silo"></param>
public record NewConnectionCommand(OpenSilo Silo) : SiloCommand("New _Connection...", Enabler.IsSiloAuthorable)
{
    protected override async Task OnExecute(object? parameter)
    {
        // the dialog makes the connection; the Silo adds it and saves its name and URI
        if (Silo.NewConnection() is { } c)
        {
            Log.Debug("Added new connection:  {connection}", c);
            return;
        }

        Log.Debug("New connection window cancelled or failed.");
    }
}

/// <summary>
///     Silo -> New Credential
/// </summary>
/// <param name="Silo"></param>
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