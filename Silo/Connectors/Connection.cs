using Silo.DbModel.Global;
using Silo.Model;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors;

public class ConnectionBase : ModelBase, IConnection
{
    /// <summary>
    ///     Gets or sets the destination for this connection (if it is not already connected).
    /// </summary>
    public IDestination? Destination { get; set; }

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

    protected virtual async Task<bool> OnWriteConfigAsync()
    {
        return false;
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

    internal ConnectionBase(IConnector owner)
    {
        Connector = owner.Required();
        Metadata  = [];
    }

    public Window? BuildWindow()
    {
        throw new NotImplementedException();
    }

    public Task InitAsync(ConnectContext context)
    {
        return OnInitAsync(context);
    }

    string IConnection.GetHostname()
    {
        return GetHostname();
    }

    public Task<bool> Connect(CancellationToken cancel = default)
    {
        throw new NotImplementedException();
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
        throw new NotImplementedException();
    }
}