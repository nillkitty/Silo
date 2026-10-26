using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.Win32;
using Serilog;
using Silo.Commanding;
using Silo.Connectors;
using Silo.Credentials;
using Silo.Crypto;
using Silo.DbModel.Sqlite;
using Silo.Hosting;
using Silo.Providers.Crypto;
using Silo.Providers.Data.Validation;
using Silo.Providers.Format;
using Silo.Remote;
using Silo.Settings;
using Silo.Ui.Windows;
using Silo.Ui.Windows.Connecting;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Model;

/// <summary>
///     ViewModel for an open Silo file.
///     If <see cref="Solo" /> is true, the Silo was opened in its own process,
///     because it defines a custom set of assemblies.
/// </summary>
public partial class OpenSilo : ModelBase
{
    private CredentialService? _credentials;
    private SiloEncryption?    _encryption;

    [Toggle("Always Expand To Directory", "silo.alwaysExpand",
            "Whether the Silo should expand to a directory even when using compact" + " format (which is a single bytestream)", false)]
    public bool AlwaysExpandToDirectory { get; set; }

    [Toggle("Auto Import Extra Files", "silo.autoImportExtraFiles",
            "Whether files added to the expanded directory are auto-imported " + "(versus lost) when clearing the expansion directory",
            false)]
    public bool AutoImportExtraFiles { get; set; }

    public SiloCommands Commands => field ??= new SiloCommands(this);


    public required Container Components { get; init; }

    /// <summary>
    ///     The connections currently active (loaded into memory) for this Silo.  Populated as
    ///     connections are created/opened during the session; used e.g. by the connection
    ///     diagram tool to show what's actually connected right now, as distinct from the
    ///     persisted <c>Connection</c> records saved in the Silo's global database.
    /// </summary>
    public ObservableCollection<IConnection> Connections { get; } = [];

    /// <summary>The saved credentials of this Silo; null when its storage cannot keep secrets.</summary>
    public CredentialService? Credentials => Data is ISecretStore store ? _credentials ??= new CredentialService(store, Encryption) : null;

    public IDatabaseProvider Data { get; private set; }

    /// <summary>
    ///     How this Silo keeps its secrets: sealed with a data key that a pluggable protector (the Windows account by
    ///     default) has locked.
    /// </summary>
    /// <exception cref="NotSupportedException">The Silo's storage cannot keep secrets.</exception>
    public ISiloEncryption Encryption => _encryption ??= Data is ISecretStore store
                                                             ? new SiloEncryption(store, SecretProtectors.Available())
                                                             : throw new NotSupportedException("This Silo's storage cannot keep secrets.");

    public SiloFile? File     { get; private set; }
    public FileInfo? FileInfo => File is { } f ? new FileInfo(f.FilePath) : null;
    public string?   FilePath => FileInfo?.FullName;

    [Toggle("Foreground Reconnect", "silo.foregroundReconnect", "Whether to force all (re)connections to happen synchronously", false)]
    public bool ForegroundReconnect { get; set; }

    public bool IsAuthorable => UserContext.IsAuthorable;
    public bool IsDeveloper  => UserContext.IsDeveloper;

    /// <summary>
    ///     True when the document has changes that are not yet saved to its .silo file. Set when the provider reports a
    ///     change (<see cref="IDatabaseChanges" />), cleared by <see cref="SaveAsync" />.
    /// </summary>
    public bool IsDirty { get; private set; }

    public bool IsOpensSolo => UserContext.IsSoloSilo;
    public bool IsOwner     => UserContext.IsOwner;
    public bool IsReadable  => UserContext.IsReadable;

    /// <summary>
    ///     True when this Silo runs in a process of its own (Solo mode): it carries .NET assemblies, was opened with
    ///     <c>/P</c>, or the person marked it Solo.
    /// </summary>
    public bool IsSolo { get; private set; }

    /// <summary>True when the last save reached this computer but could not be uploaded to <see cref="RemoteUri" />.</summary>
    public bool IsUnsynced => Data is ISiloFile { IsUnsynced: true };

