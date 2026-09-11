using Silo.DbModel.Global;
using Telefrag.DI;

namespace Silo.Connectors;

public interface IConnection : IContainerHost, IProvider<Container>
{
    string?    PersistentConfig { get; }
    string?    TempConfig       { get; }
    object?    State            { get; }
    string?    GetHostname();
    Task<bool> Connect(CancellationToken cancel = default);
    Task<bool> Disconnect();
    Task<bool> Remove();
    Type       GetStateType();
    bool       IsConnected { get; }
    bool       IsInTree    { get; }
    SiloNode?  Node        { get; }
}