namespace Silo.Shell;

/// <summary>
///     Pairs a shell configuration element with an Action indicating
///     what we're willing to do to set/enforce/apply it.
/// </summary>
/// <param name="Action">The action(s) to take</param>
/// <param name="Configuration">The configuration data/value</param>
public record ShellConfigure<TConfigType>(ShellConfigAction Action, TConfigType? Configuration = default);