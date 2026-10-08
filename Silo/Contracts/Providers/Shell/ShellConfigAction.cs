namespace Silo.Shell;

/// <summary>
///     Action to take for a given Shell configuration element
///     (and presumably the data it wants the shell to be configured with)
/// </summary>
public enum ShellConfigAction
{
    /// <summary>
    ///     Do nothing
    /// </summary>
    None,

    /// <summary>
    ///     If existing configuration is found, just log it, and then
    ///     do nothing
    /// </summary>
    Log,

    /// <summary>
    ///     If existing configuration is found, log it and remove it.
    /// </summary>
    Remove,

    /// <summary>
    ///     Create the configuration only if it does not exist.  Do not
    ///     update an existing configuration/value.
    /// </summary>
    Create,

    /// <summary>
    ///     Create the configuration/value if does not exist,
    ///     and update an existing configuration if existent,
    ///     but otherwise preserve non-conflicting aspects of the existing
    ///     configuration.
    /// </summary>
    Update,

    /// <summary>
    ///     Replace the item with our configuration anytime the current
    ///     state is not the exact desired outcome (i.e. only do
    ///     enough checking to avoid re-writing the same file or regkey with the
    ///     same data over and over)
    /// </summary>
    Replace
}