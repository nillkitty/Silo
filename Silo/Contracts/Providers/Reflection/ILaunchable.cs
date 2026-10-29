namespace Silo.Providers.Reflection;

public interface ILaunchable
{
    /// <summary>
    ///     Gets the template used for arguments;  if null, arguments are
    ///     sent in normal form
    /// </summary>
    string? ArgTemplate { get; }

    /// <summary>
    ///     The path of the executable to launch
    /// </summary>
    string? FilePath { get; }

    /// <summary>
    ///     Whether to run the processes using the shell
    /// </summary>
    bool UseShellExecute { get; }
}