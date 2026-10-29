using Serilog;
using Silo.Commanding;
using Silo.Providers.Action;
using Silo.Providers.Monitoring;
using Silo.Providers.Reflection;
using Silo.Ui.Windows;
using Silo.Model;

namespace Silo.Roles;

/// <summary>
///     "Auto-Add Items..." on any container that can list its items (<see cref="IItemContainer" />: a folder, a drive, a
///     registry key): asks for a role, whether to look inside folders and keys, and how to watch for changes, then adds what is there and
///     keeps adding what appears and updating what changes. Once set up, the menu offers to scan again or to stop.
/// </summary>
public sealed class AutoAddActions : IActionProvider<IItemContainer>
{
    private const string Group = "Silo";

    public IEnumerable<ProvidedCommand> GetCommands(IItemContainer container)
    {
        var silo = OpenSilo.OnlySilo ?? App.Instance?.ActiveSilo;
        if (container.ContainerUri is not { } uri || silo?.Data is not IStoredStore store) yield break;

        var service = AutoAddService.For(store);
        var rule    = service.RuleFor(uri);
        bool usable = container.CanEnumerate && silo!.IsWritable;

        yield return ProvidedCommand.From("autoadd.setup", rule is null ? "Auto-_Add Items..." : "Auto-_Add Items (change)...",
                                          () => _ = SetUpAsync(service, container, uri, rule), "Add", () => usable,
                                          "Adds the items in here to the Silo with a role, then keeps adding new ones and updating changed ones",
                                          Group, CommandPlacement.Menu);

        if (rule is null) yield break;

        yield return ProvidedCommand.From("autoadd.scan", "_Scan Now", () => _ = ScanAsync(service, container, rule), "Refresh", () => usable,
                                          $"Looks through the folder or key again for items to add or update (role: {rule.Role})", Group,
                                          CommandPlacement.Menu);
        yield return ProvidedCommand.From("autoadd.stop", "S_top Auto-Adding", () => service.Disable(rule.Id), null, null,
                                          "Stops watching; the items already added stay in the Silo", Group, CommandPlacement.Menu);
    }

    private static async Task SetUpAsync(AutoAddService service, IItemContainer container, Uri uri, AutoAddRule? existing)
    {
        string? role = PromptDialog.Ask("Auto-Add Items", $"The role to give the items found in {container.ContainerName}:", "Next",
                                        existing?.Role ?? "Item");
        if (role is null) return;

        bool canWatch = container is IChangeMonitorable { SupportedModes: not MonitorModes.None };
        string[] buttons = canWatch
                               ? ["Add and watch", "Add and poll", "Add once", "Cancel"]
                               : ["Add once", "Cancel"];
        var (button, recursive) = PromptDialog.Show("Auto-Add Items",
                                                    $"Add the items in {container.ContainerName} as \"{role}\", and then...\n\n" +
                                                    (canWatch
                                                         ? "Watch: the system reports changes as they happen.\nPoll: look again every few seconds (also works on network drives).\n"
                                                         : string.Empty) +
                                                    "\nWhat each item is (script, program, binary, data...) is worked out from its file type and, where data specs are registered, its content. " +
                                                    $"Settings per kind are in Saved Objects under \"{RoleHandlingTable.StoredKey}\".",
                                                    buttons, "Include items in sub-folders / sub-keys");

        var monitor = canWatch ? button switch { 0 => MonitorModes.Passive, 1 => MonitorModes.Active, 2 => MonitorModes.None, _ => (MonitorModes?)null }
                          : button == 0 ? MonitorModes.None : null;
        if (monitor is null) return;

        var rule = new AutoAddRule(AutoAddService.IdFor(uri), uri, role, recursive, monitor.Value);
        try
        {
            App.Status($"Adding the items in {container.ContainerName}...");
            var result = await service.EnableAsync(rule, container);
            App.Status($"Auto-add {container.ContainerName} as \"{role}\": {result}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-add of {Container} failed", container.ContainerName);
            App.Status("Auto-add failed: " + ex.Message);
        }
    }

    private static async Task ScanAsync(AutoAddService service, IItemContainer container, AutoAddRule rule)
    {
        try
        {
            var result = await service.ScanAsync(rule, container);
            App.Status($"Auto-add {container.ContainerName}: {result}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-add scan of {Container} failed", container.ContainerName);
            App.Status("Auto-add failed: " + ex.Message);
        }
    }
}
