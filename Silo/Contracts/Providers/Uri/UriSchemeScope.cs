namespace Silo.Providers;

public enum UriSchemeScope
{
    /// <summary>
    ///     The nature of the Scheme is unknown
    /// </summary>
    Unknown,

    /// <summary>
    ///     The provider is the private owner of the scheme's use
    /// </summary>
    Private,

    /// <summary>
    ///     The Scheme is documented or well-known and the provider may
    ///     conflict with other apps that also support the Scheme
    /// </summary>
    WellKnown,

    /// <summary>
    ///     The Scheme is a thinly vailed format specifier (e.g. tel:)
    ///     and is used adhoc without coordination.
    /// </summary>
    AdHoc
}