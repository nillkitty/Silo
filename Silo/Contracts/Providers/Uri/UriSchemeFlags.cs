namespace Silo.Providers;

[Flags]
public enum UriSchemeFlags
{
    /// <summary>
    ///     Default value (CanOpenFrom | CanConnectTo)
    /// </summary>
    Default = CanOpenFrom | CanConnectTo,

    /// <summary>
    ///     The provider does nothing for this Scheme in this app
    /// </summary>
    None = 0,

    /// <summary>
    ///     The app can open a Silo repository from the URI
    ///     (regardless of how it actually gets the data)
    /// </summary>
    CanOpenFrom = 1,

    /// <summary>
    ///     The app can connect to instances of the URI
    ///     Scheme within the context of a Silo
    /// </summary>
    CanConnectTo = 2,

    /// <summary>
    ///     URIs of this Scheme can be
    ///     <list type="bullet">
    ///         <item>pasted into the 'Discover from URL' dialog</item>
    ///         <item>added to a Silo's 'Supplimental Sources' list</item>
    ///         <item>used as the Origin of discovered data, allowing update/sync</item>
    ///     </list>
    /// </summary>
    CanDiscoverAt = 4
}