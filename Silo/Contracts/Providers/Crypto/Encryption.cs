using Silo.DbModel.Global;

namespace Silo.Contracts.Providers.Crypto;

public interface ISiloEncryption
{
    Secret? GetOrCreateSecret(string? username, string? password, string? domain);

    (string? Username, string? Password, string? Domain) GetCredential(Secret? secret);
}