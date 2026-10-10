using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

    /// <summary>
    ///     True when the document has changes that are not yet saved to its .silo file. Set when the provider reports a
    ///     change (<see cref="IDatabaseChanges" />), cleared by <see cref="SaveAsync" />.
    /// </summary>
    public bool IsDirty { get; private set; }

    /// <summary>Raised (on the UI thread) when <see cref="IsDirty" /> or the file name changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    ///     Work to run before the document is written (and before it is closed): a tool with changes it has not handed to
    ///     the document yet (e.g. a diagram editor's pending autosave) adds its flush here.
    /// </summary>
    public List<Func<Task>> SaveHooks { get; } = [];

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

    /// <summary>Creates a new, empty Silo file at <paramref name="file" />'s path and opens it.</summary>
    public static OpenSilo CreateFile(SiloFile file)
    {
        file.Required();
        return Create(file.FilePath, null);
    }

    /// <summary>
    ///     Creates a new Silo file at <paramref name="path" /> (populated from <paramref name="template" /> when given; an
    ///     existing file there is replaced), opens it and makes it the active Silo.
    /// </summary>
    public static OpenSilo Create(string path, SiloTemplate? template)
    {
        path.Required();
        var a       = App.Instance!;
        var builder = App.Require<INewSiloBuilder>();
        Log.Debug("Creating file:  {path}", path);

        var cc = a.Components.CreateChildContainer(path);
        OpenSilo c = new()
                     {
                         File       = new SiloFile(path),
                         Components = cc
                     };
        a.NotifySiloOpening(c, c);
        c.Attach(template is null ? builder.Build(path) : builder.Build(path, template));
        a.NotifySiloOpened(c, c);
        return c;
    }

    /// <summary>
    ///     Opens a Silo file and makes it the active Silo. A file that is already open is not opened twice: that Silo is
    ///     activated and returned.
    /// </summary>
    public static OpenSilo OpenFile(SiloFile file)
    {
        file.Required();
        var a = App.Instance!;

        string full = Path.GetFullPath(file.FilePath);
        if (OpenSilos.FirstOrDefault(o => string.Equals(o.FilePath, full, StringComparison.OrdinalIgnoreCase)) is { } already)
        {
            a.ActiveSilo = already;
            return already;
        }

        Log.Debug("Opening file:  {path}", file.FilePath);
        var cc = a.Components.CreateChildContainer(file.FilePath);
        OpenSilo c = new()
                     {
                         File       = new SiloFile(full),
                         Components = cc
                     };
        a.NotifySiloOpening(c, c);
        c.Attach(Database.Load(file));
        a.NotifySiloOpened(c, c);
        return c;
    }

    /// <summary>Hooks up the provider, registers the Silo as open and makes it the active one.</summary>
    private void Attach(IDatabaseProvider provider)
    {
        Data        = provider;
        UserContext = provider.GetUserContext();

        if (provider is IDatabaseChanges changes)
            changes.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(() => SetDirty(true)));

        OpenSilos.Add(this);
        App.Instance!.ActiveSilo = this;
    }

    private void SetDirty(bool dirty)
    {
        if (IsDirty == dirty) return;

        IsDirty = dirty;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs the <see cref="SaveHooks" />, then writes the document to its file.</summary>
    public async Task SaveAsync()
    {
        await RunSaveHooksAsync();
        await Data.SaveAsync();
        SetDirty(false);
        Logger.Debug("Silo saved to {path}", FilePath);
    }

    /// <summary>Saves the document under a new name; it keeps being the open document, now at the new path.</summary>
    public async Task SaveAsAsync(string path)
    {
        path.Required();
        await RunSaveHooksAsync();
        await Data.MigrateToCopyAsync(path);

        File = new SiloFile(Path.GetFullPath(path));
        SetDirty(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
        Logger.Debug("Silo saved as {path}", FilePath);
    }

    private async Task RunSaveHooksAsync()
    {
        foreach (var hook in SaveHooks.ToList())
        {
            try
            {
                await hook();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "A save hook failed");
            }
        }
    }

    /// <summary>
    ///     Closes the Silo. When there are unsaved changes and <paramref name="prompt" /> is set, asks whether to save
    ///     them first. Returns false if the person cancelled (nothing was closed).
    /// </summary>
    public async Task<bool> CloseAsync(bool prompt = true)
    {
        await RunSaveHooksAsync();

        if (prompt && IsDirty)
        {
            var answer = MessageBox.Show($"Save changes to {Name ?? "this Silo"}?", AppIdentity.AppName,
                                         MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;

            if (answer == MessageBoxResult.Yes)
            {
                try
                {
                    await SaveAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Saving {path} failed", FilePath);
                    MessageBox.Show($"The Silo could not be saved:\n{ex.Message}", AppIdentity.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }
        }

        var app = App.Instance!;
        app.NotifySiloClosing(this, this);

        await Shutdown();
        OpenSilos.Remove(this);
        if (ReferenceEquals(app.ActiveSilo, this))
            app.ActiveSilo = OpenSilos.LastOrDefault();

        app.NotifySiloClosed(this, this);
        return true;
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

    public Task<OpenSilo> Open()
    {
        return Task.FromResult(OpenSilo.OpenFile(new SiloFile(FilePath.Required())));
    }
}