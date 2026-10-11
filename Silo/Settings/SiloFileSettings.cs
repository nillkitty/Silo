using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Silo.Settings;

/// <summary>What to do, without asking, when a file (or remote copy) changed underneath an open Silo.</summary>
public enum ExternalChangePolicy
{
    /// <summary>Ask each time (the default). The prompt offers "Don't ask me again", which stores the answer here.</summary>
    Prompt,

    /// <summary>Always replace the other version.</summary>
    Overwrite,

    /// <summary>Always keep the other version and save a "conflicted copy" next to it.</summary>
    SaveCopy
}

/// <summary>
///     Settings about how .silo files are stored and saved. Author-mode settings: they are changed from the Author design
///     menu (Ctrl + right-click in the main window, see <see cref="Silo.Design.DesignMenu" />) or by the "Don't ask me
///     again" box on the conflict prompt. Persisted to <c>%APPDATA%\Silo\filesettings.json</c>; same single-instance,
///     load-on-startup, save-on-change shape as <see cref="Silo.Design.AppDesignSettings" />.
/// </summary>
public sealed class SiloFileSettings : INotifyPropertyChanged
{
    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "filesettings.json");

    private bool                 _askToExplode = true;
    private ExternalChangePolicy _conflictPolicy = ExternalChangePolicy.Prompt;
    private int                  _explodeThresholdMb = 64;
    private bool                 _newSilosAreArchives;

    private SiloFileSettings()
    {
    }

    public static SiloFileSettings Instance { get; } = new();

    /// <summary>What to do when the file changed on disk / remotely since the Silo was loaded or last saved. Default: prompt.</summary>
    public ExternalChangePolicy ConflictPolicy
    {
        get => _conflictPolicy;
        set => Set(ref _conflictPolicy, value);
    }

    /// <summary>New Silos are created as multi-file archives instead of single simple files. Default: false (simple).</summary>
    public bool NewSilosAreArchives
    {
        get => _newSilosAreArchives;
        set => Set(ref _newSilosAreArchives, value);
    }

    /// <summary>A simple Silo bigger than this many MB prompts (after a save) to be converted to an archive.</summary>
    public int ExplodeThresholdMb
    {
        get => _explodeThresholdMb;
        set => Set(ref _explodeThresholdMb, Math.Clamp(value, 1, 100_000));
    }

    /// <summary>Whether to offer the conversion at all. The prompt's "Don't ask me again" clears this.</summary>
    public bool AskToExplode
    {
        get => _askToExplode;
        set => Set(ref _askToExplode, value);
    }

    /// <summary>Loads the saved settings (defaults for anything missing/corrupt). Call once, early in startup.</summary>
    public static void Apply()
    {
        try
        {
            if (File.Exists(SettingsFile) && JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SettingsFile)) is { } s)
            {
                Instance._conflictPolicy      = s.ConflictPolicy;
                Instance._newSilosAreArchives = s.NewSilosAreArchives;
                Instance._explodeThresholdMb  = Math.Clamp(s.ExplodeThresholdMb, 1, 100_000);
                Instance._askToExplode        = s.AskToExplode;
            }
        }
        catch
        {
            /* corrupt or unreadable: defaults apply */
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Save();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new Snapshot
                                                                     {
                                                                         ConflictPolicy      = _conflictPolicy,
                                                                         NewSilosAreArchives = _newSilosAreArchives,
                                                                         ExplodeThresholdMb  = _explodeThresholdMb,
                                                                         AskToExplode        = _askToExplode
                                                                     }));
        }
        catch
        {
            /* best effort */
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private sealed class Snapshot
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ExternalChangePolicy ConflictPolicy { get; set; } = ExternalChangePolicy.Prompt;

        public bool NewSilosAreArchives { get; set; }
        public int  ExplodeThresholdMb  { get; set; } = 64;
        public bool AskToExplode        { get; set; } = true;
    }
}
