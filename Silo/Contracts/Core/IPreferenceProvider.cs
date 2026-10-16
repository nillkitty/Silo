using Silo.Model;

namespace Silo.Contracts.Core;

/// <summary>
///     Implemented by anything that has preferences to show or change -- every <see cref="ModelBase" />, whose properties marked
///     with <see cref="PreferenceAttribute" /> or <see cref="ToggleAttribute" /> are its preferences.
/// </summary>
public interface IPreferenceProvider
{
    /// <summary>The preferences, in the order their properties are declared. Empty when there are none.</summary>
    IReadOnlyList<Preference> Preferences { get; }
}
