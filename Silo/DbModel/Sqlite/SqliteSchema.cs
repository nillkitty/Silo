using System.Reflection;
using Microsoft.Data.Sqlite;
using Serilog;
using Silo.DbModel.Global;
using Silo.DbModel.Meta;
using Silo.DbModel.Temp;
using Silo.DbModel.Universal;
using Silo.DbModel.Worlds;
using Silo.Providers.Data;
using Silo.Providers.Definitions;
using Silo.ViewModel;
using DataBinding = Silo.DbModel.Global.DataBinding;
using GFile = Silo.DbModel.Global.File;
using GAssembly = Silo.DbModel.Global.Assembly;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     Which attached SQLite schema a persisted entity type lives in.
/// </summary>
public enum SchemaKind
{
    /// <summary>
    ///     Always lives under the same, fixed schema alias (e.g. 'main', 'x', 'u', 'y') no matter
    ///     which Silo document or World it's part of.
    /// </summary>
    Fixed,

    /// <summary>
    ///     Lives in one of potentially many per-connector World databases (w-*.db); which one is
    ///     determined per-instance by walking the object's own World reference.
    /// </summary>
    WorldFamily
}

/// <summary>
///     How a mapped property's value is stored.
/// </summary>
public enum ColumnKind
{
    /// <summary>Stored directly as a typed column.</summary>
    Scalar,

    /// <summary>
    ///     Stored as an integer column holding the referenced entity's Id; the referenced
    ///     entity is cascaded (saved/attached) alongside its owner.
    /// </summary>
    EntityRef,

    /// <summary>
    ///     Stored as a JSON-serialized TEXT column. Used for value-object types that are
    ///     not themselves registered, persisted entities (e.g. <see cref="Silo.DbModel.Worlds.JoinRules" />,
    ///     <see cref="Silo.DbModel.Meta.PendingUpdate" />, or <c>MvObj.FieldValues</c>).
    /// </summary>
    Json
}

public sealed class ColumnMap
{
    public required string       ColumnName { get; init; }
    public required ColumnKind   Kind       { get; init; }
    public required PropertyInfo Property   { get; init; }
}

public sealed class EntityInfo
{
    public required List<ColumnMap> Columns    { get; init; }
    public          string?         FixedAlias { get; init; }
    public          PropertyInfo?   IdProperty { get; init; }
    public required SchemaKind      SchemaKind { get; init; }
    public required string          TableName  { get; init; }
    public required Type            Type       { get; init; }
}

/// <summary>
///     The curated registry mapping <c>DbModel</c> classes onto SQLite tables.
/// </summary>
/// <remarks>
///     <para>
///         This is deliberately a curated list rather than "every type found under DbModel" -- several
///         types there are not persisted entities at all: view-model wrappers (<see cref="SiloNode" />),
///         parser/tokenizer types (<see cref="Silo.Parsing" />), and plain value-object classes that hang
///         off an entity (<see cref="JoinRules" />, <see cref="ProjRules" />, <see cref="ProvRules" />,
///         <see cref="Silo.DbModel.Worlds.ProjRules" />'s <c>WorldFilter</c>, <see cref="PendingDelete" />,
///         <see cref="PendingUpdate" />, <see cref="PendingCreate" />). Any property whose type is not in
///         this registry is treated as a value object and JSON-serialized into a single column instead
///         of being normalized into its own table -- that is how those value-object types are handled,
///         without needing to special-case them individually.
///     </para>
///     <para>
///         <see cref="Silo.DbModel.Meta.Unit" /> and <see cref="Silo.DbModel.Meta.UnitRelation" /> live
///         (physically) under <c>DbModel/Meta</c> but are registered against the Universal database, per
///         the project's repo-overview notes -- they're unit-of-measure reference data, not metaverse
///         state.
///     </para>
/// </remarks>
public static class EntitySchema
{
    public const string MainAlias      = "main";
    public const string TempAlias      = "x";
    public const string UniversalAlias = "u";
    public const string MetaverseAlias = "y";

    private static readonly Dictionary<Type, EntityInfo> _byType = new();

    /// <summary>
    ///     Public properties that are not persisted: (entity type, property name). An <c>ItemContainer</c>'s <c>Items</c>
    ///     is kept as rows of its own table, not as a column.
    /// </summary>
    private static readonly HashSet<(Type, string)> _ignored = [(typeof(MvEnum), nameof(MvEnum.Items))];

    private static readonly HashSet<Type> _scalarTypes =
    [
        typeof(int), typeof(int?), typeof(long), typeof(long?),
        typeof(double), typeof(double?), typeof(float), typeof(float?),
        typeof(bool), typeof(bool?), typeof(string),
        typeof(DateTime), typeof(DateTime?), typeof(TimeSpan), typeof(TimeSpan?),
        typeof(Guid), typeof(Guid?), typeof(Uri), typeof(byte[])
    ];