    public bool         IsWritable => UserContext.IsWritable;
    public OpenSiloMeta Meta       { get; private set; } = new();
    public string?      Name       => FileInfo?.Name;

    /// <summary>
    ///     The nodes this Silo's connections and documents contribute to the Explorer (see <see cref="INodeProvider" />).
    ///     It is also the <see cref="INavManager" /> given to connections when they are initialized.
    /// </summary>
    public NodeHost Nodes { get; } = new();

    public static OpenSilo                       OnlySilo  => OpenSilos is [OpenSilo os] ? os : null;
    public static ObservableCollection<OpenSilo> OpenSilos { get; } = [];

    [Toggle("Reconnect Connections", "silo.reconnectConnections", "Whether to reconnect persisted connections upon reopening a Silo", true)]
    public bool ReconnectConnections { get; set; } = true;

    [Toggle("Remember Connections", "silo.rememberConnections", "Whether connections created are persisted if not explicitly removed",
            true)]
    public bool RememberConnections { get; set; } = true;

    [Toggle("Remember Explorer Tree", "silo.rememberExplorerTree", "Whether the state of the explorer pane is persisted", true)]
    public bool RememberExplorerTree { get; set; } = true;

    [Toggle("Remember Window Layouts", "silo.rememberWindowLayouts", "Whether the sizes and positions of windows and panes are persisted",
            true)]
    public bool RememberWindowLayouts { get; set; } = true;

    [Toggle("Remember Window Popouts", "silo.rememberWindowPopouts", "Whether the pop-out state of windows/panes is persisted", true)]
    public bool RememberWindowPopouts { get; set; } = true;

    /// <summary>The URL this Silo is synced with, if it was opened from one (<see cref="OpenRemoteAsync" />).</summary>
    public Uri? RemoteUri { get; private set; }

    [Toggle("Re-Open Me", "silo.reopenMe",
            "Whether the app should re-open this Silo if launched with no other document, " + "and this Silo is local", true)]
    public bool ReopenMe { get; set; } = true;

    /// <summary>
    ///     Work to run before the document is written (and before it is closed): a tool with changes it has not handed to
    ///     the document yet (e.g. a diagram editor's pending autosave) adds its flush here.
    /// </summary>
    public List<Func<Task>> SaveHooks { get; } = [];

    public ISiloUserContext UserContext { get; private set; }

    /// <summary>The preferences of the Silo itself are kept in the Silo (its <c>Pref</c> table).</summary>
    protected override IPreferenceStore? PreferenceStore => Data as IPreferenceStore;

