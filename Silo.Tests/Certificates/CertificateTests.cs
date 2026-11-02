using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Silo.Certificates;
using Silo.Providers.Crypto;
using Silo.Providers.Data;

namespace Silo.Tests.Certificates;

public class CertificateTests
{
    // ---- fakes -----------------------------------------------------------------------------------------------

    private sealed class MemoryStore : IStoredStore, ISecretStore
    {
        public  readonly Dictionary<string, StoredRecord> Items   = new(StringComparer.OrdinalIgnoreCase);
        public  readonly List<SecretRecord>               Secrets = [];
        private int                                       _next   = 1;

        public StoredRecord? GetStored(string key, StoredScope scope = StoredScope.Personal, StoredTier tier = StoredTier.Permanent) => Items.GetValueOrDefault(key);

        public IReadOnlyList<StoredRecord> ListStored(string? keyPrefix = null, StoredScope scope = StoredScope.Personal, StoredTier tier = StoredTier.Permanent)
            => Items.Values.Where(r => keyPrefix is null || r.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Key).ToList();

        public StoredRecord SetStored(string key, string typeName, string format, string data, StoredScope scope = StoredScope.Personal, StoredTier tier = StoredTier.Permanent)
            => Items[key] = new StoredRecord(key, typeName, format, data, DateTime.UtcNow, DateTime.UtcNow, scope);

        public bool DeleteStored(string key, StoredScope scope = StoredScope.Personal, StoredTier tier = StoredTier.Permanent) => Items.Remove(key);

        public IReadOnlyList<SecretRecord> GetSecrets(string? kind = null) => Secrets.Where(s => kind is null || s.Kind == kind).ToList();

        public SecretRecord? GetSecret(int id) => Secrets.FirstOrDefault(s => s.Id == id);

        public SecretRecord SaveSecret(SecretRecord secret)
        {
            var saved = secret.Id == 0 ? secret with { Id = _next++ } : secret;
            Secrets.RemoveAll(s => s.Id == saved.Id);
            Secrets.Add(saved);
            return saved;
        }

        public bool DeleteSecret(int id) => Secrets.RemoveAll(s => s.Id == id) > 0;
    }

    /// <summary>"Encryption" that only reverses the bytes: enough to prove the key is never stored as it is.</summary>
    private sealed class ReversingEncryption : ISiloEncryption
    {
        public string ProviderId => "test";

        public byte[] Encrypt(byte[] plaintext, string context) => plaintext.Reverse().ToArray();

        public byte[] Decrypt(byte[] sealedData, string context) => sealedData.Reverse().ToArray();

        public int ChangeProvider(string providerId) => 0;
    }

    private static CertificateVault NewVault(out MemoryStore store)
    {
        store = new MemoryStore();
        return new CertificateVault(store, store, new ReversingEncryption());
    }

    private static KeyPair Root(KeyAlgorithm algorithm = KeyAlgorithm.EcdsaP256)
    {
        return CertificateFactory.CreateSelfSigned(new NewCertificate("CN=Test Root CA, O=Silo", algorithm, 3650, IsCa: true));
    }

    // ---- generating ------------------------------------------------------------------------------------------

    [Test]
    public void A_self_signed_root_authority_may_issue_and_is_a_root()
    {
        var root = Root();

        Assert.That(CertificateInfo.RoleOf(root.Certificate),    Is.EqualTo(CertificateRole.RootCa));
        Assert.That(CertificateInfo.CanIssue(root.Certificate),  Is.True);
        Assert.That(root.Certificate.Subject,                    Is.EqualTo("CN=Test Root CA, O=Silo"));
        Assert.That(CertificateInfo.IconFor(root.Certificate, false), Is.EqualTo(CertificateInfo.RootCaIcon));
        Assert.That(CertificateInfo.IconFor(root.Certificate, true),  Is.EqualTo(CertificateInfo.AuthorityKeypairIcon));
    }

