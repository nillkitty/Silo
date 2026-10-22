using Telefrag.DI;

namespace Silo.Providers;

public interface IContainerHost
{
    Container Components { get; }
}