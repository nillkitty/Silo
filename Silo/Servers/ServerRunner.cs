using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Silo.Providers.Network;

namespace Silo.Servers;

/// <summary>
///     One server, running: a listener per endpoint, each connection or datagram handed to the service. TCP endpoints marked
///     <see cref="ServerTls.Implicit" /> are wrapped in TLS before the service sees them; <see cref="ServerTls.StartTls" /> ones
///     let the service wrap them when its protocol asks.
/// </summary>
public sealed class ServerRunner : IAsyncDisposable
{
    private const int SioUdpConnReset = -1744830452; // stops Windows failing the next receive after an ICMP "port unreachable"

    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task>              _loops = [];
    private readonly List<IDisposable>       _sockets = [];
    private readonly Context                 _context;

    public ServerConfig       Config  { get; }
    public IServerService     Service { get; }
    public X509Certificate2?  Certificate { get; }

    /// <summary>Where it is actually listening (a port of 0 in the config becomes the one the system chose).</summary>
    public IReadOnlyList<(ServerEndpoint Endpoint, IPEndPoint Bound)> Bound { get; private set; } = [];

    private ServerRunner(ServerConfig config, IServerService service, IServerTables tables, X509Certificate2? certificate, Action<string> log)
    {
        Config      = config;
        Service     = service;
        Certificate = certificate;
        _context    = new Context(config, service, tables, log);
    }

    /// <summary>Binds every endpoint and starts serving. If any endpoint cannot be bound, none is left open.</summary>
    /// <exception cref="SocketException">A port is in use, or may not be bound.</exception>
    /// <exception cref="InvalidOperationException">The configuration cannot run (TLS without a certificate, a transport the service does not speak).</exception>
    public static ServerRunner Start(ServerConfig config, IServerService service, IServerTables tables, X509Certificate2? certificate, Action<string> log)
    {
        foreach (var e in config.Endpoints)
        {
            if (!service.Protocols.Contains(e.Protocol)) throw new InvalidOperationException($"{service.DisplayName} does not speak {e.Protocol.ToString().ToUpperInvariant()} ({e}).");
            if (e.Tls == ServerTls.Implicit && certificate is null) throw new InvalidOperationException($"{e} needs a certificate with its private key: choose one for the server.");
        }

        if (config.Endpoints.Count == 0) throw new InvalidOperationException("The server has no endpoints.");

        var runner = new ServerRunner(config, service, tables, certificate is null ? null : TlsWrap.ForServer(certificate), log);
        var bound  = new List<(ServerEndpoint, IPEndPoint)>();
        try
        {
            foreach (var e in config.Endpoints) bound.Add((e, e.Protocol == PortProtocol.Tcp ? runner.ListenTcp(e) : runner.ListenUdp(e)));
        }
        catch
        {
            runner.CloseSockets();
            throw;
        }

        runner.Bound = bound;
        log($"{config.Name}: listening on {string.Join(", ", bound.Select(b => $"{b.Item1.Protocol.ToString().ToUpperInvariant()} {b.Item2}{(b.Item1.Tls == ServerTls.None ? "" : " (" + b.Item1.Tls + ")")}"))}");
        return runner;
    }

    private static (IPAddress Address, bool DualMode) AddressOf(ServerEndpoint e)
    {
        if (!string.IsNullOrWhiteSpace(e.Address)) return (IPAddress.Parse(e.Address), false);

        return Socket.OSSupportsIPv6 ? (IPAddress.IPv6Any, true) : (IPAddress.Any, false);
    }

    private IPEndPoint ListenTcp(ServerEndpoint e)
    {
        var (address, dual) = AddressOf(e);
        var listener = new TcpListener(address, e.Port);
        if (dual) listener.Server.DualMode = true;
        listener.Start();
        _sockets.Add(new Disposer(listener.Stop));
        _loops.Add(Task.Run(() => AcceptLoop(listener, e)));
        return (IPEndPoint)listener.LocalEndpoint;
    }

