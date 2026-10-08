namespace Silo.Shell;

public enum UriSchemeRegistration
{
    /// <summary>
    ///     Do not perform any URI registration for this scheme.
    /// </summary>
    DontRegister,

    /// <summary>
    ///     Registers the URI scheme only if no other handler is handling it.
    /// </summary>
    Register,

    /// <summary>
    ///     Attempt to register the URI scheme, but continues if unsuccessful
    /// </summary>
    RegisterDefault,

    /// <summary>
    ///     Requires that the URI scheme be registered
    /// </summary>
    Require,

    /// <summary>
    ///     Requires that the URI scheme be registered and that we are the
    ///     default hndler.
    /// </summary>
    RequireDefault
}
