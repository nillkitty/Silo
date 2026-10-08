using System.Linq;
using System.Threading;
using System.IO;
using System.Text.Json;
using Serilog;

namespace Silo.Drawio;

/// <summary>
///     A named, persisted set of <see cref="DrawioOptions" /> -- "how locked down is this kind of
///     diagram pane". Panes (see <see cref="DrawioView" />) look their options up by profile name,
///     so changing a profile in the Author options window re-configures every pane that uses it.
/// </summary>
public sealed class DrawioProfile
{
    internal DrawioProfile(string name, string description, DrawioOptions defaults)
    {
        Name        = name;
        Description = description;
        Defaults    = defaults;
        Options     = defaults.Clone();
    }

    /// <summary>The built-in options this profile resets to.</summary>
    public DrawioOptions Defaults { get; }

    public string Description { get; }

    public string Name { get; }

    public DrawioOptions Options { get; internal set; }

    public event EventHandler? Changed;

    /// <summary>Call after editing <see cref="Options" />: persists and tells subscribed panes to reconfigure.</summary>
    public void NotifyChanged()
    {
        DrawioProfiles.Instance.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResetToDefaults()
    {
        Options = Defaults.Clone();
        NotifyChanged();
    }
}

/// <summary>
///     The set of known profiles. Three are built in, spanning the range the person asked for --
///     a locked read-only view of existing constructs, the same with room for annotations, and the
///     full editor for design-world drawing and notes -- and every field of each is editable from the
///     Author-level options window. Edits persist to <c>%AppData%\Silo\drawio-profiles.json</c>.
/// </summary>
public sealed class DrawioProfiles
{
    public const string ModelReadOnly   = "model-readonly";
    public const string ModelAnnotatable = "model-annotatable";
    public const string Canvas          = "canvas";

    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "drawio-profiles.json");

    private readonly Dictionary<string, DrawioProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    private DrawioProfiles()
    {
        Add(new DrawioProfile(ModelReadOnly,
                              "Existing Silo constructs, locked: a read-only chromeless viewer.",
                              new DrawioOptions
                              {
                                  Chromeless         = true,
                                  ShowLibraries      = false,
                                  LockGeneratedCells = true,
                                  ZoomWheel          = true,
                                  FitOnLoad          = true,
                                  HideViewerPrint    = true,
                                  HideViewerExport   = true,
                                  ShowLinkIcons      = true
                              }));

        Add(new DrawioProfile(ModelAnnotatable,
                              "Existing Silo constructs, locked, but you can draw and write around them.",
                              new DrawioOptions
                              {
                                  ShowLibraries          = false,
                                  HideSidebar            = true,
                                  HideAllMoreShapes      = true,
                                  DisableCustomLibraries = true,
                                  HideTemplates          = true,
                                  HideMenus              = ["file", "extras", "help"],
                                  LockGeneratedCells     = true,
                                  ZoomWheel              = true,
                                  FitOnLoad              = true,
                                  ShowLinkIcons          = true
                              }));

        Add(new DrawioProfile(Canvas,
                              "Design world, drawing and notes: the full editor, still offline-only.",
                              new DrawioOptions
                              {
                                  ShowLibraries      = true,
                                  LockGeneratedCells = false,
                                  AllowDownloads     = true
                              }));

        Load();
    }

    public static DrawioProfiles Instance { get; } = new();

    public IEnumerable<DrawioProfile> All => _profiles.Values;

    private void Add(DrawioProfile p)
    {
        _profiles[p.Name] = p;
    }

    /// <summary>Gets the named profile, falling back to <see cref="Canvas" /> for an unknown name.</summary>
    public DrawioProfile Get(string? name)
    {
        if (name is not null && _profiles.TryGetValue(name, out var p)) return p;
        return _profiles[Canvas];
    }

    /// <summary>Creates (or returns the existing) user profile named <paramref name="name" />, seeded from <paramref name="from" />.</summary>
    public DrawioProfile Create(string name, DrawioOptions from, string description = "")
    {
        if (_profiles.TryGetValue(name, out var existing)) return existing;

        var p = new DrawioProfile(name, description, from.Clone());
        Add(p);
        Save();
        return p;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;

            var saved = JsonSerializer.Deserialize<Dictionary<string, DrawioOptions>>(File.ReadAllText(SettingsFile), DrawioOptions.JsonOptions);
            if (saved is null) return;

            foreach (var (name, options) in saved)
                if (_profiles.TryGetValue(name, out var p)) p.Options = options;
                else Add(new DrawioProfile(name, "Custom profile.", options.Clone()) { Options = options });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read draw.io profiles from '{file}'; using built-in defaults", SettingsFile);
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            var snapshot = _profiles.Values.ToDictionary(p => p.Name, p => p.Options);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(snapshot, DrawioOptions.JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not save draw.io profiles");
        }
    }
}
