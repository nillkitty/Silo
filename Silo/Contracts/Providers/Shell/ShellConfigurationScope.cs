namespace Silo.Shell;

[Flags]
public enum ShellConfigurationScope
{
    /// <summary>
    ///     Write to the user shell config when running a user,
    ///     Write to the system shell config when running as TrustedInstaller,
    ///     System, BATCH, SERVICE, or as a descendant of msiexec.exe or
    ///     setup.exe
    /// </summary>
    Current,

    /// <summary>
    ///     Write to the default scope for the given configuration type
    /// </summary>
    Default = Current,

    /// <summary>
    ///     Write to the user scope (almost always accessible)
    /// </summary>
    User,

    /// <summary>
    ///     Write to the DEFAULT user profile which is copied when
    ///     new user profiles are built.
    /// </summary>
    NewProfiles,

    /// <summary>
    ///     Write to the system scope (fails if not permitted)
    /// </summary>
    System,

    /// <summary>
    ///     Write anyplace it sticks
    /// </summary>
    HighestAvailable,

    /// <summary>
    ///     Tattoo to the user as though it were defined in group policy
    /// </summary>
    TattooUser,

    /// <summary>
    ///     Tattoo to the device as though it were defined in group policy
    /// </summary>
    TattooSystem
}