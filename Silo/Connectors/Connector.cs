using Silo.Contracts.Connectors;
using Telefrag.DI;

namespace Silo.Connectors;

public abstract class ConnectorBase : IConnector
{
    public ConnectorSetupFlags Flags { get; protected set; }

    /// <summary>
    ///     Gets or sets the subscribed receiver
    /// </summary>
    public IReceiver<Container>? Receiver { get; protected set; }

    public Exception? LastError { get; protected set; }

    public List<object> Metadata { get; } = [];

    protected abstract IConnection OnCreateConnection(ConnectionRequest crq);

    /// <summary>
    ///     Initializes the provider
    /// </summary>
    public void InitProvider(IReceiver<Container> receiver)
    {
        Receiver = receiver.Required();
    }

    IConnection IConnector.CreateConnection(ConnectionRequest crq)
    {
        return OnCreateConnection(crq);
    }

    public abstract Type GetStateType();
}

public interface IConnectionState
{
    bool IsConnected { get; }
    bool IsDisposed  { get; }
    bool IsFailed    { get; }
}