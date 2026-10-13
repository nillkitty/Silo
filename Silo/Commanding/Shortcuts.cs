using System.Windows.Input;
using Silo.Model;

namespace Silo.Commanding;

/// <summary>
///     The keyboard shortcuts for the main window's menu items. The menu shows each key through
///     <c>InputGestureText</c> (MenuBar.xaml) -- WPF only displays that text, it does not make the key work, because
///     these commands are plain <see cref="ICommand" />s rather than <see cref="RoutedCommand" />s -- so the keys are
///     wired here, to the same commands the menu runs. Keep the two lists in step.
/// </summary>
/// <remarks>
///     Every binding resolves its command when the key is pressed, not when the window is built, so the Silo shortcuts
///     always act on whichever Silo is active at that moment, and do nothing (and report "not enabled") when none is.
/// </remarks>
public static class Shortcuts
{
    /// <summary>Adds the shortcut key bindings to <paramref name="target" /> (normally the main window). Alt+F4 is the system's own.</summary>
    public static void Install(UIElement target)
    {
        const ModifierKeys ctrl      = ModifierKeys.Control;
        const ModifierKeys ctrlShift = ModifierKeys.Control | ModifierKeys.Shift;

        Bind(target, Key.N, ctrl,      () => App.NewSiloCommand);
        Bind(target, Key.O, ctrl,      () => App.OpenSiloCommand);
        Bind(target, Key.O, ctrlShift, () => App.OpenSiloFromUrlCommand);

        Bind(target, Key.N, ctrlShift, () => Active?.Commands.NewConnection);
        Bind(target, Key.S, ctrl,      () => Active?.Commands.SaveSilo);
        Bind(target, Key.S, ctrlShift, () => Active?.Commands.SaveSiloAs);
    }

    private static OpenSilo? Active => App.Instance?.ActiveSilo;

    private static void Bind(UIElement target, Key key, ModifierKeys modifiers, Func<ICommand?> command)
    {
        target.InputBindings.Add(new KeyBinding(new LateBoundCommand(command), key, modifiers));
    }

    /// <summary>Forwards to whatever command <c>resolve</c> returns at the moment it is asked; disabled when it returns null.</summary>
    private sealed class LateBoundCommand(Func<ICommand?> resolve) : ICommand
    {
        public bool CanExecute(object? parameter)
        {
            return resolve() is { } c && c.CanExecute(parameter);
        }

        public void Execute(object? parameter)
        {
            if (resolve() is { } c && c.CanExecute(parameter)) c.Execute(parameter);
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
