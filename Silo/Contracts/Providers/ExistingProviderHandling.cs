namespace Silo.Providers;

/// <summary>
///     Wgether to check for and respect existing URI providers for
///     a given URI scheme or file type.
/// </summary>
public enum ExistingProviderHandling
{
    /// <summary>
    ///     Ignore the shell entirely when opening these URIs within the app
    /// </summary>
    Ignore  = 0,
    Default = Ignore,

    /// <summary>
    ///     Check for existing apps handling the scheme, and use them if
    ///     they exist
    /// </summary>
    UseExisting,

    /// <summary>
    ///     Check for existing apps handling the scheme, and if they exist,
    ///     prompt the user whether to use the existing app, open the URI inside Silo,
    ///     or set Silo as the new default.
    /// </summary>
    PromptToUseExisting
}