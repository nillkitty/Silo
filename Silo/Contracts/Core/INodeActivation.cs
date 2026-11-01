namespace Silo.Contracts.Core;

/// <summary>A node's model that does something when the node is activated in the Explorer (double-click or Enter), such as opening a Lab.</summary>
public interface INodeActivation
{
    void Activate();
}
