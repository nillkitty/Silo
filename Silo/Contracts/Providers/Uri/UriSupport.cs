namespace Silo.Shell;

/// <summary>
///     Return value of interrogating an <see cref="IUriProvider" /> as
///     to what about a given Scheme it supports.
/// </summary>
/// <param name="Scheme">The Scheme being evaluted</param>
/// <param name="Provider">The Type of he provider making these assertions</param>
/// <param name="SchemeAuthority">The degree to which we are authoritative for the Scheme's popular usage</param>
/// <param name="OtherProviders">Whether we check, respect, remove, or trample on existing URI providers for this Scheme</param>
/// <param name="UserRegistration">Whether we register the Scheme for the User</param>
/// <param name="SystemRegistration">Whether we register the Scheme for the system</param>
/// <param name="ShellOptions">
///     The options to register with the Shell, if
///     such registration ever occurs.
/// </param>
public record UriSupport(
    string                   Scheme,
    Type                     Provider,
    UriSchemeScope           SchemeAuthority,
    ExistingProviderHandling OtherProviders,
    UriSchemeRegistration    UserRegistration,
    UriSchemeRegistration    SystemRegistration,
    IShellTypeOptions        ShellOptions);
