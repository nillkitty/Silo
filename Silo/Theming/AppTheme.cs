using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Telerik.Windows.Controls;

namespace Silo.Theming;

/// <summary>
///     The app's color-scheme choice for the Windows11 Telerik theme: Light, Dark, or
///     System (follow Windows).
/// </summary>
public enum ThemeMode
{
    Light,
    Dark,
    System
}

/// <summary>
///     Owns the app's current <see cref="ThemeMode"/> and applies it via
///     <see cref="Windows11Palette.LoadPreset"/>. There's exactly one of these
///     (<see cref="Instance"/>), and it doubles as its own <see cref="ICommand"/> so the
///     View menu's Light/Dark/System items can bind straight to it
///     (<c>Command="{Binding}"</c>, <c>CommandParameter</c> = the mode name) - the same
///     "the object is its own command" pattern used for the Tools menu
///     (<see cref="Silo.Tools.ToolBase"/>), and for the same reason: it keeps a single
///     live instance's checked state in sync with a click without depending on exactly
///     how Telerik's checkable <c>RadMenuItem</c> click handling interacts with a
///     separate command-wrapper object's own change notifications.
/// </summary>
public sealed class AppTheme : ICommand, INotifyPropertyChanged
{
    public static AppTheme Instance { get; } = new();

    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "theme.json");

    private ThemeMode _mode = ThemeMode.System;

    private AppTheme()
    {
    }

    public ThemeMode Mode => _mode;

    public bool IsLight  => _mode == ThemeMode.Light;
    public bool IsDark   => _mode == ThemeMode.Dark;
    public bool IsSystem => _mode == ThemeMode.System;

    /// <summary>
    ///     Selects the Windows11 theme as the app-wide theme, turns on its compact
    ///     sizing, and applies the last-saved Light/Dark/System choice (defaulting to
    ///     System). Windows11Palette's "System" preset already tracks the OS light/dark
    ///     setting live on its own (it has its own private OS-change listeners), so no
    ///     separate polling or event subscription is needed here. Call once, early in
    ///     startup - before the main window is created.
    /// </summary>
    public static void Apply()
    {
        StyleManager.ApplicationTheme = new Windows11Theme();
        Windows11ThemeSizeHelper.Helper.IsInCompactMode = true;
        Instance.SetMode(_loadSavedMode());
    }

    /// <summary>
    ///     Switches to the given mode: reloads the Windows11 palette preset (which
    ///     updates every open window live, since the theme's styles pull their colors
    ///     from that palette's resources) and persists the choice for next launch.
    /// </summary>
    public void SetMode(ThemeMode mode)
    {
        _mode = mode;

        Windows11Palette.LoadPreset(mode switch
        {
            ThemeMode.Light => Windows11Palette.ColorVariation.Light,
            ThemeMode.Dark  => Windows11Palette.ColorVariation.Dark,
            _               => Windows11Palette.ColorVariation.System
        });

        _save(mode);

        // Always re-notify, even when the mode didn't actually change: a checkable
        // RadMenuItem flips its own IsChecked locally the instant it's clicked - before
        // Command.Execute() even runs - including when you click the item that's already
        // checked. IsChecked is bound OneWay here (see MenuBar.xaml) specifically so that
        // a fresh notification is what pulls the binding back to the real state and
        // corrects that local flip, rather than the click "sticking" an unchecked state.
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsLight));
        OnPropertyChanged(nameof(IsDark));
        OnPropertyChanged(nameof(IsSystem));
    }

    private static ThemeMode _loadSavedMode()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(SettingsFile)) is { Mode: { } m } &&
                Enum.TryParse<ThemeMode>(m, true, out var mode))
                return mode;
        }
        catch
        {
            /* corrupt or unreadable settings file - fall back to the default below */
        }

        return ThemeMode.System;
    }

    private static void _save(ThemeMode mode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new ThemeSettings { Mode = mode.ToString() }));
        }
        catch
        {
            /* persistence is best-effort; the app still runs fine without it */
        }
    }

    // --- ICommand ---
    // Lets this single AppTheme instance be bound directly as a menu item's Command;
    // CommandParameter (the mode name, set in MenuBar.xaml) picks which mode to switch to.
    bool ICommand.CanExecute(object? parameter) => true;

    void ICommand.Execute(object? parameter)
    {
        if (parameter is string s && Enum.TryParse<ThemeMode>(s, true, out var mode))
            SetMode(mode);
    }

    event EventHandler? ICommand.CanExecuteChanged
    {
        add { }
        remove { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class ThemeSettings
    {
        public string Mode { get; set; } = nameof(ThemeMode.System);
    }
}
