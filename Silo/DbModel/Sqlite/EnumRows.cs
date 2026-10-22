using System.Globalization;
using Microsoft.Data.Sqlite;
using Silo.DbModel.Meta;
using Silo.Providers.Data;
using Silo.Providers.Definitions;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     The SQL behind <see cref="IEnumStore" />: rows of the <c>MvEnum</c> and <c>MvEnumValue</c> tables. Kept apart
///     from the provider so it can be tested on its own.
/// </summary>
/// <remarks>
///     Every method takes the transaction the caller is in (or null): a command on a connection in a transaction has
///     to be given it.
/// </remarks>
public static class EnumRows
{
    private static SqliteCommand Command(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static object Db(string? text)
    {
        return text is null ? DBNull.Value : text;
    }

    private static string? Text(SqliteDataReader r, int i)
    {
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    /// <summary>Every enum with its values, ordered by name.</summary>
    public static List<EnumRecord> GetAll(SqliteConnection conn, SqliteTransaction? tx, string enumTable, string valueTable)
    {
        return Load(conn, tx, enumTable, valueTable, null, null);
    }

    public static EnumRecord? Get(SqliteConnection conn, SqliteTransaction? tx, string enumTable, string valueTable, int id)
    {
        return Load(conn, tx, enumTable, valueTable, "WHERE [Id] = @p", id).FirstOrDefault();
    }

    public static EnumRecord? Find(SqliteConnection conn, SqliteTransaction? tx, string enumTable, string valueTable, string name)
    {
        return Load(conn, tx, enumTable, valueTable, "WHERE [Name] = @p COLLATE NOCASE", name).FirstOrDefault();
    }

    private static List<EnumRecord> Load(SqliteConnection conn, SqliteTransaction? tx, string enumTable, string valueTable, string? where,
                                         object?          parameter)
    {
        var headers = new List<(int Id, string Name, string? Description, EnumType Type, EnumFlags Flags)>();
        using (var cmd = Command(conn, tx,
                                 $"SELECT [Id], [Name], [Description], [Type], [Flags] FROM {enumTable} {where} ORDER BY [Name] COLLATE NOCASE, [Id]"))
        {
            if (parameter is not null) cmd.Parameters.AddWithValue("@p", parameter);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                headers.Add((r.GetInt32(0), r.IsDBNull(1) ? string.Empty : r.GetString(1), Text(r, 2), (EnumType)r.GetInt32(3),
                             (EnumFlags)r.GetInt32(4)));
        }

        var values = new Dictionary<int, List<EnumValueRecord>>();
        using (var cmd = Command(conn, tx,
                                 $"SELECT [Id], [EnumId], [Name], [Value], [Description] FROM {valueTable} ORDER BY [EnumId], [Id]"))
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(1)) continue;
                int owner                                                   = r.GetInt32(1);
                if (!values.TryGetValue(owner, out var list)) values[owner] = list = [];
                list.Add(new EnumValueRecord(r.GetInt32(0), r.IsDBNull(2) ? string.Empty : r.GetString(2), Text(r, 3), Text(r, 4)));
            }
        }

