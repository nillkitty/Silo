using System.Globalization;
using Microsoft.Data.Sqlite;
using Silo.Providers.Data;
using DataBinding = Silo.DbModel.Global.DataBinding;

namespace Silo.DbModel.Sqlite;

/// <summary>The data binding table access (<see cref="IDataBindingStore" />) of the SQLite provider.</summary>
public sealed partial class SqliteDatabaseProvider : IDataBindingStore
{
    private const string DataBindingColumns = "Id, Name, Source, Interval, Enabled, Description, Created, Modified";

    private string DataBindingTable => T(EntitySchema.MainAlias, nameof(DataBinding));

    private static DataBinding ReadDataBinding(SqliteDataReader r)
    {
        return new DataBinding
               {
                   Id          = r.GetInt32(0),
                   Name        = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                   Source      = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                   Interval    = r.IsDBNull(3) ? TimeSpan.FromSeconds(1) : TimeSpan.FromTicks(r.GetInt64(3)),
                   Enabled     = r.IsDBNull(4) || r.GetInt64(4) != 0,
                   Description = r.IsDBNull(5) ? null : r.GetString(5),
                   Created     = r.IsDBNull(6) ? DateTime.UtcNow : ParseTime(r.GetString(6)),
                   Modified    = r.IsDBNull(7) ? DateTime.UtcNow : ParseTime(r.GetString(7))
               };
    }

    public IReadOnlyList<DataBinding> ListDataBindings()
    {
        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {DataBindingColumns} FROM {DataBindingTable} ORDER BY Name COLLATE NOCASE";

            var       list   = new List<DataBinding>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadDataBinding(reader));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public DataBinding? GetDataBinding(int id)
    {
        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {DataBindingColumns} FROM {DataBindingTable} WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadDataBinding(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public DataBinding SaveDataBinding(DataBinding binding)
    {
        _gate.Wait();
        try
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                if (binding.Id <= 0)
                {
                    using var idCmd = _conn.CreateCommand();
                    idCmd.Transaction = tx;
                    idCmd.CommandText = $"SELECT COALESCE(MAX(Id), 0) + 1 FROM {DataBindingTable}";
                    binding           = binding.Clone();
                    binding.Id        = Convert.ToInt32(idCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                UpsertRecursive(binding, tx, new HashSet<object>(ReferenceEqualityComparer.Instance));
                tx.Commit();
                RaiseChanged();
                return binding;
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

    public bool DeleteDataBinding(int id)
    {
        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {DataBindingTable} WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            bool deleted = cmd.ExecuteNonQuery() > 0;
            if (deleted) RaiseChanged();
            return deleted;
        }
        finally
        {
            _gate.Release();
        }
    }
}