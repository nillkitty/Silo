using Silo.ViewModel;

namespace Silo.Connectors;

public interface INavManager
{
    void AddNode(SiloNode siloNode, SiloNode? parent);
}