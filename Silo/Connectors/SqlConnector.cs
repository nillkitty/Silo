using Telefrag.DI;

namespace Silo.Connectors;

public abstract class ConnectorBase : IConnection
{
    public Container Components { get; } = new(Guid.NewGuid().ToString());

    protected abstract Type OnGetStateType();
    Type IConnection.       GetStateType() => OnGetStateType();

    public void InitProvider(IReceiver<Container> receiver)
    {
        throw new NotImplementedException();
    }

    protected virtual string? PersistentConfig { get; set; }
    string? IConnection.      PersistentConfig => PersistentConfig;
    protected virtual string? TempConfig       { get; set; }
    string? IConnection.      TempConfig       => TempConfig;

    public object State { get; protected set; }

    protected string? GetHostname()
    {
        throw new NotImplementedException();
    }

    string? IConnection.GetHostname() => GetHostname();

    public Task<bool> Connect(CancellationToken cancel = default)
    {
        throw new NotImplementedException();
    }

    public Task<bool> Disconnect()
    {
        throw new NotImplementedException();
    }

    public Task<bool> Remove()
    {
        throw new NotImplementedException();
    }

    public static Type GetStateType()
    {
        throw new NotImplementedException();
    }
}