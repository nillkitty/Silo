using Silo.Connectors;

namespace Silo.Contracts.Connectors;

/// <summary>
///     A connector whose connections take time to make (it has to touch a network share, a server, a disk that spins up):
///     it makes them without blocking the caller and gives up when asked. The New Connection dialog uses this, with a
///     progress dialog and a Cancel button if the attempt takes more than a second. A connector without it is asked
///     through <see cref="IConnector.CreateConnection" /> on the UI thread, so only give that one work that is instant.
/// </summary>
public interface IAsyncConnector
{
    /// <summary>
    ///     Makes the connection. Must not do slow file system or network calls on the calling (UI) thread. If
    ///     <paramref name="cancel" /> is signalled it should stop waiting and throw <see cref="OperationCanceledException" />;
    ///     a call that cannot itself be interrupted (a blocked file system call) may be abandoned to finish on its own.
    /// </summary>
    Task<IConnection> CreateConnectionAsync(ConnectionRequest crq, CancellationToken cancel);
}