    public static IReadOnlyDictionary<Type, EntityInfo> All => _byType;

    public static IEnumerable<EntityInfo> WorldFamily => _byType.Values.Where(i => i.SchemaKind == SchemaKind.WorldFamily);

    public static EntityInfo? Get(Type type)
    {
        return _byType.GetValueOrDefault(type);
    }

    public static bool IsRegisteredEntity(Type type)
    {
        return _byType.ContainsKey(type);
    }

    private static void Register(Type type, string tableName, SchemaKind kind, string? fixedAlias)
    {
        _byType[type] = new EntityInfo
                        {
                            Type       = type,
                            TableName  = tableName,
                            SchemaKind = kind,
                            FixedAlias = fixedAlias,
                            IdProperty = type.GetProperty("Id"),
                            Columns    = []
                        };
    }

    private static EntityInfo Describe(Type type, EntityInfo skeleton)
    {
        var columns = new List<ColumnMap>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            if (!prop.CanRead || !prop.CanWrite) continue; // skip computed-only properties
            if (_ignored.Contains((type, prop.Name))) continue;

            var kind = IsRegisteredEntity(prop.PropertyType) ? ColumnKind.EntityRef :
                       IsScalar(prop.PropertyType)           ? ColumnKind.Scalar : ColumnKind.Json;

            var columnName = kind == ColumnKind.EntityRef ? prop.Name + "Id" : prop.Name;
            columns.Add(new ColumnMap { Property = prop, ColumnName = columnName, Kind = kind });
        }

