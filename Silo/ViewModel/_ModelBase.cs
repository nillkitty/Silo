using System.Linq;
using System.Windows;
using Serilog;
using Silo.Contracts.Core;
using Telefrag;

namespace Silo.Model;

/// <summary>
/// Represents a ViewModel for an item whose DTO is <typeparamref name="TDataObject"/>
/// </summary>
/// <typeparam name="TDataObject"></typeparam>
public class ModelBase<TDataObject> : ModelBase
{
    public TDataObject? Data { get; protected set; }
}

/// <summary>
/// Represents the base class for all ViewModels
/// </summary>
/// <remarks>
///     Its properties marked <see cref="PreferenceAttribute" /> or <see cref="ToggleAttribute" /> are the model's preferences
///     (<see cref="Preferences" />). A model that has somewhere to keep them overrides <see cref="PreferenceStore" /> and calls
///     <see cref="LoadPreferences" /> once that place is there; a model that does not keeps them in memory.
/// </remarks>
public abstract class ModelBase : DependencyObject, ILogContext, IPreferenceProvider
{
    /// <summary>
    /// Gets a logger for the model's scope
    /// </summary>
    public ILogger Logger { get; }

    protected ModelBase()
    {
        Logger = Log.Logger.ForContext(GetType());
    }

    /// <summary>
    ///     The model's preferences: one for each property marked with <see cref="PreferenceAttribute" /> or
    ///     <see cref="ToggleAttribute" />. Made the first time they are asked for, so the defaults are the values the properties start
    ///     with (the model's constructor has run).
    /// </summary>
    public IReadOnlyList<Preference> Preferences => field ??= CreatePreferences();

    /// <summary>Where this model's preferences are kept; null (the default) for nowhere, the preferences then live as long as the model.</summary>
    protected virtual IPreferenceStore? PreferenceStore => null;

    private IReadOnlyList<Preference> CreatePreferences()
    {
        return PreferenceDefinition.For(GetType()).Select(d => new Preference(d, this, () => PreferenceStore)).ToList();
    }

    /// <summary>Gives every property its stored value, or its declared default if nothing is stored. Nothing is written.</summary>
    protected void LoadPreferences()
    {
        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (PreferenceStore is { } store)
                foreach (var record in store.GetPreferences())
                    stored[record.Key] = record.Data;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Reading the stored preferences failed");
        }

        foreach (var preference in Preferences)
            preference.Load(stored.GetValueOrDefault(preference.Key));
    }

    /// <summary>Writes the preferences whose properties were changed in code since they were loaded or last written.</summary>
    public void SavePreferences()
    {
        foreach (var preference in Preferences)
            try
            {
                preference.Persist();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Storing the preference {Key} failed", preference.Key);
            }
    }
}

public interface ILogContext
{
    ILogger Logger { get; }
}
