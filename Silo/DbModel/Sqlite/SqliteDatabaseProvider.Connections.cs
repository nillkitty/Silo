namespace Silo.DbModel.Sqlite;

/// <summary>The saved connections (<see cref="IConnectionStore" />) of the SQLite provider.</summary>
public sealed partial class SqliteDatabaseProvider : IConnectionStore
{
    private const string ConnectionTable = "[main].[Connection]";

    public IReadOnlyList<ConnectionRecord> GetConnections()
    {
        _gate.Wait();
        try
        {
            return ConnectionRows.GetAll(_conn, ConnectionTable);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ConnectionRecord SaveConnection(string displayName, Uri? sourceUri)
    {
        ConnectionRecord saved;
        _gate.Wait();
        try
        {
            long? uid = CurrentUserId();
            using var tx = _conn.BeginTransaction();
            try
            {
                saved = ConnectionRows.Save(_conn, tx, ConnectionTable, uid, displayName, sourceUri);
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

    public void ForgetConnection(long id)
    {
        bool removed;
        _gate.Wait();
        try
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                removed = ConnectionRows.Forget(_conn, tx, ConnectionTable, id);
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

        if (removed) RaiseChanged();
    }
}
