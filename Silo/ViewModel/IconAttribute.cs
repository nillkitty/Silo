using System.Reflection;

namespace Silo.ViewModel;

/// <summary>
///     Sets the fallback Icon Key for a given type (model or node)
/// </summary>
public class IconAttribute(string iconKey) : Attribute
{
    public string IconKey { get; set; } = iconKey.Required();

    /// <summary>The icon key a type has been given, or null when it has none.</summary>
    public static string? KeyOf(Type type)
    {
        return type.GetCustomAttribute<IconAttribute>(true)?.IconKey;
    }
}