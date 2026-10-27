using System.Data;
using System.Runtime.ExceptionServices;
using Serilog;
using Silo.Contracts.ConnectorServices;
using Silo.Model;
using Silo.ViewModel;
using Telefrag.DI;
using Db = Microsoft.Data.SqlClient;
using DbState = System.Data.ConnectionState;

namespace Silo.Connectors.Sql;

public class SqlBase;

public class SqlServerModel : SqlBase
{
    public SqlServerModel(Uri uri, ConnectionRequest? rq)
    {
    }
}

/// <summary>
///     A connection to a SQL Server. It signs in the way its connector's flags allow (<see cref="SqlSettings.AuthOrder" />
///     : a credential
///     from <see cref="Metadata" /> or the URI, and/or Windows authentication), and shows the server in the Explorer with
///     its databases,
///     tables, views and columns below it, each read when it is first expanded.
/// </summary>
/// <remarks>
///     <see cref="Connect" /> and <see cref="Disconnect" /> are safe to call from any thread, as are the Explorer's reads:
///     they take turns
///     on the one connection. A read on a closed connection opens it first. A connection that is restored in the
///     background does not touch the
///     server until something needs it.
/// </remarks>
public class SqlConnection(IConnector owner, Uri uri, ConnectionRequest rq) : SqlServerModel(uri, rq), IConnection, ICredentialSink
{
    private const int QueryTimeoutSeconds = 60;

    private const string DatabasesSql = "SELECT d.name, d.state_desc, d.collation_name, d.compatibility_level, " +
                                        "CASE WHEN HAS_DBACCESS(d.name) = 1 THEN 1 ELSE 0 END AS accessible "    +
                                        "FROM sys.databases d ORDER BY CASE WHEN d.database_id <= 4 THEN 1 ELSE 0 END, d.name";

    private readonly CancellationTokenSource _closing = new();
    private readonly SemaphoreSlim           _gate    = new(1, 1);
    private readonly SiloNode?               _parent  = rq?.ParentNode;

    private          string?                  _authDescription;
    private          string?                  _credConfig;
    private volatile string?                  _currentOperation; // what OperationAsync is doing right now, by name
    private          Db.SqlConnection?        _db;
    private          string?                  _loginName;
    private volatile CancellationTokenSource? _operation; // the connect or read in progress, for Disconnect to cancel
    private          string?                  _serverName;

    /// <summary>What the connection is doing at the moment ("Connect", "Listing databases", ...); null when it is idle.</summary>
    public string? CurrentOperation => _currentOperation;

    /// <summary>Why the last attempt to connect or read failed; null after a connect succeeded.</summary>
    public Exception? LastError { get; private set; }

    public SqlState State { get; private set; } = new Disconnected();
    public Uri      Uri   { get; }              = uri.Required();

    /// <summary>The version the server reported when this connected (<c>16.00.4135</c>); null until it has.</summary>
    internal string? ServerVersion { get; private set; }

    private ConnectorAuthFlags AuthFlags => (Connector as IAuthenticatable)?.AuthFlags ?? ConnectorAuthFlags.None;

    private WinAuthFlags WinFlags => (Connector as IWindowsAuthenticatable)?.WinAuthFlags ?? SqlSettings.DefaultWinAuth;

    public bool?      ConnectionState => State?.IsConnected;
    public IConnector Connector       { get; } = owner.Required();

    /// <summary>What to call the connection (the name it was given); null to use the server's name.</summary>
    public string? DisplayText { get; set; } = rq?.DisplayName;

    public bool         IsInTree         { get; set; }
    public List<object> Metadata         { get; } = [.. rq?.Metadata ?? []];
    public SiloNode?    Node             { get; protected set; }
    public string?      PersistentConfig { get; } = rq?.SerializedState;
    public string?      TempConfig       { get; }
    object IConnection. State            => State;
    public Container    Components       { get; } = new($"SqlConnection:{Guid.NewGuid()}");

    // ---- connecting ----------------------------------------------------------------------------

