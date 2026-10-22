namespace Silo.Providers.Data;

/// <summary>One stored preference: its key and its value written as text (see <c>PreferenceValues</c>).</summary>
public sealed record PreferenceRecord(string Key, string Data);

/// <summary>
///     Keeps preferences in the Silo (the <c>Pref</c> table, one row per key). A person's own row wins over the Silo's
///     shared one
///     (a row without a user), key by key, and a person's change is written as their own row.
/// </summary>
/// <remarks>
///     Setting a preference to a new value is a real change to the Silo: it raises
///     <see cref="IDatabaseChanges.Changed" />.
/// </remarks>
public interface IPreferenceStore
{
    /// <summary>Every preference that applies to the current person: theirs, else the Silo's shared one.</summary>
    IReadOnlyList<PreferenceRecord> GetPreferences();

    /// <summary>Writes the current person's value for <paramref name="key" />; nothing happens if it is already that.</summary>
    void SetPreference(string key, string data);
}