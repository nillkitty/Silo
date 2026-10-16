using System.DirectoryServices;
using System.Runtime.ExceptionServices;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors.Ldap;

/// <summary>
///     A connection to a directory: the directory entry it opens (the root of what is shown), signed in as the
///     credential in its request says (the Windows user's own when there is none), with the entries below it in the
///     Explorer, each directory's entries read when it is first expanded.
/// </summary>
/// <remarks>
///     Making one asks nothing of the directory: it is <see cref="LdapState.Disconnected" /> until <see cref="Connect" /> binds it
///     (finding the default naming context first when the address has none). Everything that talks to the directory goes through
///     <see cref="OperationAsync{T}" />, which tracks what the connection is doing in <see cref="State" />; ADSI itself blocks, so
///     its calls run on worker threads (<see cref="RunAsync{T}(string, Func{T}, CancellationToken, bool)" />) and the connection
///     takes turns (a gate), never blocking the caller. A read on a connection that was disconnected connects it again.
/// </remarks>
public class LdapConnection : LdapModel, IConnection, ICommandProvider
{
    private readonly CancellationTokenSource _closing = new();
    private readonly SemaphoreSlim           _gate    = new(1, 1);
    private readonly SiloNode?               _parent;

    private volatile CancellationTokenSource? _current; // what runs now, for Disconnect to cancel

    private volatile bool _bound; // the entry has been opened and read at least once
    private          LdapBind _bind;

    /// <summary>Makes the connection; nothing is asked of the directory until <see cref="Connect" /> (see <see cref="LdapModel.Create" />).</summary>
    public LdapConnection(LdapBind bind, Uri requested, IConnector connector, List<object> metadata, ICredential? credential = null,
                          SiloNode? parent = null, string? displayText = null)
        : base(new DirectoryEntry(bind.Required().Path, bind.UserName, bind.Password, bind.Auth), requested)
    {
        _bind       = bind;
        Connector   = connector;
        Metadata    = metadata;
        Credential  = credential;
        DisplayText = displayText;
        _parent     = parent;
    }

    /// <summary>The connection itself, which is what the entries below it ask when they need to read something.</summary>
    public override LdapConnection? Connection => this;

    /// <summary>What to call the connection (the name it was given); null to use the directory's name.</summary>
    public string? DisplayText { get; set; }

    /// <summary>What the connection is doing at the moment ("Refresh", "Reading Users"...); null when it is idle.</summary>
    public string? CurrentOperation => State switch
                                       {
                                           LdapState.Busy b      => b.Operation,
                                           LdapState.Aborting a  => a.Inner.Operation,
                                           LdapState.Connecting  => "Connecting",
                                           _                     => null
                                       };

    public override string Label => DisplayText is { Length: > 0 } text ? text : $"{Name} - {Uri.Host}";

    public override bool HasChildren => true;

    protected override bool IsLoaded => _bound;

    public bool?        ConnectionState  => State is LdapState.Connected or LdapState.Busy or LdapState.Aborting;
    public IConnector   Connector        { get; }
    public bool         IsInTree         => Node?.IsVisible ?? false;
    public List<object> Metadata         { get; }
    public string?      PersistentConfig { get; }
    public object?      State            { get; protected set; } = new LdapState.Disconnected();
    public string?      TempConfig       { get; }

    public Container Components { get; } = new($"ldap:{Guid.NewGuid()}");

    // ---- the operation that is tracked -----------------------------------------------------------

