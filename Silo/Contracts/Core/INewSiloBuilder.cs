using System.IO;
using Silo.Contracts.Providers.Data.Validation;

namespace Silo.Contracts.Core;

public interface INewSiloBuilder
{
    IDatabaseProvider Build(string path);
}

public interface ISiloLoader
{
    IDatabaseProvider Load(FileInfo fi);
}