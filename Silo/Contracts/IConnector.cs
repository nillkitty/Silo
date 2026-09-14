namespace Silo.Connectors;

public interface IConnector
{
    /// <summary>
    /// Gets the last error the connector encountered
    /// </summary>
    Exception? LastError { get; }

    /// <summary>
    /// Flags related to the operation of the connector
    /// </summary>
    ConnectorSetupFlags Flags { get; }

    /// <summary>
    /// Creates a connection
    /// </summary>
    /// <returns></returns>
    IConnection CreateConnection();

    /// <summary>
    /// Gets a collection of arbitrary metadata associated with the connection
    /// </summary>
    List<object> Metadata { get; }


    /// <summary>
    /// Gets the type of object this connector uses for state values
    /// </summary>
    Type GetStateType();

    /// <summary>
    /// Gets whether the connector is connectionless based on its Flags.
    /// </summary>
    public bool IsConnectionless => Flags.HasFlag(ConnectorSetupFlags.Connectionless);
}