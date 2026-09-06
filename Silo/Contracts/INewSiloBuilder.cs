using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Silo.Contracts;

public interface INewSiloBuilder
{
    IDatabaseProvider Build(string path);
}

public interface ISiloLoader
{
    IDatabaseProvider Load(FileInfo fi);
}