    /// <summary>
    ///     Runs <paramref name="action" /> as the connection's current operation: while it runs the state says what it is
    ///     (<see cref="LdapState.Connecting" /> when there was no connection, <see cref="LdapState.Busy" /> when there was), after it the
    ///     connection is <see cref="LdapState.Connected" /> (a new one, with the time of now, when <paramref name="touch" />), and if it throws
    ///     it is <see cref="LdapState.Failed" />. Call it through <see cref="RunAsync{T}(string, Func{CancellationToken, Task{T}}, CancellationToken, bool)" />,
    ///     which makes it take turns.
    /// </summary>
    private async Task<T> OperationAsync<T>(string operation, Func<CancellationToken, Task<T>> action, bool throwErrors = true,
                                            CancellationToken cancel = default, bool touch = false)
    {
        if (State is LdapState.Connected c)
            State = new LdapState.Busy(c, operation, cancel);
        else if (State is LdapState.Disconnected or LdapState.Failed)
            State = new LdapState.Connecting(Uri);

        try
        {
            var result = await action.Required().Invoke(cancel);

            if (State is LdapState.Busy b)
                State = touch ? new LdapState.Connected() : b.Inner;
            else if (State is LdapState.Aborting a)
                State = a.Inner.Inner; // it ran to its end although Disconnect asked it to stop; Disconnect closes it next

            if (State is not LdapState.Connected)
                State = new LdapState.Connected();

            return result;
        }
        catch (Exception ex)
        {
            State = new LdapState.Failed(ex);
            if (!throwErrors) return default!;
            throw;
        }
    }

