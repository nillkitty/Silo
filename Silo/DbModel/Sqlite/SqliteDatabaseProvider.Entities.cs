using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Serilog;
using Silo.DbModel.Worlds;

namespace Silo.DbModel.Sqlite;

public sealed partial class SqliteDatabaseProvider : IEntityStore
{
    /// <inheritdoc />
    public IReadOnlyList<T> ListItems<T>(string? worldKey = null) where T : class
    {
        _gate.Wait();
        try
        {
            return ReadAll(typeof(T), worldKey, new Dictionary<(Type, long), object>()).Cast<T>().ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<World> ListWorlds()
    {
        _gate.Wait();
        try
        {
            var worlds = new List<World>();
            var cache  = new Dictionary<(Type, long), object>();
            foreach (string alias in _worldAliases.Keys.ToList())
                worlds.AddRange(ReadAll(typeof(World), alias, cache, isAlias: true).Cast<World>());

            return worlds;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? AliasFor(EntityInfo info, string? worldKey, bool isAlias)
    {
        if (info.SchemaKind == SchemaKind.Fixed) return info.FixedAlias;
        if (worldKey is null) return null;

        string alias = isAlias ? worldKey : "w_" + SanitizeKey(worldKey);
        return _worldAliases.ContainsKey(alias) ? alias : null;
    }

    private List<object> ReadAll(Type type, string? worldKey, Dictionary<(Type, long), object> cache, bool isAlias = false)
    {
        var info = EntitySchema.Get(type) ?? throw new InvalidOperationException($"Type '{type.Name}' is not a registered persistable entity.");
        var rows = new List<object>();
        if (AliasFor(info, worldKey, isAlias) is not { } alias) return rows;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {T(alias, info.TableName)}";
        using var reader = cmd.ExecuteReader();
        var       fixedKey = worldKey;
        while (reader.Read()) rows.Add(Materialize(info, reader, alias, cache));

        return rows;
    }

    private object? ReadById(EntityInfo info, long id, string alias, Dictionary<(Type, long), object> cache)
    {
        if (cache.TryGetValue((info.Type, id), out var known)) return known;

        // a referenced entity is in the same database as the one that refers to it, unless it is a fixed one
        string where = info.SchemaKind == SchemaKind.Fixed ? info.FixedAlias! : alias;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {T(where, info.TableName)} WHERE [Id] = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Materialize(info, reader, where, cache) : null;
    }

    private object Materialize(EntityInfo info, SqliteDataReader reader, string alias, Dictionary<(Type, long), object> cache)
    {
        var instance = Activator.CreateInstance(info.Type)!;

        long id = 0;
        if (info.IdProperty is not null)
        {
            int ordinal = Ordinal(reader, "Id");
            if (ordinal >= 0 && !reader.IsDBNull(ordinal))
            {
                id = reader.GetInt64(ordinal);
                info.IdProperty.SetValue(instance, Convert.ChangeType(id, info.IdProperty.PropertyType));
                cache[(info.Type, id)] = instance; // before the references are followed: they may refer back
            }
        }

        foreach (var col in info.Columns)
        {
            if (col.Property == info.IdProperty) continue;

            int ordinal = Ordinal(reader, col.ColumnName);
            if (ordinal < 0 || reader.IsDBNull(ordinal)) continue;

            try
            {
                object raw = reader.GetValue(ordinal);
                switch (col.Kind)
                {
                    case ColumnKind.EntityRef:
                        var refInfo = EntitySchema.Get(col.Property.PropertyType)!;
                        col.Property.SetValue(instance, ReadById(refInfo, Convert.ToInt64(raw), alias, cache));
                        break;
                    case ColumnKind.Json:
                        col.Property.SetValue(instance, JsonConvert.DeserializeObject((string)raw, col.Property.PropertyType, JsonSettings));
                        break;
                    default:
                        col.Property.SetValue(instance, SqliteTypeConvertFrom.FromSqlValue(raw, col.Property.PropertyType));
                        break;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidCastException or FormatException or ArgumentException or UriFormatException)
            {
                Log.Debug(ex, "{Type}.{Column} could not be read", info.Type.Name, col.ColumnName);
            }
        }

        return instance;
    }

    private static int Ordinal(SqliteDataReader reader, string column)
    {
        for (int i = 0; i < reader.FieldCount; i++)
            if (string.Equals(reader.GetName(i), column, StringComparison.OrdinalIgnoreCase))
                return i;

        return -1;
    }
}
