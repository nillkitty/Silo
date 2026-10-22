namespace Silo.Model;

/// <summary>
///     A <see cref="PreferenceAttribute" /> that is a yes/no choice: the property it marks has to be a
///     <see cref="bool" />.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class ToggleAttribute : PreferenceAttribute
{
    /// <summary>On or off when the Silo holds no value; null to use the property's initial value.</summary>
    public new bool? DefaultValue
    {
        get => base.DefaultValue as bool?;
        set => base.DefaultValue = value;
    }

    public ToggleAttribute(string displayName) : base(displayName)
    {
    }

    public ToggleAttribute(string displayName, string prefKey, string description) : base(displayName, prefKey, description)
    {
    }

    public ToggleAttribute(string displayName, string prefKey, string description, bool defaultValue) : base(displayName, prefKey,
                                                                                                             description, defaultValue)
    {
    }
}