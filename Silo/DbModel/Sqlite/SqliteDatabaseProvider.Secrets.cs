using Silo.Providers.Data;

namespace Silo.DbModel.Sqlite;

/// <summary>The secrets kept in the Silo (<see cref="ISecretStore" />) by the SQLite provider.</summary>
public sealed partial class SqliteDatabaseProvider : ISecretStore
{
    private const string SecretTable = "[main].[Secret]";

    public IReadOnlyList<SecretRecord> GetSecrets(string? kind = null)
    {
        _gate.Wait();
        try
        {
            return SecretRows.Get(_conn, SecretTable, CurrentUserId(), kind);
        }
        finally
        {
            _gate.Release();
        }
    }

    public SecretRecord? GetSecret(int id)
    {
        _gate.Wait();
        try
        {
            return SecretRows.GetOne(_conn, SecretTable, CurrentUserId(), id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public SecretRecord SaveSecret(SecretRecord secret)
    {
        SecretRecord saved;
        _gate.Wait();
        try
        {
            long?     uid = CurrentUserId();
            using var tx  = _conn.BeginTransaction();
            try
            {
                if (secret.Id == 0)
                {
                    saved = SecretRows.Insert(_conn, tx, SecretTable, uid, secret);
                }
                else
                {
                    if (!SecretRows.Update(_conn, tx, SecretTable, uid, secret, out var modified))
                        throw new KeyNotFoundException($"There is no secret {secret.Id} in this Silo.");

                    saved = secret with { Modified = modified };
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
        return saved;
    }

    public bool DeleteSecret(int id)
    {
        bool removed;
        _gate.Wait();
        try
        {
            removed = SecretRows.Delete(_conn, null, SecretTable, CurrentUserId(), id);
        }
        finally
        {
            _gate.Release();
        }

        if (removed) RaiseChanged();
        return removed;
    }
}