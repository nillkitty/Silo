namespace Silo.Connectors.Sql;

/// <summary>
///     Base class for SQL connection states
/// </summary>
public abstract record SqlState
{
    public         TimeSpan Duration    => DateTime.Now - Time;
    public virtual bool     IsBusy      => false;
    public virtual bool     IsConnected => false;
    public         DateTime Time        { get; } = DateTime.Now;
}

/// <summary>
///     Disconnected state
/// </summary>
public record Disconnected : SqlState;

/// <summary>
///     Pending connect state
/// </summary>
public record Connecting(string HostPort, CancellationToken Cancel) : SqlState
{
    public override string ToString()
    {
        return $"Connecting to {HostPort} ({Duration})";
    }
}

/// <summary>
///     Connected, idle state
/// </summary>
public record Connected(Uri Uri) : SqlState
{
    public override bool IsBusy      => false;
    public override bool IsConnected => true;

    public override string ToString()
    {
        return "Connected";
    }
}

/// <summary>
///     The last attempt to connect failed (or the link broke under a read): there is no connection, and why is kept here.
///     A read that fails on a connection that is still open does not end up here; it only sets
///     <see cref="SqlConnection.LastError" />.
/// </summary>
public record Failed(Exception Error) : SqlState
{
    public override string ToString()
    {
        return $"Failed: {Error.Message}";
    }
}

/// <summary>
///     Connected and busy
/// </summary>
public record Busy(string Operation, CancellationToken Cancel) : SqlState
{
    public override bool IsBusy      => true;
    public override bool IsConnected => true;

    public override string ToString()
    {
        return $"Busy: {Operation} ({Duration})";
    }
}