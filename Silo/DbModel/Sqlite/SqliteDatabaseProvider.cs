using System.IO;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Serilog;
using Silo.Contracts.Providers.Data;
using Silo.DbModel.Global;
using Silo.DbModel.Worlds;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     A SQLite-backed <see cref="IDatabaseProvider" /> for a single open Silo document.
/// </summary>
/// <remarks>
///     <para>
///         Holds one <see cref="SqliteConnection" /> against the Silo's extracted <c>z.db</c> (as the
///         connection's <c>main</c> schema), with <c>x.db</c>/<c>u.db</c>/<c>y.db</c> permanently
///         attached as <c>x</c>/<c>u</c>/<c>y</c>, and every World database (<c>w-*.db</c>) attached
///         on demand as <c>w_&lt;key&gt;</c>. This lets an entity reference that crosses database files
///         (e.g. a Temp-db row's reference to a Global-db <see cref="User" />) be resolved and joined
///         against in plain SQL, and lets a whole document save be as simple as re-zipping the
///         extraction directory -- see <see cref="SiloArchive" />.
///     </para>
///     <para>
///         <b>Scope of this implementation</b> (documented rather than silently assumed): there are no
///         SQL-level FOREIGN KEY/NOT NULL constraints -- reference integrity is handled at the
///         application layer only, partly because SQLite does not enforce FKs across ATTACHed databases
///         anyway. <see cref="AddItem{TItem}" /> is an upsert keyed by the entity's own <c>Id</c>; since
///         the interface returns only a <see cref="bool" /> (not the saved item), there is no way for
///         this layer to hand an auto-assigned Id back to the caller, so callers are expected to assign
///         a meaningful, unique Id before calling AddItem. Value-object properties that are not
///         themselves registered entities (<see cref="JoinRules" />, <see cref="ProjRules" />,
///         <see cref="ProvRules" />, the pending-change types, <c>MvObj.FieldValues</c>, etc.) are
///         JSON-serialized into a single column rather than normalized into their own tables.
///         Two storage formats share this one implementation (see <see cref="SiloLayout" />): <b>archive</b> (zip of
///         several database files, extracted to a working directory) and <b>simple</b> (one bare SQLite file; the
///         original is copied to a working copy so it is never locked and other processes can use it while it is open).
///         Saving, the <c>SaveLog</c>, conflict detection and format conversion live in
///         <c>SqliteDatabaseProvider.Files.cs</c>.
///         Encrypted Silos are not supported. <see cref="ImportFileAsync" /> is intentionally left a
///         stub -- importing external connector data is a separate, connector-specific concern.
///     </para>
/// </remarks>
public sealed partial class SqliteDatabaseProvider : IDatabaseProvider, IDatabaseChanges, ISiloFile, IDisposable
{
    private static readonly JsonSerializerSettings JsonSettings = new()
                                                                  {
                                                                      ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                                                                      NullValueHandling     = NullValueHandling.Include
                                                                  };

    /// <summary>
    ///     SQLite allows 10 attached databases by default; x, u and y use three, leaving this many for Worlds in the archive
    ///     layout. (The simple layout has no such limit.)
    /// </summary>
    internal const int MaxArchiveWorlds = 7;

    private readonly SemaphoreSlim              _gate = new(1, 1);
    private readonly Dictionary<string, string> _worldAliases = new();
    private          SqliteConnection           _conn;
    private          SiloLayout                 _layout;
    private          string                     _siloPath;
    private          string                     _tempDir;

    /// <summary>The qualified physical table name for a logical (alias, table), per the storage format in use.</summary>
    private string T(string alias, string table)
    {
        return _layout.Table(alias, table);
    }

    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Makes every World already present in the document available (called once, right after loading an existing Silo --
    ///     a freshly-built Silo has none yet). Archive layout: attaches every <c>w-*.db</c> in the working directory.
    ///     Simple layout: finds the <c>w_&lt;key&gt;_World</c> tables.
    /// </summary>
    internal void AttachExistingWorldDatabases()
    {
        if (_layout.Format == SiloStorageFormat.Simple)
        {
            var keys = new List<string>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = """SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'w\_%\_World' ESCAPE '\'""";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    if (SiloLayout.WorldKeyFromTable(reader.GetString(0)) is { } key)
                        keys.Add(key);
            }

            foreach (var key in keys)
            {
                var alias = "w_" + key;
                if (_worldAliases.ContainsKey(alias)) continue;

                EnsureWorldTables(alias);
                _worldAliases[alias] = string.Empty;
            }

            return;
        }

        foreach (var file in Directory.EnumerateFiles(_tempDir, "w-*.db"))
        {
            var baseName = Path.GetFileNameWithoutExtension(file); // "w-<key>"
            var key      = baseName.Length > 2 ? baseName[2..] : baseName;
            var alias    = "w_" + key;
            if (_worldAliases.ContainsKey(alias)) continue;

            SqliteConnectionFactory.Attach(_conn, file, alias);
            EnsureWorldTables(alias);
            _worldAliases[alias] = Path.GetFileName(file);
        }
    }

