using Silo.Commanding;
using static Silo.Certificates.CertificateCommands;
using Silo.Contracts.Core;
using Silo.Design;
using Silo.Model;
using Silo.ViewModel;

namespace Silo.Certificates;

internal static class CertificateCommands
{
    public static ProvidedCommand Cmd(string id, string header, Action run, string? icon = null, bool isDefault = false, bool isDestructive = false, string? group = null)
    {
        return new ProvidedCommand(id, header, new DelegateCommand(_ => run()), null, icon, null, group, CommandPlacement.Menu, IsDefault: isDefault, IsDestructive: isDestructive);
    }
}

/// <summary>
///     A certificate or keypair as the Explorer shows it (under Security, Certificates): its icon and kind (a keypair is told apart from a
///     public certificate by both), its properties, and the commands that make sense for it -- an authority's keypair can issue, sign requests and
///     publish a revocation list; any keypair can sign and have its key replaced; any certificate can verify.
/// </summary>
public sealed class CertificateNodeModel(VaultEntry entry, OpenSilo silo) : INodeDetails, ICommandProvider, INodeActivation, IRealmed
{
    public VaultEntry Entry => entry;

    public IconRef? Icon => IconRef.FromKey(IsRevoked() ? CertificateInfo.RevokedIcon : CertificateInfo.IconFor(entry.Certificate, entry.HasKey));
    public string   Name     => entry.Name;
    public string   TypeName => CertificateInfo.KindFor(entry.Certificate, entry.HasKey);
    public Uri      Uri      => new($"silo:///active/#security/certificates/{entry.Id}", UriKind.Absolute);
    public Realm    Realm    => Realm.Global;

    public void Activate() => CertificateActions.ShowDetails(entry);

    // a certificate issued by an authority of this Silo and since revoked
    private bool IsRevoked()
    {
        return CertificateActions.OpenVault(silo)?.FindIssued(entry.Id) is { Revoked: true };
    }

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        foreach (var row in CertificateInfo.Describe(entry.Certificate, entry.HasKey, entry.Source, entry.Added)) yield return row;

        var vault = CertificateActions.OpenVault(silo);
        if (vault is null) yield break;

        if (vault.FindIssued(entry.Id) is { } issued)
        {
            yield return ("Issued by", vault.Find(issued.IssuerId)?.Name ?? entry.Certificate.Issuer);
            yield return ("Revocation", issued.Revoked ? $"Revoked {issued.RevokedUtc?.ToLocalTime():g} ({issued.Reason})" : "Not revoked");
        }

        if (vault.Replaces(entry.Id) is { } old) yield return ("Replaced key of", vault.Find(old)?.Name ?? old[..Math.Min(8, old.Length)]);

        if (entry.HasKey && CertificateInfo.CanIssue(entry.Certificate))
        {
            var records = vault.Issued(entry.Id);
            yield return ("Issued here", $"{records.Count} certificate(s), {records.Count(r => r.Revoked)} revoked");
        }
    }

    public IEnumerable<ProvidedCommand> GetCommands()
    {
        yield return Cmd("cert.details", "_Details...", () => CertificateActions.ShowDetails(entry), CertificateInfo.CertificateIcon, true);
        yield return Cmd("cert.copy", "_Copy PEM", () => CertificateActions.CopyPem(entry), "TbCopy");
        yield return Cmd("cert.export", "_Export certificate...", () => CertificateActions.ExportCertificate(entry), "TbExport");

        if (entry.HasKey)
        {
            yield return Cmd("cert.pfx", "Export keypair as _PFX...", () => CertificateActions.ExportPfx(entry, silo), "TbExport", group: "Keypair");
            yield return Cmd("cert.pem", "Export keypair as PE_M + key...", () => CertificateActions.ExportPemWithKey(entry, silo), "TbExport", group: "Keypair");
            yield return Cmd("cert.rekey", "Replace the _key...", () => CertificateActions.ReplaceKey(entry, silo), CertificateInfo.KeypairIcon, group: "Keypair");

            if (CertificateInfo.CanIssue(entry.Certificate))
            {
                yield return Cmd("cert.issue", "_Issue certificate...", () => CertificateActions.IssueCertificate(entry, silo), CertificateInfo.IssuedIcon, group: "Authority");
                yield return Cmd("cert.csr", "Sign certificate _request (CSR)...", () => CertificateActions.SignRequest(entry, silo), CertificateInfo.RequestIcon, group: "Authority");
                yield return Cmd("cert.crl", "Export revocation _list (CRL)...", () => CertificateActions.ExportCrl(entry, silo), CertificateInfo.RevokedIcon, group: "Authority");
            }

            yield return Cmd("cert.sign", "_Sign data...", () => CertificateActions.SignData(entry, silo), CertificateInfo.SignatureIcon, group: "Signatures");
        }

        yield return Cmd("cert.verify", "_Verify signature...", () => CertificateActions.VerifySignature(entry, silo), CertificateInfo.VerifiedIcon, group: "Signatures");

        if (CertificateActions.OpenVault(silo)?.FindIssued(entry.Id) is { Revoked: false } record)
            yield return Cmd("cert.revoke", "Re_voke (issued by this Silo)...", () => CertificateActions.RevokeIssued(record, silo), CertificateInfo.RevokedIcon, group: "Remove");

        if (entry.HasKey) yield return Cmd("cert.delkey", "Delete private _key...", () => CertificateActions.RemoveKey(entry, silo), isDestructive: true, group: "Remove");
        yield return Cmd("cert.remove", "_Remove...", () => CertificateActions.Remove(entry, silo), "TbDelete", isDestructive: true, group: "Remove");
    }
}

