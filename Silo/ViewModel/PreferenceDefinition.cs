using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace Silo.Model;

/// <summary>
///     What is declared about a preference, once for the model class (as opposed to <see cref="Preference" />, which is one model
///     instance's): its key, text, type and default, taken from the <see cref="PreferenceAttribute" /> on the property.
/// </summary>
public sealed class PreferenceDefinition
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<PreferenceDefinition>> Cache = new();

    private PreferenceDefinition(PropertyInfo property, PreferenceAttribute attribute, object? declaredDefault)
    {
        Property        = property;
        Key             = attribute.PrefKey;
        Description     = attribute.Description ?? string.Empty;
        Category        = attribute.Category;
        IsToggle        = attribute is ToggleAttribute;
        DeclaredDefault = declaredDefault;
    }

    public string? Category { get; }

    /// <summary>The default the attribute gives (converted to the property's type); null when it gives none.</summary>
    public object? DeclaredDefault { get; }

    public string Description { get; }

    /// <summary>True when the attribute is a <see cref="ToggleAttribute" />, i.e. the preference is on or off.</summary>
    public bool IsToggle { get; }

    public string Key { get; }

    public PropertyInfo Property { get; }

    /// <summary>The type of the preference's value (the property's type).</summary>
    public Type ValueType => Property.PropertyType;

    /// <summary>The preferences <paramref name="modelType" /> declares or inherits, base classes first, each class's in declaration order.</summary>
    /// <exception cref="InvalidOperationException">A marked property cannot be a preference, or two share a key: a mistake in the code, so loud.</exception>
    public static IReadOnlyList<PreferenceDefinition> For(Type modelType)
    {
        return Cache.GetOrAdd(modelType.Required(), Build);
    }

    private static IReadOnlyList<PreferenceDefinition> Build(Type modelType)
    {
        var chain = new List<Type>();
        for (var t = modelType; t is not null && t != typeof(object); t = t.BaseType) chain.Insert(0, t);

        var result = new List<PreferenceDefinition>();
        var seen   = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

        foreach (var type in chain)
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (property.GetCustomAttribute<PreferenceAttribute>() is not { } attribute) continue;

            string where = $"{property.DeclaringType?.Name}.{property.Name}";

            if (property.GetIndexParameters().Length > 0 || property.GetMethod is null || property.SetMethod is null)
                throw new InvalidOperationException($"{where} is marked as a preference, so it needs a getter and a setter (and no index).");
            if (!PreferenceValues.IsSupported(property.PropertyType))
                throw new InvalidOperationException($"{where} is a {property.PropertyType.Name}, which cannot be kept as a preference.");
            if (attribute is ToggleAttribute && (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) != typeof(bool))
                throw new InvalidOperationException($"{where} is marked [Toggle], so it has to be a bool, not a {property.PropertyType.Name}.");
            if (string.IsNullOrWhiteSpace(attribute.PrefKey))
                throw new InvalidOperationException($"{where} has an empty preference key.");
            if (seen.TryGetValue(attribute.PrefKey, out var other))
                throw new InvalidOperationException($"{where} and {other.DeclaringType?.Name}.{other.Name} have the same preference key \"{attribute.PrefKey}\".");

            seen[attribute.PrefKey] = property;
            result.Add(new PreferenceDefinition(property, attribute, DeclaredDefaultOf(property, attribute, where)));
        }

        return result;
    }

    private static object? DeclaredDefaultOf(PropertyInfo property, PreferenceAttribute attribute, string where)
    {
        var declared = attribute is ToggleAttribute toggle ? toggle.DefaultValue : attribute.DefaultValue;
        if (declared is null) return null;

        // a constant in an attribute is an int where the property is a double, a string where it is an enum...: go through text
        if (!PreferenceValues.TryFromText(property.PropertyType, PreferenceValues.ToText(declared), out object? converted) || converted is null)
            throw new InvalidOperationException($"The default \"{declared}\" of {where} is not a {property.PropertyType.Name}.");

        return converted;
    }

    /// <summary>
    ///     Keys that more than one of the given model classes use. Preferences share the Silo's <c>Pref</c> table, so every key has to
    ///     be used once; a class that only inherits a preference does not count again.
    /// </summary>
    public static IReadOnlyList<string> DuplicateKeys(IEnumerable<Type> modelTypes)
    {
        return modelTypes.SelectMany(t => For(t).Where(d => d.Property.DeclaringType == t))
                         .GroupBy(d => d.Key, StringComparer.Ordinal)
                         .Where(g => g.Count() > 1)
                         .Select(g => g.Key)
                         .OrderBy(k => k, StringComparer.Ordinal)
                         .ToList();
    }
}
