using System.Reflection;
using Serilog;
using Silo.Providers.Action;

namespace Silo.Commanding;

/// <summary>
///     Finds the <see cref="IActionProvider{TType}" />s and asks them for the commands of a model object. Providers are found
///     by scanning the app's assembly for public classes with a parameterless constructor that implement the interface (once,
///     the first time they are needed); a provider living elsewhere is added with <see cref="Register" />.
/// </summary>
public static class ActionProviders
{
    private static readonly object Gate = new();

    // model type -> the providers written for exactly that type, in discovery order
    private static Dictionary<Type, List<IActionProvider>>? _byType;

    /// <summary>
    ///     The commands the providers offer for <paramref name="model" />: those of the providers for its own type first, then
    ///     for each base type, then for the interfaces it implements, then for <see cref="object" />. Empty for null. Does not
    ///     include <see cref="ICommandProvider" /> -- the model's own commands -- which the caller asks the model for.
    /// </summary>
    public static IEnumerable<ProvidedCommand> CommandsFor(object? model)
    {
        if (model is null) yield break;

        foreach (var provider in ProvidersFor(model.GetType()))
        {
            IReadOnlyList<ProvidedCommand> commands;
            try
            {
                commands = provider.GetCommands(model).ToList();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Action provider {provider} failed for {model}", provider.GetType().Name, model.GetType().Name);
                continue;
            }

            foreach (var c in commands) yield return c;
        }
    }

    /// <summary>The providers that apply to objects of <paramref name="modelType" />, most specific first.</summary>
    public static IReadOnlyList<IActionProvider> ProvidersFor(Type modelType)
    {
        var table  = Table();
        var result = new List<IActionProvider>();

        void AddFor(Type t)
        {
            if (table.TryGetValue(t, out var list))
                foreach (var p in list)
                    if (!result.Contains(p))
                        result.Add(p);
        }

        for (var t = modelType; t is not null && t != typeof(object); t = t.BaseType)
            AddFor(t);

        foreach (var i in modelType.GetInterfaces()) AddFor(i);
        AddFor(typeof(object));
        return result;
    }

    /// <summary>Adds a provider (one the assembly scan does not find). It is asked for objects of type <typeparamref name="T" />.</summary>
    public static void Register<T>(IActionProvider<T> provider)
    {
        lock (Gate)
            Add(Table(), typeof(T), provider);
    }

    /// <summary>The provider types of <paramref name="assembly" /> and the model type each is for (what the scan does; exposed for tests).</summary>
    public static IEnumerable<(Type Model, Type Provider)> Scan(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Looking for action providers in {assembly} failed", assembly.GetName().Name);
            yield break;
        }

        foreach (var t in types)
        {
            if (t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition) continue;
            if (t.GetConstructor(Type.EmptyTypes) is null) continue;

            foreach (var i in t.GetInterfaces())
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IActionProvider<>))
                    yield return (i.GetGenericArguments()[0], t);
        }
    }

    private static Dictionary<Type, List<IActionProvider>> Table()
    {
        lock (Gate)
        {
            if (_byType is not null) return _byType;

            var table = new Dictionary<Type, List<IActionProvider>>();
            var made  = new Dictionary<Type, IActionProvider>();
            foreach (var (model, providerType) in Scan(typeof(ActionProviders).Assembly))
            {
                try
                {
                    if (!made.TryGetValue(providerType, out var provider))
                        made[providerType] = provider = (IActionProvider)Activator.CreateInstance(providerType)!;

                    Add(table, model, provider);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Creating the action provider {provider} failed", providerType.Name);
                }
            }

            return _byType = table;
        }
    }

    private static void Add(Dictionary<Type, List<IActionProvider>> table, Type model, IActionProvider provider)
    {
        if (!table.TryGetValue(model, out var list)) table[model] = list = [];
        if (!list.Contains(provider)) list.Add(provider);
    }
}
