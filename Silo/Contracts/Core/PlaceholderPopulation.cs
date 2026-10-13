using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Silo.DbModel;

namespace Silo.Contracts.Core;

/// <summary>
///     One placeholder handed to <see cref="IPlaceholderPopulationDialog.AddPlaceholder" />: the placeholder, the template that
///     defined it, and whether the person has to fill it in.
/// </summary>
public sealed record PlaceholderRequest(Placeholder Placeholder, SiloTemplate? Source, bool IsRequired);

/// <summary>
///     Gives a component a name, description and icon wherever a person picks components (for instance in
///     <see cref="IPlaceholderPopulationDialog" />). Without it the type's own name is used.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class ComponentInfoAttribute(string name) : Attribute
{
    /// <summary>The name shown to the person, e.g. <c>Local Directory</c>.</summary>
    public string Name { get; } = name;

    /// <summary>A sentence or two on what the component does; shown as its tooltip.</summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Where to load an icon from: an absolute URI (<c>pack://application:,,,/Silo;component/Resources/folder.png</c>)
    ///     or a path to an image file. Wins over <see cref="Glyph" />.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>A single character from the Segoe MDL2 Assets font (e.g. <c>""</c> for a folder) to draw as the icon.</summary>
    public string? Glyph { get; set; }

    /// <summary>Keeps the component out of pickers (for a base class or a component that is only built in code).</summary>
    public bool Hidden { get; set; }
}

/// <summary>A component that can be picked: the type to build and how to present it.</summary>
public sealed record ComponentInfo(Type Type, string Name, string? Description, Uri? Icon, string? Glyph)
{
    public override string ToString()
    {
        return Name;
    }
}

/// <summary>One placeholder, worked out for the dialog: what it stands for and whether that is one thing or a list.</summary>
public sealed class PlaceholderSlot
{
    public PlaceholderSlot(Placeholder placeholder, SiloTemplate? source = null, bool isRequired = true)
    {
        Placeholder = placeholder;
        Source      = source;
        IsRequired  = isRequired;
        var type = placeholder.PlaceholderForType;

        var isList = false;
        Type        = type;
        ElementType = type is null ? null : PlaceholderComponents.ElementTypeOf(type, out isList);
        IsList      = type is not null && isList;
        Title = !string.IsNullOrWhiteSpace(placeholder.Name)
                    ? placeholder.Name!.Trim()
                    : type is null
                        ? "Placeholder"
                        : PlaceholderComponents.Humanize(PlaceholderComponents.TypeLabel(ElementType ?? type));
    }

    /// <summary>The placeholder being filled in.</summary>
    public Placeholder Placeholder { get; }

    /// <summary>The template that defined the placeholder, if known.</summary>
    public SiloTemplate? Source { get; }

    /// <summary>Whether the person must fill it in (a list must then have at least one entry); otherwise it can be left empty.</summary>
    public bool IsRequired { get; }

    /// <summary>The type the placeholder stands for (<see cref="Placeholder.PlaceholderForType" />); null if it does not say.</summary>
    public Type? Type { get; }

    /// <summary>The type each chosen component must be assignable to: <see cref="Type" /> itself, or a list's item type.</summary>
    public Type? ElementType { get; }

    /// <summary>True if the placeholder takes an ordered list of components rather than one.</summary>
    public bool IsList { get; }

    /// <summary>Whether there is anything to pick: a placeholder that does not say what type it stands for cannot be filled in here.</summary>
    public bool IsPickable => ElementType is not null;

    /// <summary>The step's title: the placeholder's name, or failing that its type.</summary>
    public string Title { get; }

    /// <summary>"Type needed" text: <c>IObservableDataSource</c>, or <c>list of IDataTransform</c>.</summary>
    public string TypeLabel => ElementType is null ? "(not specified)" : IsList ? "list of " + PlaceholderComponents.TypeLabel(ElementType) : PlaceholderComponents.TypeLabel(ElementType);

    /// <summary>The description shown to the person, with the line breaks a XAML attribute tends to carry flattened.</summary>
    public string? Description => string.IsNullOrWhiteSpace(Placeholder.Description) ? null : Regex.Replace(Placeholder.Description, @"\s+", " ").Trim();
}

/// <summary>The logic behind the placeholder dialog: ordering the steps, finding the components, building the values. No UI in here.</summary>
public static class PlaceholderComponents
{
    private static readonly MethodInfo GetConstructableMethod = typeof(IReflectionProvider).GetMethod(nameof(IReflectionProvider.GetConstructable))!;

