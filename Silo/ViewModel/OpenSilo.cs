using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using Serilog;
using Silo.Connectors;
using Silo.Contracts;
using Silo.Extensions;
using Silo.Ui.Windows;
using Telefrag.Common;
using Telefrag.DI;

namespace Silo.Model;

public class OpenSilo : ModelBase
{
    public        SiloFile? File { get; private set; }
    public static ObservableCollection<OpenSilo> OpenSilos { get; } = [];
    public        SiloMeta Meta { get; private set; } = new();
    public static OpenSilo OnlySilo => OpenSilos is [OpenSilo os] ? os : null;

    public required Container Components { get; init; }

    public IDatabaseProvider Data { get; private set; }
    public ISiloUserContext  UserContext { get; private set; }
    public bool              IsAuthorable => UserContext.IsAuthorable;
    public bool              IsWritable => UserContext.IsWritable;
    public bool              IsReadable => UserContext.IsReadable;
    public bool              IsDeveloper => UserContext.IsDeveloper;
    public bool              IsOwner => UserContext.IsOwner;
    public string?           Name => FileInfo?.Name;
    public string?           FilePath => FileInfo?.FullName;
    public FileInfo?         FileInfo => File is { } f ? new(f.FilePath) : null;

    public SiloCommands    Commands   => field ??= new(this);
    public ISiloEncryption Encryption => Components.Require<ISiloEncryption>();

    public static OpenSilo CreateFile(SiloFile file)
    {
        file.Required();

        var cc = App.Instance!.Components.CreateChildContainer(file.FilePath);
        var d  = Database.CreateNewSilo(file.FilePath);
        OpenSilo c = new()
                     {
                         File        = file,
                         Meta        = new(),
                         Data        = d,
                         Components  = cc,
                         UserContext = d.GetUserContext()
                     };
        return c;
    }

    public static OpenSilo OpenFile(SiloFile file)
    {
        file.Required();
        var a = App.Instance!;
        Log.Debug("Opening file:  {path}", file.FilePath);
        var cc = a.Components.CreateChildContainer(file.FilePath);
        OpenSilo c = new()
                     {
                         File       = file,
                         Components = cc
                     };
        a.NotifySiloOpening(c, c);
        IDatabaseProvider dbp = Database.Load(file);
        c.Data = dbp;
        OpenSilos.Add(c);
        a.NotifySiloOpened(c, c);
        return c;
    }


    /// <summary>
    /// Initiates a ne
    /// </summary>
    /// <returns></returns>
    public IConnection? NewConnection()
    {
        if (ConnectionWindow.Modal() is IConnection c)
        {
            return c;
        }

        App.Status("New connection cancelled");
        return null;
    }


    public void ExportAs(string content, string filter, string? defaultFilename)
    {
        var s = new SaveFileDialog()
                {
                    Title    = "Export As",
                    Filter   = filter,
                    FileName = defaultFilename!
                };
        if (s.ShowDialog() is true)
        {
            System.IO.File.WriteAllText(s.FileName, content);
            string msg = "Wrote {n} bytes to file '{file}'";
            Log.Debug(msg, content.Length, content);
            MessageBox.Show(msg, "Success", MessageBoxButton.OK,
                            MessageBoxImage.Information);
        }
    }

    public void ExportLogs() => ExportAs(App.Instance!.GetLogs(),
                                         "Log Files (*.log)|*.log", null);

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