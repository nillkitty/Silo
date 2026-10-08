using Silo.ViewModel;

namespace Silo.Contracts.Core;

public interface INavManager
{
    void AddNode(SiloNode siloNode, SiloNode? parent);
}