        return headers
              .Select(h => new EnumRecord(h.Id, h.Name, h.Description, h.Type, h.Flags, values.TryGetValue(h.Id, out var list) ? list : []))
              .ToList();
    }

    /// <summary>
    ///     Adds the enum or updates the one it names, together with its values (see <see cref="IEnumStore.SaveEnum" />).
    ///     Throws
    ///     <see cref="DefinitionException" /> when the definition is not acceptable; nothing is written then.
    /// </summary>
    public static EnumRecord Save(SqliteConnection conn, SqliteTransaction? tx, string enumTable, string valueTable, EnumRecord definition)
    {
        var otherNames = new List<string>();
        using (var cmd = Command(conn, tx, $"SELECT [Name] FROM {enumTable} WHERE [Id] <> @id"))
        {
            cmd.Parameters.AddWithValue("@id", definition.Id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(0))
                    otherNames.Add(r.GetString(0));
        }

        var e  = EnumRules.Require(definition, otherNames);
        int id = e.Id;

        if (id > 0)
        {
            using var update =
                Command(conn, tx,
                        $"UPDATE {enumTable} SET [Name] = @name, [Description] = @desc, [Type] = @type, [Flags] = @flags WHERE [Id] = @id");
            update.Parameters.AddWithValue("@name",  e.Name);
            update.Parameters.AddWithValue("@desc",  Db(e.Description));
            update.Parameters.AddWithValue("@type",  (long)e.Type);
            update.Parameters.AddWithValue("@flags", (long)e.Flags);
            update.Parameters.AddWithValue("@id",    id);
            if (update.ExecuteNonQuery() == 0) throw new DefinitionException($"The enum \"{e.Name}\" no longer exists.");
        }
        else
        {
            using var insert =
                Command(conn, tx, $"INSERT INTO {enumTable} ([Name], [Description], [Type], [Flags]) VALUES (@name, @desc, @type, @flags)");
            insert.Parameters.AddWithValue("@name",  e.Name);
            insert.Parameters.AddWithValue("@desc",  Db(e.Description));
            insert.Parameters.AddWithValue("@type",  (long)e.Type);
            insert.Parameters.AddWithValue("@flags", (long)e.Flags);
            insert.ExecuteNonQuery();
            id = LastId(conn, tx);
        }

        var existing = new HashSet<int>();
        using (var cmd = Command(conn, tx, $"SELECT [Id] FROM {valueTable} WHERE [EnumId] = @id"))
        {
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add(r.GetInt32(0));
        }

        var kept = new HashSet<int>();
        foreach (var v in e.Values)
            if (v.Id > 0 && existing.Contains(v.Id))
            {
                using var update =
                    Command(conn, tx, $"UPDATE {valueTable} SET [Name] = @name, [Value] = @value, [Description] = @desc WHERE [Id] = @id");
                update.Parameters.AddWithValue("@name",  v.Name);
                update.Parameters.AddWithValue("@value", Db(v.Value));
                update.Parameters.AddWithValue("@desc",  Db(v.Description));
                update.Parameters.AddWithValue("@id",    v.Id);
                update.ExecuteNonQuery();
                kept.Add(v.Id);
            }
            else
            {
                using var insert =
                    Command(conn, tx,
                            $"INSERT INTO {valueTable} ([EnumId], [Name], [Value], [Description]) VALUES (@enum, @name, @value, @desc)");
                insert.Parameters.AddWithValue("@enum",  id);
                insert.Parameters.AddWithValue("@name",  v.Name);
                insert.Parameters.AddWithValue("@value", Db(v.Value));
                insert.Parameters.AddWithValue("@desc",  Db(v.Description));
                insert.ExecuteNonQuery();
            }

        foreach (int gone in existing.Where(x => !kept.Contains(x)))
        {
            using var delete = Command(conn, tx, $"DELETE FROM {valueTable} WHERE [Id] = @id");
            delete.Parameters.AddWithValue("@id", gone);
            delete.ExecuteNonQuery();
        }

        return Get(conn, tx, enumTable, valueTable, id)!;
    }

    private static int LastId(SqliteConnection conn, SqliteTransaction? tx)
    {
        using var last = Command(conn, tx, "SELECT last_insert_rowid()");
        return Convert.ToInt32(last.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Deletes the enum and its values; returns whether there was one.</summary>
    public static bool Delete(SqliteConnection conn, SqliteTransaction? tx, string enumTable, string valueTable, int id)
    {
        using (var values = Command(conn, tx, $"DELETE FROM {valueTable} WHERE [EnumId] = @id"))
        {
            values.Parameters.AddWithValue("@id", id);
            values.ExecuteNonQuery();
        }

        using var header = Command(conn, tx, $"DELETE FROM {enumTable} WHERE [Id] = @id");
        header.Parameters.AddWithValue("@id", id);
        return header.ExecuteNonQuery() > 0;
    }

    /// <summary>
    ///     Adds the value to the enum, or changes the one it names, checking the whole enum again (see
    ///     <see cref="IEnumStore.SaveEnumValue" />).
    /// </summary>
    public static EnumValueRecord SaveValue(SqliteConnection conn, SqliteTransaction? tx, string enumTable, string valueTable, int enumId,
                                            EnumValueRecord  value)
    {
        var owner = Get(conn, tx, enumTable, valueTable, enumId) ?? throw new DefinitionException("That enum no longer exists.");
        if (value.Id > 0 && owner.Values.All(v => v.Id != value.Id)) throw new DefinitionException("That value no longer exists.");

        var saved = Save(conn, tx, enumTable, valueTable, EnumRules.WithValue(owner, value));

        // the saved value is the one with the id, or, for a new one, the one with the name that was not there before
        string name = value.Name.Trim();
        return value.Id > 0
                   ? saved.Values.First(v => v.Id == value.Id)
                   : saved.Values.Where(v => owner.Values.All(o => o.Id != v.Id))
                          .First(v => string.Equals(v.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Deletes one value; returns whether there was one. The enum is not checked again (removing a value cannot make
    ///     it invalid).
    /// </summary>
    public static bool DeleteValue(SqliteConnection conn, SqliteTransaction? tx, string valueTable, int valueId)
    {
        using var cmd = Command(conn, tx, $"DELETE FROM {valueTable} WHERE [Id] = @id");
        cmd.Parameters.AddWithValue("@id", valueId);
        return cmd.ExecuteNonQuery() > 0;
    }
}