using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace Silo.Labs.Streams;

/// <summary>One fact about an open stream, for the properties list.</summary>
/// <param name="Group">What it belongs to: Stream, File, Connection, TLS, Certificate.</param>
public sealed record StreamFact(string Group, string Name, string Value);

/// <summary>A stream that is open: the stream itself, and the things around it that were opened to get it (the connection, the TLS layer).</summary>
public sealed class Reel : IAsyncDisposable
{
    private readonly TcpClient? _client;
    private readonly bool       _owned;

    private Reel(StreamSource source, Stream stream, TcpClient? client, bool owned)
    {
        Source  = source;
        Stream  = stream;
        _client = client;
        _owned  = owned;
    }

    public StreamSource Source { get; }

    public Stream Stream { get; }

    /// <summary>The connection under a network stream, or null.</summary>
    public Socket? Socket => _client?.Client;

    public SslStream? Tls => Stream as SslStream;

    /// <summary>What was wrong with the server's certificate (<see cref="SslPolicyErrors.None" /> when nothing); set while the TLS connection is made.</summary>
    public SslPolicyErrors CertificateErrors { get; private set; }

    /// <summary>Opens the stream the source describes. Throws what opening it throws (file not found, connection refused, certificate not trusted...).</summary>
    public static async Task<Reel> OpenAsync(StreamSource source, TimeSpan connectTimeout, CancellationToken cancellationToken = default)
    {
        if (source.Problem() is { } problem) throw new InvalidOperationException(problem);

        switch (source.Kind)
        {
            case StreamKind.File:
                return new Reel(source, new FileStream(source.Path, source.Mode, source.Access, source.Share, 4096, FileOptions.Asynchronous), null, true);

            case StreamKind.Memory:
            {
                var memory = new MemoryStream();
                memory.Write(source.Data);
                memory.Position = 0;
                return new Reel(source, memory, null, true);
            }

            case StreamKind.Existing:
                return new Reel(source, source.Live!, null, false);

            default:
            {
                var client = new TcpClient();
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(connectTimeout);
                    try
                    {
                        await client.ConnectAsync(source.Host.Trim(), source.Port, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"No connection to {source.Host}:{source.Port} within {connectTimeout.TotalSeconds:0.#} seconds.");
                    }

                    var net = client.GetStream();
                    if (source.Kind == StreamKind.Tcp) return new Reel(source, net, client, true);

                    var errors = SslPolicyErrors.None;
                    var ssl = new SslStream(net, false, (_, _, _, found) =>
                                                       {
                                                           errors = found;
                                                           return found == SslPolicyErrors.None || source.TrustAnyCertificate;
                                                       });
                    var options = new SslClientAuthenticationOptions { TargetHost = string.IsNullOrWhiteSpace(source.ServerName) ? source.Host.Trim() : source.ServerName.Trim() };
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    handshake.CancelAfter(connectTimeout);
                    try
                    {
                        await ssl.AuthenticateAsClientAsync(options, handshake.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"The TLS handshake with {source.Host}:{source.Port} took more than {connectTimeout.TotalSeconds:0.#} seconds.");
                    }

                    return new Reel(source, ssl, client, true) { CertificateErrors = errors };
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }
        }
    }

