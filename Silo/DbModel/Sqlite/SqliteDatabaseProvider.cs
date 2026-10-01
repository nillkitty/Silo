using System.IO;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Serilog;
using Silo.DbModel.Global;
using Silo.DbModel.Worlds;

namespace Silo.DbModel.Sqlite;

/// <summary>
/// A SQLite-backed <see cref="IDatabaseProvider"/> for a single open Silo document.
/// </summary>
/// <remarks>
/// <para>
/// Holds one <see cref="SqliteConnection"/> against the Silo's extracted <c>z.db</c> (as the
/// connection's <c>main</c> schema), with <c>x.db</c>/<c>u.db</c>/<c>y.db</c> permanently
/// attached as <c>x</c>/<c>u</c>/<c>y</c>, and every World database (<c>w-*.db</c>) attached
/// on demand as <c>w_&lt;key&gt;</c>. This lets an entity reference that crosses database files
/// (e.g. a Temp-db row's reference to a Global-db <see cref="User"/>) be resolved and joined
/// against in plain SQL, and lets a whole document save be as simple as re-zipping the
/// extraction directory -- see <see cref="SiloArchive"/>.
/// </para>
/// <para>
/// <b>Scope of this implementation</b> (documented rather than silently assumed): there are no
/// SQL-level FOREIGN KEY/NOT NULL constraints -- reference integrity is handled at the
/// application layer only, partly because SQLite does not enforce FKs across ATTACHed databases
/// anyway. <see cref="AddItem{TItem}"/> is an upsert keyed by the entity's own <c>Id</c>; since
/// the interface returns only a <see cref="bool"/> (not the saved item), there is no way for
/// this layer to hand an auto-assigned Id back to the caller, so callers are expected to assign
/// a meaningful, unique Id before calling AddItem. Value-object properties that are not
/// themselves registered entities (<see cref="JoinRules"/>, <see cref="ProjRules"/>,
/// <see cref="ProvRules"/>, the pending-change types, <c>MvObj.FieldValues</c>, etc.) are
/// JSON-serialized into a single column rather than normalized into their own tables.
/// Encrypted Silos are not supported. <see cref="ImportFileAsync"/> is intentionally left a
/// stub -- importing external connector data is a separate, connector-specific concern.
/// </para>
/// </remarks>
public sealed class SqliteDatabaseProvider : IDatabaseProvider, IDisposable
{
    private readonly SqliteConnection            _conn;
    private readonly string                      _tempDir;
    private          string                      _siloPath;
    private readonly Dictionary<string, string>   _worldAliases = new();
    private readonly SemaphoreSlim                _gate         = new(1, 1);

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        NullValueHandling     = NullValueHandling.Include
    };

    internal SqliteDatabaseProvider(SqliteConnection conn, string tempDir, string siloPath)
    {
        _conn     = conn.Required();
        _tempDir  = tempDir.Required();
        _siloPath = siloPath.Required();
    }

    /// <summary>
    /// Attaches every <c>w-*.db</c> file already present in the working directory (called once,
    /// right after loading an existing Silo -- a freshly-built Silo has none yet).
    /// </summary>
    internal void AttachExistingWorldDatabases()
    {
        foreach (var file in Directory.EnumerateFiles(_tempDir, "w-*.db"))
        {
            var baseName = Path.GetFileNameWithoutExtension(file); // "w-<key>"
            var key      = baseName.Length > 2 ? baseName[2..] : baseName;
            var alias    = "w_" + key;
            if (_worldAliases.ContainsKey(alias)) continue;

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = $"ATTACH DATABASE '{file.Replace("'", "''")}' AS [{alias}];";
                cmd.ExecuteNonQuery();
            }

            foreach (var info in EntitySchema.WorldFamily)
            {
                using var cmd2 = _conn.CreateCommand();
                cmd2.CommandText = EntitySchema.BuildCreateTableSql(info, alias);
                cmd2.ExecuteNonQuery();
            }

            _worldAliases[alias] = Path.GetFileName(file);
        }
    }

    public bool AddItem<TItem>(TItem item)
    {
        if (item is null) return false;
        _gate.Wait();
        try
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                UpsertRecursive(item, tx, visited);
                tx.Commit();
                return true;
            }
            catch (Exception ex)
            {
                tx.Rollback();
                Log.Error(ex, "AddItem failed for {Type}", typeof(TItem).Name);
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool ItemExists<TItem>(TItem item)
    {
        if (item is null) return false;
        _gate.Wait();
        try
        {
            var info = EntitySchema.Get(item.GetType());
            if (info is null) return false;

            var alias = info.SchemaKind == SchemaKind.Fixed ? info.FixedAlias! : ResolveWorldAlias(item, attachIfMissing: false);
            if (alias is null) return false;

            using var cmd = _conn.CreateCommand();
            if (info.IdProperty is not null)
            {
                cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM [{alias}].[{info.TableName}] WHERE [Id] IS @id)";
                cmd.Parameters.AddWithValue("@id", info.IdProperty.GetValue(item) ?? DBNull.Value);
            }
            else
            {
                var cols  = ProjectColumns(item, info);
                var where = string.Join(" AND ", cols.Keys.Select(k => $"[{k}] IS @{k}"));
                cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM [{alias}].[{info.TableName}] WHERE {where})";
                foreach (var (k, v) in cols)
                    cmd.Parameters.AddWithValue("@" + k, v ?? DBNull.Value);
            }

            return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool RemoveItem<TItem>(TItem required)
    {
        if (required is null) return false;
        _gate.Wait();
        try
        {
            var info = EntitySchema.Get(required.GetType());
            if (info is null) return false;

            var alias = info.SchemaKind == SchemaKind.Fixed ? info.FixedAlias! : ResolveWorldAlias(required, attachIfMissing: false);
            if (alias is null) return false;

            using var cmd = _conn.CreateCommand();
            if (info.IdProperty is not null)
            {
                cmd.CommandText = $"DELETE FROM [{alias}].[{info.TableName}] WHERE [Id] IS @id";
                cmd.Parameters.AddWithValue("@id", info.IdProperty.GetValue(required) ?? DBNull.Value);
            }
            else
            {
                var cols  = ProjectColumns(required, info);
                var where = string.Join(" AND ", cols.Keys.Select(k => $"[{k}] IS @{k}"));
                cmd.CommandText = $"DELETE FROM [{alias}].[{info.TableName}] WHERE {where}";
                foreach (var (k, v) in cols)
                    cmd.Parameters.AddWithValue("@" + k, v ?? DBNull.Value);
            }

            return cmd.ExecuteNonQuery() > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ISiloUserContext GetUserContext()
    {
        _gate.Wait();
        try
        {
            WindowsIdentity? identity = null;
            try { identity = WindowsIdentity.GetCurrent(); }
            catch (Exception ex) { Log.Warning(ex, "Unable to determine the current Windows identity"); }

            var name = identity?.Name ?? $"{Environment.UserDomainName}\\{Environment.UserName}";
            var sid  = identity?.User?.Value ?? string.Empty;

            long userCount;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM [main].[User]";
                userCount = Convert.ToInt64(cmd.ExecuteScalar());
            }

            string? owner;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT [Owner] FROM [main].[Header] LIMIT 1";
                owner = cmd.ExecuteScalar() as string;
            }

            // Bootstrap: a Silo with no User rows yet, or whose Header names this user as the
            // owner, grants full rights. Otherwise rights come from any delegated Authorization
            // row for a matching User; a recognized-but-unauthorized user still gets read-only
            // rather than being shut out entirely (they got this far, i.e. the document opened).
            var isOwner = userCount == 0 || string.Equals(owner, name, StringComparison.OrdinalIgnoreCase);

            var level = SiloUserLevel.None;
            if (isOwner)
            {
                level = SiloUserLevel.Read | SiloUserLevel.Write | SiloUserLevel.Author | SiloUserLevel.Develop;
            }
            else
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText =
                    """
                    SELECT a.[Level] FROM [main].[Authorization] a
                    JOIN [main].[User] u ON u.[Id] = a.[AuthorizedUserId]
                    WHERE u.[SID] = @sid OR u.[Name] = @name
                    ORDER BY a.[Id] DESC LIMIT 1
                    """;
                cmd.Parameters.AddWithValue("@sid", sid);
                cmd.Parameters.AddWithValue("@name", name);
                var result = cmd.ExecuteScalar();
                level = result is null or DBNull ? SiloUserLevel.Read : (SiloUserLevel)Convert.ToInt32(result);
            }

            return new SqliteUserContext(
                IsSoloSilo: userCount <= 1,
                IsAuthorable: level.HasFlag(SiloUserLevel.Author),
                IsWritable: level.HasFlag(SiloUserLevel.Write),
                IsReadable: level.HasFlag(SiloUserLevel.Read),
                IsDeveloper: level.HasFlag(SiloUserLevel.Develop),
                IsOwner: isOwner);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ShutdownAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _conn.Close();
            _conn.Dispose();
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (Exception ex) { Log.Warning(ex, "Failed to clean up temp Silo directory '{Dir}'", _tempDir); }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await SiloArchive.RepackAsync(_tempDir, _siloPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MigrateToCopyAsync(string path)
    {
        await _gate.WaitAsync();
        try
        {
            await SiloArchive.RepackAsync(_tempDir, path);
            _siloPath = path;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> ImportFileAsync(object fn)
    {
        // Importing data from an external connector/file into a World is a connector-specific
        // operation and is intentionally out of scope for this pass.
        Log.Warning("ImportFileAsync is not implemented yet (requested import target: {Fn})", fn);
        return Task.FromResult(false);
    }

    public void Dispose() => _conn.Dispose();

    // ------------------------------------------------------------------------------------------

    private void UpsertRecursive(object item, SqliteTransaction tx, HashSet<object> visited)
    {
        if (!visited.Add(item)) return;

        var type = item.GetType();
        var info = EntitySchema.Get(type) ?? throw new InvalidOperationException(
            $"Type '{type.Name}' is not a registered persistable entity (see EntitySchema).");

        var values = new Dictionary<string, object?>();
        foreach (var col in info.Columns)
        {
            var raw = col.Property.GetValue(item);
            object? sqlVal;
            switch (col.Kind)
            {
                case ColumnKind.EntityRef:
                    if (raw is not null)
                    {
                        UpsertRecursive(raw, tx, visited);
                        var nestedInfo = EntitySchema.Get(raw.GetType())!;
                        sqlVal = nestedInfo.IdProperty!.GetValue(raw);
                    }
                    else
                    {
                        sqlVal = null;
                    }
                    break;

                case ColumnKind.Json:
                    sqlVal = raw is null ? null : JsonConvert.SerializeObject(raw, JsonSettings);
                    break;

                default:
                    sqlVal = SqliteTypeConvert.ToSqlValue(raw, col.Property.PropertyType);
                    break;
            }

            values[col.ColumnName] = sqlVal;
        }

        var alias = info.SchemaKind == SchemaKind.Fixed ? info.FixedAlias! : ResolveWorldAlias(item)!;
        ExecuteUpsert(info, alias, values, item, tx);
    }

    private void ExecuteUpsert(EntityInfo info, string alias, Dictionary<string, object?> values, object item, SqliteTransaction tx)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;

        if (info.IdProperty is not null)
        {
            var cols = new Dictionary<string, object?>(values) { ["Id"] = info.IdProperty.GetValue(item) };
            var colNames  = cols.Keys.ToList();
            var setClause = string.Join(", ", colNames.Where(c => c != "Id").Select(c => $"[{c}]=excluded.[{c}]"));

            cmd.CommandText =
                $"INSERT INTO [{alias}].[{info.TableName}] ({string.Join(", ", colNames.Select(c => $"[{c}]"))}) " +
                $"VALUES ({string.Join(", ", colNames.Select(c => "@" + c))}) " +
                (setClause.Length > 0
                     ? $"ON CONFLICT([Id]) DO UPDATE SET {setClause}"
                     : "ON CONFLICT([Id]) DO NOTHING");

            foreach (var (k, v) in cols)
                cmd.Parameters.AddWithValue("@" + k, v ?? DBNull.Value);
        }
        else
        {
            // Keyless entities: Header is a true singleton (replace its one row outright); other
            // keyless types (WorldSync) have no natural key, so AddItem is a plain insert and
            // duplicate-looking rows are a known, accepted limitation of having no key to upsert on.
            if (info.Type == typeof(Header))
            {
                using var del = _conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM [{alias}].[{info.TableName}]";
                del.ExecuteNonQuery();
            }

            var colNames = values.Keys.ToList();
            cmd.CommandText =
                $"INSERT INTO [{alias}].[{info.TableName}] ({string.Join(", ", colNames.Select(c => $"[{c}]"))}) " +
                $"VALUES ({string.Join(", ", colNames.Select(c => "@" + c))})";

            foreach (var (k, v) in values)
                cmd.Parameters.AddWithValue("@" + k, v ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }

    private Dictionary<string, object?> ProjectColumns(object item, EntityInfo info)
    {
        var values = new Dictionary<string, object?>();
        foreach (var col in info.Columns)
        {
            var raw = col.Property.GetValue(item);
            object? sqlVal;
            if (col.Kind == ColumnKind.EntityRef)
            {
                if (raw is not null)
                {
                    var nestedInfo = EntitySchema.Get(raw.GetType())!;
                    sqlVal = nestedInfo.IdProperty!.GetValue(raw);
                }
                else
                {
                    sqlVal = null;
                }
            }
            else if (col.Kind == ColumnKind.Json)
            {
                sqlVal = raw is null ? null : JsonConvert.SerializeObject(raw, JsonSettings);
            }
            else
            {
                sqlVal = SqliteTypeConvert.ToSqlValue(raw, col.Property.PropertyType);
            }

            values[col.ColumnName] = sqlVal;
        }

        return values;
    }

    /// <summary>
    /// Resolves (and, if requested, lazily attaches/provisions) the <c>w_&lt;key&gt;</c> schema
    /// alias a World-family entity belongs to, by walking its own World reference.
    /// </summary>
    private string? ResolveWorldAlias(object item, bool attachIfMissing = true)
    {
        var key = item switch
        {
            World w         => w.Key,
            WorldType wt    => wt.World?.Key,
            WorldField wf   => wf.World?.Key,
            WorldMapping wm => wm.World?.Key,
            WorldSync ws    => ws.World?.Key,
            FieldMapping fm => fm.Field?.World?.Key,
            _               => null
        };

        if (key is null)
            throw new InvalidOperationException(
                $"Cannot determine the owning World for a '{item.GetType().Name}' with no World reference set.");

        var sanitized = SanitizeKey(key);
        var alias     = "w_" + sanitized;
        if (_worldAliases.ContainsKey(alias)) return alias;
        if (!attachIfMissing) return null;

        var fileName = $"w-{sanitized}.db";
        var filePath = Path.Combine(_tempDir, fileName);

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"ATTACH DATABASE '{filePath.Replace("'", "''")}' AS [{alias}];";
            cmd.ExecuteNonQuery();
        }

        foreach (var info in EntitySchema.WorldFamily)
        {
            using var cmd2 = _conn.CreateCommand();
            cmd2.CommandText = EntitySchema.BuildCreateTableSql(info, alias);
            cmd2.ExecuteNonQuery();
        }

        _worldAliases[alias] = fileName;
        return alias;
    }

    private static string SanitizeKey(string key)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in key)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        return sb.Length == 0 ? "default" : sb.ToString();
    }
}

internal sealed record SqliteUserContext(
    bool IsSoloSilo,
    bool IsAuthorable,
    bool IsWritable,
    bool IsReadable,
    bool IsDeveloper,
    bool IsOwner) : ISiloUserContext;
