using Silo.DbModel.Global;
using Telefrag.DI;

namespace Silo.Connectors;

public interface IConnection : IContainerHost, IProvider<Container>
{
    /// <summary>
    /// Gets a reference to the owning IConnector
    /// </summary>
    IConnector Connector { get; }

    /// <summary>
    /// Gets the seriailzed version of the data which is to be storeed
    /// on disk.
    /// </summary>
    string? PersistentConfig { get; }

    /// <summary>
    /// Gets the seriailzed version of the data which tempoarily
    /// overrides the config stored on disk
    /// </summary>

    string? TempConfig { get; }

    /// <summary>
    /// Gets an object representing the current connection state.
    /// </summary>
    object? State { get; }

    /// <summary>
    /// Gets the destination hostname (for TLS checks and the status bar)
    /// </summary>
    string? GetHostname();

    /// <summary>
    /// Makes an attempt to Connect to the data source
    /// </summary>
    Task<bool> Connect(CancellationToken cancel = default);

    /// <summary>
    /// Disconnects from the data source
    /// </summary>
    Task<bool> Disconnect();

    /// <summary>
    /// Removes the connection from the Silo and the tree
    /// </summary>
    Task<bool> Remove();

    /// <summary>
    /// Returns true if connected, false if disconnected, or null
    /// if connectionless.
    /// </summary>
    bool? ConnectionState { get; }

    /// <summary>
    /// Gets whether the connection is connected or is connectionless
    /// </summary>
    public bool IsConnected => ConnectionState is true or null;

    /// <summary>
    /// Gets whether this connection is visible in the tree
    /// </summary>
    bool IsInTree { get; }

    /// <summary>
    /// Gets a refrence to the node in the tree that represents this node
    /// </summary>
    SiloNode? Node { get; }

    /// <summary>
    /// Builds the UI window needed for this connection
    /// </summary>
    /// <returns>Returns a built-but-not-yet-shown Window instance or null</returns>
    Window? BuildWindow();

    /// <summary>
    /// Gets a collection of arbitrary metadata associated with the connection
    /// </summary>
    List<object> Metadata { get; }
}