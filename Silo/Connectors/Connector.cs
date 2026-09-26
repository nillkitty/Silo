using Microsoft.AspNetCore.Connections;
using Silo.DbModel.Global;
using Silo.DbModel.Meta;
using Telefrag.DI;

namespace Silo.Connectors;

public abstract class ConnectorBase : IConnector
{
    /// <summary>
    ///     Gets or sets the subscribed receiver
    /// </summary>
    public IReceiver<Container>? Receiver { get; protected set; }

    public Exception?          LastError { get; protected set; }
    public ConnectorSetupFlags Flags     { get; protected set; }

    protected abstract IConnection OnCreateConnection(ConnectionRequest crq);
    IConnection IConnector.        CreateConnection(ConnectionRequest   crq) => OnCreateConnection(crq);

    public          List<object> Metadata { get; } = [];
    public abstract Type         GetStateType();

    /// <summary>
    ///     Initializes the provider
    /// </summary>
    public void InitProvider(IReceiver<Container> receiver)
    {
        Receiver = receiver.Required();
    }
}

public interface IConnectionState
{
    bool IsConnected { get; }
    bool IsFailed    { get; }
    bool IsDisposed  { get; }
}