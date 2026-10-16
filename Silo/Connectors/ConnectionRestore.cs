using System.Linq;
using Silo.Commanding;
using Silo.ViewModel;

namespace Silo.Connectors;

/// <summary>The decisions in bringing the saved connections of a Silo back when it is opened. Pure logic, no UI.</summary>
public static class ConnectionRestore
{
    /// <summary>How long a connector that retries in the background waits before the second attempt; each later wait is twice as long.</summary>
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(15);

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait after attempt number <paramref name="attempt" /> (0 is the first) failed.</summary>
    public static TimeSpan RetryDelay(int attempt)
    {
        double seconds = FirstRetry.TotalSeconds * Math.Pow(2, Math.Clamp(attempt, 0, 20));
        return TimeSpan.FromSeconds(Math.Min(seconds, LongestRetry.TotalSeconds));
    }

    /// <summary>
    ///     Whether a connection that could not be brought back is tried again on its own: when its connector says it reconnects in the
    ///     background (<see cref="ConnectorSetupFlags.BackgroundReconnect" />). Any other waits for the person to say "try again".
    /// </summary>
    public static bool RetriesInBackground(IConnector connector)
    {
        return connector.Flags.HasFlag(ConnectorSetupFlags.BackgroundReconnect);
    }

    /// <summary>The connector that takes the saved destination: the one that owns its scheme, else one that merely handles it. Null if none does.</summary>
    public static IConnector? FindConnector(Uri uri, IEnumerable<IConnector> connectors)
    {
        var uriConnectors = ConnectorCatalog.UriConnectors(connectors.ToList());
        var found         = UriRules.FindConnector(uri, uriConnectors) ?? UriRules.FindConnector(uri, uriConnectors, UriRules.AnyScope);
        return found as IConnector;
    }

    /// <summary>The request that recreates a saved connection.</summary>
    public static ConnectionRequest RequestFor(ConnectionRecord record, IConnector connector)
    {
        return new ConnectionRequest(record.SourceUri ?? throw new ArgumentException("The saved connection has no destination.", nameof(record)))
               {
                   Connector   = connector,
                   DisplayName = string.IsNullOrWhiteSpace(record.DisplayName) ? null : record.DisplayName
               };
    }
}

/// <summary>
///     What the Explorer shows for a saved connection that is being brought back or could not be: a node that says so, with the reason,
///     and the commands to try again and to forget the connection. It goes away when the real connection's nodes are there.
/// </summary>
public sealed class PendingConnection : INodeDetails, ICommandProvider
{
    private readonly Action _retry;
    private readonly Action _forget;

    /// <param name="record">The saved connection.</param>
    /// <param name="error">Why it could not be brought back; null while it is still being tried.</param>
    /// <param name="willRetry">Whether it is tried again on its own.</param>
    /// <param name="retry">Tries now.</param>
    /// <param name="forget">Forgets the saved connection.</param>
    public PendingConnection(ConnectionRecord record, string? error, bool willRetry, Action retry, Action forget)
    {
        Record    = record;
        Error     = error;
        WillRetry = willRetry;
        _retry    = retry;
        _forget   = forget;
    }

    public ConnectionRecord Record { get; }

    /// <summary>Why the connection could not be brought back; null while it is being tried.</summary>
    public string? Error { get; }

    public bool WillRetry { get; }

    public bool Failed => Error is not null;

    /// <summary>The text of the node.</summary>
    public string Label => $"{Name} ({(Failed ? "not connected" : "connecting...")})";

    public Uri? Icon => new(Failed ? "FsFileMissing" : "Plug", UriKind.Relative);

    public string Name => string.IsNullOrWhiteSpace(Record.DisplayName) ? Record.SourceUri?.ToString() ?? "Connection" : Record.DisplayName;

    public string TypeName => "Connection";

    public Uri Uri => Record.SourceUri ?? new Uri("silo:connection/" + Record.Id);

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        yield return ("Destination", Record.SourceUri?.ToString() ?? "(none saved)");
        yield return ("State", Failed ? "Not connected" : "Connecting");
        if (Error is not null) yield return ("Problem", Error);
        if (Failed) yield return ("Next", WillRetry ? "Tries again by itself; or use Try Again" : "Use Try Again when it should be reachable");
    }

    public IEnumerable<ProvidedCommand> GetCommands()
    {
        yield return ProvidedCommand.From("connection.retry", "_Try Again", _retry, "Refresh", () => Failed, "Connects to this destination again", "Connection");
        yield return new ProvidedCommand("connection.forget", "_Forget Connection", new DelegateCommand(_ => _forget()), null, null,
                                         "Takes this connection out of the Silo", "Connection", IsDestructive: true);
    }
}
