using System.Collections.ObjectModel;
using System.IO;
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
    public        ISiloUserContext UserContext { get; private set; }
    public        bool IsAuthorable => UserContext.IsAuthorable;
    public        bool IsWritable => UserContext.IsWritable;
    public        bool IsReadable => UserContext.IsReadable;
    public        bool IsDeveloper => UserContext.IsDeveloper;
    public        bool IsOwner => UserContext.IsOwner;
    public        string? Name => FileInfo?.Name;
    public        string? FilePath => FileInfo?.FullName;
    public        FileInfo? FileInfo => File is { } f ? new(f.FilePath) : null;

    public SiloCommands Commands => field ??= new(this);

    public static OpenSilo CreateFile(SiloFile file)
    {
        file.Required();

        var d = Database.CreateNewSilo(file.FilePath);
        OpenSilo c = new()
                     {
                         File        = file,
                         Meta        = new(),
                         Data        = d,
                         UserContext = d.GetUserContext()
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

    public async Task<bool> Shutdown()
    {
        if (Data is null)
            return false;

        return await Data.ShutdownAsync();
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