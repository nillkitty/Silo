using System.Net;
using System.Net.NetworkInformation;

namespace Silo.Providers.Network;

/// <summary>The transport protocol of a socket.</summary>
public enum PortProtocol
{
    Tcp,
    Udp
}

/// <summary>
///     A process on this computer, as far as it could be described. Everything past <see cref="Id" /> and
///     <see cref="Name" /> is best effort: it is null when the process has exited since it was looked up, or belongs to
///     someone (or something) this process may not inspect.
/// </summary>
/// <param name="Id">The process id.</param>
/// <param name="Name">The process name without extension, e.g. <c>chrome</c>.</param>
/// <param name="ExecutablePath">The full path of the program file.</param>
/// <param name="UserName">The account the process runs as, as <c>DOMAIN\user</c>.</param>
/// <param name="ParentId">The id of the process that started it (which may no longer be running).</param>
/// <param name="StartedUtc">When it started; with the id this tells a process from a later one that reused the id.</param>
public record LocalProcessInfo(int Id, string Name, string? ExecutablePath, string? UserName, int? ParentId, DateTime? StartedUtc);

/// <summary>One socket on this computer and the process that owns it.</summary>
/// <param name="Protocol">TCP or UDP.</param>
/// <param name="Local">The socket's own address and port.</param>
/// <param name="Remote">The peer's address and port; null for a listener or a UDP socket.</param>
/// <param name="State">The TCP connection state; null for UDP.</param>
/// <param name="ProcessId">
///     The owning process id (0 for sockets the system itself holds, such as TCP connections in
///     TIME_WAIT).
/// </param>
/// <param name="Process">Details of the process; null when the id is 0 or the process could not be looked up.</param>
public record PortOwner(
    PortProtocol      Protocol,
    IPEndPoint        Local,
    IPEndPoint?       Remote,
    TcpState?         State,
    int               ProcessId,
    LocalProcessInfo? Process)
{
    /// <summary>True for a TCP socket that is waiting for connections.</summary>
    public bool IsListener => State == TcpState.Listen;
}

/// <summary>
///     Finds the process that owns a socket on this computer, given a port number. It answers "who is calling my
///     localhost API?" (a request that arrives from 127.0.0.1:53124 was sent by whichever process owns port 53124) as well
///     as "who is listening on 8080?".
/// </summary>
/// <remarks>
///     Each call reads the operating system's live socket table, so it reflects the moment it is asked and costs a few
///     milliseconds; ask while the connection is still open (a closed connection is gone from the table within a couple of
///     minutes, and the owner of a TIME_WAIT leftover is reported as process 0). Only sockets on this computer can be
///     answered; a port seen from another machine has no owner here.
/// </remarks>
public interface ILocalPortOwnershipProvider
{
    /// <summary>
    ///     Every socket whose local port is <paramref name="port" />, listeners first. Pass <paramref name="remotePort" /> to
    ///     keep only connections to that peer port (to tell apart sockets that share a local port).
    /// </summary>
    IReadOnlyList<PortOwner> GetOwners(int port, PortProtocol protocol = PortProtocol.Tcp, int? remotePort = null);

    /// <summary>The first of <see cref="GetOwners" />, or null if nothing on this computer uses the port.</summary>
    PortOwner? GetOwner(int port, PortProtocol protocol = PortProtocol.Tcp, int? remotePort = null);

    /// <summary>
    ///     The process that opened a connection to one of our own listening ports. <paramref name="remote" /> is the client
    ///     endpoint as the server sees it (e.g. <c>HttpContext.Connection.RemoteIpAddress/RemotePort</c>) and
    ///     <paramref name="serverPort" /> is the port it connected to. Returns null when the client is not on this
    ///     computer, or its connection is no longer in the table.
    /// </summary>
    PortOwner? GetClient(IPEndPoint remote, int serverPort);
}