        return new EntityInfo
               {
                   Type       = skeleton.Type,
                   TableName  = skeleton.TableName,
                   SchemaKind = skeleton.SchemaKind,
                   FixedAlias = skeleton.FixedAlias,
                   IdProperty = skeleton.IdProperty,
                   Columns    = columns
               };
    }

    public static bool IsScalar(Type t)
    {
        if (_scalarTypes.Contains(t)) return true;
        var under = Nullable.GetUnderlyingType(t);
        return t.IsEnum || (under?.IsEnum ?? false);
    }

    /// <summary>
    ///     Builds the (idempotent) CREATE TABLE statement for an entity under a given attached
    ///     schema alias. All identifiers are bracket-quoted so that column/table names which
    ///     happen to collide with SQLite keywords (e.g. the <see cref="Table" /> and <c>Key</c>
    ///     types/properties) are never a problem.
    /// </summary>
    public static string BuildCreateTableSql(EntityInfo info, string alias, SiloLayout? layout = null)
    {
        var parts = new List<string>();
        if (info.IdProperty is not null)
            parts.Add("[Id] INTEGER PRIMARY KEY");

        foreach (var col in info.Columns)
        {
            if (info.IdProperty is not null && col.Property == info.IdProperty) continue;
            parts.Add($"[{col.ColumnName}] {SqlTypeAffinity(col.Property.PropertyType, col.Kind)}");
        }

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE IF NOT EXISTS ").Append((layout ?? SiloLayout.Archive).Table(alias, info.TableName)).Append(" (");
        sb.Append(string.Join(", ", parts));
        sb.Append(");");
        return sb.ToString();
    }

    private static string SqlTypeAffinity(Type propType, ColumnKind kind)
    {
        if (kind == ColumnKind.EntityRef) return "INTEGER";
        if (kind == ColumnKind.Json) return "TEXT";

        var t = Nullable.GetUnderlyingType(propType) ?? propType;
        if (t.IsEnum) return "INTEGER";
        if (t == typeof(int)    || t == typeof(long) || t == typeof(bool) || t == typeof(TimeSpan)) return "INTEGER";
        if (t == typeof(double) || t == typeof(float)) return "REAL";
        if (t == typeof(byte[])) return "BLOB";
        return "TEXT"; // string, DateTime, Guid, Uri
    }

    /// <summary>
    ///     Creates the tables of every fixed entity that is missing (and adds columns an older build did not have). A Silo
    ///     that has no syntax table yet
    ///     -- a new one, or one made before syntaxes existed -- gets the predefined syntaxes (<see cref="SyntaxSeeds" />)
    ///     added once, as the table is
    ///     created, so deleting them later sticks; pass <paramref name="seed" /> false when the rows are about to be copied in
    ///     from elsewhere.
    /// </summary>
    public static void EnsureFixedSchemas(SqliteConnection conn, SiloLayout? layout = null, bool seed = true)
    {
        bool syntaxesAreNew = false;
        foreach (var info in _byType.Values.Where(i => i.SchemaKind == SchemaKind.Fixed))
        {
            if (info.Type == typeof(MvSyntax) && !TableExists(conn, layout ?? SiloLayout.Archive, info.FixedAlias!, info.TableName))
                syntaxesAreNew = true;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = BuildCreateTableSql(info, info.FixedAlias!, layout);
                cmd.ExecuteNonQuery();
            }

            // A Silo made by an older build has the table without columns added since: add them (empty), so the model
            // can grow without older Silos refusing to open.
            var columns = info.Columns.Where(c => info.IdProperty is null || c.Property != info.IdProperty)
                              .Select(c => (c.ColumnName, SqlTypeAffinity(c.Property.PropertyType, c.Kind)));
            try
            {
                SchemaUpgrade.AddMissingColumns(conn, (layout ?? SiloLayout.Archive).Table(info.FixedAlias!, info.TableName), columns);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not bring the {table} table up to date", info.TableName);
            }
        }

        if (seed && syntaxesAreNew) SeedSyntaxes(conn, layout ?? SiloLayout.Archive);
    }

    private static bool TableExists(SqliteConnection conn, SiloLayout layout, string alias, string table)
    {
        // the simple layout keeps every table in 'main', prefixing the ones that belong to another database (see SiloLayout.Table)
        bool      simple = layout.Format == SiloStorageFormat.Simple;
        using var cmd    = conn.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM [{(simple ? MainAlias : alias)}].sqlite_master WHERE type = 'table' AND name = @name LIMIT 1";
        cmd.Parameters.AddWithValue("@name", simple && alias != MainAlias ? $"{alias}_{table}" : table);
        return cmd.ExecuteScalar() is not null;
    }

    private static void SeedSyntaxes(SqliteConnection conn, SiloLayout layout)
    {
        try
        {
            using var tx    = conn.BeginTransaction();
            int       added = SyntaxRows.InsertMissing(conn, tx, layout.Table(MetaverseAlias, nameof(MvSyntax)), SyntaxSeeds.Defaults);
            tx.Commit();
            Log.Debug("Seeded {count} predefined syntaxes", added);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not add the predefined syntaxes"); // Restore Defaults can add them later
        }
    }

    static EntitySchema()
    {
        // --- Global database (z.db) -> 'main' -------------------------------------------------
        Register(typeof(Header), nameof(Header), SchemaKind.Fixed, MainAlias);
        Register(typeof(User), nameof(User), SchemaKind.Fixed, MainAlias);
        Register(typeof(Window), nameof(Window), SchemaKind.Fixed, MainAlias);
        Register(typeof(Audit), nameof(Audit), SchemaKind.Fixed, MainAlias);
        Register(typeof(Connection), nameof(Connection), SchemaKind.Fixed, MainAlias);
        Register(typeof(Location), nameof(Location), SchemaKind.Fixed, MainAlias);
        Register(typeof(Pref), nameof(Pref), SchemaKind.Fixed, MainAlias);
        Register(typeof(Secret), nameof(Secret), SchemaKind.Fixed, MainAlias);
        Register(typeof(Node), nameof(Node), SchemaKind.Fixed, MainAlias);
        Register(typeof(GFile), "File", SchemaKind.Fixed, MainAlias);
        Register(typeof(Authorization), nameof(Authorization), SchemaKind.Fixed, MainAlias);
        Register(typeof(GAssembly), "Assembly", SchemaKind.Fixed, MainAlias);
        Register(typeof(Diagram), nameof(Diagram), SchemaKind.Fixed, MainAlias);
        Register(typeof(DataBinding), nameof(DataBinding), SchemaKind.Fixed, MainAlias); // live values sampled while the Silo is open
        Register(typeof(SaveLogEntry), "SaveLog", SchemaKind.Fixed, MainAlias);
        Register(typeof(StoredItem), "Stored", SchemaKind.Fixed, MainAlias); // Stored<T>: values of any shape kept as text (the Silo's own)

        // --- Universal database (u.db) -> 'u' -------------------------------------------------
        Register(typeof(UniversalType),     nameof(UniversalType),     SchemaKind.Fixed, UniversalAlias);
        Register(typeof(UniversalProvider), nameof(UniversalProvider), SchemaKind.Fixed, UniversalAlias);
        Register(typeof(Unit),              nameof(Unit),              SchemaKind.Fixed, UniversalAlias);
        Register(typeof(UnitRelation),      nameof(UnitRelation),      SchemaKind.Fixed, UniversalAlias);

        // --- Temp / scratch database (x.db) -> 'x' --------------------------------------------
        Register(typeof(Scalar),      nameof(Scalar),      SchemaKind.Fixed, TempAlias);
        Register(typeof(Table),       nameof(Table),       SchemaKind.Fixed, TempAlias);
        Register(typeof(Function),    nameof(Function),    SchemaKind.Fixed, TempAlias);
        Register(typeof(Synonym),     nameof(Synonym),     SchemaKind.Fixed, TempAlias);
        Register(typeof(Pasted),      nameof(Pasted),      SchemaKind.Fixed, TempAlias);
        Register(typeof(ReplStack),   nameof(ReplStack),   SchemaKind.Fixed, TempAlias);
        Register(typeof(OpenFile),    nameof(OpenFile),    SchemaKind.Fixed, TempAlias);
        Register(typeof(OpenFileMru), nameof(OpenFileMru), SchemaKind.Fixed, TempAlias);
        Register(typeof(VariableMru), nameof(VariableMru), SchemaKind.Fixed, TempAlias);
        Register(typeof(AssemblyMru), nameof(AssemblyMru), SchemaKind.Fixed, TempAlias);
        Register(typeof(ColorMru),    nameof(ColorMru),    SchemaKind.Fixed, TempAlias);
        Register(typeof(TextMru),     nameof(TextMru),     SchemaKind.Fixed, TempAlias);
        Register(typeof(PeopleMru),   nameof(PeopleMru),   SchemaKind.Fixed, TempAlias);
        Register(typeof(SessionStoredItem), "Stored", SchemaKind.Fixed,
                 TempAlias); // the same table, for what is only remembered for the session

        // --- Metaverse database (y.db) -> 'y' --------------------------------------------------
        Register(typeof(MvType),           nameof(MvType),           SchemaKind.Fixed, MetaverseAlias);
        Register(typeof(MvTypeHierarchy),  nameof(MvTypeHierarchy),  SchemaKind.Fixed, MetaverseAlias);
        Register(typeof(MvAttribute),      nameof(MvAttribute),      SchemaKind.Fixed, MetaverseAlias);
        Register(typeof(MvObj),            nameof(MvObj),            SchemaKind.Fixed, MetaverseAlias);
        Register(typeof(MvEnum),           nameof(MvEnum),           SchemaKind.Fixed, MetaverseAlias);
        Register(typeof(MvEnumValue),      nameof(MvEnumValue),      SchemaKind.Fixed, MetaverseAlias);
        Register(typeof(MvSyntax),         nameof(MvSyntax),         SchemaKind.Fixed, MetaverseAlias);
        Register(typeof(Connector),        nameof(Connector),        SchemaKind.Fixed, MetaverseAlias);
        Register(typeof(ConnectorPending), nameof(ConnectorPending), SchemaKind.Fixed, MetaverseAlias);

        // --- World database(s) (w-*.db) -> 'w_<key>', one per connector ------------------------
        Register(typeof(World),        nameof(World),        SchemaKind.WorldFamily, null);
        Register(typeof(WorldType),    nameof(WorldType),    SchemaKind.WorldFamily, null);
        Register(typeof(WorldField),   nameof(WorldField),   SchemaKind.WorldFamily, null);
        Register(typeof(FieldMapping), nameof(FieldMapping), SchemaKind.WorldFamily, null);
        Register(typeof(WorldMapping), nameof(WorldMapping), SchemaKind.WorldFamily, null);
        Register(typeof(WorldSync),    nameof(WorldSync),    SchemaKind.WorldFamily, null);
        Register(typeof(AuthoredTable), nameof(AuthoredTable), SchemaKind.WorldFamily,
                 null); // rows of the built-in local world (see LocalWorld)

        // Second pass: now that every entity type is known, classify each type's columns
        // (an EntityRef column is any property whose type is itself a registered entity).
        foreach (var type in _byType.Keys.ToList())
            _byType[type] = Describe(type, _byType[type]);
    }
}

/// <summary>
///     Conversions between CLR property values and the values Microsoft.Data.Sqlite will bind as
///     parameters. SQLite is dynamically typed, so most of this is about picking a stable, portable
///     on-disk representation (ISO-8601 text for dates, ticks for TimeSpan, 0/1 for bool, etc.)
///     rather than working around any real storage restriction.
/// </summary>
public static class SqliteTypeConvert
{
    public static object? ToSqlValue(object? clrValue, Type propType)
    {
        if (clrValue is null) return null;

        var t = Nullable.GetUnderlyingType(propType) ?? propType;
        if (t.IsEnum) return Convert.ToInt64(clrValue);
        if (t == typeof(DateTime)) return ((DateTime)clrValue).ToString("O");
        if (t == typeof(TimeSpan)) return ((TimeSpan)clrValue).Ticks;
        if (t == typeof(Guid)) return ((Guid)clrValue).ToString();
        if (t == typeof(Uri)) return ((Uri)clrValue).ToString();
        if (t == typeof(bool)) return (bool)clrValue ? 1L : 0L;
        return clrValue; // int/long/double/float/string/byte[] pass straight through
    }
}