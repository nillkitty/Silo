using Silo.Model;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors;

public abstract class ConnectionBase<TModel> : ModelBase<TModel>, IConnection
{
    /// <summary>
    ///     Gets or sets the destination for this connection (if it is not already connected).
    /// </summary>
    public IDestination? Destination { get; set; }

    /// <summary>
    ///     Gets or sets the connection request object used to generate the Connection.
    /// </summary>
    protected ConnectionRequest? ConnectionRequest { get; set; }

    protected override Uri? NodeUri => Node?.Uri ?? Connection?.Node?.Uri;

    /// <summary>
    ///     Gets or sets the value stored for this connection in the persistent configuration
    ///     (on disk)
    /// </summary>
    protected virtual string? PersistentConfig { get; set; }

    /// <summary>
    ///     Gets or sets the value stored temporarily (which shadows the permament value).
    /// </summary>
    protected virtual string? TempConfig { get; set; }

    public bool? ConnectionState =>
        Connector.IsConnectionless ? null : (State as IConnectionState)?.IsConnected;

    public IConnector Connector { get; }

    /// <summary>
    ///     What to call this connection in lists (the name it was given when it was created); null to work one out
    ///     from the connector and destination.
    /// </summary>
    public string? DisplayText { get; set; }

    public bool IsInTree => Node?.IsVisible ?? false;

    public List<object> Metadata { get; }
    public SiloNode?    Node     { get; protected set; }

    /// <summary>
    ///     Gets or sets the current state of the connection
    /// </summary>
    public object? State { get; protected set; }

    string? IConnection.PersistentConfig => PersistentConfig;

    string? IConnection.TempConfig => TempConfig;

    /// <summary>
    ///     Gets the container providing component lookup for this connection (or above)
    /// </summary>
    public Container Components { get; } = new(Guid.NewGuid().ToString());

    /// <summary>
    ///     Gets the destination hostname of the connnection
    /// </summary>
    /// <returns></returns>
    protected string? GetHostname()
    {
        return Destination?.Uri?.Host;
    }

    protected virtual Task<bool> OnDisconnect()
    {
        return Task.FromResult(false);
    }

    protected virtual async Task OnShutdown()
    {
        // disconnect it if we're connected
        if (ConnectionState is true)
            await Disconnect();

        // clean up
        await WriteConfigAsync();
    }

    public Task<bool> WriteConfigAsync()
    {
        return OnWriteConfigAsync();
    }

    /// <summary>
    ///     Writes the connection's configuration; a connection with none to write says so (false) and leaves it to
    ///     the connector that has.
    /// </summary>
    protected virtual Task<bool> OnWriteConfigAsync()
    {
        return Task.FromResult(false);
    }

    protected virtual async Task<bool> OnRemove()
    {
        // shut down the connection
        await OnShutdown();

        // remove the node
        return Node?.Remove() ?? false;
    }

    protected virtual Type OnGetStateType()
    {
        return typeof(IConnectionState);
    }

    public Type GetStateType()
    {
        return OnGetStateType();
    }

    protected virtual Task OnInitAsync(ConnectContext context)
    {
        return Task.CompletedTask;
    }

    internal ConnectionBase(IConnector owner) : base(null)
    {
        Connector = owner.Required();
        Metadata  = [];
    }

    /// <summary>A connection has no window of its own unless a connector says otherwise.</summary>
    public virtual Window? BuildWindow()
    {
        return null;
    }

    public Task InitAsync(ConnectContext context)
    {
        return OnInitAsync(context);
    }

    string IConnection.GetHostname()
    {
        return GetHostname();
    }

    /// <summary>
    ///     A connectionless connection is always there; one that is not stays as it is (a connector with a real link
    ///     overrides this).
    /// </summary>
    public virtual Task<bool> Connect(CancellationToken cancel = default)
    {
        return Task.FromResult(ConnectionState is not false);
    }

    public async Task<bool> Disconnect()
    {
        if (State is IConnectionState { IsConnected: true }) return await OnDisconnect();

        return false;
    }

    public Task<bool> Remove()
    {
        return OnRemove();
    }

    public void InitProvider(IReceiver<Container> receiver)
    {
        receiver.Required().Receiver(Components);
    }
}