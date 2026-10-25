using Silo.Connectors.Virtual;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors;

/// <summary>
///     The connection of everything that has none of its own (<see cref="App.LocalConnection" />): this computer. It is
///     made when
///     the app starts, before a Silo is open.
/// </summary>
[Singleton(typeof(IConnector))]
public class LocalConnection : VirtualRoot<LocalNode>
{
    public LocalConnection() : base(false)
    {
        Node = new LocalNode(this);
    }
}

public record LocalNode : SiloNode
{
    public          LocalConnection Connection { get; init; }
    public override bool            IsVisible  => false;

    public LocalNode(LocalConnection connection) : base(connection)
    {
        Connection = connection.Required();
    }
}