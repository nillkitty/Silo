using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors.Virtual;

public abstract class VirtualRoot<TModel>(bool requireActiveSilo) : VirtualRoot<TModel, ConnectionlessState>(requireActiveSilo)
{
    protected VirtualRoot() : this(true)
    {
    }
}

/// <summary>
///     A VirtualRoot is a combination Connector/Connection/Root Node for basic virtual trees like "Process/Current
///     Process"
///     which implement a static tree and are connectionless.
/// </summary>
public abstract class VirtualRoot<TModel, TState> : ConnectorBase<TModel, TState>, IConnector, IConnection where TState : IConnectionState
{
    private IReceiver<Container>? _recv;
    public  bool?                ConnectionState  => null;
    public  IConnector           Connector        => this;
    public  bool                 IsInTree         => Node is not null;
    public  SiloNode?            Node             { get; protected set; }
    public  string?              PersistentConfig { get; protected set; }
    public  object?              State            { get; protected set; }
    public  string?              TempConfig       => null;
    public new ConnectorSetupFlags Flags           { get; } = ConnectorSetupFlags.Connectionless;
    public new Exception?        LastError        { get; protected set; }
    public new List<object>      Metadata         { get; } = [];

    public Container Components { get; }

    protected override IConnection OnCreateConnection(ConnectionRequest crq)
    {
        return this;
    }

    /// <param name="requiresSilo">
    ///     True (the default) for a root that is made for a Silo: asks for the active one (and says so when there is none).
    ///     False for one that belongs to the app (<see cref="LocalConnection" />): it is made before any Silo is open.
    /// </param>
    protected VirtualRoot(bool requiresSilo = true)
    {
        var silo = requiresSilo ? App.RequireActiveSilo() : App.Instance?.ActiveSilo;
        Components = silo is null ? new Container(GetType().FullName) : silo.Components.CreateChildContainer(GetType().FullName);
    }

    public string? GetHostname()
    {
        return ".";
    }

    Task<bool> IConnection.Connect(CancellationToken cancel)
    {
        return Task.FromResult(true);
    }

    Task<bool> IConnection.Disconnect()
    {
        return Task.FromResult(false);
    }

    public async Task<bool> Remove()
    {
        return Node?.Remove() ?? true;
    }

    Window? IConnection.BuildWindow()
    {
        return null;
    }

    Task IConnection.InitAsync(ConnectContext context)
    {
        return Task.CompletedTask;
    }

    public new void InitProvider(IReceiver<Container> receiver)
    {
        _recv = receiver;
    }

    public IConnection CreateConnection(ConnectionRequest crq)
    {
        return OnCreateConnection(crq);
    }
}