    /// <summary>Raised (on the UI thread) when <see cref="IsDirty" /> or the file name changes.</summary>
    public event EventHandler? StateChanged;

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
        WarnAboutProgramFiles(full);
        var cc = a.Components.CreateChildContainer(file.FilePath);
        OpenSilo c = new()
                     {
                         File       = new SiloFile(full),
                         Components = cc
                     };
        a.NotifySiloOpening(c, c);
        c.Attach(App.Require<ISiloLoader>().Load(new FileInfo(full)));
        a.NotifySiloOpened(c, c);
        c.BeginRestoreConnections();
        return c;
    }

    /// <summary>
    ///     This version of Silo does not run code from Silo files. Tells the person when the file they are opening carries
    ///     some (.dll/.exe), so it is not silently ignored; see <see cref="Silo.DbModel.Sqlite.SiloContentScan" />.
    /// </summary>
    private static void WarnAboutProgramFiles(string path)
    {
        var programs = SiloContentScan.FindProgramFiles(path);
        if (programs.Count == 0)
            return;

        Log.Warning("'{Path}' contains program files that this version of Silo does not load: {Files}", path, string.Join(", ", programs));
        MessageBox.Show(SiloContentScan.WarningText(programs), AppIdentity.Get().AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    ///     Opens a Silo that lives at a URL: syncs the local cache with it (only a changed file is downloaded), opens the
    ///     cache like any local Silo, and keeps it in sync -- each save is uploaded. If the same URL is already open, that
    ///     Silo is activated instead. Returns null when the person cancelled; throws if the Silo could not be opened.
    /// </summary>
    public static async Task<OpenSilo?> OpenRemoteAsync(Uri uri)
    {
        uri.Required();
        var a = App.Instance!;

        if (OpenSilos.FirstOrDefault(o => o.RemoteUri == uri) is { } already)
        {
            a.ActiveSilo = already;
            return already;
        }

        RemoteOpenResult sync;
        try
        {
            sync = await RemoteSiloOpener.OpenAsync(uri, App.Resolve<ISaveConflictResolver>());
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var silo = OpenFile(new SiloFile(sync.LocalFile));
        silo.RemoteUri = uri;
        RecentSilos.Instance.Record(silo); // now listed by its URL rather than its local cache file
        if (silo.Data is ISiloFile f) f.Remote = sync.Remote;
        if (sync.Notice is { } notice) App.Status(notice);
        silo.StateChanged?.Invoke(silo, EventArgs.Empty);
        return silo;
    }

    /// <summary>Hooks up the provider, registers the Silo as open and makes it the active one.</summary>
    private void Attach(IDatabaseProvider provider)
    {
        Data        = provider;
        UserContext = provider.GetUserContext();

        // the Silo's own preferences: what it holds, else the defaults; and what code changed is written before a save or close
        LoadPreferences();
        SaveHooks.Add(() =>
                      {
                          SavePreferences();
                          return Task.CompletedTask;
                      });

        if (provider is IDatabaseChanges changes)
            changes.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(() => SetDirty(true)));

        if (provider is ISiloFile file)
        {
            file.ConflictResolver = App.Resolve<ISaveConflictResolver>();
            IsSolo                = file.IsSolo || App.Instance!.Launch.Mode == LaunchMode.Solo;
            file.Saved += (_, e) => Log.Information("Silo '{Name}' saved: #{Seq} at {Utc:O} ({Format}, {Size} bytes, sha256 {Hash})", Name,
                                                    e.Sequence, e.SavedUtc, e.Format, e.SizeBytes, e.Hash);
        }

        OpenSilos.Add(this);
        App.Instance!.ActiveSilo = this;

        // what the Silo keeps (saved characters, strings, shortcuts, ...) is shown in the Explorer from the moment it is open
        try
        {
            Nodes.Add(new LocalSilo());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The Silo's own Explorer node could not be made");
        }

        // the Recent view lists this Silo from now on
        RecentSilos.Instance.Record(this);
    }

    private void SetDirty(bool dirty)
    {
        if (IsDirty == dirty) return;

        IsDirty = dirty;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Runs the <see cref="SaveHooks" />, then writes the document to its file (and uploads it, for a remote Silo). If the
    ///     file changed on disk meanwhile the person (or the author-mode setting) decides what happens. Throws
    ///     <see cref="OperationCanceledException" /> when the person cancels that prompt -- nothing was saved then.
    /// </summary>
    /// <param name="offerConvert">After a successful save, offer to convert a grown simple-format Silo to the archive format.</param>
    public async Task SaveAsync(bool offerConvert = true)
    {
        await RunSaveHooksAsync();
        try
        {
            await Data.SaveAsync();
        }
        catch (RemoteSyncException ex)
        {
            // Saved on this computer; only the upload failed. The document is clean, but not in sync with the remote.
            SyncFileFromProvider();
            SetDirty(false);
            StateChanged?.Invoke(this, EventArgs.Empty);
            Logger.Warning(ex, "Silo saved locally but not uploaded");
            App.Status(ex.Message);
            return;
        }

        SyncFileFromProvider();
        SetDirty(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
        Logger.Debug("Silo saved to {path}", FilePath);

        if (offerConvert) await OfferConvertToArchiveAsync();
    }

    /// <summary>A save can move the document (a conflicted copy); keep the model's file in step with the provider's.</summary>
    private void SyncFileFromProvider()
    {
        if (Data is ISiloFile f && !string.Equals(f.FilePath, File?.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            File = new SiloFile(f.FilePath);
            if (f.Remote is null) RemoteUri = null;
        }
    }

    /// <summary>
    ///     A simple-format Silo is one SQLite file; once it grows past <see cref="SiloFileSettings.ExplodeThresholdMb" /> the
    ///     archive format (several files, each smaller) is the better fit. Never done silently: this asks.
    /// </summary>
    private async Task OfferConvertToArchiveAsync()
    {
        var settings = SiloFileSettings.Instance;
        if (Data is not ISiloFile { Format: SiloStorageFormat.Simple } f || !settings.AskToExplode) return;
        if (f.WorkingSizeBytes < settings.ExplodeThresholdMb * 1024L * 1024L) return;

        var (button, dontAsk) = PromptDialog.Show("Convert to archive format?",
                                                  $"{Name} has grown to {f.WorkingSizeBytes / (1024.0 * 1024.0):0.#} MB. The archive format splits a " +
                                                  "Silo into several database files, which is faster for large Silos. The Silo stays one .silo file.",
                                                  ["Convert now", "Not now"], "Don't ask me again");
        if (dontAsk) settings.AskToExplode = false;
        if (button != 0) return;

        try
        {
            await f.ExplodeToArchiveAsync();
            SyncFileFromProvider();
            SetDirty(false);
            App.Status($"{Name} converted to the archive format");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "Converting {path} failed", FilePath);
            MessageBox.Show($"The Silo could not be converted:\n{ex.Message}", AppIdentity.AppName, MessageBoxButton.OK,
                            MessageBoxImage.Error);
        }
    }

    /// <summary>Converts a simple-format Silo to the archive format now (author tool; also what the size prompt does).</summary>
    public async Task ConvertToArchiveAsync()
    {
        if (Data is not ISiloFile { Format: SiloStorageFormat.Simple } f) return;

        await RunSaveHooksAsync();
        await f.ExplodeToArchiveAsync();
        SyncFileFromProvider();
        SetDirty(false);
        App.Status($"{Name} converted to the archive format");
    }

    /// <summary>Saves the document under a new name; it keeps being the open document, now at the new path.</summary>
    public async Task SaveAsAsync(string path)
    {
        path.Required();
        await RunSaveHooksAsync();
        await Data.MigrateToCopyAsync(path);

        File      = new SiloFile(Data is ISiloFile saved ? saved.FilePath : Path.GetFullPath(path));
        RemoteUri = null; // a copy saved under a new name no longer syncs with the URL
        SetDirty(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
        Logger.Debug("Silo saved as {path}", FilePath);
    }

    private async Task RunSaveHooksAsync()
    {
        foreach (var hook in SaveHooks.ToList())
            try
            {
                await hook();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "A save hook failed");
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
            var answer = MessageBox.Show($"Save changes to {Name ?? "this Silo"}?", AppIdentity.AppName, MessageBoxButton.YesNoCancel,
                                         MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;

            if (answer == MessageBoxResult.Yes)
                try
                {
                    await SaveAsync(false);
                }
                catch (OperationCanceledException)
                {
                    return false; // the person cancelled the save-conflict prompt: stay open
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Saving {path} failed", FilePath);
                    MessageBox.Show($"The Silo could not be saved:\n{ex.Message}", AppIdentity.AppName, MessageBoxButton.OK,
                                    MessageBoxImage.Error);
                    return false;
                }
        }

        if (prompt && IsUnsynced)
        {
            var (button, _) = PromptDialog.Show("Changes not uploaded",
                                                $"The last save of {Name} is on this computer but was not uploaded to {RemoteUri}. It will be " +
                                                "uploaded the next time you open it, if the remote has not changed meanwhile.",
                                                ["Try uploading now", "Close anyway", "Cancel"]);
            if (button == 2) return false;
            if (button == 0)
            {
                try
                {
                    if (Data is ISiloFile retry) await retry.RetryUploadAsync();
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Uploading on close failed");
                    App.Status(ex.Message);
                }

                if (IsUnsynced) return false; // still not uploaded: let the person decide what to do next
            }
        }

        // from here the Silo is going away: nothing about it is written any more, and connections no longer come back
        IsClosing = true;
        _closing.Cancel();

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
    ///     Shows the New Connection dialog and, when a connection is made, adds it to this Silo
    ///     (see <see cref="AddConnection" />). Returns the connection, or null if the dialog was cancelled.
    /// </summary>
    public IConnection? NewConnection()
    {
        if (ConnectionWindow.Modal(this) is { } result) return AddConnection(result);

        App.Status("New connection cancelled");
        return null;
    }

    /// <summary>
    ///     Adds a connection to <see cref="Connections" /> and saves it (its name and URI) in the Silo's <c>Connection</c>
    ///     table, which marks the Silo as changed. Failing to save is logged, not thrown: the connection is still usable
    ///     for this session.
    /// </summary>
    public IConnection AddConnection(ConnectionResult result)
    {
        Connections.Add(result.Connection);
        _ = InitConnectionAsync(result.Connection, ConnectReason.UserCreate);

        if (RememberConnections && Data is IConnectionStore store)
            try
            {
                // a connection with no destination of its own (Runtime, the current user's registry...) is saved as the connector that makes it
                var destination = result.Uri ?? (result.Connection.Connector is { } connector ? ConnectionRestore.UriFor(connector) : null);
                _savedIds[result.Connection] = store.SaveConnection(result.Name, destination).Id;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "The connection {name} could not be saved in the Silo", result.Name);
            }

        return result.Connection;
    }

    /// <summary>
    ///     Initializes a connection that was just added: lets it create nodes through the <see cref="ConnectContext" />, then
    ///     puts the node it provides as an <see cref="INodeProvider" /> (if it is one) in the Explorer. A failure is logged
    ///     and shown in the status bar; the connection stays in the Silo.
    /// </summary>
    private async Task InitConnectionAsync(IConnection connection, ConnectReason reason)
    {
        try
        {
            await InitConnectionCoreAsync(connection, reason, false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Initializing the connection {name} failed", connection.DisplayText ?? connection.Connector.DisplayName);
            App.Status($"The connection could not be initialized: {ex.Message}");
        }
    }

    /// <summary>
    ///     <see cref="InitConnectionAsync" /> without the error handling: throws when the connection cannot be initialized.
    /// </summary>
    /// <param name="inBackground">
    ///     Run the connector's <c>InitAsync</c> on a worker thread, so a connector that does its work
    ///     synchronously cannot hold the UI up.
    /// </param>
    private async Task InitConnectionCoreAsync(IConnection connection, ConnectReason reason, bool inBackground)
    {
        var context = new ConnectContext(reason, Nodes);
        if (inBackground)
            await Task.Run(() => connection.InitAsync(context));
        else
            await connection.InitAsync(context);

        if (connection is INodeProvider provider)
        {
            // a connection that already created its node through the context has nothing more to add
            if (connection.Node is { } own && Nodes.Contains(own))
                return;

            Nodes.Add(provider);
        }
    }

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is null) return;

        // a connection that leaves the Silo takes its nodes out of the Explorer
        foreach (var old in e.OldItems.OfType<IConnection>())
        {
            if (old is INodeProvider provider && Nodes.Remove(provider)) continue;
            Nodes.RemoveNode(old.Node);
        }
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
        ExportAs(App.Instance!.GetLogs(), Formats.Of<LogFormat>().Filter(), null);
    }

    public async Task<bool> Shutdown()
    {
        if (Data is null)
            return false;

        return await Data.ShutdownAsync();
    }

    public OpenSilo() : base(App.LocalConnection)
    {
        Connections.CollectionChanged += OnConnectionsChanged;
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