using System.Threading;
using Silo.Providers.Network;
using Telefrag.DI;

namespace Silo.Servers;

/// <summary>
///     Passes each TCP connection on to another host and port, byte for byte. With TLS on either side it is a TLS wrapper for
///     any TCP protocol: a <c>+tls</c> endpoint takes TLS in and passes plain text on (TLS offload), and the Upstream TLS setting
///     passes it on over TLS (so a client that only speaks plain text can reach a TLS-only server); both together re-encrypt.
/// </summary>
[Singleton(typeof(IServerService))]
public sealed class TcpRelay : IServerService
{
    public string Name        => "tcp-relay";
    public string DisplayName => "TCP relay";
    public string Description => "Passes each connection on to another host, adding or removing TLS on either side.";

    public IReadOnlyList<PortProtocol> Protocols { get; } = [PortProtocol.Tcp];

    public IReadOnlyList<ServerEndpoint> DefaultEndpoints { get; } = [new(PortProtocol.Tcp, 8443, ServerTls.Implicit, "127.0.0.1")];

    public IReadOnlyList<ServerSetting> Settings { get; } =
    [
        new("host", "Upstream host", ServerSettingKind.Text, "localhost", "Where connections are passed on to."),
        new("port", "Upstream port", ServerSettingKind.Number, "80"),
        new("tls", "Upstream TLS", ServerSettingKind.Boolean, "false", "Speaks TLS to the upstream host (as its client)."),
        new("verify", "Check upstream certificate", ServerSettingKind.Boolean, "true", "Turn off for an upstream with a self-signed certificate.")
    ];

    public async Task ServeAsync(IServerConnection connection, IServerContext context, CancellationToken cancel)
    {
        string host   = context.Setting("host") ?? "localhost";
        int    port   = int.TryParse(context.Setting("port"), out int p) ? p : 80;
        bool   tls    = string.Equals(context.Setting("tls"), "true", StringComparison.OrdinalIgnoreCase);
        bool   verify = !string.Equals(context.Setting("verify"), "false", StringComparison.OrdinalIgnoreCase);

        await using var upstream = await context.ConnectAsync(host, port, tls, verify, cancel);
        context.Log($"Relay {connection.Remote} -> {host}:{port}{(tls ? " (TLS)" : "")}");

        using var done = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var       up   = Pump(connection.Stream, upstream, done.Token);
        var       down = Pump(upstream, connection.Stream, done.Token);
        await Task.WhenAny(up, down);
        await done.CancelAsync(); // one side closed: the other is finished too
        await Task.WhenAll(up, down);
    }

    private static async Task Pump(Stream from, Stream to, CancellationToken cancel)
    {
        try
        {
            await from.CopyToAsync(to, cancel);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}
