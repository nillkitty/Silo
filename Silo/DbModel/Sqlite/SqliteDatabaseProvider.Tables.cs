using System.Globalization;
using Microsoft.Data.Sqlite;
using Silo.DbModel.Global;
using Silo.DbModel.Worlds;
using Silo.Tabular;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     The authored-table access (<see cref="ITableStore" />) of the SQLite provider. Tables live in the built-in
///     <see cref="LocalWorld" /> (database <c>w-local.db</c> / the <c>w_local_*</c> tables), which is created the first
///     time one is saved. Each table is a <see cref="AuthoredTable" /> row (the rows as JSON) plus a
///     <see cref="WorldType" /> and one <see cref="WorldField" /> per column, so the world describes its own content.
/// </summary>
public sealed partial class SqliteDatabaseProvider : ITableStore
{
    private const string TableColumns = "Id, Name, Description, DataJson, Created, Modified, WorldTypeId";

    public IReadOnlyList<AuthoredTable> ListTables()
    {
        _gate.Wait();
        try
        {
            var list = new List<AuthoredTable>();
            if (!_worldAliases.ContainsKey(LocalWorld.Alias)) return list; // nothing has ever been authored

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {TableColumns} FROM {T(LocalWorld.Alias, nameof(AuthoredTable))} ORDER BY Name COLLATE NOCASE";
            using var reader = cmd.ExecuteReader();
            var world = LocalWorld.Create();
            while (reader.Read()) list.Add(ReadTable(reader, world));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public AuthoredTable? GetTable(int id)
    {
        _gate.Wait();
        try
        {
            if (!_worldAliases.ContainsKey(LocalWorld.Alias)) return null;

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {TableColumns} FROM {T(LocalWorld.Alias, nameof(AuthoredTable))} WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadTable(reader, LocalWorld.Create()) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public AuthoredTable SaveTable(AuthoredTable table)
    {
        _gate.Wait();
        try
        {
            var world = LocalWorld.Create();

            // The local world is created the first time something is saved to it. Like AddItem, attach / create its database
            // before the transaction starts (SQLite cannot ATTACH inside one).
            ProvisionWorlds(world, new HashSet<object>(ReferenceEqualityComparer.Instance));

            using var tx = _conn.BeginTransaction();
            try
            {
                var saved = table.Clone();
                saved.World = world;

                // an existing table keeps its world type; a new one (or a copy of one) gets its own
                int typeId = saved.Id > 0 ? ExistingTypeId(saved.Id, tx) : 0;
                if (typeId <= 0) typeId = NextLocalId(nameof(WorldType), tx);
                if (saved.Id <= 0) saved.Id = NextLocalId(nameof(AuthoredTable), tx);

                var type = new WorldType
                           {
                               Id          = typeId,
                               Name        = saved.Name,
                               DisplayName = saved.Name,
                               NativeName  = saved.Name,
                               Provider    = LocalWorld.Provider,
                               TableName   = nameof(AuthoredTable),
                               Flags       = WorldTypeFlags.AuthoritativeSource,
                               World       = world
                           };
                saved.WorldType = type;

                var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                UpsertRecursive(saved, tx, visited); // also saves the World and the WorldType it references

                // the type's fields are simply rewritten from the current columns
                using (var del = _conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = $"DELETE FROM {T(LocalWorld.Alias, nameof(WorldField))} WHERE [WorldTypeId] = @t";
                    del.Parameters.AddWithValue("@t", typeId);
                    del.ExecuteNonQuery();
                }

                int fieldId = NextLocalId(nameof(WorldField), tx);
                var columns = saved.ReadSheet().Columns;
                for (int i = 0; i < columns.Count; i++)
                {
                    var field = new WorldField
                                {
                                    Id         = fieldId++,
                                    World      = world,
                                    WorldType  = type,
                                    Index      = i,
                                    Name       = columns[i].Name,
                                    DataType   = ClrTypeName(columns[i].Type),
                                    NativeType = columns[i].Type.ToString()
                                };
                    UpsertRecursive(field, tx, visited);
                }

                tx.Commit();
                RaiseChanged();
                return saved;
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

    public bool DeleteTable(int id)
    {
        _gate.Wait();
        try
        {
            if (!_worldAliases.ContainsKey(LocalWorld.Alias)) return false;

            using var tx = _conn.BeginTransaction();
            try
            {
                int typeId = ExistingTypeId(id, tx);

                bool deleted;
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = $"DELETE FROM {T(LocalWorld.Alias, nameof(AuthoredTable))} WHERE [Id] = @id";
                    cmd.Parameters.AddWithValue("@id", id);
                    deleted = cmd.ExecuteNonQuery() > 0;
                }

                if (deleted && typeId > 0)
                {
                    foreach (var table in new[] { nameof(WorldField), nameof(WorldType) })
                    {
                        using var cmd = _conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = table == nameof(WorldField)
                                              ? $"DELETE FROM {T(LocalWorld.Alias, table)} WHERE [WorldTypeId] = @t"
                                              : $"DELETE FROM {T(LocalWorld.Alias, table)} WHERE [Id] = @t";
                        cmd.Parameters.AddWithValue("@t", typeId);
                        cmd.ExecuteNonQuery();
                    }
                }

                if (deleted) WindowRows.Forget(_conn, WindowTable, "Table", $"silo:table/{id}", tx);

                tx.Commit();
                if (deleted) RaiseChanged();
                return deleted;
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

    /// <summary>The world type id of an existing table, or 0 if there is no such table.</summary>
    private int ExistingTypeId(int tableId, SqliteTransaction tx)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT [WorldTypeId] FROM {T(LocalWorld.Alias, nameof(AuthoredTable))} WHERE [Id] = @id";
        cmd.Parameters.AddWithValue("@id", tableId);
        return cmd.ExecuteScalar() is { } v and not DBNull ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>The next free id in one of the local world's tables.</summary>
    private int NextLocalId(string table, SqliteTransaction tx)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COALESCE(MAX([Id]), 0) + 1 FROM {T(LocalWorld.Alias, table)}";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ClrTypeName(CellType type) => type switch
                                                        {
                                                            CellType.Number  => typeof(decimal).FullName!,
                                                            CellType.Boolean => typeof(bool).FullName!,
                                                            CellType.Date    => typeof(DateTime).FullName!,
                                                            _                => typeof(string).FullName!
                                                        };

    private static AuthoredTable ReadTable(SqliteDataReader r, World world)
    {
        string name = r.GetString(1);
        return new AuthoredTable
               {
                   Id          = r.GetInt32(0),
                   World       = world,
                   WorldType   = new WorldType { Id = r.IsDBNull(6) ? 0 : r.GetInt32(6), Name = name, DisplayName = name, NativeName = name, World = world },
                   Name        = name,
                   Description = r.GetString(2),
                   DataJson    = r.GetString(3),
                   Created     = ParseTime(r.GetString(4)),
                   Modified    = ParseTime(r.GetString(5))
               };
    }
}
