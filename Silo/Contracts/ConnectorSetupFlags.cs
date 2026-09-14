using System.Data;
using System.Drawing;
using Telefrag.Common;

namespace Silo.Connectors;

[Flags]
public enum ConnectorSetupFlags
{
    /// <summary>
    /// Default behavior
    /// </summary>
    None,

    /// <summary>
    /// This flag indicates the configuration UI should always be
    /// shown, even if the connection config is saved.  Without this
    /// flag set, the configuration UI is only shown for new
    /// connetions, imported/cloned/re-used connections, and upon editing
    /// the connection.
    /// </summary>
    AlwaysRequiresConfig = 1,

    /// <summary>
    /// Accepts a local file path as its configuration
    /// </summary>
    RequiresFile = 2,

    /// <summary>
    /// Once the connector is instantiated, the connection is
    /// always there, and there is no need for Connect,
    /// Disconnect, Reconnect, or AutoReconnect
    /// </summary>
    Connectionless = 4,

    /// <summary>
    /// If this flag is is set, a connector with a broken connection will
    /// periodivcally attempt to auto-reconnect in the background.  If
    /// not set, a severed connection is not considered until the connector
    /// performs its next online operation.
    /// </summary>
    BackgroundReconnect = 8,

    /// <summary>
    /// Normally the app tracks the ICMP visibility of destinations and uses this to
    /// determine if the source is available for background reconnection if the
    /// destination is known to respond to ICMP.   This flag ignores that and always
    /// attempts a full reconnnection in the background.
    /// </summary>
    IgnoreIcmpVisibility = 0x10,
}