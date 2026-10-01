using System.Reflection;
using Microsoft.Data.Sqlite;
using Silo.DbModel.Global;
using Silo.DbModel.Meta;
using Silo.DbModel.Temp;
using Silo.DbModel.Universal;
using Silo.DbModel.Worlds;
using Silo.ViewModel;
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
///     The curated registry mapping <c>DbModel</c> record/class types onto SQLite tables.
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
            if (prop.Name == "EqualityContract") continue;

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
    public static string BuildCreateTableSql(EntityInfo info, string alias)
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
        sb.Append("CREATE TABLE IF NOT EXISTS [").Append(alias).Append("].[").Append(info.TableName).Append("] (");
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

    public static void EnsureFixedSchemas(SqliteConnection conn)
    {
        foreach (var info in _byType.Values.Where(i => i.SchemaKind == SchemaKind.Fixed))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = BuildCreateTableSql(info, info.FixedAlias!);
            cmd.ExecuteNonQuery();
        }
    }

    static EntitySchema()
    {
        // --- Global database (z.db) -> 'main' -------------------------------------------------
        Register(typeof(Header),        nameof(Header),        SchemaKind.Fixed, MainAlias);
        Register(typeof(User),          nameof(User),          SchemaKind.Fixed, MainAlias);
        Register(typeof(Window),        nameof(Window),        SchemaKind.Fixed, MainAlias);
        Register(typeof(Audit),         nameof(Audit),         SchemaKind.Fixed, MainAlias);
        Register(typeof(Connection),    nameof(Connection),    SchemaKind.Fixed, MainAlias);
        Register(typeof(Location),      nameof(Location),      SchemaKind.Fixed, MainAlias);
        Register(typeof(Pref),          nameof(Pref),          SchemaKind.Fixed, MainAlias);
        Register(typeof(Secret),        nameof(Secret),        SchemaKind.Fixed, MainAlias);
        Register(typeof(Node),          nameof(Node),          SchemaKind.Fixed, MainAlias);
        Register(typeof(GFile),         "File",                SchemaKind.Fixed, MainAlias);
        Register(typeof(Authorization), nameof(Authorization), SchemaKind.Fixed, MainAlias);
        Register(typeof(GAssembly),     "Assembly",            SchemaKind.Fixed, MainAlias);

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

        // --- Metaverse database (y.db) -> 'y' --------------------------------------------------
        Register(typeof(MvType),           nameof(MvType),           SchemaKind.Fixed, MetaverseAlias);
        Register(typeof(MvTypeHierarchy),  nameof(MvTypeHierarchy),  SchemaKind.Fixed, MetaverseAlias);
        Register(typeof(MvAttribute),      nameof(MvAttribute),      SchemaKind.Fixed, MetaverseAlias);
        Register(typeof(MvObj),            nameof(MvObj),            SchemaKind.Fixed, MetaverseAlias);
        Register(typeof(Connector),        nameof(Connector),        SchemaKind.Fixed, MetaverseAlias);
        Register(typeof(ConnectorPending), nameof(ConnectorPending), SchemaKind.Fixed, MetaverseAlias);

        // --- World database(s) (w-*.db) -> 'w_<key>', one per connector ------------------------
        Register(typeof(World),        nameof(World),        SchemaKind.WorldFamily, null);
        Register(typeof(WorldType),    nameof(WorldType),    SchemaKind.WorldFamily, null);
        Register(typeof(WorldField),   nameof(WorldField),   SchemaKind.WorldFamily, null);
        Register(typeof(FieldMapping), nameof(FieldMapping), SchemaKind.WorldFamily, null);
        Register(typeof(WorldMapping), nameof(WorldMapping), SchemaKind.WorldFamily, null);
        Register(typeof(WorldSync),    nameof(WorldSync),    SchemaKind.WorldFamily, null);

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