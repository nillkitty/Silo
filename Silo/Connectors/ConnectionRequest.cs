using Silo.ViewModel;

namespace Silo.Connectors;

/// <summary>
///     Object passed to an <see cref="IConnector" /> to create an <see cref="IConnection" />
/// </summary>
public class ConnectionRequest
{
    /// <summary>
    ///     The requested connector used to create the connection;  if this
    ///     is null, the appropriate connector is detected from the destination
    ///     URI, which must be provided if Connector is null.
    /// </summary>
    public IConnector? Connector { get; set; }

    /// <summary>
    ///     The requested destination, if any
    /// </summary>
    public IDestination? Destination { get; set; }

    /// <summary>
    ///     Collection of additional metadata for use by specific connectors
    /// </summary>
    public List<object> Metadata { get; set; } = [];

    /// <summary>
    ///     An optional existing nav tree node to which the root connection node
    ///     (if any is created) should be a child of; if null -- then the
    ///     connnection will create root-level navigation nodes.
    /// </summary>
    public SiloNode? ParentNode { get; set; }

    /// <summary>
    ///     The requested URI, if any
    /// </summary>
    public Uri? RequestedUri { get; set; }

    /// <summary>
    ///     An optional serialized configuration, used when restoring
    ///     previously active connections which emitted a serialized
    ///     state.
    /// </summary>
    public string? SerializedState { get; set; }

    /// <summary>
    ///     Creates an empty connection request.
    /// </summary>
    public ConnectionRequest()
    {
    }

    /// <summary>
    ///     Creates a connection request to a specific destination.
    /// </summary>
    public ConnectionRequest(IDestination destination)
    {
        Destination  = destination.Required();
        RequestedUri = destination.Uri;
    }

    /// <summary>
    ///     Creates a connection request to a specific Uri
    /// </summary>
    public ConnectionRequest(Uri uri)
    {
    }
}