    /// <summary>Stops sending (the other side sees the end of what it reads) while still receiving. Only for a connection.</summary>
    public bool TryShutdownSend()
    {
        try
        {
            if (Socket is not { Connected: true } socket) return false;

            socket.Shutdown(SocketShutdown.Send);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Sets how long a read or a write may wait, in milliseconds (0 or less: for ever). False when the stream has no time limits.</summary>
    public bool TrySetTimeout(bool read, int milliseconds)
    {
        try
        {
            if (!Stream.CanTimeout) return false;

            if (read) Stream.ReadTimeout = milliseconds <= 0 ? Timeout.Infinite : milliseconds;
            else Stream.WriteTimeout = milliseconds <= 0 ? Timeout.Infinite : milliseconds;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ObjectDisposedException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_owned) await Stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // closing a connection that is already gone
        }

        _client?.Dispose();
    }

    /// <summary>What a person would want to know about the stream and what is under it, as it is now.</summary>
    public IReadOnlyList<StreamFact> Facts()
    {
        var facts = new List<StreamFact>();
        void Add(string group, string name, Func<object?> get)
        {
            try
            {
                object? v = get();
                facts.Add(new StreamFact(group, name, v switch
                                                      {
                                                          null     => "(none)",
                                                          bool b   => b ? "yes" : "no",
                                                          long n   => n.ToString("N0"),
                                                          int n    => n.ToString("N0"),
                                                          DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss"),
                                                          _        => v.ToString() ?? string.Empty
                                                      }));
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ObjectDisposedException or IOException or SocketException or PlatformNotSupportedException)
            {
                facts.Add(new StreamFact(group, name, "(not available)"));
            }
        }

        var s = Stream;
        Add("Stream", "Type", () => s.GetType().FullName);
        Add("Stream", "CanRead", () => s.CanRead);
        Add("Stream", "CanWrite", () => s.CanWrite);
        Add("Stream", "CanSeek", () => s.CanSeek);
        Add("Stream", "CanTimeout", () => s.CanTimeout);
        if (s.CanSeek)
        {
            Add("Stream", "Length", () => s.Length);
            Add("Stream", "Position", () => s.Position);
        }
        else
        {
            Add("Stream", "Length", () => "(not known: the stream cannot seek)");
            Add("Stream", "Position", () => "(not known: the stream cannot seek)");
        }

        if (s.CanTimeout)
        {
            Add("Stream", "ReadTimeout", () => s.ReadTimeout == Timeout.Infinite ? "none" : s.ReadTimeout + " ms");
            Add("Stream", "WriteTimeout", () => s.WriteTimeout == Timeout.Infinite ? "none" : s.WriteTimeout + " ms");
        }

        if (s is FileStream f)
        {
            Add("File", "Name", () => f.Name);
            Add("File", "IsAsync", () => f.IsAsync);
            Add("File", "Modified", () => File.GetLastWriteTime(f.Name));
        }

        if (s is MemoryStream m)
        {
            Add("Memory", "Capacity", () => m.Capacity);
            Add("Memory", "Writable", () => m.CanWrite);
        }

        var net = s as NetworkStream ?? (_client is { Connected: true } ? _client.GetStream() : null);
        if (net is not null) Add("Connection", "Data waiting", () => net.DataAvailable);

        if (Socket is { } sock)
        {
            Add("Connection", "Local", () => sock.LocalEndPoint);
            Add("Connection", "Remote", () => sock.RemoteEndPoint is IPEndPoint ep ? $"{ep.Address} port {ep.Port}" : sock.RemoteEndPoint);
            Add("Connection", "Connected", () => sock.Connected);
            Add("Connection", "Receive buffer", () => sock.ReceiveBufferSize);
            Add("Connection", "Send buffer", () => sock.SendBufferSize);
            Add("Connection", "No delay", () => sock.NoDelay);
            Add("Connection", "Bytes in the OS buffer", () => sock.Available);
        }

        if (s is SslStream ssl)
        {
            Add("TLS", "Protocol", () => ssl.SslProtocol);
            Add("TLS", "Cipher suite", () => ssl.NegotiatedCipherSuite);
            Add("TLS", "Application protocol", () => ssl.NegotiatedApplicationProtocol.Protocol.IsEmpty ? null : System.Text.Encoding.ASCII.GetString(ssl.NegotiatedApplicationProtocol.Protocol.Span));
            Add("TLS", "Server name", () => ssl.TargetHostName);
            Add("TLS", "Authenticated", () => ssl.IsAuthenticated);
            Add("TLS", "Encrypted", () => ssl.IsEncrypted);
            Add("TLS", "Signed", () => ssl.IsSigned);
            Add("TLS", "Mutually authenticated", () => ssl.IsMutuallyAuthenticated);
            Add("TLS", "Certificate problems", () => CertificateErrors == SslPolicyErrors.None ? "none" : CertificateErrors + (Source.TrustAnyCertificate ? " (accepted anyway)" : string.Empty));
            if (ssl.RemoteCertificate is { } cert)
            {
                var x = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);
                Add("Certificate", "Subject", () => x.Subject);
                Add("Certificate", "Issuer", () => x.Issuer);
                Add("Certificate", "Valid from", () => x.NotBefore);
                Add("Certificate", "Valid to", () => x.NotAfter);
                Add("Certificate", "Serial number", () => x.SerialNumber);
                Add("Certificate", "Thumbprint", () => x.Thumbprint);
                Add("Certificate", "Signature", () => x.SignatureAlgorithm.FriendlyName);
                Add("Certificate", "Key", () => x.PublicKey.Oid.FriendlyName);
            }
        }

        return facts;
    }
}
