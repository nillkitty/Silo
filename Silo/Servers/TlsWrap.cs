using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace Silo.Servers;

/// <summary>
///     Wraps any TCP stream in TLS, in either direction: as the server (presenting a certificate to whoever connected) or as
///     the client (checking the certificate of whatever was connected to). Protocol code stays the same either way; only the
///     stream it is handed changes.
/// </summary>
public static class TlsWrap
{
    /// <summary>How long a handshake may take before the connection is given up.</summary>
    public static TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Wraps <paramref name="inner" /> as the TLS server, presenting <paramref name="certificate" /> (which must hold its private key).</summary>
    /// <exception cref="AuthenticationException">The handshake failed.</exception>
    public static async Task<SslStream> AsServerAsync(Stream inner, X509Certificate2 certificate, CancellationToken cancel = default)
    {
        var ssl = new SslStream(inner, false);
        try
        {
            using var timeout = Timeout(cancel);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                                                {
                                                    ServerCertificate         = certificate,
                                                    ClientCertificateRequired = false,
                                                    EnabledSslProtocols       = SslProtocols.None, // the system's choice (TLS 1.2 and 1.3)
                                                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                                                }, timeout.Token);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    ///     Wraps <paramref name="inner" /> as the TLS client of <paramref name="host" />. With <paramref name="verify" /> false any
    ///     certificate is taken (for a test server with a self-signed one).
    /// </summary>
    /// <exception cref="AuthenticationException">The handshake failed, or the certificate is not good for <paramref name="host" />.</exception>
    public static async Task<SslStream> AsClientAsync(Stream inner, string host, bool verify = true, CancellationToken cancel = default)
    {
        var ssl = new SslStream(inner, false);
        try
        {
            using var timeout = Timeout(cancel);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                                                {
                                                    TargetHost = host,
                                                    RemoteCertificateValidationCallback = verify ? null : static (_, _, _, _) => true
                                                }, timeout.Token);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }
    }

    /// <summary>Opens a TCP connection to <paramref name="host" />, wrapped in TLS as the client when <paramref name="tls" /> is set.</summary>
    public static async Task<Stream> ConnectAsync(string host, int port, bool tls, bool verify = true, CancellationToken cancel = default)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancel);
            Stream stream = client.GetStream();
            return tls ? await AsClientAsync(stream, host, verify, cancel) : stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     A certificate that Windows' TLS (SChannel) will serve with. A key made in memory (as the Silo's keypairs are) is not
    ///     one SChannel can use, so the pair goes through a PKCS #12 round trip that puts the key where it can.
    /// </summary>
    public static X509Certificate2 ForServer(X509Certificate2 withKey)
    {
        if (!withKey.HasPrivateKey) throw new ArgumentException("The certificate has no private key, so it cannot serve TLS.", nameof(withKey));
        if (!OperatingSystem.IsWindows()) return withKey;

        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    private static CancellationTokenSource Timeout(CancellationToken cancel)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(HandshakeTimeout);
        return cts;
    }
}
