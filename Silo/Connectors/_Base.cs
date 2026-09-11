using Silo.DbModel.Global;
using Telefrag.DI;

namespace Silo.Connectors;

public abstract class ConnectorBase : IConnection
{
    /// <summary>
    ///     Gets or sets the subscribed receiver
    /// </summary>
    public IReceiver<Container>? Receiver { get; protected set; }

    /// <summary>
    ///     Gets or sets the value stored for this connection in the persistent configuration
    ///     (on disk)
    /// </summary>
    protected virtual string? PersistentConfig { get; set; }

    /// <summary>
    ///     Gets or sets the value stored temporarily (which shadows the permament value).
    /// </summary>
    protected virtual string? TempConfig { get; set; }

    /// <summary>
    ///     Gets or sets the destination for this connection (if it is not already connected).
    /// </summary>
    public Destination? Destination { get; set; }

    /// <summary>
    ///     Gets the container providing component lookup for this connection (or above)
    /// </summary>
    public Container Components { get; } = new(Guid.NewGuid().ToString());

    Type IConnection.GetStateType()
    {
        return OnGetStateType();
    }

    public bool      IsConnected => (State as IConnectionState)?.IsConnected ?? false;
    public bool      IsInTree    => Node?.IsVisible                          ?? false;
    public SiloNode? Node        { get; protected set; }

    /// <summary>
    ///     Initializes the provider
    /// </summary>
    public void InitProvider(IReceiver<Container> receiver)
    {
        Receiver = receiver.Required();
    }

    string? IConnection.PersistentConfig => PersistentConfig;

    string? IConnection.TempConfig => TempConfig;

    /// <summary>
    ///     Gets or sets the current state of the connection
    /// </summary>
    public object? State { get; protected set; }

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

    /// <summary>
    ///     Gets the destination hostname of the connnection
    /// </summary>
    /// <returns></returns>
    protected string? GetHostname()
    {
        return Destination?.Uri?.Host ?? Destination?.Address?.ToString();
    }

    protected virtual Task<bool> OnDisconnect()
    {
        return Task.FromResult(false);
    }

    protected virtual async Task OnShutdown()
    {
        // disconnect it if we're connected
        if (IsConnected)
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
}

public interface IConnectionState
{
    bool IsConnected { get; }
    bool IsFailed    { get; }
    bool IsDisposed  { get; }
}