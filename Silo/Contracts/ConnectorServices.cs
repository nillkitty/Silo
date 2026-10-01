using Silo.Connectors;

namespace Silo.Contracts.ConnectorServices;

/// <summary>
///     Items with a display name.
/// </summary>
public interface IDisplayName
{
    string? DisplayName { get; }
}

/// <summary>
///     A connector which can create an automatic connection to a known
///     context without any arguments or a destination, for example,
///     a file system root connection, or a connection to a machine's
///     active directory domain/forest, or the registry hive of the current
///     user.
/// </summary>
public interface IAutoConnectable
{
    IConnection? CreateAutoConnection();
}

/// <summary>
///     A connector which can accept a Credential object in its
///     <see cref="ConnectionRequest.Metadata" />
/// </summary>
public interface IAuthenticatable
{
    ConnectorAuthFlags AuthFlags { get; }
}

/// <summary>
///     Flags used to define how a connector facilitates authentciation
/// </summary>
[Flags]
public enum ConnectorAuthFlags
{
    /// <summary>
    ///     Default behavior
    /// </summary>
    None = 0,

    /// <summary>
    ///     Require a credential for every connection
    /// </summary>
    RequireCredential = 1,

    /// <summary>
    ///     Whether connections should make an attempt to connect
    ///     anonymously and only attempt authentication when challenged.
    /// </summary>
    AttemptAnonymouslyFirst = 2,

    /// <summary>
    ///     Whether a failed authentication/challenge triggers the UI to
    ///     provide a new credential.
    /// </summary>
    PromptOnChallenge = 4,

    /// <summary>
    ///     Don't permit the user to save entered credentials
    /// </summary>
    DontAllowSavingCredentials = 8,

    /// <summary>
    ///     Always store credentials entered which result in a successful
    ///     connection in the Silo
    /// </summary>
    AlwaysStoreEnteredCredentials = 0x10,

    /// <summary>
    ///     Normally Silo will track whether a credential was successful
    ///     (or not) for a given destination/URI.  This will prevent that.
    /// </summary>
    DontTrackCredentialSuccess = 0x20
}

/// <summary>
///     A connector which can use the current security context as a
///     authenticatable credential.
/// </summary>
public interface IWindowsAuthenticatable
{
    /// <summary>
    ///     Windows authentication flags.
    /// </summary>
    WinAuthFlags WinAuthFlags { get; }
}

/// <summary>
///     Flags pretaining to Integrated Windows Authentication
/// </summary>
[Flags]
public enum WinAuthFlags
{
    /// <summary>
    ///     Default behavior
    /// </summary>
    None = 0,

    /// <summary>
    ///     Attempts IWA if no credential was provided for a connection
    /// </summary>
    WinAuthWhenNoCreds = 1,

    /// <summary>
    ///     Attempts IWA if explicit credentials for a connection fail
    ///     to authenticate.
    /// </summary>
    WinAuthIfCredsFail = 2,

    /// <summary>
    ///     Attempt IWA before any configured credentials, and only fall back
    ///     if IWA negotiation fails.
    /// </summary>
    WinAuthThenCreds = 4,

    /// <summary>
    ///     Whether to prompt for new credentials if Windows Auth fails
    /// </summary>
    WinAuthPromptOnFail = 8,

    /// <summary>
    ///     Allow saving the windows credential (if entered) to the Cred Store
    /// </summary>
    AllowSavingToCredMgr = 0x10,

    /// <summary>
    ///     Whether an attempt can be made to use a Credential (in the Silo)
    ///     as a network credential for accessing the remote system.
    /// </summary>
    AllowWinAuthRuntimeCreds = 0x20
}