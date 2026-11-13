using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Silo.Ui;
using Telefrag.DI;

namespace Silo.Tools.TypeMap;

/// <summary>A DI registration a type declares: the service it is registered as, its lifetime and its key (if any).</summary>
public sealed record TypeRegistration(Type Service, string Lifetime, string? Key);

/// <summary>One public type, as the Type Map shows it.</summary>
public sealed class TypeMapEntry
{
    public required Type Type { get; init; }

    /// <summary>What kind of type it is: interface, class, abstract class, static class, record, struct, enum or delegate.</summary>
    public required string Kind { get; init; }

    /// <summary>The name as written in C#, generic parameters included, nested types after their outer type: <c>Outer.Inner&lt;T&gt;</c>.</summary>
    public required string Name { get; init; }

    public string Namespace => Type.Namespace ?? "(global)";

    /// <summary>The base class, unless it is one every type of its kind has (object, ValueType, Enum, MulticastDelegate).</summary>
    public Type? Base { get; init; }

    /// <summary>The interfaces the type itself adds: not the ones its base class or its other interfaces already bring.</summary>
    public IReadOnlyList<Type> Interfaces { get; init; } = [];

    /// <summary>How the type is registered with the container (see <see cref="TypeMapModel.RegistrationOf" />), or null.</summary>
    public TypeRegistration? Registration { get; init; }

    /// <summary>An interface marked <see cref="RequireRegistrationAttribute" />: every implementation must be registered.</summary>
    public bool RequiresRegistration { get; init; }

    public TypeDoc? Doc { get; init; }

    /// <summary>The text of the type's <see cref="DescriptionAttribute" />, if it has one.</summary>
    public string? Description { get; init; }

    /// <summary>The public members the type declares itself, one line each (enum values for an enum).</summary>
    public IReadOnlyList<string> Members { get; init; } = [];
}

