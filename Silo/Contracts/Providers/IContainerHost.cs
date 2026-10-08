using Telefrag.DI;

namespace Silo.Contracts.Providers;

public interface IContainerHost
{
    Container Components { get; }
}