namespace Silo.DbModel.Sqlite;

/// <summary>The preferences kept in the Silo (<see cref="IPreferenceStore" />) by the SQLite provider.</summary>
public sealed partial class SqliteDatabaseProvider : IPreferenceStore
{
    private const string PrefTable = "[main].[Pref]";

    public IReadOnlyList<PreferenceRecord> GetPreferences()
    {
        _gate.Wait();
        try
        {
            return PrefRows.Get(_conn, PrefTable, CurrentUserId());
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SetPreference(string key, string data)
    {
        bool changed;
        _gate.Wait();
        try
        {
            long? uid = CurrentUserId();
            using var tx = _conn.BeginTransaction();
            try
            {
                changed = PrefRows.Set(_conn, tx, PrefTable, uid, key, data);
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
    }
}
