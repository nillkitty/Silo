using System.Linq;
using System.Text;
using Silo.Contracts.Providers.Crypto;
using Silo.Crypto;

namespace Silo.Tests.Security;

public class SiloEncryptionTests
{
    private InMemorySecretStore _store = null!;
    private ScramblingProtector _a = null!;
    private ScramblingProtector _b = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new InMemorySecretStore();
        _a     = new ScramblingProtector("scramble-a", 0x5A);
        _b     = new ScramblingProtector("scramble-b", 0x33);
    }

    private SiloEncryption Make(params ISecretProtector[] protectors) =>
        new(_store, protectors.Length == 0 ? [_a, _b] : protectors, "scramble-a");

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Test]
    public void The_first_encrypt_makes_one_data_key_locked_by_the_default_protector()
    {
        using var e = Make();

        e.Encrypt(Bytes("x"), "c");
        e.Encrypt(Bytes("y"), "c");

        var keys = _store.GetSecrets(SiloEncryption.KeyKind);
        Assert.That(keys, Has.Count.EqualTo(1));
        Assert.That(keys[0].Provider, Is.EqualTo("scramble-a"));
        Assert.That(keys[0].Key, Is.EqualTo(SiloEncryption.KeyName));
        Assert.That(_a.ProtectCalls, Is.EqualTo(1));
    }

    [Test]
    public void The_stored_key_is_the_locked_one_not_the_plain_key()
    {
        using var e = Make();
        var sealedData = e.Encrypt(Bytes("x"), "c");

        // opening needs the plain key, which is the stored one run back through the protector
        var stored = _store.GetSecrets(SiloEncryption.KeyKind)[0].Data;
        var plain  = _a.Unprotect(stored);
        Assert.That(stored, Is.Not.EqualTo(plain));
        Assert.That(SecretEnvelope.Open(plain, sealedData, "c"), Is.EqualTo(Bytes("x")));
    }

    [Test]
    public void A_new_instance_over_the_same_store_opens_what_an_earlier_one_sealed()
    {
        byte[] sealedData;
        using (var first = Make()) sealedData = first.Encrypt(Bytes("hunter2"), "credential");

        using var second = Make();
        Assert.That(second.Decrypt(sealedData, "credential"), Is.EqualTo(Bytes("hunter2")));
        Assert.That(_store.GetSecrets(SiloEncryption.KeyKind), Has.Count.EqualTo(1));
    }

    [Test]
    public void The_key_is_unlocked_once_per_instance()
    {
        using var e = Make();
        var sealedData = e.Encrypt(Bytes("x"), "c");

        e.Decrypt(sealedData, "c");
        e.Decrypt(sealedData, "c");

        Assert.That(_a.UnprotectCalls, Is.EqualTo(0)); // the key made by this instance was already at hand
    }

    [Test]
    public void ProviderId_is_the_protector_of_the_key_or_the_default_before_there_is_one()
    {
        using var e = Make();
        Assert.That(e.ProviderId, Is.EqualTo("scramble-a"));

        e.Encrypt(Bytes("x"), "c");
        Assert.That(e.ProviderId, Is.EqualTo("scramble-a"));
    }

    [Test]
    public void Changing_the_provider_relocks_the_key_and_old_secrets_still_open()
    {
        using var e = Make();
        var sealedData = e.Encrypt(Bytes("keep me"), "c");

        Assert.That(e.ChangeProvider("scramble-b"), Is.EqualTo(1));

        var key = _store.GetSecrets(SiloEncryption.KeyKind).Single();
        Assert.That(key.Provider, Is.EqualTo("scramble-b"));
        Assert.That(e.ProviderId, Is.EqualTo("scramble-b"));

        using var fresh = Make();
        Assert.That(fresh.Decrypt(sealedData, "c"), Is.EqualTo(Bytes("keep me")));
        Assert.That(_store.GetSecrets(SiloEncryption.KeyKind), Has.Count.EqualTo(1), "no second key was made");
    }

    [Test]
    public void Changing_to_the_provider_already_in_use_moves_nothing()
    {
        using var e = Make();
        e.Encrypt(Bytes("x"), "c");

        Assert.That(e.ChangeProvider("scramble-a"), Is.EqualTo(0));
    }

    [Test]
    public void Changing_to_a_provider_that_does_not_exist_is_refused_and_changes_nothing()
    {
        using var e = Make();
        e.Encrypt(Bytes("x"), "c");

        Assert.Throws<SecretException>(() => e.ChangeProvider("nope"));
        Assert.That(_store.GetSecrets(SiloEncryption.KeyKind).Single().Provider, Is.EqualTo("scramble-a"));
    }

    [Test]
    public void Changing_the_provider_before_any_key_exists_makes_the_first_key_with_the_new_one()
    {
        using var e = Make();

        Assert.That(e.ChangeProvider("scramble-b"), Is.EqualTo(0));
        e.Encrypt(Bytes("x"), "c");

        Assert.That(_store.GetSecrets(SiloEncryption.KeyKind).Single().Provider, Is.EqualTo("scramble-b"));
    }

    [Test]
    public void A_key_locked_by_a_protector_this_copy_does_not_have_cannot_be_used_and_says_so()
    {
        byte[] sealedData;
        using (var first = Make()) sealedData = first.Encrypt(Bytes("x"), "c");

        using var onlyB = new SiloEncryption(_store, [_b], "scramble-b");

        var ex = Assert.Throws<SecretException>(() => onlyB.Decrypt(sealedData, "c"));
        Assert.That(ex!.Message, Does.Contain("scramble-a"));
    }

    [Test]
    public void A_key_that_was_locked_for_someone_else_cannot_be_unlocked()
    {
        byte[] sealedData;
        using (var first = Make()) sealedData = first.Encrypt(Bytes("x"), "c");

        // same protector id, but it no longer recognises the bytes (another account, another computer)
        using var other = new SiloEncryption(_store, [new ScramblingProtector("scramble-a", 0x11)], "scramble-a");

        Assert.Throws<SecretException>(() => other.Decrypt(sealedData, "c"));
    }

    [Test]
    public void A_secret_sealed_by_a_key_that_is_not_here_cannot_be_opened()
    {
        var sealedData = SecretEnvelope.Seal(SecretEnvelope.NewKey(), 99, Bytes("x"), "c");
        using var e    = Make();

        Assert.Throws<SecretException>(() => e.Decrypt(sealedData, "c"));
    }

    [Test]
    public void The_last_key_of_the_store_is_the_one_used_for_new_secrets()
    {
        using var first = Make();
        first.Encrypt(Bytes("x"), "c");

        // a person's own key follows the shared one in the store's order, and wins
        var ownKey = SecretEnvelope.NewKey();
        var added  = _store.SaveSecret(new SecretRecord(0, SiloEncryption.KeyName, SiloEncryption.KeyKind, "scramble-a", _a.Protect(ownKey), DateTime.UtcNow, DateTime.UtcNow));

        using var second = Make();
        var sealedData = second.Encrypt(Bytes("y"), "c");

        Assert.That(SecretEnvelope.KeyIdOf(sealedData), Is.EqualTo(added.Id));
    }

    [Test]
    public void The_default_must_be_one_of_the_protectors()
    {
        Assert.Throws<ArgumentException>(() => new SiloEncryption(_store, [_a], "scramble-z"));
    }
}