    // with the gate held
    private Task ConnectCoreAsync(CancellationToken cancel)
    {
        return OperationAsync("Connect", async ct =>
                                         {
                                             await OpenAsync(ct);
                                             return true;
                                         }, cancel);
    }

    /// <summary>
    ///     Runs <paramref name="action" /> as the connection's current operation, the way <c>LdapConnection.Operation</c>
    ///     does: while it runs the state says what it is (<see cref="Connecting" /> when there is no connection yet,
    ///     <see cref="Busy" />
    ///     with the operation's name when there is), after it the connection is <see cref="Connected" />, and if it throws the
    ///     state records why.
    ///     A read that fails while the link is still up leaves the connection <see cref="Connected" /> (the reason is in
    ///     <see cref="LastError" />); a connect that fails, or a link that broke, is <see cref="Failed" />; a cancelled
    ///     operation is not a failure.
    ///     With the gate held.
    /// </summary>
    private async Task<T> OperationAsync<T>(string operation, Func<CancellationToken, Task<T>> action, CancellationToken cancel,
                                            bool   throwErrors = true)
    {
        bool wasConnected = State.IsConnected && _db is not null;

        State             = wasConnected ? new Busy(operation, cancel) : new Connecting(SqlSettings.HostPort(Uri), cancel);
        _currentOperation = operation;

        try
        {
            var result = await action(cancel);

            // unless the link broke meanwhile
            if (State is Busy or Connecting) State = new Connected(SqlNames.SafeUri(Uri));
            return result;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // somebody stopped it (Disconnect, closing): not a failure
            if (wasConnected)
            {
                if (State is Busy) State = new Connected(SqlNames.SafeUri(Uri));
            }
            else
            {
                await DropAsync();
                State = new Disconnected();
            }

            throw;
        }
        catch (Exception ex)
        {
            LastError = ex;
            if (wasConnected && _db?.State == DbState.Open)
            {
                if (State is Busy) State = new Connected(SqlNames.SafeUri(Uri));
            }
            else
            {
                await DropAsync();
                State = new Failed(ex);
            }

            if (throwErrors) throw;
            return default!;
        }
        finally
        {
            _currentOperation = null;
        }
    }

    // signs in, trying the ways AuthOrder allows in turn; the state is the caller's (see OperationAsync)
    private async Task OpenAsync(CancellationToken cancel)
    {
        // the credential tells this connection how it signs in (see ConfigureFrom); with none, the URI's user and Windows are tried
        _credConfig = null;
        Metadata.OfType<ICredential>().FirstOrDefault()?.Configure(this);

        var attempts = SqlSettings.Attempts(Uri, _credConfig, AuthFlags, WinFlags);

        for (int i = 0; i < attempts.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();

            var attempt = attempts[i];
            var db      = new Db.SqlConnection(attempt.ConnectionString);
            try
            {
                await db.OpenAsync(cancel);
            }
            catch (Db.SqlException ex) when (i < attempts.Count - 1 && SqlSettings.IsAuthFailure(ex.Number))
            {
                Log.Information("{Attempt} was refused by {Server} ({Message}); trying the next way to sign in", attempt,
                                SqlSettings.HostPort(Uri), ex.Message);
                await db.DisposeAsync();
                continue;
            }
            catch
            {
                await db.DisposeAsync();
                throw;
            }

            _db              =  db;
            db.StateChange   += OnDbStateChange;
            _authDescription =  attempt.Description;
            ServerVersion    =  db.ServerVersion;
            await ReadIdentityAsync(db, cancel);

            LastError = null;
            return;
        }

        throw new InvalidOperationException("There was no way to sign in."); // AuthOrder never returns none
    }

