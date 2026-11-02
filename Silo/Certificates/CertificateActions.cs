using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Serilog;
using Silo.Model;
using Silo.Providers.Crypto;
using Silo.Providers.Data;
using Silo.Ui;
using Silo.Ui.Windows;

namespace Silo.Certificates;

internal static class CertificateActionsHelpers
{
    public static bool TryCopy(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false; // another program has the clipboard open
        }
    }
}

/// <summary>
///     What the certificate commands do, from the Silo menu and from the Explorer's nodes: import certificates (from files, a Windows store, a
///     server, the clipboard), install keypairs (from a PFX, from PEM, generated), and with a keypair issue certificates, sign requests, sign
///     data and verify signatures. Each asks what it needs, does it, and says what happened.
/// </summary>
public static class CertificateActions
{
    private static readonly (string Label, KeyAlgorithm Algorithm)[] Algorithms =
    [
        ("ECDSA P-256 (recommended)", KeyAlgorithm.EcdsaP256), ("ECDSA P-384", KeyAlgorithm.EcdsaP384), ("ECDSA P-521", KeyAlgorithm.EcdsaP521),
        ("RSA 2048", KeyAlgorithm.Rsa2048), ("RSA 3072", KeyAlgorithm.Rsa3072), ("RSA 4096", KeyAlgorithm.Rsa4096)
    ];

    private static readonly (string Label, HashAlgorithmName Hash)[] Hashes =
        [("SHA-256", HashAlgorithmName.SHA256), ("SHA-384", HashAlgorithmName.SHA384), ("SHA-512", HashAlgorithmName.SHA512)];

    private const string CertificateFilter = "Certificates|*.cer;*.crt;*.pem;*.der;*.p7b;*.p7c;*.pfx;*.p12|PEM / DER (*.cer;*.crt;*.pem;*.der)|*.cer;*.crt;*.pem;*.der|PKCS #12 (*.pfx;*.p12)|*.pfx;*.p12|All files (*.*)|*.*";

    // ---- plumbing --------------------------------------------------------------------------------------------

    private static OpenSilo? Active => App.Instance?.ActiveSilo;

    /// <summary>The vault of a Silo; null (quietly) when its storage cannot hold certificates.</summary>
    public static CertificateVault? OpenVault(OpenSilo? silo)
    {
        if (silo?.Data is not IStoredStore stored) return null;

        var              secrets    = silo.Data as ISecretStore;
        ISiloEncryption? encryption = null;
        if (secrets is not null)
            try
            {
                encryption = silo.Encryption;
            }
            catch (NotSupportedException)
            {
                // a Silo whose storage cannot seal secrets: certificates only
            }

        return new CertificateVault(stored, secrets, encryption);
    }

    /// <summary>The vault of the Silo (the active one when none is given); says why and returns null when there is none to use.</summary>
    public static CertificateVault? VaultOf(OpenSilo? silo = null, bool needKeys = false)
    {
        silo ??= Active;
        if (OpenVault(silo) is not { } vault)
        {
            App.Alert("Open a Silo first: certificates and keys are kept in it.");
            return null;
        }

        if (needKeys && !vault.CanKeepKeys)
        {
            App.Alert("This Silo's storage cannot keep private keys, so a keypair cannot be installed in it.");
            return null;
        }

        return vault;
    }

