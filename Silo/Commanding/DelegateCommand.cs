using System.Windows.Input;

namespace Silo.Commanding;

/// <summary>An <see cref="ICommand" /> that runs a delegate; enabled when the optional predicate says so (re-asked whenever WPF re-queries commands).</summary>
public sealed class DelegateCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

/// <summary>
///     Forwards to whatever command <c>resolve</c> returns at the moment it is asked (disabled when it returns null), so a
///     key or a button that was built once always acts on the Silo that is active when it is used.
/// </summary>
public sealed class LateBoundCommand(Func<ICommand?> resolve) : ICommand
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