    /// <summary>
    ///     Turns placeholders into steps, in the order they should be asked: a placeholder's
    ///     <see cref="Placeholder.DependsOn" /> come before it (an entry can be a placeholder, a placeholder's name, or an
    ///     array/list of those), and otherwise the given order is kept. A dependency cycle is broken where it is found
    ///     rather than reported. Dependencies on placeholders that are not in the list are ignored, and a placeholder that
    ///     appears twice is asked once.
    /// </summary>
    public static List<PlaceholderSlot> Order(IEnumerable<PlaceholderRequest> requests)
    {
        var all = new List<Placeholder>();
        var by  = new Dictionary<Placeholder, PlaceholderRequest>(ReferenceEqualityComparer.Instance);
        foreach (var r in requests)
        {
            if (r?.Placeholder is not { } p) continue;

            if (by.TryGetValue(p, out var first))
            {
                if (r.IsRequired && !first.IsRequired) by[p] = first with { IsRequired = true }; // asked twice: required if either asked for it
                continue;
            }

            by[p] = r;
            all.Add(p);
        }

        var ordered = new List<Placeholder>();
        var state   = new HashSet<Placeholder>(ReferenceEqualityComparer.Instance);

        void Visit(Placeholder p)
        {
            if (!state.Add(p)) return; // already placed, or part of a cycle that is being broken here

            foreach (var dependency in DependenciesOf(p, all))
                Visit(dependency);

            ordered.Add(p);
        }

        foreach (var p in all) Visit(p);
        return ordered.Select(p => new PlaceholderSlot(p, by[p].Source, by[p].IsRequired)).ToList();
    }

