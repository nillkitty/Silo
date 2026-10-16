using System.Text;
using Silo.Contracts.Providers.Crypto;
using Silo.Crypto;

namespace Silo.Tests.Security;

public class SecretEnvelopeTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Test]
    public void A_sealed_secret_opens_with_the_same_key_and_context()
    {
        var key    = SecretEnvelope.NewKey();
        var sealedData = SecretEnvelope.Seal(key, 7, Bytes("hunter2"), "credential");

        Assert.That(Encoding.UTF8.GetString(SecretEnvelope.Open(key, sealedData, "credential")), Is.EqualTo("hunter2"));
    }

    [Test]
    public void The_sealed_bytes_do_not_contain_the_secret_and_name_the_key()
    {
        var key        = SecretEnvelope.NewKey();
        var sealedData = SecretEnvelope.Seal(key, 42, Bytes("a-very-recognisable-password"), "credential");

        Assert.That(Encoding.UTF8.GetString(sealedData), Does.Not.Contain("recognisable"));
        Assert.That(SecretEnvelope.KeyIdOf(sealedData), Is.EqualTo(42));
    }

    [Test]
    public void Sealing_twice_gives_different_bytes_because_the_nonce_is_fresh()
    {
        var key = SecretEnvelope.NewKey();

        Assert.That(SecretEnvelope.Seal(key, 1, Bytes("same"), "c"), Is.Not.EqualTo(SecretEnvelope.Seal(key, 1, Bytes("same"), "c")));
    }

    [Test]
    public void An_empty_secret_can_be_sealed()
    {
        var key = SecretEnvelope.NewKey();

        Assert.That(SecretEnvelope.Open(key, SecretEnvelope.Seal(key, 1, [], "c"), "c"), Is.Empty);
    }

    [Test]
    public void Another_key_cannot_open_it()
    {
        var sealedData = SecretEnvelope.Seal(SecretEnvelope.NewKey(), 1, Bytes("x"), "c");

        Assert.Throws<SecretException>(() => SecretEnvelope.Open(SecretEnvelope.NewKey(), sealedData, "c"));
    }

    [Test]
    public void Another_context_cannot_open_it()
    {
        var key        = SecretEnvelope.NewKey();
        var sealedData = SecretEnvelope.Seal(key, 1, Bytes("x"), "credential");

        Assert.Throws<SecretException>(() => SecretEnvelope.Open(key, sealedData, "something-else"));
    }

    [Test]
    public void A_changed_byte_anywhere_after_the_header_is_noticed()
    {
        var key        = SecretEnvelope.NewKey();
        var sealedData = SecretEnvelope.Seal(key, 1, Bytes("some secret text"), "c");

        for (int i = 7; i < sealedData.Length; i++)
        {
            var copy = (byte[])sealedData.Clone();
            copy[i] ^= 1;
            Assert.Throws<SecretException>(() => SecretEnvelope.Open(key, copy, "c"), $"byte {i}");
        }
    }

    [Test]
    public void Bytes_that_are_not_a_sealed_secret_are_refused_by_name()
    {
        Assert.Throws<SecretException>(() => SecretEnvelope.KeyIdOf([1, 2, 3]));
        Assert.Throws<SecretException>(() => SecretEnvelope.KeyIdOf(new byte[64]));
        Assert.Throws<SecretException>(() => SecretEnvelope.KeyIdOf(null!));
    }

    [Test]
    public void A_newer_format_is_refused_rather_than_misread()
    {
        var sealedData = SecretEnvelope.Seal(SecretEnvelope.NewKey(), 1, Bytes("x"), "c");
        sealedData[2] = 99;

        Assert.Throws<SecretException>(() => SecretEnvelope.KeyIdOf(sealedData));
    }

    [Test]
    public void A_key_of_the_wrong_size_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => SecretEnvelope.Seal(new byte[16], 1, Bytes("x"), "c"));
    }
}
