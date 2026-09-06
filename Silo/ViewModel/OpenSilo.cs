using System.Collections.ObjectModel;
using System.Reflection;
using Silo.Extensions;

namespace Silo.Model;

public class OpenSilo
{
    public        SiloFile? File { get; private set; }
    public static ObservableCollection<OpenSilo> OpenSilos { get; } = [];
    public        SiloMeta Meta { get; private set; } = new();
    public static OpenSilo OnlySilo => OpenSilos is [OpenSilo os] ? os : null;

    public static OpenSilo CreateFile(SiloFile file)
    {
        file.Required();


        OpenSilo c = new()
                     {
                         File = file,
                         Meta = new()
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
        if (_load(c))
        {
            OpenSilos.Add(c);
            SiloOpened?.Invoke();
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