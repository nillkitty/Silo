namespace Silo.Connectors;

/// <summary>Makes connections the way the New Connection dialog needs: asynchronously where the connector can.</summary>
public static class ConnectionCreation
{
    /// <summary>
    ///     <see cref="IAsyncConnector.CreateConnectionAsync" /> when the connector has it; otherwise the plain
    ///     <see cref="IConnector.CreateConnection" />, called right here (so the caller's thread, usually the UI thread).
    /// </summary>
    public static Task<IConnection> CreateAsync(IConnector connector, ConnectionRequest request, CancellationToken cancel)
    {
        if (connector is IAsyncConnector async) return async.CreateConnectionAsync(request, cancel);

        cancel.ThrowIfCancellationRequested();
        return Task.FromResult(connector.CreateConnection(request));
    }
}
