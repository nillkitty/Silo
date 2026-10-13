using System.Reflection;
using Assembly = Silo.DbModel.Global.Assembly;

namespace Silo.Contracts.Providers.Reflection;

public interface IAssemblyReference
{
    object GetAssembly();
}

public interface IAssemblyFilter : IReflectionFilter<Assembly>;

public interface ITypeFilter : IReflectionFilter<Type>;

public interface IPropertyFilter : IReflectionFilter<PropertyInfo>;

public interface IEventFilter : IReflectionFilter<EventInfo>;

public interface IMethodFilter : IReflectionFilter<MethodInfo>;

public interface IConstructorFilter : IReflectionFilter<ConstructorInfo>;

public interface IFieldFilter : IReflectionFilter<FieldInfo>;

public interface IInterfaceFilter : IReflectionFilter<Type>;

public interface IReflectionFilter<TType> : IReflectionFilter
{
    Type IReflectionFilter.TypeFiltered => typeof(TType);
}

public interface IReflectionFilter
{
    /// <summary>
    ///     Gets the type of reflection items which this filter filters.
    /// </summary>
    Type TypeFiltered { get; }

    /// <summary>
    ///     Checks if the item is filtered.
    /// </summary>
    bool IsIgnore(object o);
}

public interface ITypeClassifier
{
    TypeCategory Classify(Type type);
}

public interface IReflectionProvider
{
    /// <summary>
    ///     Gets the configured filters
    /// </summary>
    IEnumerable<IReflectionFilter> Filters { get; }

    /// <summary>
    ///     Called once to initialize the provider
    /// </summary>
    void Initialize();

    /// <summary>
    ///     Adds a filter to the reflection provider
    /// </summary>
    /// <param name="filter">An assembly, type, interface, property, event, nethod, field, or constructor filter.</param>
    bool AddFilter(IReflectionFilter filter);

    /// <summary>
    ///     Removes a filter previously added to the provider
    /// </summary>
    bool RemoveFilter(IReflectionFilter filter);

    /// <summary>
    ///     Loads an assembly for reflection only
    /// </summary>
    Assembly Load(IAssemblyReference r, TypeCategory category);

    /// <summary>
    ///     Gets all types implementing or inheriting <typeparamref name="TType" /> which
    ///     are constructable via DI
    /// </summary>
    /// <typeparam name="TType">The type of object which the resulting types must be assignable to</typeparam>
    /// <returns>Returns a list of matching Types.</returns>
    List<Type> GetConstructable<TType>(TypeCategory category = TypeCategory.All);

    /// <summary>
    ///     Gets all (unfiltered) types in the matching type category/categories
    /// </summary>
    List<Type> GetTypes(TypeCategory category = TypeCategory.All);

    /// <summary>
    ///     Finds a type given the specified name and namespace context and type category filter
    /// </summary>
    Type? FindType(string name, string defaultNamespace = null, TypeCategory category = TypeCategory.All);

    /// <summary>
    ///     Event which is raised when new types are loaded
    /// </summary>
    event EventHandler? TypesLoaded;
}

[Flags]
public enum TypeCategory
{
    None                   = 0,
    SiloTypes              = 1,
    MicrosoftTypes         = 2,
    ThirdPartyLibraryTypes = 4,
    UserTypes              = 8,

    All = 0xFFFF
}