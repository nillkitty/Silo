using Silo.Providers;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors.Virtual;

/// <summary>
///     Provides a connector to reflect on the services registered in the DI Container
/// </summary>
[Transient(typeof(IConnector))]
public class DiConnector : VirtualRoot<DiModel?, ConnectionlessState>, INodeProvider<Container>
{
    /// <summary>
    ///     Gets the Container that this instance is bound to
    /// </summary>
    public Container Container { get; private set; }

    Container? INodeProvider<Container>.Data { get; }

    protected override IConnection OnCreateConnection(ConnectionRequest crq)
    {
        if (crq.RequestedUri is Uri u)
            if (u.ResolvePrivate() is IContainerHost { Components: var c })
            {
                if (c == Components)
                    return this;

                return new DiConnector(c);
            }

        return this;
    }

    public DiConnector()
    {
        Container = App.Instance!.Components;
    }

    public DiConnector(Container container)
    {
        Container = container.Required();
    }

    public SiloNode? GetNode()
    {
        Container ??= App.Instance!.Components!;
        Node      ??= SiloNode.ForObject(Container);
        return Node;
    }
}