using Serilog;
using Silo.Providers.Crypto;

namespace Silo.Crypto;

/// <summary>The protectors that are registered with the application, and the default one.</summary>
public static class SecretProtectors
{
    /// <summary>What a Silo uses when nothing else was chosen.</summary>
    public const string DefaultId = DpapiSecretProtector.ProviderId;

    /// <summary>Every registered protector that this computer can use. The default is always among them.</summary>
    public static IReadOnlyList<ISecretProtector> Available()
    {
        var list = new List<ISecretProtector>();
        try
        {
            list.AddRange(App.Resolve<IEnumerable<ISecretProtector>>() ?? []);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The secret protectors could not be listed");
        }

        if (list.All(p => p.Id != DefaultId)) list.Add(new DpapiSecretProtector());
        return list.Where(p => p.IsAvailable).GroupBy(p => p.Id).Select(g => g.First()).ToList();
    }
}