    private void EnsureWorldTables(string alias)
    {
        foreach (var info in EntitySchema.WorldFamily)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = EntitySchema.BuildCreateTableSql(info, alias, _layout);
            cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>Walks the same entity graph an upsert will, making sure every World-family entity's World is attached and has its tables.</summary>
    private void ProvisionWorlds(object item, HashSet<object> visited)
    {
        if (!visited.Add(item)) return;

        var info = EntitySchema.Get(item.GetType());
        if (info is null) return;

        if (info.SchemaKind == SchemaKind.WorldFamily) ResolveWorldAlias(item);

        foreach (var col in info.Columns)
            if (col.Kind == ColumnKind.EntityRef && col.Property.GetValue(item) is { } nested)
                ProvisionWorlds(nested, visited);
    }

    private void UpsertRecursive(object item, SqliteTransaction tx, HashSet<object> visited)
    {
        if (!visited.Add(item)) return;

        var type = item.GetType();
        var info = EntitySchema.Get(type) ??
                   throw new InvalidOperationException($"Type '{type.Name}' is not a registered persistable entity (see EntitySchema).");

        var values = new Dictionary<string, object?>();
        foreach (var col in info.Columns)
        {
            var     raw = col.Property.GetValue(item);
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
            var cols      = new Dictionary<string, object?>(values) { ["Id"] = info.IdProperty.GetValue(item) };
            var colNames  = cols.Keys.ToList();
            var setClause = string.Join(", ", colNames.Where(c => c != "Id").Select(c => $"[{c}]=excluded.[{c}]"));

            cmd.CommandText = $"INSERT INTO {T(alias, info.TableName)} ({string.Join(", ", colNames.Select(c => $"[{c}]"))}) " +
                              $"VALUES ({string.Join(", ",                                   colNames.Select(c => "@" + c))}) "  +
                              (setClause.Length > 0 ? $"ON CONFLICT([Id]) DO UPDATE SET {setClause}" : "ON CONFLICT([Id]) DO NOTHING");

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
                del.CommandText = $"DELETE FROM {T(alias, info.TableName)}";
                del.ExecuteNonQuery();
            }

            var colNames = values.Keys.ToList();
            cmd.CommandText = $"INSERT INTO {T(alias, info.TableName)} ({string.Join(", ", colNames.Select(c => $"[{c}]"))}) " +
                              $"VALUES ({string.Join(", ",                                   colNames.Select(c => "@" + c))})";

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
            var     raw = col.Property.GetValue(item);
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
    ///     Resolves (and, if requested, lazily attaches/provisions) the <c>w_&lt;key&gt;</c> schema
    ///     alias a World-family entity belongs to, by walking its own World reference.
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
                      AuthoredTable at => at.World?.Key,
                      _               => null
                  };

        if (key is null)
            throw new
                InvalidOperationException($"Cannot determine the owning World for a '{item.GetType().Name}' with no World reference set.");

        var sanitized = SanitizeKey(key);
        var alias     = "w_" + sanitized;
        if (_worldAliases.ContainsKey(alias)) return alias;
        if (!attachIfMissing) return null;

        if (_layout.Format == SiloStorageFormat.Simple)
        {
            // One file: a World is just another set of prefixed tables.
            EnsureWorldTables(alias);
            _worldAliases[alias] = string.Empty;
            return alias;
        }

        if (_worldAliases.Count >= MaxArchiveWorlds)
            throw new InvalidOperationException($"An archive-format Silo can hold at most {MaxArchiveWorlds} Worlds (SQLite attaches at most 10 " +
                                                "database files to one connection). Remove a World or combine connectors.");

        var fileName = $"w-{sanitized}.db";
        SqliteConnectionFactory.Attach(_conn, Path.Combine(_tempDir, fileName), alias);
        EnsureWorldTables(alias);

        _worldAliases[alias] = fileName;
        return alias;
    }

    private static string SanitizeKey(string key)
    {
        var sb = new StringBuilder();
        foreach (var ch in key)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        return sb.Length == 0 ? "default" : sb.ToString();
    }

