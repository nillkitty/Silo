using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Silo.Certificates;

public enum KeyAlgorithm
{
    Rsa2048,
    Rsa3072,
    Rsa4096,
    EcdsaP256,
    EcdsaP384,
    EcdsaP521
}

/// <summary>What a new certificate is to say: who it is, how long it lasts, what it may do.</summary>
/// <param name="Subject">A distinguished name (<c>CN=Example Root CA, O=Example</c>), or just a common name.</param>
/// <param name="AltNames">Subject alternative names: IP addresses are recognised, anything else is a DNS name.</param>
/// <param name="IsCa">A certificate authority: it may sign certificates (and, with no <paramref name="PathLength" />, authorities below it).</param>
/// <param name="PathLength">For an authority, how many authorities may stand between it and a leaf; null for no limit.</param>
public sealed record NewCertificate(
    string                Subject,
    KeyAlgorithm          Algorithm   = KeyAlgorithm.EcdsaP256,
    int                   ValidDays   = 365,
    IReadOnlyList<string>? AltNames   = null,
    bool                  IsCa        = false,
    int?                  PathLength  = null,
    bool                  ServerAuth  = false,
    bool                  ClientAuth  = false,
    bool                  CodeSigning = false,
    HashAlgorithmName?    Hash        = null)
{
    public HashAlgorithmName HashAlgorithm => Hash ?? Algorithm switch
                                                    {
                                                        KeyAlgorithm.EcdsaP384 or KeyAlgorithm.Rsa4096 => HashAlgorithmName.SHA384,
                                                        KeyAlgorithm.EcdsaP521                          => HashAlgorithmName.SHA512,
                                                        _                                               => HashAlgorithmName.SHA256
                                                    };
}

/// <summary>What of a signing request to keep when an authority signs it.</summary>
/// <param name="ValidDays">How long the certificate lasts (never past the authority's own end).</param>
public sealed record SigningOptions(int ValidDays = 365, bool ServerAuth = false, bool ClientAuth = false);

/// <summary>What a certificate signing request asks for, read without signing anything.</summary>
public sealed record RequestInfo(string Subject, string PublicKey, IReadOnlyList<string> AltNames, string Pem);

