using System.Windows.Threading;
using Serilog;
using Silo.Connectors;
using Silo.ViewModel;

namespace Silo.Model;

/// <summary>
///     Bringing the Silo's saved connections back when it is opened, and keeping the saved list in step with the live
///     one.
/// </summary>
public partial class OpenSilo
{
    private readonly CancellationTokenSource                   _closing    = new();
    private readonly Dictionary<long, SiloNode>                _pending    = [];
    private readonly Dictionary<long, CancellationTokenSource> _retryWaits = [];
    private readonly Dictionary<IConnection, long>             _savedIds   = new(ReferenceEqualityComparer.Instance);

    private IReadOnlyList<IConnector> _connectors = [];

    /// <summary>True from the point of no return of closing the Silo: nothing should be written about it any more.</summary>
    public bool IsClosing { get; private set; }

    /// <summary>
    ///     Raised (on the UI thread) when every saved connection has had its first attempt -- brought back, or failed and left
    ///     as a node
    ///     that says why. A connection whose connector retries in the background may still come back later.
    /// </summary>
    public event EventHandler? ConnectionsRestored;

    /// <summary>
    ///     Starts bringing the saved connections back, in the background: the Silo is usable at once and each connection's
    ///     nodes appear in
    ///     the Explorer as the connection is ready, so a slow or unreachable destination holds nothing up.
    /// </summary>
    internal void BeginRestoreConnections()
    {
        if (!ReconnectConnections || Data is not IConnectionStore) return;

        Dispatcher.BeginInvoke(new Action(async () =>
                                          {
                                              try
                                              {
                                                  await RestoreConnectionsAsync();
                                              }
                                              catch (Exception ex)
                                              {
                                                  Log.Warning(ex, "Restoring the saved connections failed");
                                              }
                                          }), DispatcherPriority.Background);
    }

    private async Task RestoreConnectionsAsync()
    {
        if (Data is not IConnectionStore store || IsClosing) return;

        var records = await Task.Run(() => store.GetConnections());
        var todo    = records.Where(r => !_savedIds.ContainsValue(r.Id)).ToList();
        if (todo.Count > 0)
        {
            _connectors = ConnectorCatalog.Connectors();
            await Task.WhenAll(todo.Select(r => RestoreConnectionAsync(r, 0)));
        }

        ConnectionsRestored?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     One attempt to bring a saved connection back. While it runs the Explorer shows a "connecting..." node; success
    ///     replaces it with
    ///     the connection's own nodes, failure leaves a node that says why -- and, for a connector that reconnects in the
    ///     background,
    ///     schedules the next attempt.
    /// </summary>
    private async Task RestoreConnectionAsync(ConnectionRecord record, int attempt)
    {
        if (IsClosing) return;

        ShowPending(record, null, false);

        IConnector? connector = null;
        string      error;
        try
        {
            var uri = record.SourceUri ??
                      throw new InvalidOperationException("It was saved without a destination, so it cannot be recreated.");
            connector = ConnectionRestore.FindConnector(uri, _connectors) ??
                        throw new InvalidOperationException($"No connector handles \"{uri.Scheme}:\" destinations.");

            var connection =
                await ConnectionCreation.CreateAsync(connector, ConnectionRestore.RequestFor(record, connector), _closing.Token);
            Connections.Add(connection);
            _savedIds[connection] = record.Id;

            try
            {
                await InitConnectionCoreAsync(connection, ConnectReason.BackgroundRestore, !ForegroundReconnect);
            }
            catch
            {
                // not usable: take it (and whatever nodes it made) out again, but keep it saved
                Connections.Remove(connection);
                _savedIds.Remove(connection);
                throw;
            }

            RemovePending(record.Id);
            return;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The saved connection {Name} could not be brought back", record.DisplayName);
            error = ex.Message;
        }

        if (IsClosing) return;

        bool retry = connector is not null && ConnectionRestore.RetriesInBackground(connector);
        ShowPending(record, error, retry);
        if (retry) _ = RetryAfterAsync(record, attempt);
    }

    private async Task RetryAfterAsync(ConnectionRecord record, int attempt)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _retryWaits[record.Id] = wait;
        try
        {
            await Task.Delay(ConnectionRestore.RetryDelay(attempt), wait.Token);
        }
        catch (OperationCanceledException)
        {
            if (_closing.IsCancellationRequested) return; // otherwise: "Try Again" was chosen, go now
        }
        finally
        {
            _retryWaits.Remove(record.Id);
        }

        // forgotten, or brought back by hand, while it waited
        if (IsClosing || !_pending.ContainsKey(record.Id)) return;

        await RestoreConnectionAsync(record, attempt + 1);
    }

    /// <summary>The "Try Again" command of a connection that is not connected: now, whatever it was waiting for.</summary>
    private void RetryNow(ConnectionRecord record)
    {
        if (_retryWaits.TryGetValue(record.Id, out var wait))
            wait.Cancel();
        else
            _ = RestoreConnectionAsync(record, 0);
    }

    /// <summary>The "Forget Connection" command of a connection that is not connected.</summary>
    private void ForgetPending(ConnectionRecord record)
    {
        RemovePending(record.Id);
        if (_retryWaits.TryGetValue(record.Id, out var wait)) wait.Cancel();

        try
        {
            (Data as IConnectionStore)?.ForgetConnection(record.Id);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Forgetting the saved connection {Name} failed", record.DisplayName);
        }
    }

    private void ShowPending(ConnectionRecord record, string? error, bool willRetry)
    {
        RemovePending(record.Id);

        var model = new PendingConnection(record, error, willRetry, () => RetryNow(record), () => ForgetPending(record));
        var node  = new SiloNode(model, model.Label, model.Uri, model.Icon, null);
        _pending[record.Id] = node;
        Nodes.AddNode(node, null);
    }

    private void RemovePending(long id)
    {
        if (_pending.Remove(id, out var node)) Nodes.RemoveNode(node);
    }

    /// <summary>
    ///     Takes a connection out of the Silo for good: from the Explorer, from the live connections and from the saved
    ///     list.
    /// </summary>
    public async Task RemoveConnectionAsync(IConnection connection)
    {
        connection.Required();

        await connection.Remove();
        Connections.Remove(connection);

        if (_savedIds.Remove(connection, out var id) && Data is IConnectionStore store)
            try
            {
                store.ForgetConnection(id);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Forgetting the saved connection failed");
            }
    }
}