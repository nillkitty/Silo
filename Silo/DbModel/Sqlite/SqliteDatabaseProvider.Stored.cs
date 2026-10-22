using Microsoft.Data.Sqlite;
using Silo.Providers.Data;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     The stored items (<see cref="IStoredStore" />) of the SQLite provider: the <c>Stored</c> table of the main
///     database.
/// </summary>
public sealed partial class SqliteDatabaseProvider : IStoredStore
{
    // the same table in two databases: the main one keeps the Silo's own, the scratch one what is only remembered for the session
    private string StoredTable(StoredTier tier)
    {
        return T(tier == StoredTier.Session ? EntitySchema.TempAlias : EntitySchema.MainAlias, "Stored");
    }

    // a personal item is the current person's own; until the person is one of the Silo's users there is no one to own it, and it is kept as a shared one
    private long? StoredOwner(StoredScope scope)
    {
        return scope == StoredScope.Shared ? null : CurrentUserId();
    }

    // Like WriteLocked, but the owner is looked up before the transaction begins: a command on the connection has to be given the transaction once there is one,
    // and the lookup of the current user does not take it.
    private TResult WriteStored<TResult>(StoredScope scope, Func<SqliteTransaction, long?, (TResult Result, bool Changed)> write)
    {
        TResult result;
        bool    changed;
        _gate.Wait();
        try
        {
            long?     owner = StoredOwner(scope);
            using var tx    = _conn.BeginTransaction();
            try
            {
                (result, changed) = write(tx, owner);
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

        if (changed) RaiseChanged();
        return result;
    }

    public StoredRecord? GetStored(string key, StoredScope scope = StoredScope.Personal, StoredTier tier = StoredTier.Permanent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return ReadLocked(() => StoredRows.Get(_conn, null, StoredTable(tier), StoredOwner(scope), scope, key));
    }

    public IReadOnlyList<StoredRecord> ListStored(string?    keyPrefix = null, StoredScope scope = StoredScope.Personal,
                                                  StoredTier tier      = StoredTier.Permanent)
    {
        return ReadLocked(() => StoredRows.List(_conn, null, StoredTable(tier), StoredOwner(scope), scope, keyPrefix));
    }

    public StoredRecord SetStored(string     key, string typeName, string format, string data, StoredScope scope = StoredScope.Personal,
                                  StoredTier tier = StoredTier.Permanent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(data);
        return WriteStored(scope, (tx, owner) => StoredRows.Set(_conn, tx, StoredTable(tier), owner, scope, key, typeName, format, data));
    }

    public bool DeleteStored(string key, StoredScope scope = StoredScope.Personal, StoredTier tier = StoredTier.Permanent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return WriteStored(scope, (tx, owner) =>
                                  {
                                      bool removed = StoredRows.Delete(_conn, tx, StoredTable(tier), owner, key);
                                      return (removed, removed);
                                  });
    }
}