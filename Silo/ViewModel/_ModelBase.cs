using Serilog;
using Silo.Providers.Data;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Model;

/// <summary>
///     Represents a ViewModel for an item whose DTO is <typeparamref name="TDataObject" />
/// </summary>
/// <typeparam name="TDataObject"></typeparam>
public abstract class ModelBase<TDataObject>(IConnection? connection) : ModelBase(connection)
{
    public TDataObject? Data { get; protected set; }
}

/// <summary>
///     Represents the base class for all ViewModels
/// </summary>
/// <remarks>
///     Its properties marked <see cref="PreferenceAttribute" /> or <see cref="ToggleAttribute" /> are the model's
///     preferences
///     (<see cref="Preferences" />). A model that has somewhere to keep them overrides <see cref="PreferenceStore" /> and
///     calls
///     <see cref="LoadPreferences" /> once that place is there; a model that does not keeps them in memory.
/// </remarks>
[Transient(typeof(IPreferenceProvider))]
public abstract class ModelBase : DependencyObject, ILogContext, IPreferenceProvider
{
    protected virtual IConnection Connection { get; }
    protected virtual IConnector  Connector  => Connection.Connector;

    protected virtual Uri? NodeUri => this switch
                                      {
                                          //  IConnectionBase cb      => cb.Node?.Uri,
                                          INodeProvider np       => np.GetNode()?.Uri,
                                          ILocatable l           => l.GetUri(UriContext.Node),
                                          ModelBase<SiloNode> sn => sn.Data?.Uri,
                                          ModelBase<Uri> ub      => ub.Data,
                                          _ => throw new
                                                   NotImplementedException($"Model '{GetType().Name}' does not implement NodeUri either by overriding this property in a " +
                                                                           "base class, or by implementing ILocatable or an ILocatable interface or base type.")
                                      };

    /// <summary>
    ///     Where this model's preferences are kept; null (the default) for nowhere, the preferences then live as long as
    ///     the model.
    /// </summary>
    protected virtual IPreferenceStore? PreferenceStore => null;

    /// <summary>
    ///     Gets a unique identifier associated with this model
    /// </summary>
    protected Guid UniqueId { get; } = Guid.NewGuid();

    // the same list as it is made: Load and Persist are the model's business, not part of what a preference is
    private IReadOnlyList<Preference> OwnPreferences => field ??= CreatePreferences();

    /// <summary>
    ///     Gets a logger for the model's scope
    /// </summary>
    public ILogger Logger { get; }

    /// <summary>
    ///     The model's preferences: one for each property marked with <see cref="PreferenceAttribute" /> or
    ///     <see cref="ToggleAttribute" />. Made the first time they are asked for, so the defaults are the values the
    ///     properties start
    ///     with (the model's constructor has run).
    /// </summary>
    public IReadOnlyList<IPreference> Preferences => OwnPreferences;

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

        foreach (var preference in OwnPreferences)
            preference.Load(stored.GetValueOrDefault(preference.Key));
    }

    /// <summary>Writes the preferences whose properties were changed in code since they were loaded or last written.</summary>
    public void SavePreferences()
    {
        foreach (var preference in OwnPreferences)
            try
            {
                preference.Persist();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Storing the preference {Key} failed", preference.Key);
            }
    }

    protected ModelBase(IConnection? conn)
    {
        conn       = this is IConnection cx ? cx : conn.Required();
        Logger     = Log.Logger.ForContext(GetType());
        Connection = conn!;
    }
}

public interface ILogContext
{
    ILogger Logger { get; }
}