/// <summary>
///     Reads the public types of an assembly for the Type Map: what kind each is, what it derives from and implements, how it
///     is registered with the container, its XML documentation (<see cref="XmlDocs" />) and its <c>[Description]</c>.
/// </summary>
public static class TypeMapModel
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>The assemblies worth mapping: Silo's own that are loaded now, Silo first.</summary>
    public static IReadOnlyList<Assembly> Assemblies()
    {
        var silo = typeof(TypeMapModel).Assembly;
        return AppDomain.CurrentDomain.GetAssemblies()
                        .Where(a => !a.IsDynamic && a.GetName().Name is { } n && n.StartsWith("Silo", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(a => a == silo ? 0 : 1)
                        .ThenBy(a => a.GetName().Name)
                        .ToList();
    }

    /// <summary>
    ///     The public types of <paramref name="assembly" /> whose namespace or name contains one of the
    ///     <paramref name="filters" /> (all of them when there are none).
    /// </summary>
    public static List<TypeMapEntry> Read(Assembly assembly, IReadOnlyCollection<string> filters)
    {
        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.OfType<Type>().Where(t => t.IsVisible).ToArray();
        }

        var docs = XmlDocs.For(assembly);

        return types.Where(t => !t.IsDefined(typeof(CompilerGeneratedAttribute), false) && !t.Name.Contains('<') && t.Namespace != "XamlGeneratedNamespace")
                    .Where(t => filters.Count == 0 || filters.Any(f => (t.FullName ?? t.Name).Contains(f, StringComparison.OrdinalIgnoreCase)))
                    .Select(t => new TypeMapEntry
                                 {
                                     Type                 = t,
                                     Kind                 = KindOf(t),
                                     Name                 = NameOf(t),
                                     Base                 = BaseOf(t),
                                     Interfaces           = OwnInterfaces(t),
                                     Registration         = RegistrationOf(t),
                                     RequiresRegistration = t.IsInterface && t.IsDefined(typeof(RequireRegistrationAttribute), false),
                                     Doc                  = docs.Of(t),
                                     Description          = t.GetCustomAttribute<DescriptionAttribute>(false)?.Description is { Length: > 0 } d ? d : null,
                                     Members              = MembersOf(t)
                                 })
                    .OrderBy(e => e.Namespace, StringComparer.Ordinal)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    /// <summary>
    ///     How the type is registered with the container: the same rule <c>App.DiscoverRegistrations</c> applies -- only public,
    ///     concrete classes; the first <c>[Register]</c>, else the first <c>[Transient]</c>, else the first <c>[Singleton]</c>
    ///     (inherited ones count).
    /// </summary>
    public static TypeRegistration? RegistrationOf(Type t)
    {
        if (t.IsAbstract || t.IsInterface || !t.IsPublic) return null;

        try
        {
            if (t.GetCustomAttributes<RegisterAttribute>(true).FirstOrDefault() is { } ra)
                return new TypeRegistration(ra.ServiceType, ra.IsSingleton ? "singleton" : "transient", KeyOf(ra.Key));
            if (t.GetCustomAttributes<TransientAttribute>(true).FirstOrDefault() is { } ta)
                return new TypeRegistration(ta.ServiceType, "transient", KeyOf(ta.Name));
            if (t.GetCustomAttributes<SingletonAttribute>(true).FirstOrDefault() is { } sa)
                return new TypeRegistration(sa.ServiceType, "singleton", KeyOf(sa.Name));
        }
        catch
        {
            /* an attribute whose type cannot be loaded: the type is shown unregistered */
        }

        return null;
    }

    private static string? KeyOf(object? key)
    {
        return key?.ToString() is { Length: > 0 } k ? k : null;
    }

    public static string KindOf(Type t)
    {
        if (t.IsInterface) return "interface";
        if (t.IsEnum) return "enum";
        if (typeof(Delegate).IsAssignableFrom(t)) return "delegate";
        if (t.IsValueType) return IsRecord(t) ? "record struct" : "struct";
        if (t.IsAbstract && t.IsSealed) return "static class";
        if (IsRecord(t)) return t.IsAbstract ? "abstract record" : "record";
        return t.IsAbstract ? "abstract class" : "class";
    }

    private static bool IsRecord(Type t)
    {
        // records have a compiler-made EqualityContract (classes) or PrintMembers (both)
        return t.GetProperty("EqualityContract", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) is not null ||
               t.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, [typeof(StringBuilder)]) is not null;
    }

    /// <summary>A type's name as written in C#: <c>Dictionary&lt;TKey, TValue&gt;</c>, nested ones after their outer type.</summary>
    public static string NameOf(Type t)
    {
        string own = t.Name;
        int tick = own.IndexOf('`');
        if (tick >= 0) own = own[..tick];

        if (t.IsGenericType)
        {
            // a nested type's generic arguments include its outer type's: show only its own
            int outer = t.DeclaringType?.GetGenericArguments().Length ?? 0;
            var args = t.GetGenericArguments().Skip(outer).ToList();
            if (args.Count > 0) own += "<" + string.Join(", ", args.Select(NameOf)) + ">";
        }

        return t.IsNested && !t.IsGenericParameter && t.DeclaringType is { } d ? NameOf(d) + "." + own : own;
    }

    private static Type? BaseOf(Type t)
    {
        if (t.IsInterface || t.IsEnum || t.IsValueType || typeof(Delegate).IsAssignableFrom(t)) return null;
        return t.BaseType is { } b && b != typeof(object) ? b : null;
    }

    private static IReadOnlyList<Type> OwnInterfaces(Type t)
    {
        if (t.IsEnum || typeof(Delegate).IsAssignableFrom(t)) return [];

        try
        {
            var all       = t.GetInterfaces();
            var inherited = new HashSet<Type>(t.BaseType?.GetInterfaces() ?? []);
            foreach (var i in all)
                foreach (var parent in i.GetInterfaces())
                    inherited.Add(parent);

            return all.Where(i => !inherited.Contains(i)).OrderBy(i => i.Name).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<string> MembersOf(Type t)
    {
        try
        {
            if (t.IsEnum) return Enum.GetNames(t);

            if (typeof(Delegate).IsAssignableFrom(t))
                return t.GetMethod("Invoke") is { } invoke ? [Signature(invoke)] : [];

            var lines = new List<string>();
            lines.AddRange(t.GetProperties(Declared).Where(p => p.GetIndexParameters().Length == 0)
                            .Select(p => $"{p.Name}: {NameOf(p.PropertyType)}"));
            lines.AddRange(t.GetEvents(Declared).Select(e => $"event {e.Name}"));
            lines.AddRange(t.GetFields(Declared).Where(f => !f.IsSpecialName).Select(f => $"{f.Name}: {NameOf(f.FieldType)}"));
            lines.AddRange(t.GetMethods(Declared).Where(m => !m.IsSpecialName && !m.Name.Contains('<') && m.DeclaringType == t && !IsObjectNoise(m))
                            .Select(Signature)
                            .Distinct());
            return lines;
        }
        catch
        {
            return [];
        }
    }

    private static bool IsObjectNoise(MethodInfo m)
    {
        return m.Name is "ToString" or "Equals" or "GetHashCode" or "Deconstruct" or "PrintMembers" && m.IsVirtual || m.Name == "<Clone>$";
    }

    private static string Signature(MethodInfo m)
    {
        string args = string.Join(", ", m.GetParameters().Select(p => NameOf(p.ParameterType)));
        string ret  = m.ReturnType == typeof(void) ? "" : ": " + NameOf(m.ReturnType);
        return $"{m.Name}({args}){ret}";
    }
}
