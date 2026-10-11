using Silo.Model;

namespace Silo.Commanding;

/// <summary>
///     Static instances of <see cref="EnablerFunc" />, used to determine if a command CanExecute (or it's grayed out)
/// </summary>
public static class Enabler
{
    public delegate bool EnablerFunc(object? o);

    /// <summary>
    ///     Always enabled
    /// </summary>
    public static EnablerFunc Always => _ => true;

    /// <summary>
    ///     Enabled if the Silo is authorable
    /// </summary>
    public static EnablerFunc IsSiloAuthorable => _ => _active.IsAuthorable;

    /// <summary>
    ///     Enabled if the Active SIlo has been loaded.
    /// </summary>
    public static EnablerFunc IsSiloLoaded => _ => _active is { Data: not null };

    /// <summary>
    ///     Never enabled
    /// </summary>
    public static EnablerFunc Never => _ => false;

    /// <summary>
    ///     Enabled if there is an Active Silo document
    /// </summary>
    private static OpenSilo? _active => App.Instance?.ActiveSilo;
}

/// <summary>
///     Static instances of <see cref="CheckerFunc" />, used to determine if a menu item/command IsChecked
/// </summary>
public static class Checker
{
    public delegate bool CheckerFunc(object? o);

    public static CheckerFunc Always => _ => true;

    public static CheckerFunc IsSiloActive =>
        o => App.Instance?.ActiveSilo is { } a && a == o;

    public static CheckerFunc Never => _ => false;
}