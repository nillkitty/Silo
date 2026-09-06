using System.Collections.ObjectModel;
using System.Reflection;
using Silo.Contracts;
using Silo.Extensions;
using Telefrag.Common;

namespace Silo.Model;

public class OpenSilo
{
    public        SiloFile? File { get; private set; }
    public static ObservableCollection<OpenSilo> OpenSilos { get; } = [];
    public        SiloMeta Meta { get; private set; } = new();
    public static OpenSilo OnlySilo => OpenSilos is [OpenSilo os] ? os : null;
    public        IDatabaseProvider Data { get; private set; }

    public static OpenSilo CreateFile(SiloFile file)
    {
        file.Required();

        OpenSilo c = new()
                     {
                         File = file,
                         Meta = new(),
                         Data = Database.CreateNewSilo(file.FilePath)
                     };
        return c;
    }

    public static OpenSilo OpenFile(SiloFile file)
    {
        file.Required();
        OpenSilo c = new()
                     {
                         File = file
                     };
        if (Database.Load(file) is IDatabaseProvider dbp)
        {
            c.Data = dbp;
            OpenSilos.Add(c);
        }

        return c;
    }
}

public class SiloFile(string path)
{
    public string FilePath { get; set; } = path;

    public async Task<OpenSilo> Open()
    {
        return OpenSilo.OpenFile(new SiloFile(path.Required()));
    }
}