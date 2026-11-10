using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Serilog;

namespace Silo.Design;

/// <summary>One thing the app gives an icon to (a registry key, a kind of node...), and the icon it has unless the person remapped it.</summary>
/// <param name="Id">Stable name the mapping is kept under, such as <c>registry.key</c> or <c>type.Connection</c>.</param>
/// <param name="Category">Groups the roles in the Icon Editor.</param>
/// <param name="Name">What the person sees.</param>
/// <param name="DefaultKey">The icon key (Resources/NavIcons.xaml) used when there is no override.</param>
public sealed record IconRole(string Id, string Category, string Name, string DefaultKey);

/// <summary>
///     Which icon each <see cref="IconRole" /> shows, kept in <c>%APPDATA%\Silo\iconmap.json</c> (role id to icon key). Code that picks an
///     icon for a kind of thing asks <see cref="Resolve" /> with its role and its own default; the Icon Editor changes the map. Icons are
///     read when a node or view is made, so a change shows on what is made after it (refresh a node to see it).
/// </summary>
public static class IconMap
{
    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "iconmap.json");

    private static readonly object                         Gate      = new();
    private static readonly Dictionary<string, string>     Overrides = Load();
    private static readonly Dictionary<string, IconRole>   Known     = new(StringComparer.OrdinalIgnoreCase);

    private static bool _saveQueued;

    /// <summary>Raised on the UI thread after the map changed.</summary>
    public static event EventHandler? Changed;

    /// <summary>The roles that were declared, by category and then name. Declaring is done by <see cref="Role" /> as code first asks for one.</summary>
    public static IReadOnlyList<IconRole> Roles
    {
        get
        {
            IconRoles.EnsureDeclared();
            lock (Gate) return Known.Values.OrderBy(r => r.Category, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    /// <summary>Declares a role (once; the first declaration stands) so the editor lists it, and returns the key to draw: the override, else <paramref name="defaultKey" />.</summary>
    public static string Resolve(string id, string category, string name, string defaultKey)
    {
        lock (Gate)
        {
            Known.TryAdd(id, new IconRole(id, category, name, defaultKey));
            return Overrides.TryGetValue(id, out var key) ? key : defaultKey;
        }
    }

    /// <summary>The key to draw for a declared role (its default when it was not declared and has no override).</summary>
    public static string KeyOf(string id, string fallback)
    {
        lock (Gate)
        {
            if (Overrides.TryGetValue(id, out var key)) return key;
            return Known.TryGetValue(id, out var role) ? role.DefaultKey : fallback;
        }
    }

    public static bool IsOverridden(string id)
    {
        lock (Gate) return Overrides.ContainsKey(id);
    }

    public static int OverrideCount
    {
        get
        {
            lock (Gate) return Overrides.Count;
        }
    }

    /// <summary>Makes the role show <paramref name="key" />; the role's own default (or null) removes the override.</summary>
    public static void Set(string id, string? key)
    {
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(key) || (Known.TryGetValue(id, out var role) && string.Equals(role.DefaultKey, key, StringComparison.Ordinal))) Overrides.Remove(id);
            else Overrides[id] = key;
        }

        Raise();
    }

    /// <summary>Puts every role back to its own icon.</summary>
    public static void ResetAll()
    {
        lock (Gate) Overrides.Clear();
        Raise();
    }

    private static void Raise()
    {
        QueueSave();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(SettingsFile) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsFile)) is { } map)
                return new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Reading the icon map failed; starting empty");
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void QueueSave()
    {
        if (_saveQueued) return;

        _saveQueued = true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Save();
            return;
        }

        dispatcher.BeginInvoke(new Action(Save), DispatcherPriority.Background);
    }

    private static void Save()
    {
        _saveQueued = false;
        try
        {
            Dictionary<string, string> copy;
            lock (Gate) copy = new Dictionary<string, string>(Overrides);

            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(copy));
        }
        catch
        {
            /* best effort, like the other settings files */
        }
    }
}

/// <summary>The roles whose icons are chosen in code that may not have run yet, declared up front so the Icon Editor lists them all.</summary>
internal static class IconRoles
{
    private static bool _done;

    public const string RegistryCategory = "Registry";
    public const string TypeCategory     = "Kinds of thing (by name)";

    public static string Registry(string suffix, string name, string defaultKey) => IconMap.Resolve("registry." + suffix, RegistryCategory, name, defaultKey);

    public static string Type(string fragment, string defaultKey) => IconMap.Resolve("type." + fragment, TypeCategory, fragment, defaultKey);

    public static void EnsureDeclared()
    {
        if (_done) return;

        _done = true;
        Registry("hive", "Registry hive", "RegHive");
        Registry("key", "Registry key", "RegKey");
        Registry("value", "Registry value", "RegValue");
        Registry("missing", "Registry item that cannot be read", "FsFileMissing");
        TypeIcons.DeclareRoles();
    }
}