    private static IEnumerable<Placeholder> DependenciesOf(Placeholder p, List<Placeholder> all)
    {
        foreach (var entry in Flatten(p.DependsOn))
        {
            var match = entry switch
                        {
                            Placeholder other => all.FirstOrDefault(x => ReferenceEquals(x, other)),
                            string name       => all.FirstOrDefault(x => !ReferenceEquals(x, p) && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)),
                            _                 => null
                        };
            if (match is not null && !ReferenceEquals(match, p)) yield return match;
        }
    }

    private static IEnumerable<object> Flatten(object? value)
    {
        switch (value)
        {
            case null: yield break;
            case string s: yield return s; yield break;
            case Placeholder p: yield return p; yield break;
            case IEnumerable many:
                foreach (var item in many)
                foreach (var inner in Flatten(item))
                    yield return inner;

                yield break;
            default: yield return value; yield break;
        }
    }

    /// <summary>
    ///     What each chosen component must be assignable to: the item type if <paramref name="type" /> is a list
    ///     (<c>T[]</c>, <c>IEnumerable&lt;T&gt;</c>, <c>List&lt;T&gt;</c>, <see cref="IDataTransformGroup" />...), else the type itself.
    /// </summary>
    public static Type ElementTypeOf(Type type, out bool isList)
    {
        isList = true;

        if (type.IsArray) return type.GetElementType()!;
        if (type.IsGenericType && ListDefinitions.Contains(type.GetGenericTypeDefinition())) return type.GetGenericArguments()[0];

        for (var b = type.BaseType; b is not null; b = b.BaseType)
            if (b.IsGenericType && (b.GetGenericTypeDefinition() == typeof(List<>) || b.GetGenericTypeDefinition() == typeof(System.Collections.ObjectModel.Collection<>)))
                return b.GetGenericArguments()[0];

        if (typeof(IDataTransformGroup).IsAssignableFrom(type)) return typeof(IDataTransform);

        isList = false;
        return type;
    }

    /// <summary>Whether a placeholder of this type takes a list of components (see <see cref="ElementTypeOf" />).</summary>
    public static bool IsListType(Type type)
    {
        ElementTypeOf(type, out var isList);
        return isList;
    }

    private static readonly HashSet<Type> ListDefinitions =
    [
        typeof(IEnumerable<>), typeof(ICollection<>), typeof(IList<>), typeof(IReadOnlyCollection<>), typeof(IReadOnlyList<>),
        typeof(List<>), typeof(System.Collections.ObjectModel.Collection<>), typeof(System.Collections.ObjectModel.ObservableCollection<>)
    ];

    /// <summary>
    ///     Makes the value for a list placeholder out of the chosen components, as the type the placeholder asks for: an
    ///     array, a <see cref="DataTransformGroup" /> for an <see cref="IDataTransformGroup" />, a
    ///     <see cref="List{T}" /> for <c>IEnumerable&lt;T&gt;</c>/<c>IList&lt;T&gt;</c>..., or an instance of the collection
    ///     class itself.
    /// </summary>
    public static object CreateList(Type placeholderType, IReadOnlyList<object> items)
    {
        var element = ElementTypeOf(placeholderType, out _);

        if (placeholderType.IsArray)
        {
            var array = Array.CreateInstance(element, items.Count);
            for (var i = 0; i < items.Count; i++) array.SetValue(items[i], i);
            return array;
        }

        if (element == typeof(IDataTransform) && placeholderType.IsAssignableFrom(typeof(DataTransformGroup)))
        {
            var group = new DataTransformGroup();
            foreach (var item in items) group.Add((IDataTransform)item);
            return group;
        }

        var listType = typeof(List<>).MakeGenericType(element);
        object list;
        if (placeholderType.IsAssignableFrom(listType))
        {
            list = Activator.CreateInstance(listType)!;
        }
        else if (!placeholderType.IsInterface && !placeholderType.IsAbstract && placeholderType.GetConstructor(Type.EmptyTypes) is not null &&
                 typeof(IList).IsAssignableFrom(placeholderType))
        {
            list = Activator.CreateInstance(placeholderType)!;
        }
        else
        {
            list = Activator.CreateInstance(listType)!;
        }

        var target = (IList)list;
        foreach (var item in items) target.Add(item);
        return list;
    }

    /// <summary>
    ///     The components that can stand in for <paramref name="elementType" />: the types DI can build that are assignable
    ///     to it, presented through their <see cref="ComponentInfoAttribute" />, hidden ones left out, sorted by name.
    /// </summary>
    public static List<ComponentInfo> Find(Type elementType, IReflectionProvider reflection)
    {
        ArgumentNullException.ThrowIfNull(elementType);
        ArgumentNullException.ThrowIfNull(reflection);

        if (elementType.IsGenericTypeDefinition || elementType.ContainsGenericParameters) return [];

        List<Type> types;
        try
        {
            types = (List<Type>)GetConstructableMethod.MakeGenericMethod(elementType).Invoke(reflection, [TypeCategory.All])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }

        return types.Where(t => !t.IsAbstract && !t.IsInterface && !t.IsGenericTypeDefinition && elementType.IsAssignableFrom(t))
                    .Distinct()
                    .Select(Describe)
                    .Where(c => c is not null)
                    .Select(c => c!)
                    .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(c => c.Type.FullName, StringComparer.Ordinal)
                    .ToList();
    }

    /// <summary>How a component is presented; null if it asked to be hidden.</summary>
    public static ComponentInfo? Describe(Type type)
    {
        var info = type.GetCustomAttribute<ComponentInfoAttribute>(false);
        if (info?.Hidden == true) return null;

        Uri? icon = null;
        if (!string.IsNullOrWhiteSpace(info?.Icon) && Uri.TryCreate(info.Icon, UriKind.Absolute, out var uri)) icon = uri;

        return new ComponentInfo(type, !string.IsNullOrWhiteSpace(info?.Name) ? info.Name.Trim() : Humanize(type.Name), info?.Description?.Trim(), icon,
                                 string.IsNullOrEmpty(info?.Glyph) ? null : info.Glyph);
    }

    /// <summary>"LocalDirectorySource" becomes "Local Directory Source"; a generic type's arity suffix is dropped.</summary>
    public static string Humanize(string name)
    {
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];

        name = name.Replace('_', ' ').Trim();
        name = Regex.Replace(name, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return name.Length == 0 ? "(unnamed)" : name;
    }

    /// <summary>A short, readable type name for "Type needed": <c>IObservableDataSource</c>, <c>IEnumerable&lt;IDataTransform&gt;</c>.</summary>
    public static string TypeLabel(Type type)
    {
        if (!type.IsGenericType) return type.IsArray ? TypeLabel(type.GetElementType()!) + "[]" : type.Name;

        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeLabel)) + ">";
    }

    /// <summary>
    ///     Whether the component has settings the person could change: a public property with a public setter. (Used to
    ///     decide between showing a settings pane and saying there is nothing to configure.)
    /// </summary>
    public static bool IsConfigurable(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                   .Any(p => p.GetIndexParameters().Length == 0 && p.GetSetMethod() is not null);
    }
}
