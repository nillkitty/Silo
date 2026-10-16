using System.Linq;
using Silo.Contracts.Providers.Crypto;
using Silo.Contracts.Providers.Data;
using Silo.Crypto;

namespace Silo.Tests.Security;

/// <summary>A secret store that keeps its rows in a list: one person, every row visible.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private int _next = 1;

    public List<SecretRecord> Rows { get; } = [];

    public IReadOnlyList<SecretRecord> GetSecrets(string? kind = null) => Rows.Where(r => kind is null || r.Kind == kind).ToList();

    public SecretRecord? GetSecret(int id) => Rows.FirstOrDefault(r => r.Id == id);

    public SecretRecord SaveSecret(SecretRecord secret)
    {
        if (secret.Id == 0)
        {
            var added = secret with { Id = _next++ };
            Rows.Add(added);
            return added;
        }

        int at = Rows.FindIndex(r => r.Id == secret.Id);
        if (at < 0) throw new KeyNotFoundException();

        Rows[at] = secret;
        return secret;
    }

    public bool DeleteSecret(int id) => Rows.RemoveAll(r => r.Id == id) > 0;
}

/// <summary>A protector that only scrambles: enough to tell locked bytes from plain ones, and to test several of them.</summary>
public sealed class ScramblingProtector(string id, byte mask) : ISecretProtector
{
    public string Id          => id;
    public string DisplayName => id;
    public string Description => "test";
    public bool   IsAvailable => true;

    public int ProtectCalls   { get; private set; }
    public int UnprotectCalls { get; private set; }

    public byte[] Protect(byte[] data)
    {
        ProtectCalls++;
        return [.. data.Select(b => (byte)(b ^ mask)), mask];
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        UnprotectCalls++;
        if (protectedData.Length == 0 || protectedData[^1] != mask) throw new SecretException("locked by something else");

        return protectedData[..^1].Select(b => (byte)(b ^ mask)).ToArray();
    }
}
