using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Silo.Certificates;

/// <summary>What a certificate is for in a chain of trust.</summary>
public enum CertificateRole
{
    /// <summary>An end-entity certificate (a server, a client, a person, a code signer).</summary>
    Leaf,

    /// <summary>A certificate authority that another authority issued.</summary>
    IntermediateCa,

    /// <summary>A certificate authority that vouches for itself (self-signed).</summary>
    RootCa
}

/// <summary>
///     Describes certificates for people: what they are, what they may be used for, and the icon of each kind. The icon keys are in
///     <c>Resources/NavIcons.xaml</c>: one per construct, so a certificate, a root authority, an intermediate authority, a keypair,
///     an authority's keypair, a store, a request and a signature never look alike.
/// </summary>
public static class CertificateInfo
{
    // ---- icons (Resources/NavIcons.xaml) ---------------------------------------------------------------------

    public const string CertificateIcon     = "Certificate";
    public const string RootCaIcon          = "CertRoot";
    public const string IntermediateCaIcon  = "CertCa";
    public const string KeypairIcon         = "Keypair";
    public const string AuthorityKeypairIcon = "KeypairCa";
    public const string StoreIcon           = "CertStore";
    public const string RequestIcon         = "CertRequest";
    public const string SignatureIcon       = "Signature";
    public const string VerifiedIcon        = "SignatureVerified";
    public const string RevokedIcon         = "CertRevoked";
    public const string IssuedIcon          = "CertIssued";

    /// <summary>The icon of a certificate, or of a keypair when <paramref name="hasKey" /> (an authority's keypair has its own).</summary>
    public static string IconFor(X509Certificate2 cert, bool hasKey)
    {
        var role = RoleOf(cert);
        if (hasKey) return role == CertificateRole.Leaf ? KeypairIcon : AuthorityKeypairIcon;

        return role switch
               {
                   CertificateRole.RootCa         => RootCaIcon,
                   CertificateRole.IntermediateCa => IntermediateCaIcon,
                   _                              => CertificateIcon
               };
    }

    /// <summary>What the node is called: "Root CA Certificate", "Keypair", "CA Keypair"...</summary>
    public static string KindFor(X509Certificate2 cert, bool hasKey)
    {
        var role = RoleOf(cert);
        if (hasKey) return role == CertificateRole.Leaf ? "Keypair" : "CA Keypair";

        return role switch
               {
                   CertificateRole.RootCa         => "Root CA Certificate",
                   CertificateRole.IntermediateCa => "Intermediate CA Certificate",
                   _                              => "Certificate"
               };
    }

    // ---- what it is ------------------------------------------------------------------------------------------

    public static X509BasicConstraintsExtension? BasicConstraints(X509Certificate2 cert)
    {
        return cert.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
    }

    public static bool IsCa(X509Certificate2 cert)
    {
        return BasicConstraints(cert)?.CertificateAuthority == true;
    }

