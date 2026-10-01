using Silo.ViewModel;

namespace Silo.Connectors.System;

public class RegistryConnection : ConnectionBase
{
    private readonly SiloNode?    _parent;
    public           RegistryPath Path { get; }

    protected override Task OnInitAsync(ConnectContext context)
    {
        Node = context.CreateNode(_parent, this);
        return base.OnInitAsync(context);
    }

    public RegistryConnection(RegistryConnector owner, IDestination? destination, SiloNode? parent, RegistryPath path) : base(owner)
    {
        _parent     = parent;
        Destination = destination;
        Path        = path.Required();
    }
}