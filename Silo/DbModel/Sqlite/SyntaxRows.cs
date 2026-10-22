using System.Globalization;
using Microsoft.Data.Sqlite;
using Silo.DbModel.Meta;
using Silo.Providers.Data;
using Silo.Providers.Definitions;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     The SQL behind <see cref="ISyntaxStore" />: rows of the <c>MvSyntax</c> table. Kept apart from the provider so
///     it can be tested on its own.
/// </summary>
/// <remarks>
///     Every method takes the transaction the caller is in (or null): a command on a connection in a transaction has
///     to be given it.
/// </remarks>
public static class SyntaxRows
{
    private const string Columns = "[Id], [Name], [Alias], [Description], [Kind], [Parser], [Pattern], [Example], [Flags]";

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

    private static SyntaxRecord Read(SqliteDataReader r)
    {
        return new SyntaxRecord(r.GetInt32(0), r.IsDBNull(1) ? string.Empty : r.GetString(1), Text(r, 2), Text(r, 3),
                                (SyntaxKind)r.GetInt32(4), Text(r, 5), Text(r, 6), Text(r, 7), (SyntaxFlags)r.GetInt32(8));
    }

    /// <summary>Every syntax, ordered by name.</summary>
    public static List<SyntaxRecord> GetAll(SqliteConnection conn, SqliteTransaction? tx, string table)
    {
        return Load(conn, tx, table, null, null);
    }

    public static SyntaxRecord? Get(SqliteConnection conn, SqliteTransaction? tx, string table, int id)
    {
        return Load(conn, tx, table, "WHERE [Id] = @p", id).FirstOrDefault();
    }

    /// <summary>The syntax whose name, or else whose alias, is the text (any case).</summary>
    public static SyntaxRecord? Find(SqliteConnection conn, SqliteTransaction? tx, string table, string nameOrAlias)
    {
        return Load(conn, tx, table, "WHERE [Name] = @p COLLATE NOCASE",  nameOrAlias).FirstOrDefault() ??
               Load(conn, tx, table, "WHERE [Alias] = @p COLLATE NOCASE", nameOrAlias).FirstOrDefault();
    }

    private static List<SyntaxRecord> Load(SqliteConnection conn, SqliteTransaction? tx, string table, string? where, object? parameter)
    {
        var       result = new List<SyntaxRecord>();
        using var cmd    = Command(conn, tx, $"SELECT {Columns} FROM {table} {where} ORDER BY [Name] COLLATE NOCASE, [Id]");
        if (parameter is not null) cmd.Parameters.AddWithValue("@p", parameter);

        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(Read(r));
        return result;
    }

    /// <summary>
    ///     Adds the syntax or updates the one it names (see <see cref="ISyntaxStore.SaveSyntax" />). Throws
    ///     <see cref="DefinitionException" />
    ///     when the definition is not acceptable; nothing is written then.
    /// </summary>
    public static SyntaxRecord Save(SqliteConnection conn, SqliteTransaction? tx, string table, SyntaxRecord syntax)
    {
        var others = GetAll(conn, tx, table).Where(o => o.Id != syntax.Id).ToList();
        var s      = SyntaxRules.Require(syntax, others);

        int id = s.Id;
        if (id > 0)
        {
            using var update =
                Command(conn, tx,
                        $"UPDATE {table} SET [Name] = @name, [Alias] = @alias, [Description] = @desc, [Kind] = @kind, [Parser] = @parser, [Pattern] = @pattern, [Example] = @example, [Flags] = @flags WHERE [Id] = @id");
            Bind(update, s);
            update.Parameters.AddWithValue("@id", id);
            if (update.ExecuteNonQuery() == 0) throw new DefinitionException($"The syntax \"{s.Name}\" no longer exists.");
        }
        else
        {
            id = Insert(conn, tx, table, s);
        }

        return Get(conn, tx, table, id)!;
    }

    private static void Bind(SqliteCommand cmd, SyntaxRecord s)
    {
        cmd.Parameters.AddWithValue("@name",    s.Name);
        cmd.Parameters.AddWithValue("@alias",   Db(s.Alias));
        cmd.Parameters.AddWithValue("@desc",    Db(s.Description));
        cmd.Parameters.AddWithValue("@kind",    (long)s.Kind);
        cmd.Parameters.AddWithValue("@parser",  Db(s.Parser));
        cmd.Parameters.AddWithValue("@pattern", Db(s.Pattern));
        cmd.Parameters.AddWithValue("@example", Db(s.Example));
        cmd.Parameters.AddWithValue("@flags",   (long)s.Flags);
    }

    private static int Insert(SqliteConnection conn, SqliteTransaction? tx, string table, SyntaxRecord s)
    {
        using (var insert =
               Command(conn, tx,
                       $"INSERT INTO {table} ([Name], [Alias], [Description], [Kind], [Parser], [Pattern], [Example], [Flags]) VALUES (@name, @alias, @desc, @kind, @parser, @pattern, @example, @flags)"))
        {
            Bind(insert, s);
            insert.ExecuteNonQuery();
        }

        using var last = Command(conn, tx, "SELECT last_insert_rowid()");
        return Convert.ToInt32(last.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Deletes the syntax; returns whether there was one.</summary>
    public static bool Delete(SqliteConnection conn, SqliteTransaction? tx, string table, int id)
    {
        using var cmd = Command(conn, tx, $"DELETE FROM {table} WHERE [Id] = @id");
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    ///     Adds each of the syntaxes that is acceptable next to the ones already there (a name or alias that is taken is
    ///     skipped).
    ///     Returns how many were added. Used to seed a new Silo and to restore the predefined syntaxes.
    /// </summary>
    public static int InsertMissing(SqliteConnection conn, SqliteTransaction? tx, string table, IEnumerable<SyntaxRecord> candidates)
    {
        var current = GetAll(conn, tx, table);
        int added   = 0;
        foreach (var candidate in candidates)
        {
            var s = SyntaxRules.Normalize(candidate with { Id = 0 });
            if (SyntaxRules.Validate(s, current).Count > 0) continue;

            int id = Insert(conn, tx, table, s);
            current.Add(s with { Id = id });
            added++;
        }

        return added;
    }
}