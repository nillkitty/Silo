using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Silo.Design;

/// <summary>
///     Runtime-adjustable, persisted "how the app is built" values - the things a design menu
///     (see <see cref="DesignMenu"/>) lets an Author (or higher) tweak live instead of them
///     being buried as literals in XAML/code. Follows the same single-instance,
///     load-on-startup, save-on-change shape as <see cref="Silo.Theming.AppTheme"/>; see that
///     class's remarks for the underlying rationale.
/// </summary>
/// <remarks>
///     Each property here is meant to be bound from exactly one place: an implicit
///     <c>Style</c> setter (see the <c>AppIcon</c> style in <c>App.xaml</c> for
///     <see cref="IconSize"/>) so every instance of the thing it controls updates live and in
///     lockstep the moment a design-menu slider changes it, with no per-instance wiring. Adding
///     a new tunable here is: add the property (with the same clamp-and-notify shape as
///     <see cref="IconSize"/>), bind a Style setter to it, and give some element a
///     <see cref="DesignMenuItem.Slider"/> that calls its setter.
/// </remarks>
public sealed class AppDesignSettings : INotifyPropertyChanged
{
    public static AppDesignSettings Instance { get; } = new();

    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "designsettings.json");

    private AppDesignSettings()
    {
    }

    /// <summary>
    ///     The <c>Viewbox</c> size (in DIPs) every <c>AppIcon</c> renders at, unless a specific
    ///     instance was given an explicit <c>Size=</c> in its markup (see
    ///     <c>AppIconExtension</c>). 24 matches what every icon in the app already rendered at
    ///     before this became adjustable.
    /// </summary>
    public double IconSize
    {
        get;
        set
        {
            double clamped = Math.Clamp(value, 10, 96);
            if (Math.Abs(field - clamped) < 0.01) return;
            field = clamped;
            _save();
            OnPropertyChanged();
        }
    } = 24;

    /// <summary>Loads the last-saved settings (falling back to the defaults above for anything missing or corrupt). Call once, early in startup.</summary>
    public static void Apply()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SettingsFile)) is { } snapshot)
            {
                if (snapshot.IconSize is { } iconSize) Instance.IconSize = iconSize;
            }
        }
        catch
        {
            /* corrupt or unreadable settings file - the defaults above still apply */
        }
    }

    private void _save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new Snapshot { IconSize = IconSize }));
        }
        catch
        {
            /* persistence is best-effort; the app still runs fine without it */
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class Snapshot
    {
        public double? IconSize { get; set; }
    }
}
