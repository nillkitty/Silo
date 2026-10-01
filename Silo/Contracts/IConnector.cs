namespace Silo.Connectors;

public interface IConnector
{
    /// <summary>
    ///     Flags related to the operation of the connector
    /// </summary>
    ConnectorSetupFlags Flags { get; }

    /// <summary>
    ///     Gets the last error the connector encountered
    /// </summary>
    Exception? LastError { get; }

    /// <summary>
    ///     Gets a collection of arbitrary metadata associated with the connection
    /// </summary>
    List<object> Metadata { get; }

    /// <summary>
    ///     Gets a human-readable name for this connector, e.g. to label a diagram edge between
    ///     a Silo and one of its connections.  Defaults to the connector's type name with a
    ///     trailing "Connector" trimmed (so <c>FileConnector</c> reads as "File"); override for
    ///     a friendlier name.
    /// </summary>
    public string DisplayName
    {
        get
        {
            var          n      = GetType().Name;
            const string suffix = "Connector";
            return n.EndsWith(suffix, StringComparison.Ordinal) ? n[..^suffix.Length] : n;
        }
    }

    /// <summary>
    ///     Gets whether the connector is connectionless based on its Flags.
    /// </summary>
    public bool IsConnectionless => Flags.HasFlag(ConnectorSetupFlags.Connectionless);

    /// <summary>
    ///     Creates a connection
    /// </summary>
    /// <returns></returns>
    IConnection CreateConnection(ConnectionRequest crq);


    /// <summary>
    ///     Gets the type of object this connector uses for state values
    /// </summary>
    Type GetStateType();
}