using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Silo.Certificates;

/// <summary>The file holds a password-protected PKCS #12 (PFX) and the password was missing or wrong.</summary>
public sealed class PasswordNeededException(string message) : Exception(message);

/// <summary>What a server presented when a TLS connection was started: the leaf first, then the rest of the chain it sent or that was built.</summary>
public sealed record RemoteChain(string Host, int Port, IReadOnlyList<X509Certificate2> Certificates, string Protocol, bool TrustedBySystem);

/// <summary>
///     Reads certificates from where they come from: files (CER, CRT, PEM, DER, P7B, PFX), text (PEM, or bare base64), the Windows certificate
///     stores, and a server (the certificate it presents when a TLS connection is made to it).
/// </summary>
public static class CertificateImport
{
    // ---- text and files --------------------------------------------------------------------------------------

    /// <summary>The certificates in PEM text (any number of <c>BEGIN CERTIFICATE</c> blocks), or in bare base64 of a DER certificate.</summary>
    public static IReadOnlyList<X509Certificate2> ParseText(string text)
    {
        var certs = new List<X509Certificate2>();
        var span  = text.AsSpan();
        while (PemEncoding.TryFind(span, out var fields))
        {
            var label = span[fields.Label].ToString();
            if (label is "CERTIFICATE" or "X509 CERTIFICATE" or "TRUSTED CERTIFICATE")
                certs.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(span[fields.Base64Data].ToString())));

            span = span[fields.Location.End..];
        }

        if (certs.Count > 0) return certs;

        // no armor: a person may have copied only the base64
        var bare = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (bare.Length >= 100 && TryBase64(bare) is { } der) return [X509CertificateLoader.LoadCertificate(der)];

        return certs;
    }

    private static byte[]? TryBase64(string text)
    {
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static bool LooksLikePem(ReadOnlySpan<byte> data)
    {
        return Encoding.ASCII.GetString(data[..Math.Min(data.Length, 4096)]).Contains("-----BEGIN", StringComparison.Ordinal);
    }

    /// <summary>
    ///     The certificates in the bytes of a file, whatever its format: PEM, DER, PKCS #7 (P7B) or PKCS #12 (PFX, whose private keys are left
    ///     out: this reads certificates only).
    /// </summary>
    /// <exception cref="PasswordNeededException">It is a PKCS #12 and <paramref name="password" /> does not open it.</exception>
    /// <exception cref="CryptographicException">It is none of those.</exception>
    public static IReadOnlyList<X509Certificate2> ParseBytes(byte[] data, string? password = null)
    {
        if (LooksLikePem(data)) return ParseText(Encoding.UTF8.GetString(data));

        try
        {
            return [X509CertificateLoader.LoadCertificate(data)];
        }
        catch (CryptographicException)
        {
            // not a lone DER certificate
        }

        try
        {
            var cms = new SignedCms();
            cms.Decode(data);
            if (cms.Certificates.Count > 0) return cms.Certificates.Cast<X509Certificate2>().ToList();
        }
        catch (CryptographicException)
        {
            // not PKCS #7
        }

        return LoadPkcs12(data, password, X509KeyStorageFlags.EphemeralKeySet).ToList();
    }

    /// <summary>The certificates (with their private keys, exportable) of a PKCS #12 file.</summary>
    /// <exception cref="PasswordNeededException">The password is missing or wrong.</exception>
    /// <exception cref="CryptographicException">The bytes are not a PKCS #12 at all.</exception>
    public static X509Certificate2Collection LoadPkcs12(byte[] data, string? password, X509KeyStorageFlags flags)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12Collection(data, password, flags | X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException) when (IsPkcs12(data))
        {
            throw new PasswordNeededException(string.IsNullOrEmpty(password) ? "The file is protected by a password." : "That password does not open the file.");
        }
    }

    public static bool IsPkcs12(byte[] data)
    {
        try
        {
            Pkcs12Info.Decode(data, out _);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    // ---- the Windows certificate stores ----------------------------------------------------------------------

    public static readonly string[] StoreNames = ["My", "Root", "CA", "AuthRoot", "TrustedPeople", "TrustedPublisher", "AddressBook", "Disallowed"];

    /// <summary>The certificates of one store (public parts only: nothing is read from a private key).</summary>
    public static IReadOnlyList<X509Certificate2> ReadStore(StoreLocation location, string storeName)
    {
        using var store = new X509Store(storeName, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates.Cast<X509Certificate2>().Select(c => X509CertificateLoader.LoadCertificate(c.RawData)).OrderBy(CertificateInfo.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---- a server --------------------------------------------------------------------------------------------

    /// <summary>
    ///     The host and port a person meant by <c>host</c>, <c>host:port</c>, <c>[::1]:8443</c> or a URL (<c>https://host/path</c>, whose scheme
    ///     gives the port when none is written). 443 when nothing says otherwise.
    /// </summary>
    public static (string Host, int Port) ParseEndpoint(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) throw new ArgumentException("Give a host and port, or a URL.");

        if (!text.Contains("://")) text = "tls://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0) throw new ArgumentException($"\"{input}\" is not a host, host:port or URL.");

        var port = HasExplicitPort(text) && uri.Port > 0 ? uri.Port : DefaultPort(uri.Scheme);

        return (uri.Host.Trim('[', ']'), port);
    }

    private static bool HasExplicitPort(string text)
    {
        // "tls://host:8443" / "tls://[::1]:8443": a colon after the host
        var rest = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var end  = rest.IndexOfAny(['/', '?', '#']);
        if (end >= 0) rest = rest[..end];
        var at = rest.LastIndexOf('@');
        if (at >= 0) rest = rest[(at + 1)..];

        return rest.StartsWith('[') ? rest.Contains("]:") : rest.Contains(':');
    }

    private static int DefaultPort(string scheme)
    {
        return scheme.ToLowerInvariant() switch
               {
                   "ldaps"         => 636,
                   "imaps"         => 993,
                   "pop3s"         => 995,
                   "smtps"         => 465,
                   "ftps"          => 990,
                   "https" or "wss" => 443,
                   "mqtts"         => 8883,
                   _               => 443
               };
    }

    /// <summary>
    ///     Connects to the host, starts TLS and returns what the server presented, whether or not that is trusted (the point is to look at it).
    ///     The connection is closed at once and nothing is sent.
    /// </summary>
    public static async Task<RemoteChain> FetchAsync(string hostOrUrl, CancellationToken cancel = default)
    {
        var (host, port) = ParseEndpoint(hostOrUrl);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        List<X509Certificate2> presented = [];
        var                    trusted   = false;
        using var              tcp       = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token);
            await using var tls = new SslStream(tcp.GetStream(), false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                                                {
                                                    TargetHost = host,
                                                    RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                                                                                          {
                                                                                              trusted = errors == SslPolicyErrors.None;
                                                                                              if (cert is not null) presented.Add(X509CertificateLoader.LoadCertificate(cert.GetRawCertData()));

                                                                                              if (chain is not null)
                                                                                                  foreach (var element in chain.ChainElements.Skip(1))
                                                                                                      presented.Add(X509CertificateLoader.LoadCertificate(element.Certificate.RawData));

                                                                                              return true; // looking, not trusting
                                                                                          }
                                                }, timeout.Token);

            return new RemoteChain(host, port, presented.DistinctBy(c => c.Thumbprint).ToList(), tls.SslProtocol.ToString(), trusted);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"{host}:{port} did not answer within 15 seconds.");
        }
    }
}