/// <summary>
///     Makes certificates: self-signed ones (a root certificate authority, or a lone certificate), certificates signed by an authority whose
///     private key is held, and certificates made from signing requests (CSRs).
/// </summary>
public static class CertificateFactory
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1", ClientAuthOid = "1.3.6.1.5.5.7.3.2", CodeSigningOid = "1.3.6.1.5.5.7.3.3";
    private const string SanOid = "2.5.29.17", EkuOid = "2.5.29.37";

    // ---- new pairs -------------------------------------------------------------------------------------------

    /// <summary>A new key and a self-signed certificate for it: a root certificate authority when <see cref="NewCertificate.IsCa" />.</summary>
    public static KeyPair CreateSelfSigned(NewCertificate options)
    {
        Check(options);
        using var key = NewKey(options.Algorithm);
        var       req = Request(options, key, out var now);

        using var cert = req.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(options.ValidDays));
        return new KeyPair(X509CertificateLoader.LoadCertificate(cert.RawData), key.ExportPkcs8PrivateKey());
    }

    /// <summary>A new key and a certificate for it signed by <paramref name="issuer" /> (which must hold its private key and be allowed to issue).</summary>
    public static KeyPair Issue(X509Certificate2 issuer, NewCertificate options)
    {
        Check(options);
        RequireIssuer(issuer);

        using var key = NewKey(options.Algorithm);
        var       req = Request(options, key, out var now);
        req.CertificateExtensions.Add(AuthorityKey(issuer));

        using var cert = CreateSigned(req, issuer, now.AddMinutes(-5), NotAfter(issuer, now, options.ValidDays));
        return new KeyPair(X509CertificateLoader.LoadCertificate(cert.RawData), key.ExportPkcs8PrivateKey());
    }

    // ---- replacing a key -------------------------------------------------------------------------------------

    /// <summary>The kind of key a certificate has, as the nearest <see cref="KeyAlgorithm" />.</summary>
    public static KeyAlgorithm AlgorithmOf(X509Certificate2 cert)
    {
        if (cert.GetRSAPublicKey() is { } rsa)
        {
            using (rsa) return rsa.KeySize >= 4096 ? KeyAlgorithm.Rsa4096 : rsa.KeySize >= 3072 ? KeyAlgorithm.Rsa3072 : KeyAlgorithm.Rsa2048;
        }

        if (cert.GetECDsaPublicKey() is { } ec)
        {
            using (ec) return ec.KeySize >= 521 ? KeyAlgorithm.EcdsaP521 : ec.KeySize >= 384 ? KeyAlgorithm.EcdsaP384 : KeyAlgorithm.EcdsaP256;
        }

        return KeyAlgorithm.EcdsaP256;
    }

    /// <summary>
    ///     What a new certificate must say to stand in for <paramref name="cert" /> with a different key: the same subject, names, usages and
    ///     authority settings. The key kind and the length of validity default to the old ones.
    /// </summary>
    public static NewCertificate Like(X509Certificate2 cert, KeyAlgorithm? algorithm = null, int? validDays = null)
    {
        var bc    = CertificateInfo.BasicConstraints(cert);
        var isCa  = bc?.CertificateAuthority == true;
        var usages = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault()?.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value).ToList() ?? [];
        var names = CertificateInfo.SubjectAlternativeNames(cert).Select(l => l[(l.IndexOf('=') + 1)..].Trim())
                                   .Where(v => v.Length > 0 && !v.Contains('@') && (IPAddress.TryParse(v, out _) || !v.Contains(' '))).ToList();

        return new NewCertificate(cert.SubjectName.Name, algorithm ?? AlgorithmOf(cert), validDays ?? Math.Max(1, (int)Math.Round((cert.NotAfter - cert.NotBefore).TotalDays)),
                                  names, isCa, isCa && bc!.HasPathLengthConstraint ? bc.PathLengthConstraint : null, usages.Contains(ServerAuthOid), usages.Contains(ClientAuthOid),
                                  usages.Contains(CodeSigningOid));
    }

    /// <summary>
    ///     A new key and certificate that say what <paramref name="old" /> said: self-signed when <paramref name="issuer" /> is null, else signed by it.
    ///     The old key is untouched; the caller decides what becomes of it.
    /// </summary>
    public static KeyPair Rekey(X509Certificate2 old, X509Certificate2? issuer, KeyAlgorithm? algorithm = null, int? validDays = null)
    {
        var options = Like(old, algorithm, validDays);
        return issuer is null ? CreateSelfSigned(options) : Issue(issuer, options);
    }

    // ---- requests --------------------------------------------------------------------------------------------

    /// <summary>What the signing request (PEM) asks for. Nothing is trusted or signed.</summary>
    /// <exception cref="CryptographicException">It is not a valid request, or its own signature does not check out.</exception>
    public static RequestInfo ReadRequest(string csrPem)
    {
        var req = Load(csrPem);
        return new RequestInfo(req.SubjectName.Name, req.PublicKey.Oid.FriendlyName ?? req.PublicKey.Oid.Value ?? "?", AltNamesOf(req), csrPem.Trim());
    }

    /// <summary>
    ///     Signs a certificate signing request with <paramref name="issuer" />. The subject and public key are the request's; so are its
    ///     alternative names and extended key usages. What it says about being an authority or its key usage is ignored: the issuer decides
    ///     that, and the result is an end-entity certificate.
    /// </summary>
    public static X509Certificate2 SignRequest(X509Certificate2 issuer, string csrPem, SigningOptions options)
    {
        RequireIssuer(issuer);
        if (options.ValidDays < 1) throw new ArgumentException("The certificate has to last at least a day.");

        var req  = Load(csrPem);
        var kept = req.CertificateExtensions.Where(e => e.Oid?.Value is SanOid or EkuOid).ToList();
        req.CertificateExtensions.Clear();

        var isRsa = req.PublicKey.Oid.Value == "1.2.840.113549.1.1.1";
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | (isRsa ? X509KeyUsageFlags.KeyEncipherment : 0), true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(AuthorityKey(issuer));
        foreach (var e in kept) req.CertificateExtensions.Add(e);

        if (kept.All(e => e.Oid?.Value != EkuOid) && (options.ServerAuth || options.ClientAuth))
            req.CertificateExtensions.Add(Eku(options.ServerAuth, options.ClientAuth, false));

        var now = DateTimeOffset.UtcNow;
        using var cert = CreateSigned(req, issuer, now.AddMinutes(-5), NotAfter(issuer, now, options.ValidDays));
        return X509CertificateLoader.LoadCertificate(cert.RawData);
    }

    private static CertificateRequest Load(string csrPem)
    {
        // Unsafe load: the request's extensions (names, key usages) are read; SignRequest keeps only the ones it understands
        return CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
    }

    private static List<string> AltNamesOf(CertificateRequest req)
    {
        var san = req.CertificateExtensions.FirstOrDefault(e => e.Oid?.Value == SanOid);
        return san is null ? [] : san.Format(true).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
    }

    // ---- building blocks -------------------------------------------------------------------------------------

    private static void Check(NewCertificate o)
    {
        if (string.IsNullOrWhiteSpace(o.Subject)) throw new ArgumentException("The certificate needs a subject (a name).");
        if (o.ValidDays < 1) throw new ArgumentException("The certificate has to last at least a day.");
        if (o.PathLength is < 0) throw new ArgumentException("The path length cannot be negative.");
    }

    /// <summary>
    ///     Why <paramref name="issuer" /> cannot sign certificates, as an exception; nothing when it can: it must hold its private key, be a
    ///     certificate authority whose key usage allows Key Cert Sign, and be valid now.
    /// </summary>
    public static void RequireIssuer(X509Certificate2 issuer)
    {
        var name = CertificateInfo.DisplayName(issuer);
        if (!issuer.HasPrivateKey) throw new InvalidOperationException($"{name} has no private key here, so it cannot sign.");
        if (!CertificateInfo.IsCa(issuer)) throw new InvalidOperationException($"{name} is not a certificate authority (its basic constraints do not say CA).");
        if (!CertificateInfo.CanIssue(issuer)) throw new InvalidOperationException($"{name} is not allowed to sign certificates (its key usage lacks Key Cert Sign).");
        if (DateTime.Now > issuer.NotAfter) throw new InvalidOperationException($"{name} has expired.");
        if (DateTime.Now < issuer.NotBefore) throw new InvalidOperationException($"{name} is not valid yet.");
    }

    /// <summary>
    ///     Signs the request with the issuer's key, whatever kind of key the request is for (an RSA authority may sign an EC key and the other way
    ///     round: <c>CertificateRequest.Create(issuer, ...)</c> only allows the same kind).
    /// </summary>
    private static X509Certificate2 CreateSigned(CertificateRequest req, X509Certificate2 issuer, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = issuer.GetRSAPrivateKey();
        using var ec  = rsa is null ? issuer.GetECDsaPrivateKey() : null;
        var generator = rsa is not null ? X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1)
                        : ec is not null ? X509SignatureGenerator.CreateForECDsa(ec)
                        : throw new InvalidOperationException($"{CertificateInfo.DisplayName(issuer)} has no RSA or ECDSA private key to sign with.");

        return req.Create(issuer.SubjectName, generator, notBefore, notAfter, Serial());
    }

    private static AsymmetricAlgorithm NewKey(KeyAlgorithm algorithm)
    {
        return algorithm switch
               {
                   KeyAlgorithm.Rsa2048  => RSA.Create(2048),
                   KeyAlgorithm.Rsa3072  => RSA.Create(3072),
                   KeyAlgorithm.Rsa4096  => RSA.Create(4096),
                   KeyAlgorithm.EcdsaP384 => ECDsa.Create(ECCurve.NamedCurves.nistP384),
                   KeyAlgorithm.EcdsaP521 => ECDsa.Create(ECCurve.NamedCurves.nistP521),
                   _                     => ECDsa.Create(ECCurve.NamedCurves.nistP256)
               };
    }

    private static CertificateRequest Request(NewCertificate o, AsymmetricAlgorithm key, out DateTimeOffset now)
    {
        now = DateTimeOffset.UtcNow;
        var name = new X500DistinguishedName(o.Subject.Contains('=') ? o.Subject : "CN=" + o.Subject.Trim());
        var req = key is RSA rsa
                      ? new CertificateRequest(name, rsa, o.HashAlgorithm, RSASignaturePadding.Pkcs1)
                      : new CertificateRequest(name, (ECDsa)key, o.HashAlgorithm);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(o.IsCa, o.IsCa && o.PathLength.HasValue, o.PathLength ?? 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(o.IsCa
                                                                    ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature
                                                                    : X509KeyUsageFlags.DigitalSignature | (key is RSA ? X509KeyUsageFlags.KeyEncipherment : 0), true));
        if (o.ServerAuth || o.ClientAuth || o.CodeSigning) req.CertificateExtensions.Add(Eku(o.ServerAuth, o.ClientAuth, o.CodeSigning));

        var names = (o.AltNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        if (names.Count > 0)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var n in names)
                if (IPAddress.TryParse(n, out var ip)) san.AddIpAddress(ip);
                else san.AddDnsName(n);

            req.CertificateExtensions.Add(san.Build());
        }

        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req;
    }

    private static X509EnhancedKeyUsageExtension Eku(bool server, bool client, bool code)
    {
        var usages = new OidCollection();
        if (server) usages.Add(new Oid(ServerAuthOid, "Server Authentication"));
        if (client) usages.Add(new Oid(ClientAuthOid, "Client Authentication"));
        if (code) usages.Add(new Oid(CodeSigningOid, "Code Signing"));
        return new X509EnhancedKeyUsageExtension(usages, false);
    }

    private static X509Extension AuthorityKey(X509Certificate2 issuer)
    {
        var hasKeyId = issuer.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Any();
        return X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, hasKeyId, !hasKeyId);
    }

    /// <summary>The end of a certificate that is to last <paramref name="days" />, but never past the end of its issuer.</summary>
    private static DateTimeOffset NotAfter(X509Certificate2 issuer, DateTimeOffset now, int days)
    {
        var end = now.AddDays(days);
        var cap = new DateTimeOffset(issuer.NotAfter.ToUniversalTime());
        return end > cap ? cap : end;
    }

    private static byte[] Serial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // positive
        serial[0] |= 0x01; // and never zero
        return serial;
    }
}