/// <summary>A certificate an authority of this Silo issued, as the Explorer shows it under that authority: its status, and the commands to revoke it.</summary>
public sealed class IssuedCertificateModel(IssuedRecord record, VaultEntry? held, OpenSilo silo) : INodeDetails, ICommandProvider, INodeActivation, IRealmed
{
    public IconRef? Icon     => IconRef.FromKey(record.Revoked ? CertificateInfo.RevokedIcon : CertificateInfo.CertificateIcon);
    public string   Name     => record.Name;
    public string   TypeName => record.Revoked ? "Revoked Certificate" : record.IsExpired ? "Expired Issued Certificate" : "Issued Certificate";
    public Uri      Uri      => new($"silo:///active/#security/certificates/{record.IssuerId}/issued/{record.Serial}", UriKind.Absolute);
    public Realm    Realm    => Realm.Global;

    public void Activate() => CertificateActions.ShowIssued(record, silo);

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        foreach (var row in CertificateActions.IssuedDetails(record)) yield return row;

        yield return ("In this Silo", held is null ? "No (only the record of it)" : held.HasKey ? "Yes, with its private key" : "Yes");
    }

    public IEnumerable<ProvidedCommand> GetCommands()
    {
        yield return Cmd("issued.details", "_Details...", () => CertificateActions.ShowIssued(record, silo), CertificateInfo.CertificateIcon, true);
        if (held is not null) yield return Cmd("issued.cert", "_Certificate details...", () => CertificateActions.ShowDetails(held), CertificateInfo.CertificateIcon);

        if (!record.Revoked) yield return Cmd("issued.revoke", "Re_voke...", () => CertificateActions.RevokeIssued(record, silo), CertificateInfo.RevokedIcon, isDestructive: true);
    }
}

/// <summary>What a Certificates folder holds, which decides the commands it offers.</summary>
public enum CertificateFolderKind
{
    /// <summary>Security, Certificates: everything.</summary>
    All,

    /// <summary>Keypairs: what installs one.</summary>
    Keypairs,

    /// <summary>Public Certificates: what imports one.</summary>
    Public,

    /// <summary>The certificates an authority issued.</summary>
    Issued
}