    private IPEndPoint ListenUdp(ServerEndpoint e)
    {
        var (address, dual) = AddressOf(e);
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (dual) socket.DualMode = true;
            if (OperatingSystem.IsWindows()) socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            socket.Bind(new IPEndPoint(address, e.Port));
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _sockets.Add(socket);
        _loops.Add(Task.Run(() => ReceiveLoop(socket, e)));
        return (IPEndPoint)socket.LocalEndPoint!;
    }

    private async Task AcceptLoop(TcpListener listener, ServerEndpoint e)
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException || token.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                _context.Log($"{e}: accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => ServeOne(client, e, token), token);
        }
    }

    private async Task ServeOne(TcpClient client, ServerEndpoint e, CancellationToken token)
    {
        using var _      = client;
        var       remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        try
        {
            Stream stream = client.GetStream();
            if (e.Tls == ServerTls.Implicit) stream = await TlsWrap.AsServerAsync(stream, Certificate!, token);

            var connection = new Connection(e, (IPEndPoint)client.Client.LocalEndPoint!, remote, stream, e.Tls == ServerTls.Implicit,
                                            e.Tls == ServerTls.StartTls ? Certificate : null);
            try
            {
                await Service.ServeAsync(connection, _context, token);
            }
            finally
            {
                await connection.Stream.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException && token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
        {
            _context.Log($"{remote}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _context.Log($"{remote}: {Service.DisplayName} failed: {ex.Message}");
            Serilog.Log.Warning(ex, "Server {Name} failed serving {Remote}", Config.Name, remote);
        }
    }

    private async Task ReceiveLoop(Socket socket, ServerEndpoint e)
    {
        var token  = _stop.Token;
        var buffer = new byte[65535];
        EndPoint any = socket.AddressFamily == AddressFamily.InterNetworkV6 ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException || token.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                _context.Log($"{e}: receive failed: {ex.Message}");
                continue;
            }

            var datagram = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
            var remote   = (IPEndPoint)received.RemoteEndPoint;
            _ = Task.Run(async () =>
                         {
                             try
                             {
                                 if (await Service.AnswerAsync(datagram, remote, _context, token) is { } reply)
                                     await socket.SendToAsync(reply, SocketFlags.None, remote, token);
                             }
                             catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException && token.IsCancellationRequested)
                             {
                             }
                             catch (Exception ex)
                             {
                                 _context.Log($"{remote}: {Service.DisplayName} failed: {ex.Message}");
                             }
                         }, token);
        }
    }

    private void CloseSockets()
    {
        foreach (var s in _sockets)
            try
            {
                s.Dispose();
            }
            catch (SocketException)
            {
            }

        _sockets.Clear();
    }

    /// <summary>Stops listening and cancels the connections being served.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;

        await _stop.CancelAsync();
        CloseSockets();
        try
        {
            await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }

        _context.Log($"{Config.Name}: stopped");
    }

    private sealed class Disposer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class Context(ServerConfig config, IServerService service, IServerTables tables, Action<string> log) : IServerContext
    {
        public string        ServerName => config.Name;
        public IServerTables Tables     => tables;

        public string? Setting(string key)
        {
            if (config.Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;

            return service.Settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))?.Default;
        }

        public void Log(string message) => log(message);

        public Task<Stream> ConnectAsync(string host, int port, bool tls, bool verify = true, CancellationToken cancel = default)
        {
            return TlsWrap.ConnectAsync(host, port, tls, verify, cancel);
        }
    }

    private sealed class Connection(ServerEndpoint endpoint, IPEndPoint local, IPEndPoint remote, Stream stream, bool secure, X509Certificate2? startTls)
        : IServerConnection
    {
        public ServerEndpoint Endpoint    => endpoint;
        public IPEndPoint     Local       => local;
        public IPEndPoint     Remote      => remote;
        public Stream         Stream      { get; private set; } = stream;
        public bool           IsSecure    { get; private set; } = secure;
        public bool           CanStartTls => !IsSecure && startTls is not null;

        public async Task<Stream> StartTlsAsync(CancellationToken cancel = default)
        {
            if (!CanStartTls) throw new InvalidOperationException("STARTTLS is not offered on this endpoint.");

            Stream   = await TlsWrap.AsServerAsync(Stream, startTls!, cancel);
            IsSecure = true;
            return Stream;
        }
    }
}
