using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Silo.Certificates;

/// <summary>
///     A certificate together with its private key (as unencrypted PKCS #8; whatever holds a <see cref="KeyPair" /> is responsible for
///     keeping it secret -- the Silo seals it with its own encryption). <see cref="Certificate" /> is the public part only.
/// </summary>
public sealed record KeyPair(X509Certificate2 Certificate, byte[] Pkcs8)
{
    /// <summary>The pair held by a certificate that has an exportable private key.</summary>
    /// <exception cref="CryptographicException">It has no private key, or one that cannot be exported.</exception>
    public static KeyPair FromCertificate(X509Certificate2 withKey)
    {
        using AsymmetricAlgorithm key = (AsymmetricAlgorithm?)withKey.GetRSAPrivateKey() ?? (AsymmetricAlgorithm?)withKey.GetECDsaPrivateKey() ??
                                        withKey.GetDSAPrivateKey() ?? throw new CryptographicException("The certificate has no private key.");

        return new KeyPair(X509CertificateLoader.LoadCertificate(withKey.RawData), key.ExportPkcs8PrivateKey());
    }

    /// <summary>The certificate with its private key attached, ready to sign with.</summary>
    public X509Certificate2 Open()
    {
        if (Certificate.GetRSAPublicKey() is { } rsaPublic)
        {
            rsaPublic.Dispose();
            var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(Pkcs8, out _);
            return Certificate.CopyWithPrivateKey(rsa);
        }

        if (Certificate.GetECDsaPublicKey() is { } ecPublic)
        {
            ecPublic.Dispose();
            var ec = ECDsa.Create();
            ec.ImportPkcs8PrivateKey(Pkcs8, out _);
            return Certificate.CopyWithPrivateKey(ec);
        }

        if (Certificate.GetDSAPublicKey() is { } dsaPublic)
        {
            dsaPublic.Dispose();
            var dsa = DSA.Create();
            dsa.ImportPkcs8PrivateKey(Pkcs8, out _);
            return Certificate.CopyWithPrivateKey(dsa);
        }

        throw new CryptographicException($"The key algorithm of {CertificateInfo.DisplayName(Certificate)} is not supported.");
    }

    // ---- reading ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     The keypairs of a PFX (PKCS #12) file: each certificate in it that comes with a private key. The rest of its certificates (the chain)
    ///     are returned in <paramref name="chain" />.
    /// </summary>
    /// <exception cref="PasswordNeededException">The password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">The file holds no private key.</exception>
    public static IReadOnlyList<KeyPair> FromPfx(byte[] data, string? password, out IReadOnlyList<X509Certificate2> chain)
    {
        var all   = CertificateImport.LoadPkcs12(data, password, X509KeyStorageFlags.EphemeralKeySet);
        var pairs = all.Cast<X509Certificate2>().Where(c => c.HasPrivateKey).Select(FromCertificate).ToList();
        if (pairs.Count == 0) throw new InvalidDataException("The file holds certificates but no private key.");

        chain = all.Cast<X509Certificate2>().Where(c => !c.HasPrivateKey).Select(c => X509CertificateLoader.LoadCertificate(c.RawData)).ToList();
        return pairs;
    }

    /// <summary>
    ///     The keypair of PEM text: a certificate and its private key, either both in <paramref name="certificatePem" /> or the key in
    ///     <paramref name="keyPem" />. Reads RSA, EC and PKCS #8 keys (encrypted PKCS #8 with <paramref name="password" />).
    /// </summary>
    /// <exception cref="PasswordNeededException">The key is encrypted and the password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">There is no certificate or no key, or they do not belong together.</exception>
    public static KeyPair FromPem(string certificatePem, string? keyPem, string? password)
    {
        var certs = CertificateImport.ParseText(certificatePem);
        if (certs.Count == 0) throw new InvalidDataException("No certificate was found (a PEM certificate starts with -----BEGIN CERTIFICATE-----).");

        var pkcs8 = ReadPrivateKey(keyPem ?? certificatePem, password) ?? throw new InvalidDataException(keyPem is null
                        ? "The file holds a certificate but no private key; choose the key file too."
                        : "No private key was found (a PEM key starts with -----BEGIN PRIVATE KEY-----).");

        // the certificate of the pair is the one whose public key the private key makes
        var spki = PublicKeyOf(pkcs8);
        var cert = certs.FirstOrDefault(c => c.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki)) ??
                   throw new InvalidDataException("The private key does not belong to the certificate (their public keys differ).");

