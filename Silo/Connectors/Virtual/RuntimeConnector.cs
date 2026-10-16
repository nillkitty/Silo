using System.Reflection;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors.Virtual;

[Transient(typeof(IConnector))]
public class RuntimeConnector : VirtualRoot, INodeProvider<AppDomain>
{
    public AppDomain? Data => AppDomain.CurrentDomain;

    public RuntimeConnector()
    {
        Node = new AppDomainNode(AppDomain.CurrentDomain);
    }

    public SiloNode? GetNode()
    {
        return Node;
    }
}

/// <summary>
///     The icons of the runtime nodes: keys in <c>Resources/NavIcons.xaml</c> as relative URIs, which is how the
///     Explorer reads one.
/// </summary>
internal static class RuntimeIcons
{
    public static readonly Uri AppDomain = new("Modules", UriKind.Relative);
    public static readonly Uri Assembly  = new("Puzzle", UriKind.Relative);
    public static readonly Uri Resource  = new("Document", UriKind.Relative);
    public static readonly Uri Type      = new("Cube", UriKind.Relative);
}

public record AppDomainNode : SiloNode
{
    public AppDomain Domain { get; init; }

    public AppDomainNode(AppDomain Domain) : base(Domain)
    {
        this.Domain = Domain.Required();
        Icon        = RuntimeIcons.AppDomain;
        TypeName    = "App Domain";
        Text        = Domain.FriendlyName;

        // the assemblies loaded now, by name; read off the UI thread
        ChildLoader = cancel => Task.Run(() => (IReadOnlyList<SiloNode>)Domain.GetAssemblies()
                                                                              .OrderBy(a => a.GetName().Name,
                                                                                       StringComparer.OrdinalIgnoreCase)
                                                                              .Select(a => (SiloNode)new AssemblyNode(a))
                                                                              .ToList(), cancel);
    }
}

public record AssemblyNode : SiloNode
{
    public Assembly Assembly { get; init; }

    /// <summary>The node for the manifest resource called <paramref name="name" />.</summary>
    private AssemblyResourceNode MakeResourceNode(string name)
    {
        return new AssemblyResourceNode(Assembly.GetManifestResourceInfo(name), name);
    }

    /// <summary>
    ///     The types of an assembly, less any that could not be loaded (a type whose base class is in an assembly that is not
    ///     there makes <c>GetTypes</c> throw, though the rest are fine).
    /// </summary>
    public static IReadOnlyList<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>().ToList();
        }
    }

    /// <summary>The names of an assembly's manifest resources; none for a dynamic assembly, which has no manifest to ask.</summary>
    public static IReadOnlyList<string> ResourceNames(Assembly assembly)
    {
        if (assembly.IsDynamic) return [];

        try
        {
            return assembly.GetManifestResourceNames();
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    public AssemblyNode(Assembly Assembly) : base(Assembly)
    {
        this.Assembly = Assembly;
        Text          = Assembly.Required().GetName().FullName;
        TypeName      = "Assembly";
        Icon          = RuntimeIcons.Assembly;

        // types, then resources, each by name; read off the UI thread
        ChildLoader = cancel => Task.Run(() =>
                                         {
                                             var types = LoadableTypes(Assembly)
                                                        .OrderBy(t => t.FullName, StringComparer.Ordinal)
                                                        .Select(t => (SiloNode)new TypeNode(t));
                                             var resources = ResourceNames(Assembly).Order(StringComparer.Ordinal).Select(MakeResourceNode);

                                             return (IReadOnlyList<SiloNode>)[.. types, .. resources];
                                         }, cancel);
    }
}

public record TypeNode : SiloNode
{
    public Type Type { get; init; }

    public TypeNode(Type Type) : base(Type)
    {
        this.Type = Type.Required();
        Text      = Type.FullName ?? Type.Name;
        TypeName  = "Type";
        Icon      = RuntimeIcons.Type;
    }
}

public record AssemblyResourceNode : SiloNode
{
    public ManifestResourceInfo Info { get; init; }

    /// <param name="Info">What the assembly says about the resource.</param>
    /// <param name="name">
    ///     The resource's name. A resource's <c>ManifestResourceInfo.FileName</c> is only set for one that is linked from a
    ///     file, and null for the usual embedded one, so it is no name to show.
    /// </param>
    public AssemblyResourceNode(ManifestResourceInfo Info, string name) : base(Info)
    {
        this.Info = Info.Required();
        Text      = name.Required();
        TypeName  = "Manifest Resource";
        Icon      = RuntimeIcons.Resource;
    }
}