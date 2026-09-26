using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;

namespace Silo.Design;

/// <summary>
///     How much of the app's own construction is exposed to the person currently using it -
///     a HyperCard-style progression from plain <see cref="User"/> up through <see cref="Debug"/>,
///     each level a strict superset of the one below it.
/// </summary>
/// <remarks>
///     This is deliberately a different thing from <see cref="Silo.DbModel.Global.SiloUserLevel"/>.
///     That one is a <em>per-Silo, per-user database authorization</em> - what a specific person
///     is allowed to do to a specific Silo document, granted by an <c>Authorization</c> row and
///     enforced on write. <see cref="DesignLevel"/> is a <em>session-wide interaction mode</em> -
///     whether the pink design menus (see <see cref="DesignMenu"/>) are even reachable right now
///     - and applies the same way regardless of which Silo (if any) is open or who's authorized
///     to write to it. A future feature could default a person's <see cref="DesignLevel"/> from
///     their highest <c>SiloUserLevel</c> across open Silos, but the two enums stay separate:
///     one is authorization, the other is a UI mode.
/// </remarks>
public enum DesignLevel
{
    /// <summary>Plain use of the app. No design menus, no Ctrl+right-click hooks fire.</summary>
    User,

    /// <summary>Can reach "authoring"-level design menu items - the ones a power user, not just a developer, would want (see the icon-size slider).</summary>
    Author,

    /// <summary>Can also reach developer-level items - things that expose how a piece of UI is built, not just how it's configured.</summary>
    Developer,

    /// <summary>Can also reach debug-level items - raw internal state, the kind of thing you'd only want visible while chasing a bug.</summary>
    Debug
}

/// <summary>
///     Owns the app's current <see cref="DesignLevel"/>. There's exactly one of these
///     (<see cref="Instance"/>), following the same "singleton that's also its own
///     <see cref="ICommand"/>" shape as <see cref="Silo.Theming.AppTheme"/> - see that class's
///     remarks for why (in short: it keeps a checkable View-menu item's local click-flip in
///     sync with the real state without a separate command-wrapper object in between).
/// </summary>
public sealed class AppDesignLevel : ICommand, INotifyPropertyChanged
{
    public static AppDesignLevel Instance { get; } = new();

    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "designlevel.json");

    private DesignLevel _level = DesignLevel.User;

    private AppDesignLevel()
    {
    }

    public DesignLevel Level => _level;

    public bool IsUser      => _level == DesignLevel.User;
    public bool IsAuthor    => _level == DesignLevel.Author;
    public bool IsDeveloper => _level == DesignLevel.Developer;
    public bool IsDebug     => _level == DesignLevel.Debug;

    /// <summary>Whether the current level is <paramref name="min"/> or higher - the check every gated capability (a design-menu item, eventually anything else) should use rather than comparing <see cref="Level"/> for exact equality.</summary>
    public bool IsAtLeast(DesignLevel min) => _level >= min;

    /// <summary>Loads the last-saved level (defaulting to <see cref="DesignLevel.User"/>). Call once, early in startup.</summary>
    public static void Apply() => Instance.SetLevel(_loadSavedLevel());

    public void SetLevel(DesignLevel level)
    {
        _level = level;
        _save(level);

        // Same reasoning as AppTheme.SetMode: always re-notify, even when the level didn't
        // change, so a checkable RadMenuItem's own local IsChecked flip (which happens before
        // Command.Execute runs) gets corrected by a fresh OneWay-bound notification rather than
        // sticking.
        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(IsUser));
        OnPropertyChanged(nameof(IsAuthor));
        OnPropertyChanged(nameof(IsDeveloper));
        OnPropertyChanged(nameof(IsDebug));
    }

    private static DesignLevel _loadSavedLevel()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<DesignLevelSettings>(File.ReadAllText(SettingsFile)) is { Level: { } l } &&
                Enum.TryParse<DesignLevel>(l, true, out var level))
                return level;
        }
        catch
        {
            /* corrupt or unreadable settings file - fall back to the default below */
        }

        return DesignLevel.User;
    }

    private static void _save(DesignLevel level)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new DesignLevelSettings { Level = level.ToString() }));
        }
        catch
        {
            /* persistence is best-effort; the app still runs fine without it */
        }
    }

    // --- ICommand ---
    // Lets this instance be bound directly as a View > User Level menu item's Command;
    // CommandParameter (the level name, set in MenuBar.xaml) picks which level to switch to.
    bool ICommand.CanExecute(object? parameter) => true;

    void ICommand.Execute(object? parameter)
    {
        if (parameter is string s && Enum.TryParse<DesignLevel>(s, true, out var level))
            SetLevel(level);
    }

    event EventHandler? ICommand.CanExecuteChanged
    {
        add { }
        remove { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class DesignLevelSettings
    {
        public string Level { get; set; } = nameof(DesignLevel.User);
    }
}