    private static void Run(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{What} failed", what);
            App.Alert(ex, what + " failed.");
        }
    }

    private static async Task RunAsync(string what, Func<Task> action)
    {
        var previous = System.Windows.Input.Mouse.OverrideCursor;
        try
        {
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            await action();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{What} failed", what);
            App.Alert(ex, what + " failed.");
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = previous;
        }
    }

    private static string? AskPassword(string what, string reason)
    {
        var values = CertificateDialogs.Form("Password", $"{what}\n\n{reason}", [new FormField("pw", "Password", FieldKind.Password)], "OK");
        return values?["pw"];
    }

    private static string Plural(int n, string noun)
    {
        return n == 1 ? $"1 {noun}" : $"{n} {noun}s";
    }

    private static string Id(X509Certificate2 cert)
    {
        return CertificateInfo.ThumbprintSha256(cert).ToLowerInvariant();
    }

    private static void AddAll(CertificateVault vault, IEnumerable<X509Certificate2> certs, string source)
    {
        var list  = certs.ToList();
        var added = list.Count(c => vault.Add(c, source));
        var known = list.Count - added;
        App.Status(added == 0 ? $"{(list.Count == 1 ? "That certificate is" : "Those certificates are")} already in the Silo"
                              : $"Imported {Plural(added, "certificate")} into the Silo" + (known > 0 ? $" ({known} already there)" : ""));
    }

    // ---- Import X509 Certificate -----------------------------------------------------------------------------

    public static void ImportFromFile(OpenSilo? silo = null)
    {
        if (VaultOf(silo) is not { } vault) return;

        Run("Importing the certificate file", () =>
                                             {
                                                 if (Dialogs.OpenFiles("Import X509 Certificates", CertificateFilter) is not { Count: > 0 } files) return;

                                                 var all = new List<X509Certificate2>();
                                                 foreach (var path in files)
                                                 {
                                                     var certs = ReadCertificates(path);
                                                     if (certs is null) continue;

                                                     if (certs.Count == 0) App.Alert($"{Path.GetFileName(path)} holds no certificate.");
                                                     AddNew(vault, certs, $"File: {Path.GetFileName(path)}", all);
                                                 }

                                                 Summarize(all);
                                             });
    }

    /// <summary>The certificates of a file, asking for the password when it is a PFX that needs one; null when the person gave up.</summary>
    private static IReadOnlyList<X509Certificate2>? ReadCertificates(string path)
    {
        var     data = File.ReadAllBytes(path);
        string? pw   = null;
        while (true)
        {
            try
            {
                return CertificateImport.ParseBytes(data, pw);
            }
            catch (PasswordNeededException ex)
            {
                pw = AskPassword(Path.GetFileName(path), ex.Message);
                if (pw is null) return null;
            }
            catch (CryptographicException)
            {
                App.Alert($"{Path.GetFileName(path)} is not a certificate file Silo can read (CER, CRT, PEM, DER, P7B or PFX).");
                return null;
            }
        }
    }

    // the files of one import are added together: collect what was new, then say it once
    private static void AddNew(CertificateVault vault, IReadOnlyList<X509Certificate2> certs, string source, List<X509Certificate2> collected)
    {
        foreach (var c in certs)
            if (vault.Add(c, source))
                collected.Add(c);
    }

    private static void Summarize(IReadOnlyList<X509Certificate2> added)
    {
        App.Status(added.Count == 0 ? "Nothing new: the Silo already had those certificates" : $"Imported {Plural(added.Count, "certificate")} into the Silo");
    }

    public static void ImportFromStore(OpenSilo? silo = null)
    {
        if (VaultOf(silo) is not { } vault) return;

        Run("Importing from the certificate store", () =>
                                                   {
                                                       if (CertificateDialogs.PickFromStore() is not { } picked) return;

                                                       var (certs, store) = picked;

                                                       AddAll(vault, certs, "Windows store: " + store);
                                                   });
    }

    public static void ImportFromRemote(OpenSilo? silo = null)
    {
        if (VaultOf(silo) is not { } vault) return;

        var values = CertificateDialogs.Form("Import X509 Certificate from a Server",
                                             "Silo connects to the server, starts TLS and keeps the certificate it presents. The server is not trusted and nothing is sent.\n\n" +
                                             "TLS on connect only (https, ldaps, imaps...); servers that start in plain text and upgrade (STARTTLS) are not supported.",
                                             [
                                                 new FormField("target", "Host, host:port or URL", Hint: "For example example.com, example.com:8443, https://example.com/ or ldaps://dc.corp.local"),
                                                 new FormField("chain", "Also import the intermediate and root certificates the server sent", FieldKind.Check, "true")
                                             ], "Connect",
                                             v =>
                                             {
                                                 try
                                                 {
                                                     CertificateImport.ParseEndpoint(v["target"]);
                                                     return null;
                                                 }
                                                 catch (ArgumentException ex)
                                                 {
                                                     return ex.Message;
                                                 }
                                             });
        if (values is null) return;

        _ = RunAsync("Getting the certificate", async () =>
                                               {
                                                   var remote = await CertificateImport.FetchAsync(values["target"]);
                                                   var certs  = values["chain"] == "true" ? remote.Certificates : remote.Certificates.Take(1).ToList();
                                                   AddAll(vault, certs, $"Server: {remote.Host}:{remote.Port}");
                                                   App.Status($"{remote.Host}:{remote.Port} presented {Plural(remote.Certificates.Count, "certificate")} ({remote.Protocol}, {(remote.TrustedBySystem ? "trusted by this computer" : "not trusted by this computer")})");
                                               });
    }

    public static void ImportFromClipboard(OpenSilo? silo = null)
    {
        if (VaultOf(silo) is not { } vault) return;

        Run("Importing from the clipboard", () =>
                                           {
                                               var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
                                               IReadOnlyList<X509Certificate2> certs;
                                               try
                                               {
                                                   certs = CertificateImport.ParseText(text);
                                               }
                                               catch (Exception ex) when (ex is CryptographicException or FormatException)
                                               {
                                                   certs = [];
                                               }

                                               if (certs.Count == 0)
                                               {
                                                   App.Alert("The clipboard holds no certificate. Copy PEM text (-----BEGIN CERTIFICATE-----) or the base64 of a certificate.");
                                                   return;
                                               }

                                               AddAll(vault, certs, "Clipboard");
                                           });
    }

    // ---- Install Keypair -------------------------------------------------------------------------------------

    public static void InstallFromPfx(OpenSilo? silo = null)
    {
        if (VaultOf(silo, true) is not { } vault) return;

        Run("Installing the keypair", () =>
                                     {
                                         if (Dialogs.OpenFile("Install Keypair from a PFX File", "PKCS #12 (*.pfx;*.p12)|*.pfx;*.p12|All files (*.*)|*.*") is not { } path) return;

                                         var     data = File.ReadAllBytes(path);
                                         string? pw   = null;
                                         while (true)
                                         {
                                             try
                                             {
                                                 var pairs = KeyPair.FromPfx(data, pw, out var chain);
                                                 foreach (var pair in pairs) vault.AddKeyPair(pair, $"PFX: {Path.GetFileName(path)}");
                                                 foreach (var c in chain) vault.Add(c, $"PFX chain: {Path.GetFileName(path)}");

                                                 App.Status($"Installed {Plural(pairs.Count, "keypair")} from {Path.GetFileName(path)}" + (chain.Count > 0 ? $" and {Plural(chain.Count, "chain certificate")}" : ""));
                                                 return;
                                             }
                                             catch (PasswordNeededException ex)
                                             {
                                                 pw = AskPassword(Path.GetFileName(path), ex.Message);
                                                 if (pw is null) return;
                                             }
                                             catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
                                             {
                                                 App.Alert(ex is InvalidDataException ? ex.Message : $"{Path.GetFileName(path)} is not a PFX (PKCS #12) file.");
                                                 return;
                                             }
                                         }
                                     });
    }

    public static void InstallFromPem(OpenSilo? silo = null)
    {
        if (VaultOf(silo, true) is not { } vault) return;

        Run("Installing the keypair", () =>
                                     {
                                         if (Dialogs.OpenFile("Install Keypair from PEM: the certificate (or a file that holds the certificate and its key)",
                                                              "PEM (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|All files (*.*)|*.*") is not { } certPath) return;

                                         var certPem = File.ReadAllText(certPath);
                                         var keyPem  = (string?)null;
                                         if (!certPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
                                         {
                                             if (Dialogs.OpenFile("Choose the private key for " + Path.GetFileName(certPath), "Private key (*.key;*.pem)|*.key;*.pem|All files (*.*)|*.*") is not { } keyPath) return;

                                             keyPem = File.ReadAllText(keyPath);
                                         }

                                         string? pw = null;
                                         while (true)
                                         {
                                             try
                                             {
                                                 var pair  = KeyPair.FromPem(certPem, keyPem, pw);
                                                 var entry = vault.AddKeyPair(pair, $"PEM: {Path.GetFileName(certPath)}");
                                                 App.Status($"Installed the keypair of {entry.Name}");
                                                 return;
                                             }
                                             catch (PasswordNeededException ex)
                                             {
                                                 pw = AskPassword("The private key", ex.Message);
                                                 if (pw is null) return;
                                             }
                                             catch (Exception ex) when (ex is CryptographicException or InvalidDataException or FormatException)
                                             {
                                                 App.Alert(ex.Message);
                                                 return;
                                             }
                                         }
                                     });
    }

    /// <summary>Install Keypair &gt; Generate New...: a root certificate authority, or a self-signed certificate.</summary>
    public static void GenerateNew(OpenSilo? silo = null)
    {
        if (VaultOf(silo, true) is not { } vault) return;

        const string rootCa = "Root certificate authority (self-signed)", selfSigned = "Self-signed certificate";
        var values = CertificateDialogs.Form("Generate a New Keypair",
                                             "Makes a new private key and a self-signed certificate for it, and keeps both in the Silo.",
                                             [
                                                 new FormField("subject", "Subject", Initial: "CN=My Root CA", Hint: "A common name, or a distinguished name such as CN=My Root CA, O=My Company"),
                                                 new FormField("type", "Type", FieldKind.Choice, rootCa, [rootCa, selfSigned]),
                                                 new FormField("algorithm", "Key", FieldKind.Choice, Algorithms[0].Label, Algorithms.Select(a => a.Label).ToList()),
                                                 new FormField("days", "Valid for (days)", Initial: "3650"),
                                                 new FormField("path", "Path length (authority only)", Hint: "How many authorities may sit below this one. Leave empty for no limit; 0 means it can only sign end certificates."),
                                                 new FormField("names", "Alternative names (certificate only)", Hint: "DNS names and IP addresses, separated by commas"),
                                                 new FormField("server", "Server authentication (certificate only)", FieldKind.Check),
                                                 new FormField("client", "Client authentication (certificate only)", FieldKind.Check)
                                             ], "Generate", ValidateNew);
        if (values is null) return;

        var isCa    = values["type"] == rootCa;
        var options = ToOptions(values, isCa);
        _ = RunAsync("Generating the keypair", async () =>
                                              {
                                                  var pair  = await Task.Run(() => CertificateFactory.CreateSelfSigned(options));
                                                  var entry = vault.AddKeyPair(pair, isCa ? "Generated root CA" : "Generated");
                                                  App.Status($"Generated {(isCa ? "the root certificate authority" : "the keypair")} {entry.Name}");
                                              });
    }

    private static string? ValidateNew(Dictionary<string, string> v)
    {
        if (string.IsNullOrWhiteSpace(v["subject"])) return "The subject (a name) is needed.";
        if (!int.TryParse(v["days"], out var days) || days < 1 || days > 36500) return "Valid for: a number of days from 1 to 36500.";
        if (v.TryGetValue("path", out var path) && path.Trim().Length > 0 && (!int.TryParse(path, out var p) || p < 0)) return "Path length: empty, or a number from 0.";

        try
        {
            _ = new X500DistinguishedName(v["subject"].Contains('=') ? v["subject"] : "CN=" + v["subject"].Trim());
        }
        catch (CryptographicException)
        {
            return "That is not a distinguished name (try CN=Name, O=Company).";
        }

        return null;
    }

    private static NewCertificate ToOptions(Dictionary<string, string> v, bool isCa)
    {
        var path  = v.GetValueOrDefault("path", "").Trim();
        var names = v.GetValueOrDefault("names", "").Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new NewCertificate(v["subject"].Trim(), Algorithms.First(a => a.Label == v["algorithm"]).Algorithm, int.Parse(v["days"]), isCa ? null : names, isCa,
                                  isCa && path.Length > 0 ? int.Parse(path) : null, !isCa && v.GetValueOrDefault("server") == "true", !isCa && v.GetValueOrDefault("client") == "true",
                                  v.GetValueOrDefault("code") == "true");
    }

    // ---- With a certificate authority's keypair --------------------------------------------------------------

    /// <summary>Issues a new certificate (and its key) signed by the authority, and keeps the new keypair in the Silo.</summary>
    public static void IssueCertificate(VaultEntry authority, OpenSilo? silo = null)
    {
        if (VaultOf(silo, true) is not { } vault) return;

        var values = CertificateDialogs.Form($"Issue a Certificate from {authority.Name}",
                                             "Makes a new private key and a certificate for it signed by this authority. Both are kept in the Silo.",
                                             [
                                                 new FormField("subject", "Subject", Initial: "CN=", Hint: "A common name, or a distinguished name such as CN=www.example.com, O=Example"),
                                                 new FormField("algorithm", "Key", FieldKind.Choice, Algorithms[0].Label, Algorithms.Select(a => a.Label).ToList()),
                                                 new FormField("days", "Valid for (days)", Initial: "365", Hint: "Never longer than the authority itself lasts"),
                                                 new FormField("names", "Alternative names", Hint: "DNS names and IP addresses, separated by commas"),
                                                 new FormField("server", "Server authentication", FieldKind.Check, "true"),
                                                 new FormField("client", "Client authentication", FieldKind.Check),
                                                 new FormField("code", "Code signing", FieldKind.Check),
                                                 new FormField("ca", "Make it an intermediate certificate authority", FieldKind.Check),
                                                 new FormField("path", "Path length (authority only)", Hint: "Leave empty for no limit")
                                             ], "Issue", v => ValidateNew(v) ?? (v["subject"].Trim() is "CN=" ? "The subject (a name) is needed." : null));
        if (values is null) return;

        var isCa    = values["ca"] == "true";
        var options = ToOptions(values, isCa);
        if (isCa) options = options with { AltNames = null };

        _ = RunAsync("Issuing the certificate", async () =>
                                               {
                                                   using var issuer = vault.OpenKeyPair(authority.Id) ?? throw new InvalidOperationException($"The Silo holds no private key for {authority.Name}.");
                                                   var       pair   = await Task.Run(() => CertificateFactory.Issue(issuer, options));
                                                   var       entry  = vault.AddKeyPair(pair, $"Issued by {authority.Name}");
                                                   vault.RecordIssued(authority.Id, pair.Certificate);
                                                   App.Status($"Issued {entry.Name}, signed by {authority.Name}");
                                               });
    }

    /// <summary>Signs a certificate signing request (read from a file or the clipboard) with the authority and keeps the certificate in the Silo.</summary>
    public static void SignRequest(VaultEntry authority, OpenSilo? silo = null)
    {
        if (VaultOf(silo, true) is not { } vault) return;

        Run("Signing the request", () =>
                                  {
                                      var (choice, _) = PromptDialog.Show("Sign a Certificate Request", "Where is the certificate signing request (CSR)?", ["From _File...", "From _Clipboard", "Cancel"]);
                                      string csr;
                                      if (choice == 0)
                                      {
                                          if (Dialogs.OpenFile("Choose the certificate signing request", "Requests (*.csr;*.req;*.pem)|*.csr;*.req;*.pem|All files (*.*)|*.*") is not { } path) return;

                                          csr = File.ReadAllText(path);
                                      }
                                      else if (choice == 1)
                                      {
                                          csr = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
                                      }
                                      else
                                      {
                                          return;
                                      }

                                      RequestInfo request;
                                      try
                                      {
                                          request = CertificateFactory.ReadRequest(csr);
                                      }
                                      catch (Exception ex) when (ex is CryptographicException or ArgumentException)
                                      {
                                          App.Alert("That is not a valid certificate signing request (PEM text starting -----BEGIN CERTIFICATE REQUEST-----), or its signature does not check out.\n\n" + ex.Message);
                                          return;
                                      }

                                      var values = CertificateDialogs.Form($"Sign with {authority.Name}",
                                                                           $"The request is for:\n{request.Subject}\nPublic key: {request.PublicKey}" +
                                                                           (request.AltNames.Count > 0 ? "\nNames: " + string.Join(", ", request.AltNames) : "") +
                                                                           "\n\nThe certificate keeps the request's subject, key and names. It is an end certificate, whatever the request asked for.",
                                                                           [
                                                                               new FormField("days", "Valid for (days)", Initial: "365"),
                                                                               new FormField("server", "Server authentication (when the request names no usage)", FieldKind.Check, "true"),
                                                                               new FormField("client", "Client authentication (when the request names no usage)", FieldKind.Check)
                                                                           ], "Sign", v => int.TryParse(v["days"], out var d) && d is >= 1 and <= 36500 ? null : "Valid for: a number of days from 1 to 36500.");
                                      if (values is null) return;

                                      using var issuer = vault.OpenKeyPair(authority.Id) ?? throw new InvalidOperationException($"The Silo holds no private key for {authority.Name}.");
                                      var       cert   = CertificateFactory.SignRequest(issuer, csr, new SigningOptions(int.Parse(values["days"]), values["server"] == "true", values["client"] == "true"));
                                      vault.Add(cert, $"Signed request by {authority.Name}");
                                      vault.RecordIssued(authority.Id, cert);
                                      App.Status($"Signed the request for {CertificateInfo.DisplayName(cert)}");

                                      CertificateDialogs.Result("Signed Certificate", $"The certificate for {request.Subject}, signed by {authority.Name}. It is also kept in the Silo.",
                                                                CertificateInfo.ToPem([cert, authority.Certificate]).Trim(),
                                                                ("Save As...", text => SaveText(text, "Save the signed certificate", $"{CertificateInfo.DisplayName(cert)}.pem", "PEM (*.pem)|*.pem|Certificate (*.crt)|*.crt")));
                                  });
    }

    // ---- With any keypair: signing and verifying -------------------------------------------------------------

    private static byte[]? ChooseData(string title)
    {
        var (choice, _) = PromptDialog.Show(title, "What is the data?", ["_Text...", "_File...", "Cancel"]);
        switch (choice)
        {
            case 0:
            {
                var text = CertificateDialogs.Form(title, null, [new FormField("text", "Text (signed as UTF-8, exactly as typed)", FieldKind.Multiline)], "OK");
                return text is null ? null : Encoding.UTF8.GetBytes(text["text"]);
            }
            case 1:
                return Dialogs.OpenFile(title + ": the file", null) is { } path ? File.ReadAllBytes(path) : null;
            default:
                return null;
        }
    }

    private static List<string> HashLabels()
    {
        return Hashes.Select(h => h.Label).ToList();
    }

    private static SignatureScheme SchemeOf(Dictionary<string, string> v)
    {
        return new SignatureScheme(Hashes.First(h => h.Label == v["hash"]).Hash, v.GetValueOrDefault("pss") == "true");
    }

    public static void SignData(VaultEntry keypair, OpenSilo? silo = null)
    {
        if (VaultOf(silo) is not { } vault) return;

        Run("Signing", () =>
                      {
                          if (ChooseData($"Sign Data with {keypair.Name}") is not { } data) return;

                          var isRsa  = keypair.Certificate.GetRSAPublicKey() is not null;
                          var fields = new List<FormField> { new("hash", "Digest", FieldKind.Choice, Hashes[0].Label, HashLabels()) };
                          if (isRsa) fields.Add(new FormField("pss", "Use RSA-PSS padding (the default is PKCS #1 v1.5)", FieldKind.Check));

                          if (CertificateDialogs.Form($"Sign with {keypair.Name}", $"{data.Length:N0} byte(s) to sign.", fields, "Sign") is not { } values) return;

                          using var key       = vault.OpenKeyPair(keypair.Id) ?? throw new InvalidOperationException($"The Silo holds no private key for {keypair.Name}.");
                          var       scheme    = SchemeOf(values);
                          var       signature = DataSigning.Sign(key, data, scheme);
                          App.Status($"Signed {data.Length:N0} byte(s) with {keypair.Name}");

                          CertificateDialogs.Result("Signature", $"The {scheme} signature by {keypair.Name} (base64). Verify it with the certificate's public key.",
                                                    Convert.ToBase64String(signature),
                                                    ("Save Signature...", _ => SaveBytes(signature, "Save the signature", "data.sig", "Signature (*.sig)|*.sig|All files (*.*)|*.*")));
                      });
    }

    /// <summary>Verifies a signature against the data with a certificate's public key (the given certificate, or one the person chooses).</summary>
    public static void VerifySignature(VaultEntry? certificate = null, OpenSilo? silo = null)
    {
        if (VaultOf(silo) is not { } vault) return;

        Run("Verifying", () =>
                        {
                            var entry = certificate;
                            if (entry is null)
                            {
                                var all = vault.List().Where(e => e.Certificate.GetRSAPublicKey() is not null || e.Certificate.GetECDsaPublicKey() is not null).ToList();
                                if (all.Count == 0)
                                {
                                    App.Alert("The Silo holds no certificate to verify with. Import one first.");
                                    return;
                                }

                                var labels = all.ToDictionary(e => $"{e.Name}  [{e.Id[..8]}]", e => e);
                                if (CertificateDialogs.Form("Verify a Signature", "Which certificate's public key made the signature?",
                                                            [new FormField("cert", "Certificate", FieldKind.Choice, labels.Keys.First(), labels.Keys.ToList())], "Next") is not { } pick) return;

                                entry = labels[pick["cert"]];
                            }

                            if (ChooseData($"Verify a Signature by {entry.Name}") is not { } data) return;

                            var clip   = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText().Trim() : "";
                            var fields = new List<FormField>
                                         {
                                             new("signature", "Signature (base64 or hex)", FieldKind.Multiline, TryParses(clip) ? clip : ""),
                                             new("hash", "Digest", FieldKind.Choice, Hashes[0].Label, HashLabels())
                                         };
                            if (entry.Certificate.GetRSAPublicKey() is not null) fields.Add(new FormField("pss", "RSA-PSS padding (the default is PKCS #1 v1.5)", FieldKind.Check));

                            if (CertificateDialogs.Form($"Verify with {entry.Name}", $"{data.Length:N0} byte(s) of data.", fields, "Verify", v => TryParses(v["signature"]) ? null : "The signature is neither base64 nor hex.") is not { } values) return;

                            var ok = DataSigning.Verify(entry.Certificate, data, DataSigning.ParseSignature(values["signature"]), SchemeOf(values));
                            App.Status(ok ? $"The signature is valid ({entry.Name})" : "The signature is NOT valid");
                            App.Alert(ok ? $"The signature is valid: it was made over exactly this data with the private key of {entry.Name}."
                                         : $"The signature is NOT valid for this data and {entry.Name}. The data changed, a different key signed it, or the digest or padding differs.",
                                      "Verify a Signature");
                        });
    }

    private static bool TryParses(string signature)
    {
        try
        {
            DataSigning.ParseSignature(signature);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ---- looking at, copying, exporting, removing ------------------------------------------------------------

    public static void ShowDetails(VaultEntry entry)
    {
        var rows = CertificateInfo.Describe(entry.Certificate, entry.HasKey, entry.Source, entry.Added).ToList();
        var text = string.Join(Environment.NewLine, rows.Select(r => $"{r.Name,-20} {r.Value}"));
        CertificateDialogs.Result(entry.Name, CertificateInfo.KindFor(entry.Certificate, entry.HasKey), text, ("Copy PEM", _ => CopyPem(entry)));
    }

    public static void CopyPem(VaultEntry entry)
    {
        App.Status(CertificateActionsHelpers.TryCopy(CertificateInfo.ToPem(entry.Certificate)) ? $"Copied the PEM of {entry.Name}" : "The clipboard is in use by another program");
    }

    public static void ExportCertificate(VaultEntry entry)
    {
        Run("Exporting", () =>
                        {
                            if (Dialogs.SaveFile("Export Certificate", "PEM (*.pem)|*.pem|Certificate (*.crt)|*.crt|DER (*.cer)|*.cer", entry.Name + ".pem") is not { } path) return;

                            if (path.EndsWith(".cer", StringComparison.OrdinalIgnoreCase)) File.WriteAllBytes(path, entry.Certificate.RawData);
                            else File.WriteAllText(path, CertificateInfo.ToPem(entry.Certificate));

                            App.Status($"Exported {entry.Name} to {path}");
                        });
    }

    public static void ExportPfx(VaultEntry entry, OpenSilo? silo = null)
    {
        if (VaultOf(silo) is not { } vault) return;

        Run("Exporting", () =>
                        {
                            if (CertificateDialogs.Form("Export Keypair as PFX", $"The PFX holds {entry.Name} and its private key, protected by a password. Anyone with the file and the password can use the key.",
                                                        [new FormField("pw", "Password", FieldKind.Password), new FormField("again", "Password again", FieldKind.Password)], "Export",
                                                        v => v["pw"].Length == 0 ? "A password is needed." : v["pw"] != v["again"] ? "The passwords differ." : null) is not { } values) return;

                            var pair = vault.GetKeyPair(entry.Id) ?? throw new InvalidOperationException($"The Silo holds no private key for {entry.Name}.");
                            if (Dialogs.SaveFile("Export Keypair as PFX", "PKCS #12 (*.pfx)|*.pfx", entry.Name + ".pfx") is not { } path) return;

                            File.WriteAllBytes(path, pair.ExportPfx(values["pw"]));
                            App.Status($"Exported {entry.Name} with its key to {path}");
                        });
    }

    public static void ExportPemWithKey(VaultEntry entry, OpenSilo? silo = null)
    {
        if (VaultOf(silo) is not { } vault) return;

        Run("Exporting", () =>
                        {
                            if (CertificateDialogs.Form("Export Keypair as PEM", $"The file holds {entry.Name} and its private key. Give a password to encrypt the key, or leave it empty to write the key unprotected.",
                                                        [new FormField("pw", "Password (optional)", FieldKind.Password)], "Export") is not { } values) return;

                            var pair = vault.GetKeyPair(entry.Id) ?? throw new InvalidOperationException($"The Silo holds no private key for {entry.Name}.");
                            if (Dialogs.SaveFile("Export Keypair as PEM", "PEM (*.pem)|*.pem", entry.Name + ".pem") is not { } path) return;

                            File.WriteAllText(path, pair.ToPem(values["pw"]));
                            App.Status($"Exported {entry.Name} with its key to {path}");
                        });
    }

    public static void Remove(VaultEntry entry, OpenSilo? silo = null)
    {
        if (VaultOf(silo) is not { } vault) return;

        var detail = entry.HasKey ? "\n\nIts private key is kept in the Silo and will be deleted too. That cannot be undone." : "";
        if (PromptDialog.Show("Remove Certificate", $"Remove {entry.Name} from the Silo?{detail}", ["_Remove", "Cancel"]).Button != 0) return;

        Run("Removing", () => App.Status(vault.Remove(entry.Id) ? $"Removed {entry.Name}" : "It was already gone"));
    }

    // ---- replacing keys, and revoking what was issued --------------------------------------------------------

    /// <summary>
    ///     Replaces the key of a keypair: a new private key and a certificate that says what the old one said (subject, names, usages, authority
    ///     settings), self-signed or signed by the authority that issued the old one. The old private key is deleted unless the person keeps it.
    /// </summary>
    public static void ReplaceKey(VaultEntry entry, OpenSilo? silo = null)
    {
        if (VaultOf(silo, true) is not { } vault) return;

        Run("Replacing the key", () =>
                                {
                                    var issuerEntry = vault.IssuerOf(entry.Certificate);
                                    if (issuerEntry is null && !CertificateInfo.IsSelfSigned(entry.Certificate))
                                    {
                                        App.Alert($"{entry.Name} was issued by {entry.Certificate.Issuer}, and this Silo holds no private key for that authority, so it cannot sign a certificate for a new key.\n\n" +
                                                  "Ask the authority to sign a request for a new key and install the result with Install Keypair.");
                                        return;
                                    }

                                    var children = vault.Issued(entry.Id).Where(r => !r.Revoked).ToList();
                                    var asIssued = vault.FindIssued(entry.Id) is { Revoked: false } r ? r : null;
                                    var current  = CertificateFactory.AlgorithmOf(entry.Certificate);

                                    var fields = new List<FormField>
                                                 {
                                                     new("algorithm", "New key", FieldKind.Choice, Algorithms.First(a => a.Algorithm == current).Label, Algorithms.Select(a => a.Label).ToList()),
                                                     new("days", "Valid for (days)", Initial: Math.Max(1, (int)Math.Round((entry.Certificate.NotAfter - entry.Certificate.NotBefore).TotalDays)).ToString(),
                                                         Hint: issuerEntry is null ? null : "Never longer than the issuing authority lasts"),
                                                     new("delete", "Delete the old private key (the old certificate stays, without its key)", FieldKind.Check, "true")
                                                 };
                                    if (children.Count > 0) fields.Add(new FormField("revokeChildren", $"Revoke the {Plural(children.Count, "certificate")} issued with the old key (superseded)", FieldKind.Check));
                                    if (asIssued is not null) fields.Add(new FormField("revokeOld", "Revoke the old certificate at its issuer (superseded)", FieldKind.Check, "true"));

                                    var intro = $"Makes a new private key and a certificate with the same subject{(CertificateInfo.IsCa(entry.Certificate) ? " and authority settings" : "")} for {entry.Name}, " +
                                                (issuerEntry is null ? "self-signed." : $"signed by {issuerEntry.Name}.");
                                    if (children.Count > 0)
                                        intro += $"\n\nThe {Plural(children.Count, "certificate")} the old key issued will not chain to the new key: re-issue them from the new one.";

                                    if (CertificateDialogs.Form($"Replace the Key of {entry.Name}", intro, fields, "Replace",
                                                                v => int.TryParse(v["days"], out var d) && d is >= 1 and <= 36500 ? null : "Valid for: a number of days from 1 to 36500.") is not { } values) return;

                                    var algorithm = Algorithms.First(a => a.Label == values["algorithm"]).Algorithm;
                                    var days      = int.Parse(values["days"]);

                                    _ = RunAsync("Replacing the key", async () =>
                                                                      {
                                                                          using var issuer = issuerEntry is null ? null : vault.OpenKeyPair(issuerEntry.Id);
                                                                          var       pair   = await Task.Run(() => CertificateFactory.Rekey(entry.Certificate, issuer, algorithm, days));
                                                                          var       fresh  = vault.AddKeyPair(pair, $"Replaced the key of {entry.Name}");
                                                                          vault.SetReplaces(fresh.Id, entry.Id);
                                                                          if (issuerEntry is not null) vault.RecordIssued(issuerEntry.Id, pair.Certificate);

                                                                          var revoked = 0;
                                                                          if (values.GetValueOrDefault("revokeOld") == "true" && asIssued is not null &&
                                                                              vault.Revoke(asIssued.IssuerId, asIssued.Serial, X509RevocationReason.Superseded) is not null)
                                                                              revoked++;

                                                                          if (values.GetValueOrDefault("revokeChildren") == "true")
                                                                              foreach (var c in children)
                                                                                  if (vault.Revoke(entry.Id, c.Serial, X509RevocationReason.Superseded) is not null)
                                                                                      revoked++;

                                                                          if (values["delete"] == "true") vault.RemoveKey(entry.Id);

                                                                          App.Status($"Replaced the key of {entry.Name}" + (revoked > 0 ? $"; revoked {Plural(revoked, "certificate")} (publish an updated CRL)" : ""));
                                                                      });
                                });
    }

    /// <summary>Revokes a certificate an authority of this Silo issued: it is marked revoked, and goes into the CRL the authority signs.</summary>
    public static void RevokeIssued(IssuedRecord record, OpenSilo? silo = null)
    {
        if (VaultOf(silo) is not { } vault) return;

        Run("Revoking", () =>
                       {
                           if (record.Revoked)
                           {
                               App.Alert($"{record.Name} is already revoked ({record.Reason}).");
                               return;
                           }

                           var authority = vault.Find(record.IssuerId);
                           if (CertificateDialogs.Form($"Revoke {record.Name}",
                                                       $"Serial {record.Serial}, issued {record.IssuedUtc.ToLocalTime():g}, valid until {record.NotAfter.ToLocalTime():d}.\n\n" +
                                                       "The certificate is marked revoked here and goes into the revocation list (CRL) its authority signs. Nothing is sent anywhere: " +
                                                       "others learn of it only when you publish an updated CRL. Revoking cannot be undone.",
                                                       [new FormField("reason", "Reason", FieldKind.Choice, RevocationLists.Reasons[0].Label, RevocationLists.Reasons.Select(x => x.Label).ToList())], "Revoke") is not { } values) return;

                           var reason = RevocationLists.Reasons.First(x => x.Label == values["reason"]).Reason;
                           vault.Revoke(record.IssuerId, record.Serial, reason);
                           App.Status($"Revoked {record.Name} ({RevocationLists.Describe(reason)})");

                           if (authority is { HasKey: true } &&
                               PromptDialog.Show("Revoked", $"{record.Name} is revoked. Export an updated revocation list (CRL) from {authority.Name} now?", ["Export _CRL...", "_Later"]).Button == 0)
                               ExportCrl(authority, silo);
                       });
    }

    /// <summary>Signs and saves the authority's revocation list (CRL), holding every certificate it issued that was revoked.</summary>
    public static void ExportCrl(VaultEntry authority, OpenSilo? silo = null)
    {
        if (VaultOf(silo) is not { } vault) return;

        Run("Exporting the revocation list", () =>
                                            {
                                                var revoked = vault.Issued(authority.Id).Count(r => r.Revoked);
                                                if (CertificateDialogs.Form($"Export the CRL of {authority.Name}", $"The list names {Plural(revoked, "revoked certificate")}. Publish it where the certificates' users can fetch it.",
                                                                            [new FormField("days", "Next update in (days)", Initial: "30", Hint: "How long this list may be used before a fresher one is expected")], "Export",
                                                                            v => int.TryParse(v["days"], out var d) && d is >= 1 and <= 3650 ? null : "A number of days from 1 to 3650.") is not { } values) return;

                                                if (Dialogs.SaveFile("Export Revocation List", "CRL, DER (*.crl)|*.crl|CRL, PEM (*.pem)|*.pem", authority.Name + ".crl") is not { } path) return;

                                                using var key = vault.OpenKeyPair(authority.Id) ?? throw new InvalidOperationException($"The Silo holds no private key for {authority.Name}.");
                                                var       der = vault.BuildCrl(authority.Id, key, int.Parse(values["days"]));
                                                if (path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)) File.WriteAllText(path, PemEncoding.WriteString("X509 CRL", der));
                                                else File.WriteAllBytes(path, der);

                                                App.Status($"Exported the revocation list of {authority.Name} ({Plural(revoked, "revoked certificate")}) to {path}");
                                            });
    }

    public static void ShowIssued(IssuedRecord record, OpenSilo? silo = null)
    {
        var text = string.Join(Environment.NewLine, IssuedDetails(record).Select(r => $"{r.Name,-16} {r.Value}"));
        CertificateDialogs.Result(record.Name, "A certificate issued from this Silo", text);
    }

    /// <summary>The rows that describe an issued certificate's record: its serial, dates and whether it is revoked.</summary>
    public static IEnumerable<(string Name, string Value)> IssuedDetails(IssuedRecord record)
    {
        yield return ("Subject", record.Subject);
        yield return ("Status", record.Status);
        yield return ("Serial number", record.Serial);
        yield return ("Issued", record.IssuedUtc.ToLocalTime().ToString("g"));
        yield return ("Not before", record.NotBefore.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        yield return ("Not after", record.NotAfter.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        if (!record.Revoked) yield break;

        yield return ("Revoked", record.RevokedUtc?.ToLocalTime().ToString("g") ?? "");
        yield return ("Reason", Enum.TryParse<X509RevocationReason>(record.Reason, out var reason) ? RevocationLists.Describe(reason) : record.Reason ?? "");
    }

    public static void RemoveKey(VaultEntry entry, OpenSilo? silo = null)
    {
        if (VaultOf(silo) is not { } vault) return;

        if (PromptDialog.Show("Remove Private Key", $"Delete the private key of {entry.Name} from the Silo? The certificate stays. That cannot be undone unless you have exported the keypair.", ["_Delete Key", "Cancel"]).Button != 0) return;

        Run("Removing", () => App.Status(vault.RemoveKey(entry.Id) ? $"Deleted the private key of {entry.Name}" : "There was no private key"));
    }

    private static void SaveText(string text, string title, string name, string filter)
    {
        Run("Saving", () =>
                     {
                         if (Dialogs.SaveFile(title, filter, name) is not { } path) return;

                         File.WriteAllText(path, text);
                         App.Status("Saved " + path);
                     });
    }

    private static void SaveBytes(byte[] data, string title, string name, string filter)
    {
        Run("Saving", () =>
                     {
                         if (Dialogs.SaveFile(title, filter, name) is not { } path) return;

                         File.WriteAllBytes(path, data);
                         App.Status("Saved " + path);
                     });
    }
}
