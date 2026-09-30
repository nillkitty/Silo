using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace Silo.Theming;

/// <summary>
///     The app's color-scheme choice for the hand-rolled theme (see
///     <c>Theming/Theme.Light.xaml</c>/<c>Theme.Dark.xaml</c>/<c>Theme.Styles.xaml</c>):
///     Light, Dark, or System (follow Windows).
/// </summary>
public enum ThemeMode
{
    Light,
    Dark,
    System
}

/// <summary>
///     Owns the app's current <see cref="ThemeMode"/> and applies it by hot-swapping one
///     of two palette <see cref="ResourceDictionary"/>s (<c>Theme.Light.xaml</c> /
///     <c>Theme.Dark.xaml</c>) into <see cref="Application.Resources"/>. There's exactly
///     one of these (<see cref="Instance"/>), and it doubles as its own
///     <see cref="ICommand"/> so the View menu's Light/Dark/System items can bind straight
///     to it (<c>Command="{Binding}"</c>, <c>CommandParameter</c> = the mode name) - the
///     same "the object is its own command" pattern used for the Tools menu
///     (<see cref="Silo.Tools.ToolBase"/>), and for the same reason: it keeps a single
///     live instance's checked state in sync with a click without depending on exactly
///     how a checkable <c>MenuItem</c>'s own local click-driven <c>IsChecked</c> flip
///     interacts with a separate command-wrapper object's own change notifications.
/// </summary>
/// <remarks>
///     This previously delegated to Telerik's <c>Windows11Theme</c>/<c>Windows11Palette</c>,
///     which tracked OS light/dark changes live on its own. The hand-rolled replacement
///     reads the OS setting once, at the moment <see cref="ThemeMode.System"/> is applied
///     (at startup, or when the user picks "System" from the View menu) rather than
///     subscribing to live OS theme-change notifications - a deliberate simplification
///     (see the Theme system design conversation this replaced Telerik in). Re-applying
///     "System" (e.g. by toggling it in the View menu, or relaunching the app) picks up a
///     changed OS setting.
/// </remarks>
public sealed class AppTheme : ICommand, INotifyPropertyChanged
{
    public static AppTheme Instance { get; } = new();

    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "theme.json");

    private static readonly Uri LightPaletteUri = new("/Theming/Theme.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkPaletteUri  = new("/Theming/Theme.Dark.xaml", UriKind.Relative);

    private ThemeMode           _mode = ThemeMode.System;
    private ResourceDictionary? _appliedPalette;

    private AppTheme()
    {
    }

    public ThemeMode Mode => _mode;

    public bool IsLight  => _mode == ThemeMode.Light;
    public bool IsDark   => _mode == ThemeMode.Dark;
    public bool IsSystem => _mode == ThemeMode.System;

    /// <summary>
    ///     Applies the last-saved Light/Dark/System choice (defaulting to System). Call
    ///     once, early in startup - before the main window is created - and after
    ///     <c>Theme.Styles.xaml</c> has already been merged into
    ///     <see cref="Application.Resources"/> (see <c>App.xaml</c>), since that's the
    ///     dictionary whose styles actually read the palette brushes this swaps in.
    /// </summary>
    public static void Apply()
    {
        Instance.SetMode(_loadSavedMode());
    }

    /// <summary>
    ///     Switches to the given mode: swaps in the matching palette dictionary (which
    ///     updates every open window live, since every themed style in
    ///     <c>Theme.Styles.xaml</c> pulls its colors from that palette's brush resources
    ///     via <c>DynamicResource</c>) and persists the choice for next launch.
    /// </summary>
    public void SetMode(ThemeMode mode)
    {
        _mode = mode;

        bool dark = mode switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark  => true,
            _               => _isSystemDark()
        };

        var palette = new ResourceDictionary { Source = dark ? DarkPaletteUri : LightPaletteUri };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_appliedPalette != null) merged.Remove(_appliedPalette);
        merged.Add(palette);
        _appliedPalette = palette;

        _save(mode);

        // Always re-notify, even when the mode didn't actually change: a checkable
        // MenuItem flips its own IsChecked locally the instant it's clicked - before
        // Command.Execute() even runs - including when you click the item that's already
        // checked. IsChecked is bound OneWay here (see MenuBar.xaml) specifically so that
        // a fresh notification is what pulls the binding back to the real state and
        // corrects that local flip, rather than the click "sticking" an unchecked state.
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsLight));
        OnPropertyChanged(nameof(IsDark));
        OnPropertyChanged(nameof(IsSystem));
    }

    /// <summary>
    ///     Reads the Windows 10/11 "choose your color" setting
    ///     (Settings -> Personalization -> Colors -> "Choose your mode") once, best-effort.
    ///     Falls back to Light if the key is missing or unreadable (e.g. a locked-down
    ///     machine, or a Windows version that predates this key).
    /// </summary>
    private static bool _isSystemDark()
    {
        try
        {
            using var key =
                Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 0;
        }
        catch
        {
            /* fall back to Light below */
        }

        return false;
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
