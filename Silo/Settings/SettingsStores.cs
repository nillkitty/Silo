using Newtonsoft.Json;
using Serilog;
using Silo.Model;
using Silo.Storage;

namespace Silo.Settings;

/// <summary>
///     The settings of one Settings pane that are kept in the active Silo, shared by everyone who opens it: one
///     <see cref="Stored{T}" /> row (<see cref="StoredScope.Shared" />) under <c>settings.&lt;name&gt;</c>.
/// </summary>
/// <typeparam name="T">A plain class JSON can write and read; a new one holds the defaults.</typeparam>
/// <remarks>
///     Always the active Silo's: what is read and written follows <c>App.ActiveSilo</c>. Writing a changed value modifies the
///     Silo (it is saved with it).
/// </remarks>
public sealed class SiloSettings<T> where T : class, new()
{
    /// <summary>What every key starts with.</summary>
    public const string KeyPrefix = "settings.";

    /// <summary>What the settings are stored under.</summary>
    public string Key { get; }

    private static OpenSilo? ActiveSilo => SiloSettings.ActiveSilo;

    private static IStoredStore? Store => ActiveSilo?.Data as IStoredStore;

    /// <summary>Whether a Silo is open that can keep them.</summary>
    public bool IsAvailable => Store is not null;

    /// <summary>Whether the current person may change them (the Silo is writable).</summary>
    public bool IsWritable => IsAvailable && ActiveSilo?.IsWritable != false;

    private Stored<T> Row => new(Key, StoredScope.Shared, () => Store);

    /// <summary>The stored settings; the defaults when nothing (or nothing readable) is stored, or no Silo is open.</summary>
    public T Load()
    {
        try
        {
            return Row.TryGet(out var value) && value is not null ? value : new T();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The settings {key} could not be read", Key);
            return new T();
        }
    }

    /// <summary>Reads, changes and writes back the settings. Does nothing when no Silo is open.</summary>
    public void Update(Action<T> change)
    {
        if (!IsAvailable) return;

        var value = Load();
        change(value);
        try
        {
            Row.Set(value);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The settings {key} could not be saved", Key);
            App.Alert(ex, "Saving the settings");
        }
    }

    /// <param name="name">The part of the key after <see cref="KeyPrefix" />: <c>silo.security</c>.</param>
    public SiloSettings(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Key = KeyPrefix + name;
    }
}

/// <summary>
///     Settings kept in a JSON file in <c>%APPDATA%\Silo</c>: the person's own (they follow the person, not the Silo) or the
///     app's on this computer. Loaded once, written on every change; a missing or unreadable file gives the defaults.
/// </summary>
/// <typeparam name="T">A plain class JSON can write and read; a new one holds the defaults.</typeparam>
public sealed class SettingsFile<T> where T : class, new()
{
    private static readonly JsonSerializerSettings Json = new()
                                                          {
                                                              // lists and dictionaries with defaults are replaced, not added to
                                                              ObjectCreationHandling = ObjectCreationHandling.Replace,
                                                              Formatting             = Formatting.Indented
                                                          };

    private readonly object _gate = new();
    private          T?     _value;

    /// <summary>The full path of the file.</summary>
    public string Path { get; }

    /// <summary>The settings (loaded the first time they are asked for). Change them through <see cref="Update" />.</summary>
    public T Value
    {
        get
        {
            lock (_gate)
                return _value ??= Read();
        }
    }

    /// <summary>Raised after <see cref="Update" /> changed and wrote the settings. On the thread that changed them.</summary>
    public event EventHandler? Changed;

    /// <summary>Changes the settings and writes the file (best effort).</summary>
    public void Update(Action<T> change)
    {
        lock (_gate)
        {
            _value ??= Read();
            change(_value);
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, JsonConvert.SerializeObject(_value, Json));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "The settings file {path} could not be written", Path);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private T Read()
    {
        try
        {
            if (File.Exists(Path) && JsonConvert.DeserializeObject<T>(File.ReadAllText(Path), Json) is { } value) return value;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The settings file {path} could not be read; the defaults apply", Path);
        }

        return new T();
    }

    /// <param name="fileName">The file's name in <c>%APPDATA%\Silo</c>: <c>authoring.json</c>.</param>
    public SettingsFile(string fileName)
    {
        Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", fileName);
    }
}

/// <summary>The person's own settings (Settings ▸ User Settings): they follow the person, not the Silo.</summary>
public static class UserSettings
{
    /// <summary>Settings ▸ User Settings ▸ Authoring Settings.</summary>
    public static SettingsFile<AuthoringSettings> Authoring { get; } = new("authoring.json");

    /// <summary>Settings ▸ User Settings ▸ Developer Settings.</summary>
    public static SettingsFile<DeveloperSettings> Developer { get; } = new("developer.json");

    /// <summary>
    ///     Whether a Ctrl+right-click may show the design menu: the authoring context menu is on, or (at Developer level or
    ///     above) the developer context menu is.
    /// </summary>
    public static bool DesignMenuAllowed =>
        Authoring.Value.ContextMenu ||
        (Design.AppDesignLevel.Instance.IsAtLeast(Design.DesignLevel.Developer) && Developer.Value.ContextMenu);
}

/// <summary>The app's settings on this computer (Settings ▸ App Settings).</summary>
public static class AppSettings
{
    /// <summary>Settings ▸ App Settings ▸ Auto-Update.</summary>
    public static SettingsFile<AutoUpdateSettings> AutoUpdate { get; } = new("autoupdate.json");

    /// <summary>Settings ▸ App Settings ▸ Registered Handlers.</summary>
    public static SettingsFile<HandlerSettings> Handlers { get; } = new("handlers.json");
}

/// <summary>The settings of each Silo pane (Settings ▸ Silo Settings and Owner Settings), in the active Silo.</summary>
public static class SiloSettings
{
    /// <summary>The Silo whose settings are shown and changed: the active one (unless it is closing), or null.</summary>
    public static OpenSilo? ActiveSilo => App.Instance?.ActiveSilo is { IsClosing: false } silo ? silo : null;

    public static SiloSettings<AssembliesSettings>   Assemblies { get; } = new("silo.assemblies");
    public static SiloSettings<BrandingSettings>     Branding   { get; } = new("silo.branding");
    public static SiloSettings<SecuritySettings>     Security   { get; } = new("silo.security");
    public static SiloSettings<BackupSettings>       Backup     { get; } = new("silo.backup");
    public static SiloSettings<NetworkSettings>      Network    { get; } = new("silo.network");
    public static SiloSettings<AuditUndoSettings>    AuditUndo  { get; } = new("silo.auditUndo");
    public static SiloSettings<KeyboardSettings>     Keyboard   { get; } = new("silo.keyboard");
    public static SiloSettings<AccessSettings>       Access     { get; } = new("owner.access");
}
