using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Silo.Certificates;

/// <summary>An issued certificate the Silo remembers: who it was for and whether its issuer has revoked it.</summary>
/// <param name="Serial">The serial number in hex, as the certificate has it.</param>
/// <param name="Thumbprint">SHA-256 of the certificate, lower case: the id it has in the vault when the Silo keeps it.</param>
/// <param name="Reason">When revoked, the name of the <see cref="X509RevocationReason" />.</param>
public sealed record IssuedRecord(string Serial, string Subject, string Thumbprint, DateTime NotBefore, DateTime NotAfter, DateTime IssuedUtc,
                                  bool   Revoked = false, DateTime? RevokedUtc = null, string? Reason = null)
{
    /// <summary>The vault id of the authority that issued it (not stored in the record: it is in the key).</summary>
    public string IssuerId { get; init; } = "";

    public string Name => Subject.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? Subject[3..].Split(',')[0].Trim() : Subject;

    public bool IsExpired => DateTime.UtcNow > NotAfter.ToUniversalTime();

    public string Status => Revoked ? "Revoked" : IsExpired ? "Expired" : "Valid";
}

/// <summary>Certificate revocation lists (CRLs): what an authority publishes so others can tell which of its certificates are no longer to be trusted.</summary>
public static class RevocationLists
{
    /// <summary>The reasons a person can give, in the order they are offered.</summary>
    public static readonly (string Label, X509RevocationReason Reason)[] Reasons =
    [
        ("Unspecified", X509RevocationReason.Unspecified), ("Key compromise", X509RevocationReason.KeyCompromise), ("CA compromise", X509RevocationReason.CACompromise),
        ("Affiliation changed", X509RevocationReason.AffiliationChanged), ("Superseded (replaced by another)", X509RevocationReason.Superseded),
        ("Cessation of operation", X509RevocationReason.CessationOfOperation)
    ];

    public static string Describe(X509RevocationReason reason)
    {
        return Reasons.FirstOrDefault(r => r.Reason == reason).Label ?? reason.ToString();
    }

    /// <summary>The serial number as the hex string records keep.</summary>
    public static string SerialOf(X509Certificate2 cert)
    {
        return Convert.ToHexString(cert.SerialNumberBytes.Span);
    }

    /// <summary>
    ///     The CRL (DER) the authority signs for its revoked certificates, numbered <paramref name="number" />, to be fetched again before
    ///     <paramref name="validDays" /> days are up.
    /// </summary>
    /// <param name="issuer">The authority's certificate with its private key.</param>
    public static byte[] Build(X509Certificate2 issuer, IEnumerable<IssuedRecord> revoked, BigInteger number, int validDays = 30)
    {
        CertificateFactory.RequireIssuer(issuer);

        var builder = new CertificateRevocationListBuilder();
        foreach (var r in revoked.Where(r => r.Revoked))
            builder.AddEntry(Convert.FromHexString(r.Serial), new DateTimeOffset(DateTime.SpecifyKind(r.RevokedUtc ?? DateTime.UtcNow, DateTimeKind.Utc)),
                             Enum.TryParse<X509RevocationReason>(r.Reason, out var reason) ? reason : X509RevocationReason.Unspecified);

        var now = DateTimeOffset.UtcNow;
        return builder.Build(issuer, number, now.AddDays(validDays), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, now);
    }
}
