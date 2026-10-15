using System.Globalization;
using Pasted = Silo.DbModel.Temp.Pasted;
using Silo.Tabular;

namespace Silo.DbModel.Sqlite;

/// <summary>The paste history (<see cref="IPastedStore" />) of the SQLite provider: rows of the scratch database's <c>Pasted</c> table.</summary>
public sealed partial class SqliteDatabaseProvider : IPastedStore
{
    public int AddPasted(string source, string type, byte[] data)
    {
        _gate.Wait();
        try
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                int id;
                using (var idCmd = _conn.CreateCommand())
                {
                    idCmd.Transaction = tx;
                    idCmd.CommandText = $"SELECT COALESCE(MAX([Id]), 0) + 1 FROM {T(EntitySchema.TempAlias, nameof(Pasted))}";
                    id = Convert.ToInt32(idCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                var now = DateTime.UtcNow;
                var row = new Pasted { Id = id, Source = source, Type = type, Data = data, Created = now, Modified = now };
                UpsertRecursive(row, tx, new HashSet<object>(ReferenceEqualityComparer.Instance));

                tx.Commit();
                RaiseChanged();
                return id;
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
    }
}