    /// <summary>
    ///     Runs <paramref name="work" /> as an operation (see <see cref="OperationAsync{T}" />), one at a time with the others and with
    ///     <see cref="Disconnect" />.
    /// </summary>
    internal async Task<T> RunAsync<T>(string operation, Func<CancellationToken, Task<T>> work, CancellationToken cancel = default, bool touch = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _closing.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            _current = linked;
            return await OperationAsync(operation, work, true, linked.Token, touch);
        }
        finally
        {
            _current = null;
            _gate.Release();
        }
    }

    /// <summary>
    ///     <see cref="RunAsync{T}(string, Func{CancellationToken, Task{T}}, CancellationToken, bool)" /> for ADSI work, which blocks: it runs on a worker
    ///     thread.
    /// </summary>
    internal Task<T> RunAsync<T>(string operation, Func<T> work, CancellationToken cancel = default, bool touch = false)
    {
        return RunAsync(operation, ct => Task.Run(work, ct), cancel, touch);
    }

    private void ApplyOptions(DirectoryEntry entry)
    {
        var connector = (LdapConnector)Connector;

        // touching the options binds the entry, so it has to know who it is by now
        entry.Options.PageSize         = connector.PageSize ?? 10_000;
        entry.Options.Referral         = connector.Referral;
        entry.Options.PasswordEncoding = connector.PasswordEncoding;
    }

    /// <summary>
    ///     Opens the directory (the first time: an address with no distinguished name is the directory's default naming context, which
    ///     the root DSE says) or asks it for the entry again. Inside an operation.
    /// </summary>
    private async Task<bool> BindAsync(CancellationToken cancel)
    {
        if (_bound)
        {
            await Task.Run(() =>
                           {
                               ApplyOptions(Entry);
                               Entry.RefreshCache(); // binds again if it was closed
                           }, cancel);
            return true;
        }

        string path = await WithDefaultNamingContextAsync(_bind.Path, _bind.UserName, _bind.Password, _bind.Auth, cancel);

        await Task.Run(() =>
                       {
                           var  first    = Entry;
                           bool resolved = !string.Equals(path, _bind.Path, StringComparison.OrdinalIgnoreCase);
                           var  entry    = resolved ? new DirectoryEntry(path, _bind.UserName, _bind.Password, _bind.Auth) : first;
                           try
                           {
                               ApplyOptions(entry);
                               entry.RefreshCache();
                               Populate(entry, true);
                           }
                           catch
                           {
                               if (resolved) entry.Dispose();
                               throw;
                           }

                           if (resolved) first.Dispose();
                       }, cancel);

        _bound = true;
        return true;
    }

    private async Task<bool> Refresh(bool throwErrors = true, CancellationToken cancel = default)
    {
        try
        {
            return await RunAsync(_bound ? "Refresh" : "Connect", BindAsync, cancel, true);
        }
        catch (Exception) when (!throwErrors)
        {
            return false;
        }
    }

    public void SetBasicCredential(string username, string password)
    {
        Entry.Username = username;
        Entry.Password = password;
        _bind          = _bind with { UserName = username, Password = password };
    }

    // ---- IConnection -----------------------------------------------------------------------------

    public void InitProvider(IReceiver<Container> receiver)
    {
        receiver.Receiver(Components);
    }

    public string? GetHostname()
    {
        return Uri.Host;
    }

    public async Task<bool> Connect(CancellationToken cancel = default)
    {
        Connector.Required();
        if (Connector is not LdapConnector lc)
            throw new NotSupportedException($"Connector type '{Connector.GetType()}' unsupported");
        // Check if already connected

        if (State is LdapState.Connected cn)
        {
            if (cn.Duration < lc.ConnectionStateThreshold) return true;
            return await Refresh(true, cancel);
        }

        if (State is LdapState.Busy b)
            if (b.Cancel.IsCancellationRequested)
                State = new LdapState.Aborting(b);

        if (State is LdapState.Disconnected or LdapState.Failed) return await Refresh(true, cancel);

        return false;
    }

    /// <summary>Closes the connection, first asking whatever it is doing to stop. Returns false when it was not connected.</summary>
    public async Task<bool> Disconnect()
    {
        if (State is LdapState.Disconnected) return false;

        try
        {
            _current?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // it just finished
        }

        if (State is LdapState.Busy busy) State = new LdapState.Aborting(busy);

        await _gate.WaitAsync();
        try
        {
            if (State is LdapState.Disconnected) return false;

            Entry.Close(); // the entry binds again if something asks it for more
            State = new LdapState.Disconnected();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> Remove()
    {
        _closing.Cancel();
        await Disconnect();
        return Node?.Remove() ?? false;
    }

    /// <summary>A directory connection has no window of its own.</summary>
    public Window? BuildWindow()
    {
        return null;
    }

    /// <summary>
    ///     Connects (unless the connection is being restored in the background, which waits until the directory is needed)
    ///     and puts the directory in the Explorer: expanded, with its entries below it, when it connected. If connecting fails
    ///     the directory is still shown, closed, and the failure is thrown after.
    /// </summary>
    public async Task InitAsync(ConnectContext context)
    {
        context.Required();

        bool       restoring = context.Reason == ConnectReason.BackgroundRestore;
        Exception? failure   = null;

        if (!restoring)
            try
            {
                await Connect();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

        if (Node is null)
        {
            Node = new LdapNode(this, restoring || failure is not null ? 0 : 1);
            context.NavigationManager.AddNode(Node, _parent);
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // ---- what the Explorer shows -----------------------------------------------------------------

    public override IEnumerable<(string Name, string Value)> GetDetails()
    {
        yield return ("Type", "Directory");
        yield return ("URI", Uri.OriginalString);
        yield return ("State", State is LdapState s ? s.DisplayName : "Not connected");

        if (CurrentOperation is { } doing) yield return ("Doing", doing);
        if (Credential is { } credential) yield return ("Signed in with", credential.DisplayName ?? "a credential");
        if (State is LdapState.Failed failed) yield return ("Last error", failed.Exception.Message);

        foreach (var row in base.GetDetails().Skip(1)) yield return row; // the directory's own attributes, but not its type again
    }

    /// <summary>The directory's commands (the entries below it have none).</summary>
    public IEnumerable<ProvidedCommand> GetCommands()
    {
        yield return ProvidedCommand.From("ldap.connect", "_Connect", ConnectFromUi, "Plug",
                                          () => ConnectionState is not true, "Connects to the directory", "Connection");
        yield return ProvidedCommand.From("ldap.disconnect", "_Disconnect", DisconnectFromUi, "Power",
                                          () => State is not LdapState.Disconnected, "Closes the connection to the directory", "Connection");
    }

    private async void ConnectFromUi()
    {
        try
        {
            await Connect();
            if (Node is { ChildLoader: not null, ChildrenLoaded: true } node) await node.ReloadChildrenAsync();
        }
        catch (OperationCanceledException)
        {
            // the person cancelled
        }
        catch (Exception ex)
        {
            App.Status($"Could not connect to {Uri.Host}: {ex.Message}");
        }
    }

    private async void DisconnectFromUi()
    {
        try
        {
            await Disconnect();
        }
        catch (Exception ex)
        {
            App.Status($"Could not disconnect from {Uri.Host}: {ex.Message}");
        }
    }
}
