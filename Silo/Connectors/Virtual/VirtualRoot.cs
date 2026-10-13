using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors.Virtual;

/// <summary>
///     A VirtualRoot is a combination Connector/Connection/Root Node for basic virtual trees like "Process/Current
///     Process"
///     which implement a static tree and are connectionless.
/// </summary>
public abstract class VirtualRoot : IConnector, IConnection
{
    private IReceiver<Container> _recv;
    public  bool?                ConnectionState  => null;
    public  IConnector           Connector        => this;
    public  bool                 IsInTree         => Node is not null;
    public  SiloNode?            Node             { get; protected set; }
    public  string?              PersistentConfig { get; protected set; }
    public  object?              State            { get; protected set; }
    public  string?              TempConfig       => null;
    public  ConnectorSetupFlags  Flags            { get; } = ConnectorSetupFlags.Connectionless;
    public  Exception?           LastError        { get; protected set; }
    public  List<object>         Metadata         { get; } = [];


    public Container Components { get; }

    public VirtualRoot()
    {
        var s = App.RequireActiveSilo();
        Components = s.Components.CreateChildContainer(GetType().FullName);
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

    public void InitProvider(IReceiver<Container> receiver)
    {
        _recv = receiver;
    }

    public IConnection CreateConnection(ConnectionRequest crq)
    {
        return this;
    }

    public Type GetStateType()
    {
        return typeof(object);
    }
}