    internal SqliteDatabaseProvider(SqliteConnection conn, string tempDir, string siloPath, SiloLayout layout, string? workingFile = null,
                                    DiskState?       disk = null)
    {
        _conn        = conn.Required();
        _tempDir     = tempDir.Required();
        _siloPath    = siloPath.Required();
        _layout      = layout.Required();
        _workingFile = workingFile;
        _disk        = disk;
        _isSolo      = ReadIsSolo();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>
    ///     Adds an item to the Silo, using the schema to determine which database and table represents the supplied object's
    ///     type.
    /// </summary>
    /// <typeparam name="TItem">The type of item being added;  determines where the item goes</typeparam>
    /// <param name="item">The item being added.</param>
    /// <returns>Returns true if the item was added, or false if it existed.</returns>
    public bool AddItem<TItem>(TItem item)
    {
        if (item is null) return false;
        _gate.Wait();
        try
        {
            // Worlds are attached/created *before* the transaction starts: SQLite cannot ATTACH inside one, and a command
            // on a connection with a pending transaction must carry that transaction.
            try
            {
                ProvisionWorlds(item, new HashSet<object>(ReferenceEqualityComparer.Instance));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AddItem failed for {Type}: its World could not be provisioned", typeof(TItem).Name);
                return false;
            }

            using var tx = _conn.BeginTransaction();
            try
            {
                var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                UpsertRecursive(item, tx, visited);
                tx.Commit();
                RaiseChanged();
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

    /// <summary>
    ///     Gets whether an item exists in the Silo, based on the item's type to determine where it would be found.
    /// </summary>
    public bool ItemExists<TItem>(TItem item)
    {
        if (item is null) return false;
        _gate.Wait();
        try
        {
            var info = EntitySchema.Get(item.GetType());
            if (info is null) return false;

            var alias = info.SchemaKind == SchemaKind.Fixed ? info.FixedAlias! : ResolveWorldAlias(item, false);
            if (alias is null) return false;

            using var cmd = _conn.CreateCommand();
            if (info.IdProperty is not null)
            {
                cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {T(alias, info.TableName)} WHERE [Id] IS @id)";
                cmd.Parameters.AddWithValue("@id", info.IdProperty.GetValue(item) ?? DBNull.Value);
            }
            else
            {
                var cols  = ProjectColumns(item, info);
                var where = string.Join(" AND ", cols.Keys.Select(k => $"[{k}] IS @{k}"));
                cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {T(alias, info.TableName)} WHERE {where})";
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

    /// <summary>
    ///     Removes an item from the Silo (and returns true) if it exists, or returns false if it wasn't found in the Silo.
    /// </summary>
    public bool RemoveItem<TItem>(TItem required)
    {
        if (required is null) return false;
        _gate.Wait();
        try
        {
            var info = EntitySchema.Get(required.GetType());
            if (info is null) return false;

            var alias = info.SchemaKind == SchemaKind.Fixed ? info.FixedAlias! : ResolveWorldAlias(required, false);
            if (alias is null) return false;

            using var cmd = _conn.CreateCommand();
            if (info.IdProperty is not null)
            {
                cmd.CommandText = $"DELETE FROM {T(alias, info.TableName)} WHERE [Id] IS @id";
                cmd.Parameters.AddWithValue("@id", info.IdProperty.GetValue(required) ?? DBNull.Value);
            }
            else
            {
                var cols  = ProjectColumns(required, info);
                var where = string.Join(" AND ", cols.Keys.Select(k => $"[{k}] IS @{k}"));
                cmd.CommandText = $"DELETE FROM {T(alias, info.TableName)} WHERE {where}";
                foreach (var (k, v) in cols)
                    cmd.Parameters.AddWithValue("@" + k, v ?? DBNull.Value);
            }

            bool removed = cmd.ExecuteNonQuery() > 0;
            if (removed) RaiseChanged();
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Gets the user context based on the current user identity and the Silo's configured Users and Owner.
    /// </summary>
    public ISiloUserContext GetUserContext()
    {
        _gate.Wait();
        try
        {
            WindowsIdentity? identity = null;
            try
            {
                identity = WindowsIdentity.GetCurrent();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Unable to determine the current Windows identity");
            }

            var name = identity?.Name        ?? $"{Environment.UserDomainName}\\{Environment.UserName}";
            var sid  = identity?.User?.Value ?? string.Empty;

            long userCount;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM [main].[User]";
                userCount       = Convert.ToInt64(cmd.ExecuteScalar());
            }

            string? owner;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT [Owner] FROM [main].[Header] LIMIT 1";
                owner           = cmd.ExecuteScalar() as string;
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
                cmd.CommandText = """
                                  SELECT a.[Level] FROM [main].[Authorization] a
                                  JOIN [main].[User] u ON u.[Id] = a.[AuthorizedUserId]
                                  WHERE u.[SID] = @sid OR u.[Name] = @name
                                  ORDER BY a.[Id] DESC LIMIT 1
                                  """;
                cmd.Parameters.AddWithValue("@sid",  sid);
                cmd.Parameters.AddWithValue("@name", name);
                var result = cmd.ExecuteScalar();
                level = result is null or DBNull ? SiloUserLevel.Read : (SiloUserLevel)Convert.ToInt32(result);
            }

            return new SqliteUserContext(_isSolo, level.HasFlag(SiloUserLevel.Author), level.HasFlag(SiloUserLevel.Write),
                                         level.HasFlag(SiloUserLevel.Read), level.HasFlag(SiloUserLevel.Develop), isOwner);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Imports a file into the Silo
    /// </summary>
    public Task<bool> ImportFileAsync(object fn)
    {
        // Importing data from an external connector/file into a World is a connector-specific
        // operation and is intentionally out of scope for this pass.
        Log.Warning("ImportFileAsync is not implemented yet (requested import target: {Fn})", fn);
        return Task.FromResult(false);
    }

    public void Dispose()
    {
        _conn.Dispose();
    }
}

internal sealed record SqliteUserContext(
    bool IsSoloSilo,
    bool IsAuthorable,
    bool IsWritable,
    bool IsReadable,
    bool IsDeveloper,
    bool IsOwner) : ISiloUserContext;