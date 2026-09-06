using Telefrag.DI;

namespace Silo.Connectors;

public interface IContainerHost
{
    Container Components { get; }
}