        return new KeyPair(cert, pkcs8);
    }

    /// <summary>The unencrypted PKCS #8 of the first private key in PEM text; null when there is none.</summary>
    public static byte[]? ReadPrivateKey(string pem, string? password)
    {
        var span = pem.AsSpan();
        while (PemEncoding.TryFind(span, out var fields))
        {
            var label = span[fields.Label].ToString();
            if (label.EndsWith("PRIVATE KEY", StringComparison.Ordinal))
                return ToPkcs8(label, Convert.FromBase64String(span[fields.Base64Data].ToString()), password);

            span = span[fields.Location.End..];
        }

        return null;
    }

    private static byte[] ToPkcs8(string label, byte[] der, string? password)
    {
        switch (label)
        {
            case "PRIVATE KEY":
                PublicKeyOf(der); // reads as a key of a kind we know, or throws
                return der;
            case "RSA PRIVATE KEY":
            {
                using var rsa = RSA.Create();
                rsa.ImportRSAPrivateKey(der, out _);
                return rsa.ExportPkcs8PrivateKey();
            }
            case "EC PRIVATE KEY":
            {
                using var ec = ECDsa.Create();
                ec.ImportECPrivateKey(der, out _);
                return ec.ExportPkcs8PrivateKey();
            }
            case "ENCRYPTED PRIVATE KEY":
            {
                if (string.IsNullOrEmpty(password)) throw new PasswordNeededException("The private key is encrypted: it needs its password.");

                try
                {
                    using var rsa = RSA.Create();
                    rsa.ImportEncryptedPkcs8PrivateKey(password, der, out _);
                    return rsa.ExportPkcs8PrivateKey();
                }
                catch (CryptographicException)
                {
                    // perhaps not an RSA key (or the wrong password: the next one says so)
                }

                try
                {
                    using var ec = ECDsa.Create();
                    ec.ImportEncryptedPkcs8PrivateKey(password, der, out _);
                    return ec.ExportPkcs8PrivateKey();
                }
                catch (CryptographicException)
                {
                    throw new PasswordNeededException("That password does not open the private key.");
                }
            }
            default:
                throw new InvalidDataException($"A \"{label}\" is not supported; convert it to PKCS #8 (openssl pkcs8 -topk8).");
        }
    }

    /// <summary>The SubjectPublicKeyInfo of the public half of a PKCS #8 private key.</summary>
    public static byte[] PublicKeyOf(byte[] pkcs8)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return rsa.ExportSubjectPublicKeyInfo();
        }
        catch (CryptographicException)
        {
        }

        try
        {
            using var ec = ECDsa.Create();
            ec.ImportPkcs8PrivateKey(pkcs8, out _);
            return ec.ExportSubjectPublicKeyInfo();
        }
        catch (CryptographicException)
        {
        }

        try
        {
            using var dsa = DSA.Create();
            dsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return dsa.ExportSubjectPublicKeyInfo();
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("The private key is not an RSA, EC or DSA key.");
        }
    }

    // ---- writing ---------------------------------------------------------------------------------------------

    /// <summary>The PKCS #12 (PFX) file for the pair, the chain after it, protected by <paramref name="password" />.</summary>
    public byte[] ExportPfx(string password, IEnumerable<X509Certificate2>? chain = null)
    {
        using var keyed = Open();
        var       all   = new X509Certificate2Collection();
        all.Add(keyed);
        foreach (var c in chain ?? []) all.Add(c);

        return all.Export(X509ContentType.Pfx, password) ?? throw new CryptographicException("The PFX could not be made.");
    }

    /// <summary>The private key as PEM (<c>BEGIN PRIVATE KEY</c>, or <c>BEGIN ENCRYPTED PRIVATE KEY</c> with a password).</summary>
    public string PrivateKeyPem(string? password = null)
    {
        if (string.IsNullOrEmpty(password)) return PemEncoding.WriteString("PRIVATE KEY", Pkcs8);

        var parameters = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 200_000);
        using var rsa  = TryRsa(Pkcs8);
        if (rsa is not null) return PemEncoding.WriteString("ENCRYPTED PRIVATE KEY", rsa.ExportEncryptedPkcs8PrivateKey(password, parameters));

        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(Pkcs8, out _);
        return PemEncoding.WriteString("ENCRYPTED PRIVATE KEY", ec.ExportEncryptedPkcs8PrivateKey(password, parameters));
    }

    private static RSA? TryRsa(byte[] pkcs8)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return rsa;
        }
        catch (CryptographicException)
        {
            rsa.Dispose();
            return null;
        }
    }

    /// <summary>Certificate and private key in one PEM text (the "PEM + key" file).</summary>
    public string ToPem(string? keyPassword = null)
    {
        return new StringBuilder().AppendLine(Certificate.ExportCertificatePem()).Append(PrivateKeyPem(keyPassword)).ToString();
    }
}
