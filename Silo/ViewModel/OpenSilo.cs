using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using Serilog;
using Silo.Extensions;
using Silo.Ui.Windows;
using Telefrag.DI;

namespace Silo.Model;

/// <summary>
///     ViewModel for an open Silo file.
///     If <see cref="Solo" /> is true, the Silo was opened in its own process,
///     because it defines a custom set of assemblies.
/// </summary>
public class OpenSilo : ModelBase
{
    public SiloCommands Commands => field ??= new SiloCommands(this);

    public required Container Components { get; init; }

    /// <summary>
    ///     The connections currently active (loaded into memory) for this Silo.  Populated as
    ///     connections are created/opened during the session; used e.g. by the connection
    ///     diagram tool to show what's actually connected right now, as distinct from the
    ///     persisted <c>Connection</c> records saved in the Silo's global database.
    /// </summary>
    public ObservableCollection<IConnection> Connections { get; } = [];

    public        IDatabaseProvider              Data         { get; private set; }
    public        ISiloEncryption                Encryption   => Components.Require<ISiloEncryption>();
    public        SiloFile?                      File         { get; private set; }
    public        FileInfo?                      FileInfo     => File is { } f ? new FileInfo(f.FilePath) : null;
    public        string?                        FilePath     => FileInfo?.FullName;
    public        bool                           IsAuthorable => UserContext.IsAuthorable;
    public        bool                           IsDeveloper  => UserContext.IsDeveloper;
    public        bool                           IsOpensSolo  => UserContext.IsSoloSilo;
    public        bool                           IsOwner      => UserContext.IsOwner;
    public        bool                           IsReadable   => UserContext.IsReadable;
    public        bool                           IsSolo       { get; private set; }
    public        bool                           IsWritable   => UserContext.IsWritable;
    public        OpenSiloMeta                   Meta         { get; private set; } = new();
    public        string?                        Name         => FileInfo?.Name;
    public static OpenSilo                       OnlySilo     => OpenSilos is [OpenSilo os] ? os : null;
    public static ObservableCollection<OpenSilo> OpenSilos    { get; } = [];
    public        ISiloUserContext               UserContext  { get; private set; }

    public static OpenSilo CreateFile(SiloFile file)
    {
        file.Required();

        var cc = App.Instance!.Components.CreateChildContainer(file.FilePath);
        var d  = Database.CreateNewSilo(file.FilePath);
        OpenSilo c = new()
                     {
                         File        = file,
                         Meta        = new OpenSiloMeta(),
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
    ///     Initiates a ne
    /// </summary>
    /// <returns></returns>
    public IConnection? NewConnection()
    {
        if (ConnectionWindow.Modal() is IConnection c) return c;

        App.Status("New connection cancelled");
        return null;
    }


    public void ExportAs(string content, string filter, string? defaultFilename)
    {
        var s = new SaveFileDialog
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
            MessageBox.Show(msg, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public void ExportLogs()
    {
        ExportAs(App.Instance!.GetLogs(), "Log Files (*.log)|*.log", null);
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