    public static bool IsSelfSigned(X509Certificate2 cert)
    {
        return cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData) && SignedBySelf(cert);
    }

    private static bool SignedBySelf(X509Certificate2 cert)
    {
        // same name is not enough (a cross-signed certificate shares its name with the one it replaces): check the signature too
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode                     = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(cert);
            chain.ChainPolicy.RevocationMode                = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags             = X509VerificationFlags.AllFlags;
            chain.ChainPolicy.DisableCertificateDownloads   = true;
            chain.Build(cert);
            return chain.ChainElements.Count == 1 && !chain.ChainStatus.Any(s => s.Status.HasFlag(X509ChainStatusFlags.NotSignatureValid));
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static CertificateRole RoleOf(X509Certificate2 cert)
    {
        if (!IsCa(cert)) return CertificateRole.Leaf;

        return IsSelfSigned(cert) ? CertificateRole.RootCa : CertificateRole.IntermediateCa;
    }

    /// <summary>
    ///     True when the certificate may sign other certificates: it is a certificate authority and its key usage, when it says anything,
    ///     includes key cert sign. (Holding the private key is a separate matter.)
    /// </summary>
    public static bool CanIssue(X509Certificate2 cert)
    {
        if (!IsCa(cert)) return false;

        var usage = cert.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        return usage is null || usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign);
    }

    public static string ThumbprintSha256(X509Certificate2 cert)
    {
        return cert.GetCertHashString(HashAlgorithmName.SHA256);
    }

    /// <summary>The name shown for a certificate: its common name, else its subject.</summary>
    public static string DisplayName(X509Certificate2 cert)
    {
        var name = cert.GetNameInfo(X509NameType.SimpleName, false);
        return string.IsNullOrWhiteSpace(name) ? cert.Subject : name;
    }

    public static string KeyDescription(X509Certificate2 cert)
    {
        var alg = cert.PublicKey.Oid.FriendlyName ?? cert.PublicKey.Oid.Value ?? "?";
        try
        {
            if (cert.GetRSAPublicKey() is { } rsa)
            {
                using (rsa) return $"{alg} {rsa.KeySize} bit";
            }

            if (cert.GetECDsaPublicKey() is { } ec)
            {
                using (ec) return $"{alg} {ec.KeySize} bit ({ec.ExportParameters(false).Curve.Oid?.FriendlyName})";
            }
        }
        catch (CryptographicException)
        {
        }

        return alg;
    }

    public static string KeyUsages(X509Certificate2 cert)
    {
        var usage = cert.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        return usage is null ? "" : usage.KeyUsages.ToString().Replace(", ", ", ");
    }

    public static string ExtendedUsages(X509Certificate2 cert)
    {
        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return eku is null ? "" : string.Join(", ", eku.EnhancedKeyUsages.Cast<Oid>().Select(o => o.FriendlyName ?? o.Value));
    }

    public static IReadOnlyList<string> SubjectAlternativeNames(X509Certificate2 cert)
    {
        var san = cert.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        return san is null ? [] : SanText(san);
    }

    private static List<string> SanText(X509Extension san)
    {
        // "DNS Name=a.example\r\nIP Address=10.0.0.1" -- the framework's own text avoids parsing the ASN.1 here
        return san.Format(true).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
    }

    /// <summary>The label / value rows the Properties pane shows.</summary>
    public static IEnumerable<(string Name, string Value)> Describe(X509Certificate2 cert, bool hasKey, string? source = null, DateTime? added = null)
    {
        yield return ("Subject", cert.Subject);
        yield return ("Issuer", cert.Issuer);
        yield return ("Role", RoleOf(cert) switch
                              {
                                  CertificateRole.RootCa         => "Root certificate authority (self-signed)",
                                  CertificateRole.IntermediateCa => "Intermediate certificate authority",
                                  _                              => "End entity"
                              });
        yield return ("Private key", hasKey ? "Held in this Silo" : "No");
        if (hasKey && IsCa(cert)) yield return ("May issue", CanIssue(cert) ? "Yes: certificates and signing requests" : "No: key usage lacks Key Cert Sign");

        yield return ("Not before", cert.NotBefore.ToString("yyyy-MM-dd HH:mm:ss"));
        yield return ("Not after", cert.NotAfter.ToString("yyyy-MM-dd HH:mm:ss"));
        yield return ("Validity", DateTime.Now < cert.NotBefore ? "Not yet valid" : DateTime.Now > cert.NotAfter ? "Expired" : "Valid now");
        yield return ("Serial number", cert.SerialNumber);
        yield return ("Public key", KeyDescription(cert));
        yield return ("Signature", cert.SignatureAlgorithm.FriendlyName ?? cert.SignatureAlgorithm.Value ?? "");
        if (KeyUsages(cert) is { Length: > 0 } ku) yield return ("Key usage", ku);
        if (ExtendedUsages(cert) is { Length: > 0 } eku) yield return ("Extended key usage", eku);
        if (BasicConstraints(cert) is { CertificateAuthority: true } bc)
            yield return ("Path length", bc.HasPathLengthConstraint ? bc.PathLengthConstraint.ToString() : "Unlimited");

        foreach (var name in SubjectAlternativeNames(cert)) yield return ("Alt name", name);

        yield return ("SHA-1", cert.Thumbprint);
        yield return ("SHA-256", ThumbprintSha256(cert));
        if (source is not null) yield return ("Source", source);
        if (added is { } when) yield return ("Added", when.ToLocalTime().ToString("g"));
    }

    // ---- text forms ------------------------------------------------------------------------------------------

    public static string ToPem(X509Certificate2 cert)
    {
        return cert.ExportCertificatePem();
    }

    public static string ToPem(IEnumerable<X509Certificate2> certs)
    {
        var text = new StringBuilder();
        foreach (var c in certs) text.AppendLine(c.ExportCertificatePem());
        return text.ToString();
    }
}
