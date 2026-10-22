using System.Reflection;
using Serilog;

namespace Silo.Connectors;

/// <summary>
///     Finds the connectors and destination discoveries Silo has, for the New Connection dialog. Whatever is registered in the
///     component container (<c>[Singleton(typeof(IConnector))]</c> and the like) comes first; then any other public class of
///     the Silo assembly that implements the interface and can be created without arguments is added, because not every
///     connector is registered yet. A connector that is itself a connection (<c>VirtualRoot</c>) is only listed when it is
///     registered: creating one has side effects.
/// </summary>
public static class ConnectorCatalog
{
    /// <summary>All the connectors, by display name.</summary>
    public static IReadOnlyList<IConnector> Connectors()
    {
        var found = Gather<IConnector>(Registered<IConnector>(), t => !typeof(IConnection).IsAssignableFrom(t));
        return found.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The connectors that take URIs (the URI tab and the Destinations tab find the connector for a URI from these).</summary>
    public static IReadOnlyList<IUriConnector> UriConnectors(IEnumerable<IConnector> connectors)
    {
        return connectors.OfType<IUriConnector>().ToList();
    }

    /// <summary>All the destination discoveries.</summary>
    public static IReadOnlyList<IDiscovery<DestinationEntry>> Discoveries()
    {
        return Gather<IDiscovery<DestinationEntry>>(Registered<IDiscovery<DestinationEntry>>(), _ => true);
    }

    /// <summary>The destinations the connectors list themselves, each with the name of the connector that listed it.</summary>
    public static IEnumerable<(string Provider, DestinationEntry Entry)> RegisteredDestinations(IEnumerable<IConnector> connectors)
    {
        foreach (var connector in connectors)
        {
            if (connector is not IDestinationSource source) continue;

            IReadOnlyList<DestinationEntry> entries;
            try
            {
                entries = source.GetRegisteredDestinations().ToList();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "{source} could not list its destinations", connector.GetType().Name);
                continue;
            }

            foreach (var entry in entries)
                yield return (connector.DisplayName, entry);
        }
    }

    /// <summary>What the component container has registered as <typeparamref name="T" />; nothing if it cannot be asked (no app yet, as in tests).</summary>
    private static IEnumerable<T>? Registered<T>() where T : class
    {
        try
        {
            return App.Resolve<IEnumerable<T>>();
        }
        catch (Exception ex)
        {
            // Not Debug: when this fails, every registered connector is missing from the list (so saved connections cannot be brought back).
            Log.Warning(ex, "Could not ask the component container for {type}", typeof(T).Name);
            return null;
        }
    }

    /// <summary>
    ///     The instances in <paramref name="registered" />, then one of each other public class of this assembly that is a
    ///     <typeparamref name="T" /> and has a public parameterless constructor.
    /// </summary>
    internal static List<T> Gather<T>(IEnumerable<T>? registered, Func<Type, bool> include) where T : class
    {
        var result = new List<T>();
        var seen   = new HashSet<Type>();

        foreach (var item in registered ?? [])
            if (seen.Add(item.GetType()))
                result.Add(item);

        foreach (var type in Candidates<T>(typeof(ConnectorCatalog).Assembly))
        {
            if (seen.Contains(type) || !include(type)) continue;

            try
            {
                if (Activator.CreateInstance(type) is T instance)
                {
                    seen.Add(type);
                    result.Add(instance);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "{type} could not be created for the connector list", type.Name);
            }
        }

        return result;
    }

    private static List<Type> Candidates<T>(Assembly assembly)
    {
        var result = new List<Type>();
        foreach (var type in ExportedTypes(assembly))
        {
            if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition) continue;
            if (!typeof(T).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;

            result.Add(type);
        }

        return result;
    }

    private static Type[] ExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not list the types of {assembly}", assembly.GetName().Name);
            return [];
        }
    }
}
