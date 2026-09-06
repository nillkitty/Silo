using System.IO;
using Silo.Contracts;
using Silo.Extensions;
using Silo.Model;
using Telefrag.Common;

namespace Silo;

public static class Database
{
    public static IDatabaseProvider CreateNewSilo(string path)
    {
        var sl = App.Require<INewSiloBuilder>();
        return sl.Build(path);
    }

    public static IDatabaseProvider Load(SiloFile file)
    {
        file.Required();
        var fi = new FileInfo(file.FilePath);
        if (!fi.Exists)
            throw new
                FileNotFoundException($"File not found or no access to file:  {file.FilePath}",
                                      file.FilePath);

        var sl = App.Require<ISiloLoader>();
        return sl.Load(fi);
    }
}