using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Silo.Certificates;

/// <summary>How a signature is made over the data: the digest, and for RSA the padding.</summary>
public sealed record SignatureScheme(HashAlgorithmName Hash, bool RsaPss = false)
{
    public static readonly SignatureScheme Sha256 = new(HashAlgorithmName.SHA256);
    public static readonly SignatureScheme Sha384 = new(HashAlgorithmName.SHA384);
    public static readonly SignatureScheme Sha512 = new(HashAlgorithmName.SHA512);

    public override string ToString()
    {
        return Hash.Name + (RsaPss ? " (RSA-PSS)" : "");
    }
}

/// <summary>
///     Signs data with a private key and verifies a signature with the public key. RSA signatures are PKCS #1 v1.5 (or PSS); ECDSA signatures
///     are the DER form OpenSSL and most tools use.
/// </summary>
public static class DataSigning
{
    /// <summary>The signature of <paramref name="data" />.</summary>
    /// <exception cref="CryptographicException">The certificate has no private key, or one that cannot sign.</exception>
    public static byte[] Sign(X509Certificate2 keyed, byte[] data, SignatureScheme scheme)
    {
        if (keyed.GetRSAPrivateKey() is { } rsa)
        {
            using (rsa) return rsa.SignData(data, scheme.Hash, Padding(scheme));
        }

        if (keyed.GetECDsaPrivateKey() is { } ec)
        {
            using (ec) return ec.SignData(data, scheme.Hash, DSASignatureFormat.Rfc3279DerSequence);
        }

        throw new CryptographicException($"{CertificateInfo.DisplayName(keyed)} has no private key that can sign (RSA and ECDSA keys can).");
    }

    /// <summary>True when <paramref name="signature" /> is the signature of <paramref name="data" /> by the private half of the certificate's public key.</summary>
    public static bool Verify(X509Certificate2 cert, byte[] data, byte[] signature, SignatureScheme scheme)
    {
        try
        {
            if (cert.GetRSAPublicKey() is { } rsa)
            {
                using (rsa) return rsa.VerifyData(data, signature, scheme.Hash, Padding(scheme));
            }

            if (cert.GetECDsaPublicKey() is { } ec)
            {
                using (ec) return ec.VerifyData(data, signature, scheme.Hash, DSASignatureFormat.Rfc3279DerSequence);
            }
        }
        catch (CryptographicException)
        {
            return false; // a signature that is not even the right shape
        }

        throw new CryptographicException($"{CertificateInfo.DisplayName(cert)} has a public key that cannot verify (RSA and ECDSA keys can).");
    }

    private static RSASignaturePadding Padding(SignatureScheme scheme)
    {
        return scheme.RsaPss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
    }

    /// <summary>A signature typed or pasted as base64 or hex, as bytes.</summary>
    /// <exception cref="FormatException">It is neither.</exception>
    public static byte[] ParseSignature(string text)
    {
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length == 0) throw new FormatException("There is no signature.");

        if (compact.Length % 2 == 0 && compact.All(Uri.IsHexDigit)) return Convert.FromHexString(compact);

        try
        {
            return Convert.FromBase64String(compact);
        }
        catch (FormatException)
        {
            throw new FormatException("The signature is neither base64 nor hex.");
        }
    }
}
