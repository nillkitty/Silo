using System.Net;

namespace Silo.Providers.Network;

/// <summary>How TLS is used on a TCP endpoint a server listens on.</summary>
public enum ServerTls
{
    /// <summary>Plain TCP.</summary>
    None,

    /// <summary>TLS from the first byte: the connection is wrapped before the service sees it (SMTPS on 465, DNS over TLS on 853).</summary>
    Implicit,

    /// <summary>
    ///     Plain at first; the service upgrades the connection when its protocol says so (SMTP's STARTTLS) with
    ///     <see cref="IServerConnection.StartTlsAsync" />.
    /// </summary>
    StartTls
}

/// <summary>Where a server listens: a protocol, a port, the address to bind (null for every address) and how TLS is used.</summary>
public sealed record ServerEndpoint
{
    public PortProtocol Protocol { get; init; } = PortProtocol.Tcp;

    public int Port { get; init; }

    /// <summary>The local address to bind, e.g. <c>127.0.0.1</c>; null or empty for every address (IPv4 and IPv6).</summary>
    public string? Address { get; init; }

    /// <summary>TLS on the endpoint; always <see cref="ServerTls.None" /> for UDP.</summary>
    public ServerTls Tls { get; init; }

    public ServerEndpoint()
    {
    }

    public ServerEndpoint(PortProtocol protocol, int port, ServerTls tls = ServerTls.None, string? address = null)
    {
        Protocol = protocol;
        Port     = port;
        Tls      = tls;
        Address  = address;
    }

    public override string ToString()
    {
        var text = $"{(string.IsNullOrEmpty(Address) ? "" : (Address.Contains(':') ? $"[{Address}]" : Address) + "/")}{Protocol.ToString().ToUpperInvariant()}:{Port}";
        return Tls switch
               {
                   ServerTls.Implicit => text + "+tls",
                   ServerTls.StartTls => text + "+starttls",
                   _                  => text
               };
    }
}

/// <summary>What kind of value a <see cref="ServerSetting" /> holds, which decides how it is edited.</summary>
public enum ServerSettingKind
{
    Text,
    Number,
    Boolean,

    /// <summary>The name of a table authored in the Silo.</summary>
    Table
}

/// <summary>Something a person can set on a server, such as the table a DNS stub answers from.</summary>
/// <param name="Key">What the value is kept under.</param>
/// <param name="DisplayName">What the setting is called on screen.</param>
/// <param name="Kind">How the value is edited.</param>
/// <param name="Default">The value until one is set.</param>
/// <param name="Description">A line about what it does.</param>
/// <param name="Columns">For a <see cref="ServerSettingKind.Table" /> setting: the columns a new table is made with, when the panel makes one.</param>
public sealed record ServerSetting(
    string                 Key,
    string                 DisplayName,
    ServerSettingKind      Kind,
    string?                Default     = null,
    string?                Description = null,
    IReadOnlyList<string>? Columns     = null);

/// <summary>A table of the Silo as a server sees it: column names and rows of cell text.</summary>
public sealed record ServerTable(string Name, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    /// <summary>The index of the first column with one of the names (any case), or -1.</summary>
    public int ColumnOf(params string[] names)
    {
        foreach (var name in names)
            for (int i = 0; i < Columns.Count; i++)
                if (string.Equals(Columns[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return i;

        return -1;
    }
}

/// <summary>The tables of the Silo a server runs in. Safe to use from any thread.</summary>
public interface IServerTables
{
    /// <summary>The table with the name (any case), or null when the Silo has none by that name.</summary>
    ServerTable? Read(string name);

    /// <summary>
    ///     Adds a row to the table, making the table (with <paramref name="columns" />) when there is none and adding any of the
    ///     columns it lacks. Cells are matched to columns by name; a column the row says nothing of is left empty.
    /// </summary>
    void Append(string name, IReadOnlyList<string> columns, IReadOnlyDictionary<string, string> row);
}

/// <summary>What a service is given to run with: its settings, the Silo's tables, a log, and a way to make TLS connections out.</summary>
public interface IServerContext
{
    /// <summary>The server's name, as the person called it.</summary>
    string ServerName { get; }

    /// <summary>The value of a setting: the one set, else the <see cref="ServerSetting.Default" />; null when neither.</summary>
    string? Setting(string key);

    IServerTables Tables { get; }

    /// <summary>Writes a line to the server's log.</summary>
    void Log(string message);

    /// <summary>
    ///     Opens a TCP connection out, wrapped in TLS (as the client, checking the server's certificate against
    ///     <paramref name="host" /> unless <paramref name="verify" /> is false) when <paramref name="tls" /> is set.
    /// </summary>
    Task<Stream> ConnectAsync(string host, int port, bool tls, bool verify = true, CancellationToken cancel = default);
}

/// <summary>One accepted TCP connection.</summary>
public interface IServerConnection
{
    /// <summary>The endpoint it arrived on.</summary>
    ServerEndpoint Endpoint { get; }

    IPEndPoint Local { get; }

    IPEndPoint Remote { get; }

    /// <summary>The stream to read and write: the TLS one once the connection is secure.</summary>
    Stream Stream { get; }

    /// <summary>Whether the connection is wrapped in TLS.</summary>
    bool IsSecure { get; }

    /// <summary>Whether <see cref="StartTlsAsync" /> can be called: the endpoint allows STARTTLS, the server has a certificate, and it is not secure yet.</summary>
    bool CanStartTls { get; }

    /// <summary>Wraps the connection in TLS, as the server, and returns the new <see cref="Stream" />.</summary>
    /// <exception cref="InvalidOperationException"><see cref="CanStartTls" /> is false.</exception>
    Task<Stream> StartTlsAsync(CancellationToken cancel = default);
}

/// <summary>
///     A network service a Silo can run: something that answers TCP connections, UDP datagrams, or both (a DNS stub, an SMTP
///     sink). The Servers panel binds services to ports; the host listens, wraps TCP in TLS where the endpoint says so, and
///     hands each connection or datagram to the service.
/// </summary>
/// <remarks>
///     Register an implementation with <c>[Singleton(typeof(IServerService))]</c>. One instance serves every server made from
///     it, concurrently: keep per-server state in what <see cref="IServerContext" /> gives, not in fields.
/// </remarks>
[RequireRegistration]
public interface IServerService
{
    /// <summary>A stable key for the service, kept in the Silo with each server made from it ("dns-stub").</summary>
    string Name { get; }

    /// <summary>What the service is called on screen ("DNS stub").</summary>
    string DisplayName { get; }

    /// <summary>A line about what it does.</summary>
    string Description { get; }

    /// <summary>The transports it speaks.</summary>
    IReadOnlyList<PortProtocol> Protocols { get; }

    /// <summary>Where a new server of this kind listens until it is told otherwise.</summary>
    IReadOnlyList<ServerEndpoint> DefaultEndpoints { get; }

    /// <summary>What a person can set on it.</summary>
    IReadOnlyList<ServerSetting> Settings { get; }

    /// <summary>Serves one TCP connection until it is done; the host closes the connection afterwards.</summary>
    Task ServeAsync(IServerConnection connection, IServerContext context, CancellationToken cancel)
    {
        throw new NotSupportedException($"{DisplayName} does not speak TCP.");
    }

    /// <summary>Answers one UDP datagram from <paramref name="remote" />: the reply to send, or null to send none.</summary>
    Task<byte[]?> AnswerAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remote, IServerContext context, CancellationToken cancel)
    {
        throw new NotSupportedException($"{DisplayName} does not speak UDP.");
    }
}