    // who the server says we are, for the Properties pane; failing to ask is not a failure to connect
    private async Task ReadIdentityAsync(Db.SqlConnection db, CancellationToken cancel)
    {
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText    = "SELECT SUSER_SNAME(), @@SERVERNAME";
            cmd.CommandTimeout = QueryTimeoutSeconds;
            using var reader = await cmd.ExecuteReaderAsync(cancel);
            if (await reader.ReadAsync(cancel))
            {
                _loginName  = reader.IsDBNull(0) ? null : reader.GetString(0);
                _serverName = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        catch (Db.SqlException ex)
        {
            Log.Debug(ex, "Asking the server who we are failed");
        }
    }

    private async Task DropAsync()
    {
        var db = _db;
        _db = null;
        if (db is null) return;

        db.StateChange -= OnDbStateChange;
        try
        {
            await db.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Closing the connection to {Server} failed", SqlSettings.HostPort(Uri));
        }
    }

    // the link broke under us (the server went away, a network cable): from here we are not connected
    private void OnDbStateChange(object? sender, StateChangeEventArgs e)
    {
        if (e.CurrentState is DbState.Closed or DbState.Broken && ReferenceEquals(sender, _db) && State.IsConnected)
            State = new Disconnected();
    }

    /// <summary>
    ///     Runs a read on the connection, one at a time with the others and with connecting, opening the connection first if
    ///     it is closed. It is the connection's current operation while it runs (see <see cref="OperationAsync{T}" />): the
    ///     state is
    ///     <see cref="Busy" /> and <see cref="CurrentOperation" /> is its name.
    /// </summary>
    private async Task<T> RunAsync<T>(string operation, Func<Db.SqlConnection, CancellationToken, Task<T>> work, CancellationToken cancel)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _closing.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            _operation = linked;
            if (_db is null || !State.IsConnected) await ConnectCoreAsync(linked.Token);

            var db = _db ?? throw new InvalidOperationException("The connection is closed.");
            return await OperationAsync(operation, ct => work(db, ct), linked.Token);
        }
        finally
        {
            _operation = null;
            _gate.Release();
        }
    }

    // ---- what the Explorer shows ---------------------------------------------------------------

    /// <summary>
    ///     The rows of the server's Properties pane (what is known from when it connected; nothing is asked of the
    ///     server).
    /// </summary>
    internal IEnumerable<(string Name, string Value)> GetServerDetails()
    {
        yield return ("Server", _serverName ?? SqlSettings.DataSource(Uri));
        yield return ("State", StateText());

        if (_currentOperation is { } doing) yield return ("Doing", doing);

        if (ServerVersion is not null) yield return ("Version", ServerVersion);
        if (_loginName is not null) yield return ("Signed in as", _loginName);
        if (_authDescription is not null) yield return ("Authentication", _authDescription);
        if (SqlSettings.Database(Uri) is { } initial) yield return ("Initial database", initial);
        if (LastError is not null) yield return ("Last error", LastError.Message);
    }

    private string StateText()
    {
        return State switch
               {
                   Connected    => "Connected",
                   Connecting c => c.ToString(),
                   Busy b       => b.ToString(),
                   Failed f     => f.ToString(),
                   _            => "Not connected"
               };
    }

    /// <summary>
    ///     The "Connect" command: connects, and reads again what the server holds if the person had already opened it.
    ///     Failures go to the status bar.
    /// </summary>
    internal async void ConnectFromUi()
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
            App.Status($"Could not connect to {SqlSettings.HostPort(Uri)}: {ex.Message}");
        }
    }

    /// <summary>The "Disconnect" command.</summary>
    internal async void DisconnectFromUi()
    {
        try
        {
            await Disconnect();
        }
        catch (Exception ex)
        {
            App.Status($"Could not disconnect from {SqlSettings.HostPort(Uri)}: {ex.Message}");
        }
    }

    /// <summary>The databases of the server, as nodes.</summary>
    internal Task<IReadOnlyList<SiloNode>> LoadDatabaseNodesAsync(CancellationToken cancel)
    {
        return RunAsync("Listing databases", async (db, ct) =>
                                             {
                                                 var       nodes = new List<SiloNode>();
                                                 using var cmd   = Command(db, DatabasesSql);
                                                 using var r     = await cmd.ExecuteReaderAsync(ct);
                                                 while (await r.ReadAsync(ct))
                                                 {
                                                     string name       = Str(r, 0);
                                                     string state      = Str(r, 1);
                                                     bool   online     = string.Equals(state, "ONLINE", StringComparison.OrdinalIgnoreCase);
                                                     bool   accessible = online && Int(r, 4) == 1;

                                                     var item = new SqlItem(this, SqlItemKind.Database, name, SqlNames.ItemUri(Uri, name),
                                                                            name)
                                                                {
                                                                    IsAccessible = accessible,
                                                                    Label = online
                                                                                ? name
                                                                                : $"{name} ({state.ToLowerInvariant()})"
                                                                };
                                                     item.Fact("State", state)
                                                         .Fact("Collation",           Str(r, 2))
                                                         .Fact("Compatibility level", Int(r, 3).ToString());
                                                     nodes.Add(item.CreateNode(0));
                                                 }

                                                 return (IReadOnlyList<SiloNode>)nodes;
                                             }, cancel);
    }

    /// <summary>What is in a database: a "Tables" and a "Views" heading with the tables and views in them.</summary>
    internal Task<IReadOnlyList<SiloNode>> LoadDatabaseContentsAsync(SqlItem database, CancellationToken cancel)
    {
        string name = database.Name;
        string sql = $"SELECT s.name, o.name, o.type FROM {SqlNames.Quote(name)}.sys.objects o " +
                     $"JOIN {SqlNames.Quote(name)}.sys.schemas s ON s.schema_id = o.schema_id "  +
                     "WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 ORDER BY s.name, o.name";

        return RunAsync($"Reading {name}", async (db, ct) =>
                                           {
                                               var tables = new List<SiloNode>();
                                               var views  = new List<SiloNode>();

                                               using var cmd = Command(db, sql);
                                               using var r   = await cmd.ExecuteReaderAsync(ct);
                                               while (await r.ReadAsync(ct))
                                               {
                                                   string schema = Str(r, 0);
                                                   string table  = Str(r, 1);
                                                   bool   view   = Str(r, 2).Trim() == "V";

                                                   var item = new SqlItem(this, view ? SqlItemKind.View : SqlItemKind.Table, table,
                                                                          SqlNames.ItemUri(Uri, name, schema, table), name, schema)
                                                              {
                                                                  Label =
                                                                      $"{schema}.{table}"
                                                              };
                                                   (view ? views : tables).Add(item.CreateNode(0));
                                               }

                                               return (IReadOnlyList<SiloNode>)
                                                   [Heading(name, "Tables", "~tables", tables), Heading(name, "Views", "~views", views)];
                                           }, cancel);
    }

    private SiloNode Heading(string database, string text, string segment, List<SiloNode> children)
    {
        var item = new SqlItem(this, SqlItemKind.Folder, text, SqlNames.ItemUri(Uri, database, segment), database)
                   { Label = $"{text} ({children.Count})" };
        var node = item.CreateNode(0);
        foreach (var child in children) node.Children.Add(child);
        return node;
    }

    /// <summary>The columns of a table or view, as nodes.</summary>
    internal Task<IReadOnlyList<SiloNode>> LoadColumnNodesAsync(SqlItem table, CancellationToken cancel)
    {
        string database = table.Database ?? throw new InvalidOperationException("The table has no database.");
        string schema   = table.Schema   ?? "dbo";
        string full     = SqlNames.FullName(database, schema, table.Name);
        string sql = "SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity " +
                     $"FROM {SqlNames.Quote(database)}.sys.columns c "                                          +
                     $"JOIN {SqlNames.Quote(database)}.sys.types t ON t.user_type_id = c.user_type_id "         +
                     "WHERE c.object_id = OBJECT_ID(@full) ORDER BY c.column_id";

        return RunAsync($"Reading the columns of {table.Name}", async (db, ct) =>
                                                                {
                                                                    var       nodes = new List<SiloNode>();
                                                                    using var cmd   = Command(db, sql);
                                                                    cmd.Parameters.AddWithValue("@full", full);
                                                                    using var r = await cmd.ExecuteReaderAsync(ct);
                                                                    while (await r.ReadAsync(ct))
                                                                    {
                                                                        string column = Str(r, 0);
                                                                        string type = SqlNames.TypeText(Str(r, 1), Int(r, 2), Int(r, 3),
                                                                                                        Int(r,            4));
                                                                        bool nullable = Int(r, 5) == 1;

                                                                        var item = new SqlItem(this, SqlItemKind.Column, column,
                                                                                               SqlNames.ItemUri(Uri, database, schema,
                                                                                                                table.Name, column),
                                                                                               database, schema)
                                                                                   {
                                                                                       Label =
                                                                                           $"{column} ({type}, {(nullable ? "null" : "not null")})"
                                                                                   };
                                                                        item.Fact("Table", $"{schema}.{table.Name}")
                                                                            .Fact("Data type",    type)
                                                                            .Fact("Allows nulls", nullable ? "Yes" : "No");
                                                                        if (Int(r, 6) == 1) item.Fact("Identity", "Yes");
                                                                        nodes.Add(item.CreateNode(0));
                                                                    }

                                                                    return (IReadOnlyList<SiloNode>)nodes;
                                                                }, cancel);
    }

    private static Db.SqlCommand Command(Db.SqlConnection db, string sql)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        return cmd;
    }

    private static string Str(Db.SqlDataReader r, int column)
    {
        return r.IsDBNull(column) ? string.Empty : Convert.ToString(r.GetValue(column)) ?? string.Empty;
    }

    private static int Int(Db.SqlDataReader r, int column)
    {
        return r.IsDBNull(column) ? 0 : Convert.ToInt32(r.GetValue(column));
    }

    /// <summary>Takes the connection string settings that sign in as the credential (see <see cref="SqlSettings.Attempts" />).</summary>
    internal void SetCredentialFragment(string fragment)
    {
        _credConfig = fragment;
    }

    public void InitProvider(IReceiver<Container> receiver)
    {
        receiver.Required().Receiver(Components);
    }

    public string? GetHostname()
    {
        return Uri.Host;
    }

    /// <summary>
    ///     Signs in to the server, trying the ways <see cref="SqlSettings.AuthOrder" /> allows in turn: when one is refused
    ///     for who we are
    ///     (not because the server cannot be reached) the next is tried. Returns true when it connected, false when it already
    ///     was.
    /// </summary>
    public async Task<bool> Connect(CancellationToken cancel = default)
    {
        // Check for already connected
        if (ConnectionState is true) return false;

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancel, _closing.Token);
        await _gate.WaitAsync(operation.Token);
        try
        {
            if (ConnectionState is true) return false; // somebody connected it while we waited

            _operation = operation;
            await ConnectCoreAsync(operation.Token);
            return true;
        }
        finally
        {
            _operation = null;
            _gate.Release();
        }
    }

    /// <summary>Closes the connection, first cancelling whatever it is doing. Returns false when it was not connected.</summary>
    public async Task<bool> Disconnect()
    {
        if (State is Disconnected) return false;

        try
        {
            _operation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // it just finished
        }

        await _gate.WaitAsync();
        try
        {
            if (State is Disconnected) return false;

            await DropAsync();
            State = new Disconnected();
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

    /// <summary>A SQL Server connection has no window of its own.</summary>
    public Window? BuildWindow()
    {
        return null;
    }

    /// <summary>
    ///     Connects (unless the connection is being restored in the background, which waits until the server is needed) and
    ///     puts the server
    ///     in the Explorer: expanded, with its databases below it, when it connected. If connecting fails the server is still
    ///     shown, closed,
    ///     and the failure is thrown after.
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
            string name  = DisplayText is { Length: > 0 } text ? text : SqlSettings.HostPort(Uri);
            var    model = new SqlItem(this, SqlItemKind.Server, name, SqlNames.ItemUri(Uri));

            Node = model.CreateNode(restoring || failure is not null ? 0 : 1);
            context.NavigationManager.AddNode(Node, _parent);
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void ConfigureFrom(ICredential credential)
    {
        credential.Required().Configure(this);
    }
}