namespace Silo.Actions;

/// <summary>What the App actions need from the running app, so they can be tried without one.</summary>
public interface IAppActionHost
{
    /// <summary>Saves every open Silo.</summary>
    Task SaveAllAsync();

    /// <summary>Opens a Silo (or, with <paramref name="forceReopen" />, closes it without asking and opens it again). Returns the path opened.</summary>
    Task<string?> OpenSiloAsync(string path, bool forceReopen);

    /// <summary>Creates a new, empty Silo at <paramref name="path" /> and opens it. Returns the path created.</summary>
    Task<string?> NewSiloAsync(string path);

    /// <summary>Closes every Silo (saving them first, or discarding their unsaved changes) and ends the app.</summary>
    Task ExitAsync(bool saveChanges);
}

/// <summary>
///     The App actions: application-global, across every Silo. All are registered, so they can be invoked from outside
///     (<c>silo action App.SaveAll</c>).
/// </summary>
public static class AppActions
{
    public const string SaveAll = "App.SaveAll";
    public const string NewSilo = "App.NewSilo";
    public const string OpenSilo = "App.OpenSilo";
    public const string Exit = "App.Exit";

    public static void Register(ActionRegistry registry, IAppActionHost host)
    {
        registry.Register(new DelegateAction(SaveAll, async (_, _) =>
                                                      {
                                                          await host.SaveAllAsync();
                                                          return null;
                                                      },
                                             description: "Saves all Silo documents", isRegistered: true));

        registry.Register(new DelegateAction(NewSilo, async (a, _) => await host.NewSiloAsync((string) a["Path"]!),
                                             [new ActionParameter("Path", typeof(string))],
                                             "Creates a new Silo at the given path", true));

        registry.Register(new DelegateAction(OpenSilo, async (a, _) => await host.OpenSiloAsync((string) a["Path"]!, a["ForceReopen"] is true),
                                             [
                                                 new ActionParameter("Path", typeof(string)),
                                                 new ActionParameter("ForceReopen", typeof(bool?), false)
                                             ],
                                             "Opens (or re-opens) a Silo from a path or URI", true));

        registry.Register(new DelegateAction(Exit, async (a, _) =>
                                                   {
                                                       await host.ExitAsync(a["SaveChanges"] is not false);
                                                       return null;
                                                   },
                                             [new ActionParameter("SaveChanges", typeof(bool), false, true)],
                                             "Exits the application after saving (or discarding) any Silos", true));
    }
}
