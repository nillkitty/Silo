using Telefrag.DI;

namespace Silo.Connectors;

public interface IConnection : IContainerHost, IProvider<Container>
{
    string     PersistentConfig { get; }
    string     TempConfig       { get; }
    object     State            { get; }
    string     GetHostname();
    Task<bool> Connect(CancellationToken cancel = default);
    Task<bool> Disconnect();
    Task<bool> Remove();
    Type       GetStateType();
}