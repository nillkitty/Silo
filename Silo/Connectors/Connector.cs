using Telefrag.DI;

namespace Silo.Connectors;

public abstract class ConnectorBase<TModel, TState> : IConnector, IConnectorUi where TState : IConnectionState
{
    /// <summary>
    ///     Gets or sets the subscribed receiver
    /// </summary>
    public IReceiver<Container>? Receiver { get; protected set; }

    protected IStateManager<TModel, TState> StateManager { get; set; }

    public ConnectorSetupFlags Flags { get; protected set; }

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

    protected virtual IConnectorSetup? OnCreateSetup(ConnectorSetupContext context)
    {
        return null;
    }

    protected virtual TState? OnGetState(TModel context)
    {
        return default;
    }

    protected virtual TState? OnGetStateOther(object context)
    {
        return default;
    }

    private IConnectionState GetDefaultState()
    {
        return ConnectionState.Default;
    }

    IConnection IConnector.CreateConnection(ConnectionRequest crq)
    {
        return OnCreateConnection(crq);
    }

    Type IConnector.GetStateType()
    {
        return StateManager?.GetStateType() ?? typeof(object);
    }

    public IConnectionState GetState(object context)
    {
        // See if we handle this directly in the connector
        if (OnGetStateOther(context) is { } o) return o;
        if (context is not TModel tm)
            return GetDefaultState();

        if (OnGetState(tm) is { } o2) return o2;

        // Get a state manager if we don't have one.
        StateManager ??= App.Require<IStateManager<TModel, TState>>();

        // Get the state from it.
        if (StateManager.GetState(tm) is { } o3)
            return o3;

        return GetDefaultState();
    }

    public IConnectorSetup CreateSetup(ConnectorSetupContext context)
    {
        return OnCreateSetup(context)!;
    }
}

public class ConnectionState : IConnectionState
{
    public static IConnectionState Default { get; } = new ConnectionState();

    public bool IsConnected => false;
    public bool IsDisposed  => false;
    public bool IsFailed    => false;

    private ConnectionState()
    {
    }
}