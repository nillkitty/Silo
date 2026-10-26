using System.Windows.Input;
using System.Windows.Threading;
using Serilog;
using Silo.Hotkeys;
using Silo.Model;
using Silo.Settings;
using Silo.Storage;

namespace Silo.Commanding;

/// <summary>One app command that has a key: what it is called, its default key, and the command the key runs.</summary>
/// <param name="Id">Stable name the key is kept under in <see cref="KeyboardSettings.CommandKeys" />.</param>
/// <param name="Name">What the person sees (Settings ▸ Silo Settings ▸ Keyboard).</param>
/// <param name="DefaultKey">The key it has unless the Silo changes it.</param>
/// <param name="Command">The command, resolved when the key is pressed.</param>
public sealed record AppCommandKey(string Id, string Name, ShortcutKey DefaultKey, Func<ICommand?> Command);

/// <summary>
///     The keyboard shortcuts for the main window's menu items. The menu shows each key through
///     <c>InputGestureText</c> (MenuBar.xaml) -- WPF only displays that text, it does not make the key work, because
///     these commands are plain <see cref="ICommand" />s rather than <see cref="RoutedCommand" />s -- so the keys are
///     wired here, to the same commands the menu runs. Keep the two lists in step.
/// </summary>
/// <remarks>
///     Every binding resolves its command when the key is pressed, not when the window is built, so the Silo shortcuts
///     always act on whichever Silo is active at that moment, and do nothing (and report "not enabled") when none is.
///     The active Silo may give a command another key, or none (<see cref="KeyboardSettings.CommandKeys" />, changed on
///     Settings ▸ Silo Settings ▸ Keyboard); the bindings are made again when that changes or another Silo becomes active.
///     The menu's <c>InputGestureText</c> still shows the default keys.
/// </remarks>
public static class Shortcuts
{
    private const ShortcutModifiers Ctrl      = ShortcutModifiers.Control;
    private const ShortcutModifiers CtrlShift = ShortcutModifiers.Control | ShortcutModifiers.Shift;

    private static readonly List<KeyBinding> Installed = [];
    private static          UIElement?       _target;

    /// <summary>The commands that have keys, in menu order. Alt+F4 is the system's own.</summary>
    public static IReadOnlyList<AppCommandKey> Commands { get; } =
    [
        new("file.new",        "New Silo",             Keys(Ctrl,      'N'), () => App.NewSiloCommand),
        new("file.open",       "Open Silo",            Keys(Ctrl,      'O'), () => App.OpenSiloCommand),
        new("file.openUrl",    "Open Silo from URL",   Keys(CtrlShift, 'O'), () => App.OpenSiloFromUrlCommand),
        new("silo.connection", "New Connection",       Keys(CtrlShift, 'N'), () => Active?.Commands.NewConnection),
        new("file.save",       "Save Silo",            Keys(Ctrl,      'S'), () => Active?.Commands.SaveSilo),
        new("file.saveAs",     "Save Silo As",         Keys(CtrlShift, 'S'), () => Active?.Commands.SaveSiloAs)
    ];

    private static OpenSilo? Active => App.Instance?.ActiveSilo;

    private static ShortcutKey Keys(ShortcutModifiers modifiers, char letter) => new(modifiers, letter);

    /// <summary>The key a command has in the active Silo: its own, else the default; <see cref="ShortcutKey.None" /> when it was taken away.</summary>
    public static ShortcutKey KeyOf(AppCommandKey command)
    {
        var keys = SiloSettings.ActiveSilo is null ? null : SiloSettings.Keyboard.Load().CommandKeys;
        if (keys is null || !keys.TryGetValue(command.Id, out string? text)) return command.DefaultKey;
        return ShortcutKey.TryParse(text, out var key) ? key : ShortcutKey.None;
    }

    /// <summary>Gives the command another key in the active Silo; <see cref="ShortcutKey.None" /> takes its key away.</summary>
    public static void SetKey(AppCommandKey command, ShortcutKey key)
    {
        SiloSettings.Keyboard.Update(s =>
                                     {
                                         if (key == command.DefaultKey) s.CommandKeys.Remove(command.Id);
                                         else s.CommandKeys[command.Id] = key.ToString();
                                     });
        Apply();
    }

    /// <summary>Gives the command its default key back.</summary>
    public static void ResetKey(AppCommandKey command)
    {
        SiloSettings.Keyboard.Update(s => s.CommandKeys.Remove(command.Id));
        Apply();
    }

    /// <summary>The other command that already has <paramref name="key" />; null when none does.</summary>
    public static AppCommandKey? UsedBy(ShortcutKey key, AppCommandKey ignoring)
    {
        return key.IsSet ? Commands.FirstOrDefault(c => c != ignoring && KeyOf(c) == key) : null;
    }

    /// <summary>Adds the shortcut key bindings to <paramref name="target" /> (normally the main window).</summary>
    public static void Install(UIElement target)
    {
        _target = target;
        Apply();

        if (App.Instance is { } app) app.ActiveSiloChanged += (_, _) => ApplyLater();
        Stored.Changed += (_, e) =>
                          {
                              if (e.Key.Equals(SiloSettings.Keyboard.Key, StringComparison.OrdinalIgnoreCase)) ApplyLater();
                          };
    }

    private static void ApplyLater()
    {
        _target?.Dispatcher.BeginInvoke(new Action(Apply), DispatcherPriority.Background);
    }

    /// <summary>Makes the bindings again from the active Silo's keys.</summary>
    public static void Apply()
    {
        if (_target is not { } target) return;

        foreach (var old in Installed) target.InputBindings.Remove(old);
        Installed.Clear();

        foreach (var command in Commands)
        {
            var key = KeyOf(command);
            if (!key.IsSet) continue;

            try
            {
                var binding = new KeyBinding(new LateBoundCommand(command.Command), KeyInterop.KeyFromVirtualKey(key.VirtualKey), ModifiersOf(key));
                target.InputBindings.Add(binding);
                Installed.Add(binding);
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                Log.Warning(ex, "{key} cannot be used for {command}", key, command.Name);
            }
        }
    }

    private static ModifierKeys ModifiersOf(ShortcutKey key)
    {
        var m = ModifierKeys.None;
        if (key.Modifiers.HasFlag(ShortcutModifiers.Control)) m |= ModifierKeys.Control;
        if (key.Modifiers.HasFlag(ShortcutModifiers.Alt)) m     |= ModifierKeys.Alt;
        if (key.Modifiers.HasFlag(ShortcutModifiers.Shift)) m   |= ModifierKeys.Shift;
        if (key.Modifiers.HasFlag(ShortcutModifiers.Windows)) m |= ModifierKeys.Windows;
        return m;
    }
}