    [Test]
    public void A_leaf_issued_by_the_authority_chains_to_it_and_keeps_its_names()
    {
        using var ca   = Root(KeyAlgorithm.Rsa2048).Open();
        var       leaf = CertificateFactory.Issue(ca, new NewCertificate("CN=www.example.test", KeyAlgorithm.EcdsaP256, 90, ["www.example.test", "10.1.2.3"], ServerAuth: true));

        Assert.That(CertificateInfo.RoleOf(leaf.Certificate), Is.EqualTo(CertificateRole.Leaf));
        Assert.That(leaf.Certificate.Issuer,                  Is.EqualTo(ca.Subject));
        Assert.That(CertificateInfo.SubjectAlternativeNames(leaf.Certificate), Has.Some.Contain("www.example.test"));
        Assert.That(CertificateInfo.SubjectAlternativeNames(leaf.Certificate), Has.Some.Contain("10.1.2.3"));
        Assert.That(CertificateInfo.ExtendedUsages(leaf.Certificate),          Does.Contain("Server Authentication"));

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode      = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(ca.RawData));
        Assert.That(chain.Build(leaf.Certificate), Is.True, string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
    }

    [Test]
    public void An_ec_authority_can_issue_an_rsa_certificate()
    {
        using var ca   = Root(KeyAlgorithm.EcdsaP384).Open();
        var       leaf = CertificateFactory.Issue(ca, new NewCertificate("CN=rsa leaf", KeyAlgorithm.Rsa2048, 30));

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode      = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(ca.RawData));
        Assert.That(chain.Build(leaf.Certificate), Is.True, string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
    }

    [Test]
    public void A_certificate_that_is_not_an_authority_cannot_issue()
    {
        var plain = CertificateFactory.CreateSelfSigned(new NewCertificate("CN=Not a CA"));
        using var opened = plain.Open();

        Assert.That(CertificateInfo.CanIssue(plain.Certificate), Is.False);
        Assert.Throws<InvalidOperationException>(() => CertificateFactory.Issue(opened, new NewCertificate("CN=x")));
        Assert.Throws<InvalidOperationException>(() => CertificateFactory.RequireIssuer(plain.Certificate)); // and a public-only certificate has no key to sign with
    }

    [Test]
    public void An_issued_certificate_never_outlives_its_authority()
    {
        var       shortCa = CertificateFactory.CreateSelfSigned(new NewCertificate("CN=Short CA", ValidDays: 10, IsCa: true));
        using var ca      = shortCa.Open();

        var leaf = CertificateFactory.Issue(ca, new NewCertificate("CN=leaf", ValidDays: 3650));

        Assert.That(leaf.Certificate.NotAfter, Is.LessThanOrEqualTo(ca.NotAfter.AddSeconds(1)));
    }

    // ---- signing requests ------------------------------------------------------------------------------------

