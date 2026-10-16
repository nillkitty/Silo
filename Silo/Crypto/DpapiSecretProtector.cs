using System.Security.Cryptography;
using System.Text;
using Silo.Contracts.Providers.Crypto;
using Telefrag.DI;

namespace Silo.Crypto;

/// <summary>
///     The default: Windows data protection (DPAPI) for the current Windows user. Nothing to remember or type, and what it locks opens
///     only for the same Windows account on the same computer (or a roaming profile of it).
/// </summary>
[Singleton(typeof(ISecretProtector))]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public const string ProviderId = "dpapi-user";

    // Not secret: it makes the data useless to another program that merely calls DPAPI as the same user with no entropy.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Silo.Secret.v1");

    public string Id          => ProviderId;
    public string DisplayName => "Windows account (DPAPI)";
    public string Description => "Locked to your Windows account on this computer. Nothing to remember; it cannot be opened by another account or on another computer.";
    public bool   IsAvailable => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            return ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            throw new SecretException("Windows could not protect the key for your account: " + ex.Message, ex);
        }
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        try
        {
            return ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            throw new SecretException("These secrets were protected for a different Windows account or computer, so they cannot be opened here.", ex);
        }
    }
}
