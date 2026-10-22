namespace Silo.Model;

/// <summary>
///     Marks a property of a <see cref="ModelBase" /> as a preference: the model registers it (see
///     <see cref="IPreferenceProvider" />), and its value is kept in the Silo's <c>Pref</c> table under
///     <see cref="PrefKey" />.
///     A yes/no preference is better marked with <see cref="ToggleAttribute" />.
/// </summary>
/// <remarks>
///     The property needs a getter and a setter, and its type has to be one <see cref="PreferenceValues" /> can write as
///     text: a
///     number, <see cref="bool" />, <see cref="string" />, an enum, <see cref="Uri" />, <see cref="DateTime" />,
///     <see cref="TimeSpan" /> or <see cref="Guid" /> (or a nullable one). <see cref="PrefKey" /> must be different for
///     every
///     preference of every model: they share one table.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public class PreferenceAttribute : Attribute
{
    /// <summary>Where the preference is listed when preferences are grouped; null for no group.</summary>
    public string? Category { get; set; }

    /// <summary>
    ///     The value used when the Silo holds none. When it is given, it wins over the value the property starts with;
    ///     when it is not, the property's initial value is the default.
    /// </summary>
    public object? DefaultValue { get; set; }

    /// <summary>What the preference does, in a sentence the person can read in a settings list.</summary>
    public string? Description { get; set; }

    /// <summary>
    ///     The name the preference should use in the UI
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>The key of the preference's row in the Silo's <c>Pref</c> table (<c>silo.rememberConnections</c>).</summary>
    public string PrefKey { get; set; }

    public PreferenceAttribute(string displayName)
    {
        DisplayName = displayName.Required();
    }

    public PreferenceAttribute(string prefKey, string displayName) : this(displayName)
    {
        PrefKey = prefKey;
    }

    public PreferenceAttribute(string displayName, string prefKey, string description) : this(displayName, prefKey)
    {
        Description = description;
        PrefKey     = prefKey;
    }

    public PreferenceAttribute(string displayName, string prefKey, string description, object defaultValue) :
        this(displayName, prefKey, description)
    {
        DefaultValue = defaultValue;
    }
}