/// <summary>A Certificates folder: what is kept in it, and the commands that add to it (each folder offers its own: import for certificates, install for keypairs).</summary>
public sealed class CertificateFolderModel(OpenSilo silo, CertificateFolderKind kind, int keypairs, int certificates, bool canKeepKeys, VaultEntry? authority = null)
    : INodeDetails, ICommandProvider, IRealmed
{
    public IconRef? Icon => IconRef.FromKey(kind switch
                                            {
                                                CertificateFolderKind.Keypairs => CertificateInfo.KeypairIcon,
                                                CertificateFolderKind.Public   => CertificateInfo.CertificateIcon,
                                                CertificateFolderKind.Issued   => CertificateInfo.IssuedIcon,
                                                _                              => CertificateInfo.StoreIcon
                                            });

    public string Name => kind switch
                          {
                              CertificateFolderKind.Keypairs => "Keypairs",
                              CertificateFolderKind.Public   => "Public Certificates",
                              CertificateFolderKind.Issued   => "Issued",
                              _                              => "Certificates"
                          };

    public string TypeName => "Folder";

    public Uri Uri => new("silo:///active/#security/certificates" + kind switch
                                                                  {
                                                                      CertificateFolderKind.Keypairs => "/keypairs",
                                                                      CertificateFolderKind.Public   => "/public",
                                                                      CertificateFolderKind.Issued   => $"/{authority?.Id}/issued",
                                                                      _                              => ""
                                                                  }, UriKind.Absolute);

    public Realm Realm => Realm.Global;

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        switch (kind)
        {
            case CertificateFolderKind.Keypairs:
                yield return ("Contains", "Certificates with their private keys: they can sign, and an authority's can issue certificates");
                yield return ("Keypairs", keypairs.ToString());
                yield return ("Private keys", canKeepKeys ? "Sealed with the Silo's encryption" : "This Silo cannot keep private keys");
                break;
            case CertificateFolderKind.Public:
                yield return ("Contains", "Certificates without a private key here: they can verify signatures and chains");
                yield return ("Public certificates", certificates.ToString());
                break;
            case CertificateFolderKind.Issued:
                yield return ("Contains", $"The certificates {authority?.Name} issued, with whether each is revoked");
                yield return ("Issued", certificates.ToString());
                break;
            default:
                yield return ("Contains", "Keypairs (a certificate with its private key) and public certificates, kept in this Silo");
                yield return ("Keypairs", keypairs.ToString());
                yield return ("Public certificates", certificates.ToString());
                yield return ("Private keys", canKeepKeys ? "Sealed with the Silo's encryption" : "This Silo cannot keep private keys");
                break;
        }
    }

    public IEnumerable<ProvidedCommand> GetCommands()
    {
        if (kind is CertificateFolderKind.All or CertificateFolderKind.Public)
        {
            yield return Cmd("certs.file", "Import from _file...", () => CertificateActions.ImportFromFile(silo), "TbOpen", group: "Import X509 Certificate");
            yield return Cmd("certs.store", "Import from certificate _store...", () => CertificateActions.ImportFromStore(silo), CertificateInfo.StoreIcon, group: "Import X509 Certificate");
            yield return Cmd("certs.remote", "Import from _remote URL / host...", () => CertificateActions.ImportFromRemote(silo), "Network", group: "Import X509 Certificate");
            yield return Cmd("certs.clipboard", "Import from _clipboard", () => CertificateActions.ImportFromClipboard(silo), "TbPaste", group: "Import X509 Certificate");
        }

        if (kind is CertificateFolderKind.All or CertificateFolderKind.Keypairs)
        {
            yield return Cmd("certs.pfx", "Install keypair from _PFX file...", () => CertificateActions.InstallFromPfx(silo), "TbOpen", group: "Install Keypair");
            yield return Cmd("certs.pem", "Install keypair from PE_M file...", () => CertificateActions.InstallFromPem(silo), "Document", group: "Install Keypair");
            yield return Cmd("certs.generate", "_Generate new keypair...", () => CertificateActions.GenerateNew(silo), "Plus", group: "Install Keypair");
        }

        if (kind == CertificateFolderKind.Issued && authority is not null)
        {
            yield return Cmd("certs.issue", "_Issue certificate...", () => CertificateActions.IssueCertificate(authority, silo), CertificateInfo.IssuedIcon);
            yield return Cmd("certs.crl", "Export revocation _list (CRL)...", () => CertificateActions.ExportCrl(authority, silo), CertificateInfo.RevokedIcon);
        }

        if (kind != CertificateFolderKind.Issued)
            yield return Cmd("certs.verify", "_Verify a signature...", () => CertificateActions.VerifySignature(null, silo), CertificateInfo.VerifiedIcon, group: "Signatures");
    }
}