    [Test]
    public void A_signing_request_is_signed_keeping_its_subject_key_and_names()
    {
        using var ca  = Root().Open();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var       san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("api.example.test");
        var request = new CertificateRequest("CN=api.example.test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(san.Build());
        // a request that asks to be an authority is not granted it
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var pem = request.CreateSigningRequestPem();

        var info = CertificateFactory.ReadRequest(pem);
        Assert.That(info.Subject,  Is.EqualTo("CN=api.example.test"));
        Assert.That(info.AltNames, Has.Some.Contain("api.example.test"));

        var cert = CertificateFactory.SignRequest(ca, pem, new SigningOptions(30, ServerAuth: true));

        Assert.That(cert.Subject,                                       Is.EqualTo("CN=api.example.test"));
        Assert.That(cert.Issuer,                                        Is.EqualTo(ca.Subject));
        Assert.That(CertificateInfo.IsCa(cert),                         Is.False);
        Assert.That(cert.PublicKey.ExportSubjectPublicKeyInfo(),        Is.EqualTo(key.ExportSubjectPublicKeyInfo()));
        Assert.That(CertificateInfo.SubjectAlternativeNames(cert),      Has.Some.Contain("api.example.test"));
        Assert.That(CertificateInfo.ExtendedUsages(cert),               Does.Contain("Server Authentication"));
    }

    [Test]
    public void A_request_whose_own_signature_is_broken_is_refused()
    {
        using var key = RSA.Create(2048);
        var       pem = new CertificateRequest("CN=x", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        // damage the signature: the last base64 line before the footer
        var lines = pem.Split('\n').ToList();
        var i     = lines.FindLastIndex(l => l.Length > 8 && !l.StartsWith("-----"));
        lines[i] = lines[i].Substring(0, 4) + (lines[i][4] == 'A' ? "B" : "A") + lines[i].Substring(5);

        Assert.Throws<CryptographicException>(() => CertificateFactory.ReadRequest(string.Join('\n', lines)));
    }

    // ---- signing data ----------------------------------------------------------------------------------------

    [TestCase(KeyAlgorithm.Rsa2048, false)]
    [TestCase(KeyAlgorithm.Rsa2048, true)]
    [TestCase(KeyAlgorithm.EcdsaP256, false)]
    [TestCase(KeyAlgorithm.EcdsaP384, false)]
    public void Data_signed_with_the_private_key_verifies_with_the_public_one_and_only_that_data(KeyAlgorithm algorithm, bool pss)
    {
        var       pair   = Root(algorithm);
        using var keyed  = pair.Open();
        var       scheme = new SignatureScheme(HashAlgorithmName.SHA256, pss);
        var       data   = Encoding.UTF8.GetBytes("hello, silo");

        var signature = DataSigning.Sign(keyed, data, scheme);

        Assert.That(DataSigning.Verify(pair.Certificate, data, signature, scheme), Is.True);
        Assert.That(DataSigning.Verify(pair.Certificate, Encoding.UTF8.GetBytes("hello, silo!"), signature, scheme), Is.False);
        Assert.That(DataSigning.Verify(pair.Certificate, data, signature.Reverse().ToArray(), scheme), Is.False);
    }

    [Test]
    public void A_signature_is_read_from_base64_or_hex()
    {
        var bytes = new byte[] { 1, 2, 250, 251 };

        Assert.That(DataSigning.ParseSignature(Convert.ToBase64String(bytes)),          Is.EqualTo(bytes));
        Assert.That(DataSigning.ParseSignature(Convert.ToHexString(bytes).ToLowerInvariant()), Is.EqualTo(bytes));
        Assert.Throws<FormatException>(() => DataSigning.ParseSignature("not a signature!"));
    }

    // ---- reading and writing ---------------------------------------------------------------------------------

    [Test]
    public void Pem_text_yields_every_certificate_and_bare_base64_one()
    {
        var a = Root().Certificate;
        var b = Root(KeyAlgorithm.Rsa2048).Certificate;

        var two = CertificateImport.ParseText(CertificateInfo.ToPem([a, b]));
        Assert.That(two.Select(c => c.Thumbprint), Is.EqualTo(new[] { a.Thumbprint, b.Thumbprint }));

        var bare = CertificateImport.ParseText(Convert.ToBase64String(a.RawData));
        Assert.That(bare.Single().Thumbprint, Is.EqualTo(a.Thumbprint));

        Assert.That(CertificateImport.ParseText("just some words"), Is.Empty);
    }

    [Test]
    public void Files_of_every_format_give_their_certificates()
    {
        var pair = Root();

        Assert.That(CertificateImport.ParseBytes(pair.Certificate.RawData).Single().Thumbprint, Is.EqualTo(pair.Certificate.Thumbprint)); // DER (.cer, .crt)
        Assert.That(CertificateImport.ParseBytes(Encoding.UTF8.GetBytes(CertificateInfo.ToPem(pair.Certificate))).Single().Thumbprint, Is.EqualTo(pair.Certificate.Thumbprint));

        var pfx = pair.ExportPfx("pw");
        Assert.Throws<PasswordNeededException>(() => CertificateImport.ParseBytes(pfx, "wrong"));
        Assert.That(CertificateImport.ParseBytes(pfx, "pw").Single().Thumbprint, Is.EqualTo(pair.Certificate.Thumbprint));
    }

    [Test]
    public void A_pfx_gives_its_keypair_which_signs_like_the_original()
    {
        var pair = Root(KeyAlgorithm.Rsa2048);
        var pfx  = pair.ExportPfx("secret");

        Assert.Throws<PasswordNeededException>(() => KeyPair.FromPfx(pfx, null, out _));
        var read = KeyPair.FromPfx(pfx, "secret", out var chain).Single();

        Assert.That(read.Certificate.Thumbprint, Is.EqualTo(pair.Certificate.Thumbprint));
        Assert.That(chain, Is.Empty);
        using var keyed = read.Open();
        Assert.That(DataSigning.Verify(pair.Certificate, [1, 2, 3], DataSigning.Sign(keyed, [1, 2, 3], SignatureScheme.Sha256), SignatureScheme.Sha256), Is.True);
    }

    [TestCase(KeyAlgorithm.Rsa2048)]
    [TestCase(KeyAlgorithm.EcdsaP256)]
    public void A_keypair_round_trips_through_pem_with_the_key_in_the_same_file_or_apart(KeyAlgorithm algorithm)
    {
        var pair = Root(algorithm);

        var together = KeyPair.FromPem(pair.ToPem(), null, null);
        var apart    = KeyPair.FromPem(CertificateInfo.ToPem(pair.Certificate), pair.PrivateKeyPem(), null);
        var locked   = KeyPair.FromPem(CertificateInfo.ToPem(pair.Certificate), pair.PrivateKeyPem("pw"), "pw");

        foreach (var read in new[] { together, apart, locked })
            Assert.That(KeyPair.PublicKeyOf(read.Pkcs8), Is.EqualTo(KeyPair.PublicKeyOf(pair.Pkcs8)));

        Assert.Throws<PasswordNeededException>(() => KeyPair.FromPem(CertificateInfo.ToPem(pair.Certificate), pair.PrivateKeyPem("pw"), null));
        Assert.Throws<PasswordNeededException>(() => KeyPair.FromPem(CertificateInfo.ToPem(pair.Certificate), pair.PrivateKeyPem("pw"), "nope"));
    }

    [Test]
    public void A_key_that_does_not_belong_to_the_certificate_is_refused_and_a_certificate_alone_is_not_a_keypair()
    {
        var one = Root();
        var two = Root();

        Assert.Throws<InvalidDataException>(() => KeyPair.FromPem(CertificateInfo.ToPem(one.Certificate), two.PrivateKeyPem(), null));
        Assert.Throws<InvalidDataException>(() => KeyPair.FromPem(CertificateInfo.ToPem(one.Certificate), null, null));
    }

    [Test]
    public void Traditional_rsa_and_ec_private_keys_are_read_as_pkcs8()
    {
        using var rsa = RSA.Create(2048);
        using var ec  = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var fromRsa = KeyPair.ReadPrivateKey(PemEncoding.WriteString("RSA PRIVATE KEY", rsa.ExportRSAPrivateKey()), null);
        var fromEc  = KeyPair.ReadPrivateKey(PemEncoding.WriteString("EC PRIVATE KEY", ec.ExportECPrivateKey()), null);

        Assert.That(KeyPair.PublicKeyOf(fromRsa!), Is.EqualTo(rsa.ExportSubjectPublicKeyInfo()));
        Assert.That(KeyPair.PublicKeyOf(fromEc!),  Is.EqualTo(ec.ExportSubjectPublicKeyInfo()));
    }

    // ---- the vault -------------------------------------------------------------------------------------------

    [Test]
    public void The_vault_keeps_certificates_once_and_lists_them_by_name()
    {
        var vault = NewVault(out _);
        var a     = Root().Certificate;

        Assert.That(vault.Add(a, "test"), Is.True);
        Assert.That(vault.Add(a, "again"), Is.False);

        var entry = vault.List().Single();
        Assert.That(entry.Name,   Is.EqualTo("Test Root CA"));
        Assert.That(entry.Source, Is.EqualTo("test"));
        Assert.That(entry.HasKey, Is.False);
        Assert.That(vault.Remove(entry.Id), Is.True);
        Assert.That(vault.List(),           Is.Empty);
    }

    [Test]
    public void A_keypair_is_sealed_in_the_vault_and_comes_back_able_to_sign()
    {
        var vault = NewVault(out var store);
        var pair  = Root(KeyAlgorithm.Rsa2048);

        var entry = vault.AddKeyPair(pair, "generated");

        Assert.That(entry.HasKey, Is.True);
        var sealedKey = store.Secrets.Single().Data;
        Assert.That(Encoding.UTF8.GetString(sealedKey.Reverse().ToArray()), Does.Contain(Convert.ToBase64String(pair.Pkcs8)));
        Assert.That(Convert.ToBase64String(sealedKey),                       Does.Not.Contain(Convert.ToBase64String(pair.Pkcs8)));
        Assert.That(store.Items.Values.Select(r => r.Data),                  Has.None.Contain(Convert.ToBase64String(pair.Pkcs8)));

        using var keyed = vault.OpenKeyPair(entry.Id)!;
        Assert.That(DataSigning.Verify(pair.Certificate, [9], DataSigning.Sign(keyed, [9], SignatureScheme.Sha256), SignatureScheme.Sha256), Is.True);

        Assert.That(vault.RemoveKey(entry.Id),         Is.True);
        Assert.That(vault.Find(entry.Id)!.HasKey,      Is.False);
        Assert.That(vault.OpenKeyPair(entry.Id),       Is.Null);
    }

    [Test]
    public void Adding_a_keypair_for_a_certificate_already_held_adds_the_key_and_replacing_it_keeps_one_row()
    {
        var vault = NewVault(out var store);
        var pair  = Root();
        vault.Add(pair.Certificate, "public");

        vault.AddKeyPair(pair, "pfx");
        vault.AddKeyPair(pair, "pfx");

        Assert.That(vault.List().Single().HasKey, Is.True);
        Assert.That(store.Secrets,                Has.Count.EqualTo(1));
    }

    [Test]
    public void A_vault_whose_silo_cannot_keep_secrets_still_keeps_certificates_but_refuses_keys()
    {
        var store = new MemoryStore();
        var vault = new CertificateVault(store, null, null);

        Assert.That(vault.CanKeepKeys, Is.False);
        Assert.That(vault.Add(Root().Certificate, "x"), Is.True);
        Assert.Throws<NotSupportedException>(() => vault.AddKeyPair(Root(), "x"));
    }

    // ---- issued certificates and revocation ------------------------------------------------------------------

    [Test]
    public void Issued_certificates_are_tracked_per_authority_and_revoking_is_remembered()
    {
        var vault = NewVault(out _);
        var ca    = vault.AddKeyPair(Root(), "ca");
        using var key = vault.OpenKeyPair(ca.Id)!;
        var one = CertificateFactory.Issue(key, new NewCertificate("CN=one"));
        var two = CertificateFactory.Issue(key, new NewCertificate("CN=two"));
        vault.AddKeyPair(one, "issued");

        vault.RecordIssued(ca.Id, one.Certificate);
        vault.RecordIssued(ca.Id, two.Certificate);
        vault.RecordIssued(ca.Id, two.Certificate); // twice: still one record

        Assert.That(vault.Issued(ca.Id).Select(r => r.Name), Is.EquivalentTo(new[] { "one", "two" }));
        Assert.That(vault.Issued("someone-else"),            Is.Empty);

        var record = vault.FindIssued(CertificateInfo.ThumbprintSha256(one.Certificate))!;
        Assert.That(record.Status, Is.EqualTo("Valid"));

        var revoked = vault.Revoke(record.IssuerId, record.Serial, X509RevocationReason.KeyCompromise)!;
        Assert.That(revoked.Revoked, Is.True);
        Assert.That(revoked.Reason,  Is.EqualTo("KeyCompromise"));
        Assert.That(vault.Issued(ca.Id).Single(r => r.Name == "two").Revoked, Is.False);
        Assert.That(vault.Revoke(ca.Id, "00FF", X509RevocationReason.Unspecified), Is.Null);
    }

    [Test]
    public void The_revocation_list_names_exactly_the_revoked_certificates_and_counts_up()
    {
        var vault = NewVault(out _);
        var ca    = vault.AddKeyPair(Root(KeyAlgorithm.Rsa2048), "ca");
        using var key = vault.OpenKeyPair(ca.Id)!;
        var one = CertificateFactory.Issue(key, new NewCertificate("CN=one"));
        var two = CertificateFactory.Issue(key, new NewCertificate("CN=two"));
        vault.RecordIssued(ca.Id, one.Certificate);
        vault.RecordIssued(ca.Id, two.Certificate);
        vault.Revoke(ca.Id, RevocationLists.SerialOf(one.Certificate), X509RevocationReason.Superseded);

        var first  = vault.BuildCrl(ca.Id, key);
        var second = vault.BuildCrl(ca.Id, key);

        var list = CertificateRevocationListBuilder.Load(first, out var number);
        Assert.That(number, Is.EqualTo(new System.Numerics.BigInteger(1)));
        Assert.That(list.RemoveEntry(one.Certificate.SerialNumberBytes.Span), Is.True);
        Assert.That(list.RemoveEntry(two.Certificate.SerialNumberBytes.Span), Is.False);
        CertificateRevocationListBuilder.Load(second, out var next);
        Assert.That(next, Is.EqualTo(new System.Numerics.BigInteger(2)));
    }

    [Test]
    public void A_certificate_that_cannot_issue_cannot_sign_a_revocation_list()
    {
        var plain = CertificateFactory.CreateSelfSigned(new NewCertificate("CN=leaf"));
        using var opened = plain.Open();

        Assert.Throws<InvalidOperationException>(() => RevocationLists.Build(opened, [], 1));
    }

    // ---- replacing keys --------------------------------------------------------------------------------------

    [Test]
    public void A_replaced_root_keeps_its_subject_and_authority_settings_with_a_new_key()
    {
        var old = CertificateFactory.CreateSelfSigned(new NewCertificate("CN=Old Root, O=Silo", KeyAlgorithm.Rsa2048, 400, IsCa: true, PathLength: 1));

        var fresh = CertificateFactory.Rekey(old.Certificate, null, KeyAlgorithm.EcdsaP384);

        Assert.That(fresh.Certificate.Subject,                              Is.EqualTo(old.Certificate.Subject));
        Assert.That(CertificateInfo.RoleOf(fresh.Certificate),              Is.EqualTo(CertificateRole.RootCa));
        Assert.That(CertificateInfo.BasicConstraints(fresh.Certificate)!.PathLengthConstraint, Is.EqualTo(1));
        Assert.That(fresh.Certificate.PublicKey.ExportSubjectPublicKeyInfo(), Is.Not.EqualTo(old.Certificate.PublicKey.ExportSubjectPublicKeyInfo()));
        Assert.That(CertificateFactory.AlgorithmOf(fresh.Certificate),      Is.EqualTo(KeyAlgorithm.EcdsaP384));
        Assert.That((fresh.Certificate.NotAfter - fresh.Certificate.NotBefore).TotalDays, Is.EqualTo(400).Within(1));
    }

    [Test]
    public void A_replaced_leaf_keeps_its_names_and_usages_and_is_signed_by_its_authority()
    {
        var       vault = NewVault(out _);
        var       ca    = vault.AddKeyPair(Root(), "ca");
        using var key   = vault.OpenKeyPair(ca.Id)!;
        var       old   = CertificateFactory.Issue(key, new NewCertificate("CN=www.example.test", AltNames: ["www.example.test", "10.9.8.7"], ServerAuth: true, ClientAuth: true));
        vault.AddKeyPair(old, "leaf");

        Assert.That(vault.IssuerOf(old.Certificate)!.Id, Is.EqualTo(ca.Id));

        var fresh = CertificateFactory.Rekey(old.Certificate, key);

        Assert.That(fresh.Certificate.Issuer,                               Is.EqualTo(old.Certificate.Issuer));
        Assert.That(CertificateInfo.SubjectAlternativeNames(fresh.Certificate), Has.Some.Contain("www.example.test"));
        Assert.That(CertificateInfo.SubjectAlternativeNames(fresh.Certificate), Has.Some.Contain("10.9.8.7"));
        Assert.That(CertificateInfo.ExtendedUsages(fresh.Certificate),      Does.Contain("Server Authentication").And.Contain("Client Authentication"));
        Assert.That(CertificateInfo.IsCa(fresh.Certificate),                Is.False);
    }

    [Test]
    public void The_issuer_of_a_certificate_whose_authority_is_not_held_is_unknown_and_a_root_has_none()
    {
        var vault = NewVault(out _);
        var root  = vault.AddKeyPair(Root(), "root");
        var other = CertificateFactory.CreateSelfSigned(new NewCertificate("CN=Other CA", IsCa: true));
        using var otherKey = other.Open();
        var leaf = CertificateFactory.Issue(otherKey, new NewCertificate("CN=leaf"));

        Assert.That(vault.IssuerOf(leaf.Certificate), Is.Null);
        Assert.That(vault.IssuerOf(root.Certificate), Is.Null);
    }

    [Test]
    public void A_replacement_remembers_which_keypair_it_replaced()
    {
        var vault = NewVault(out _);
        var old   = vault.AddKeyPair(Root(), "old");
        var fresh = vault.AddKeyPair(Root(), "new");

        vault.SetReplaces(fresh.Id, old.Id);

        Assert.That(vault.Replaces(fresh.Id), Is.EqualTo(old.Id));
        Assert.That(vault.Replaces(old.Id),   Is.Null);
    }

    // ---- a server --------------------------------------------------------------------------------------------

    [TestCase("example.com",                  "example.com", 443)]
    [TestCase("example.com:8443",             "example.com", 8443)]
    [TestCase("https://example.com/a/b?c=d",  "example.com", 443)]
    [TestCase("https://example.com:9443/x",   "example.com", 9443)]
    [TestCase("ldaps://dc.corp.local",        "dc.corp.local", 636)]
    [TestCase("[::1]:8443",                   "::1",         8443)]
    [TestCase("10.0.0.5",                     "10.0.0.5",    443)]
    public void A_host_port_or_url_names_an_endpoint(string input, string host, int port)
    {
        Assert.That(CertificateImport.ParseEndpoint(input), Is.EqualTo((host, port)));
    }

    [Test]
    public void An_empty_endpoint_is_refused()
    {
        Assert.Throws<ArgumentException>(() => CertificateImport.ParseEndpoint("   "));
    }

    [Test]
    public async Task The_certificate_a_server_presents_is_fetched_even_when_it_is_not_trusted()
    {
        var       server   = CertificateFactory.CreateSelfSigned(new NewCertificate("CN=localhost", AltNames: ["localhost", "127.0.0.1"], ServerAuth: true));
        // Schannel needs a certificate whose key it can use: round-trip through PFX
        using var tlsCert  = X509CertificateLoader.LoadPkcs12(server.ExportPfx("p"), "p");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var accept = Task.Run(async () =>
                              {
                                  using var client = await listener.AcceptTcpClientAsync();
                                  await using var tls = new SslStream(client.GetStream());
                                  await tls.AuthenticateAsServerAsync(tlsCert);
                              });

        var chain = await CertificateImport.FetchAsync($"https://127.0.0.1:{port}/");
        try
        {
            await accept;
        }
        catch (Exception)
        {
            // the client hangs up as soon as it has the certificate
        }

        Assert.That(chain.Certificates.First().Thumbprint, Is.EqualTo(server.Certificate.Thumbprint));
        Assert.That(chain.TrustedBySystem,                 Is.False);
        Assert.That(chain.Port,                            Is.EqualTo(port));
    }
}
