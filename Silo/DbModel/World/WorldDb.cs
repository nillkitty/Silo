namespace Silo.DbModel.Worlds;

/// <summary>
///     An item that is owned by a World (and can nest inside a World in XAML)
/// </summary>
public interface IWorldPart
{
    World World { get; set; }
}

public interface IWorldTypePart
{
    WorldType WorldType { get; set; }
}

/// <summary>
///     Defines a World
/// </summary>
public class World : ItemContainer<IWorldPart>
{
    /// <summary>Renamable, human-readable display name</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>World-level flags</summary>
    public WorldFlags Flags { get; set; }

    /// <summary>Primary key</summary>
    public int Id { get; set; }

    /// <summary>Unique key within the Silo for this world (cannot be changed -- used in its file name)</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The type name of the World provider</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The URI of the World root</summary>
    public Uri Uri { get; set; } = null!;
}

/// <summary>
///     Declares a type of object that a World owns
/// </summary>
public class WorldType : ItemContainer<IWorldTypePart>, IWorldPart
{
    /// <summary>Human-readable display name</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Type-level flags</summary>
    public WorldTypeFlags Flags { get; set; }

    /// <summary>Primary key</summary>
    public int Id { get; set; }

    /// <summary>
    ///     Optional list of auxiliary types to include
    /// </summary>
    public WorldType[]? Include { get; set; } = [];

    /// <summary>
    ///     Optional base type to inherit from
    /// </summary>
    public WorldType? Inherits { get; set; }

    /// <summary>The name of the type in the Silo</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The native type name</summary>
    public string NativeName { get; set; } = string.Empty;

    /// <summary>The type name of the provider which handles this type</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>An optional URI to the schema object which defines this</summary>
    public Uri SchemaUri { get; set; } = null!;

    public string TableName { get; set; } = string.Empty;

    /// <summary>The world that owns the object type</summary>
    public World World { get; set; } = null!;
}

/// <summary>
///     Declares a field for a World Type
/// </summary>
public class WorldField : Item, IWorldTypePart
{
    /// <summary>The type name of the .NET type used</summary>
    public string DataType { get; set; } = string.Empty;

    /// <summary>Field-level flags</summary>
    public WorldFieldFlags Flags { get; set; }

    /// <summary>Primary key</summary>
    public int Id { get; set; }


    /// <summary>The ordinal index of the field on the type</summary>
    public int Index { get; set; }

    /// <summary>Human-readable display name</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The native type name</summary>
    public string NativeType { get; set; } = string.Empty;

    /// <summary>Optionally URI to a schema element which defines this</summary>
    public Uri SchemaUri { get; set; } = null!;

    /// <summary>The world that owns the field</summary>
    public World World { get; set; } = null!;

    /// <summary>The type which owns the field</summary>
    public WorldType WorldType { get; set; } = null!;
}

public class FieldMapping
{
    public string            Conversion  { get; set; } = string.Empty;
    public WorldField        Field       { get; set; } = null!;
    public FieldMappingFlags Flags       { get; set; }
    public int               Id          { get; set; }
    public string            MvFieldName { get; set; } = string.Empty;
    public string            MvTypeName  { get; set; } = string.Empty;
}

/// <summary>
///     Values indicating how a field mapping is to behave upon synchronization
/// </summary>
[Flags]
public enum FieldMappingFlags
{
    /// <summary>
    ///     Default behavior
    /// </summary>
    None = 0,

    /// <summary>
    ///     Value will be imported into the Metaverse from
    ///     the world value
    /// </summary>
    Import = 1,

    /// <summary>
    ///     Value will be exported into the World from the
    ///     Metaverse value
    /// </summary>
    Export = 2,

    /// <summary>
    ///     Allow NULL to flow from World to Metaverse
    /// </summary>
    ImportNulls = 4,

    /// <summary>
    ///     Allow NULL to flow from Metaverse to World
    /// </summary>
    ExportNulls = 8,

    /// <summary>
    ///     If set on a multivalued field, each value
    ///     is treated as a keyed sub-field and thus each unique
    ///     element is tracked for adds, removes, and changes.
    /// </summary>
    Reconciled = 0x10
}

/// <summary>
///     Defines a mapping between a <see cref="WorldType" /> and a meta-type (Metaverse type).
/// </summary>
public class WorldMapping : Item, IWorldTypePart
{
    /// <summary>Primary Key</summary>
    public int Id { get; set; }

    /// <summary>Optional rules for auto-joining objects to connectors</summary>
    public JoinRules? Joining { get; set; }

    /// <summary>The meta-type name targeted</summary>
    public string MvTypeName { get; set; } = string.Empty;

    /// <summary>Relative priority used during synchronization</summary>
    public int Priority { get; set; }

    /// <summary>Optional rules for auto-creating connectors for each object</summary>
    public ProjRules? Projection { get; set; }

    /// <summary>Optional rules for custom provisioning</summary>
    public ProvRules? Provisioning { get; set; }

    /// <summary>The world owning the type being mapped</summary>
    public World World { get; set; } = null!;

    /// <summary>The world type being mapped</summary>
    public WorldType WorldType { get; set; } = null!;
}

/// <summary>
///     Defines a synchronization job profile upon which one or all WorldTypes from a World are synchronized
/// </summary>
public class WorldSync : Item, IWorldPart
{
    /// <summary>Specifies how instances are imported</summary>
    public ImportType ImportType { get; set; }

    /// <summary>Relative priority used during synchronization</summary>
    public int Priority { get; set; }

    /// <summary>Specifies the interval upon which this should take place</summary>
    public TimeSpan SyncInterval { get; set; }

    /// <summary>Specifies how instances are synchronized</summary>
    public SyncType SyncType { get; set; }

    /// <summary>Specifies a specific WorldType to sync, if null all mapped types are synced.</summary>
    public WorldType? WorldType { get; set; }

    /// <summary>Specifies the World which will be synced</summary>
    public World World { get; set; } = null!;
}

/// <summary>
///     Values indicating how an import job is to be handled
/// </summary>
public enum ImportType
{
    /// <summary>
    ///     Do not import any data, read it live as it is required
    /// </summary>
    None,

    /// <summary>
    ///     Only cache data locally for performance; do not stage object instance in
    ///     the world database.
    /// </summary>
    Cache,

    /// <summary>
    ///     Cache the data, but do so in the temp database.
    /// </summary>
    CacheTemp,

    /// <summary>
    ///     Import changes, adds, and deletes since the last import (if possible);
    ///     will be upgraded to <see cref="Full" /> if a delta import is not possible.
    /// </summary>
    Delta,

    /// <summary>
    ///     Performs a full import of all qualified objects.
    /// </summary>
    Full
}

/// <summary>
///     Values indicating how imported (or loaded) data is synchronized from
///     world space into the metaverse.
/// </summary>
[Flags]
public enum SyncType
{
    /// <summary>
    ///     The data is not synchronized at all
    /// </summary>
    NoSync = 0,

    /// <summary>
    ///     Sync objects on this sync cycle, by default delta (only changed objects)
    /// </summary>
    Sync = 1,

    /// <summary>
    ///     A full synchronization is forced across all objects
    /// </summary>
    Full = 2,

    /// <summary>
    ///     Whether to enable Provisioning
    /// </summary>
    Provision = 4,

    /// <summary>
    ///     Whether to enable Joins
    /// </summary>
    Join = 8,

    /// <summary>
    ///     Whether to enable Projection
    /// </summary>
    Project = 0x10,

    /// <summary>
    ///     Whether to enable metaverse attribute flow
    /// </summary>
    FlowToMv = 0x20,
    Default  = Sync | Provision | Join | Project | FlowToMv
}

[Flags]
public enum WorldFlags
{
    /// <summary>
    ///     Default behavior
    /// </summary>
    None = 0,

    /// <summary>
    ///     When set, data from this world type will never be mirrored in
    ///     the world db.
    /// </summary>
    NeverMirror = 1,

    /// <summary>
    ///     When set, data from this world type will never be mirrored in
    ///     the world db.
    /// </summary>
    NeverCache = 2,

    /// <summary>
    ///     Indicates that we are authoritative (e.g. this is the true data,
    ///     stored in the Silo -- it's not from someplace else)
    /// </summary>
    Authority = 4
}

[Flags]
public enum WorldTypeFlags
{
    /// <summary>
    ///     Default behavior
    /// </summary>
    None = 0,

    /// <summary>
    ///     When set, data from this world type will never be mirrored in
    ///     the world db.
    /// </summary>
    NeverMirror = 1,

    /// <summary>
    ///     When set, data from this world type will never be cached in the
    ///     world db.
    /// </summary>
    NeverCache = 2,

    /// <summary>
    ///     Flags this (world's view of this data) as the authoritative source
    ///     for the meta-type -- meaning, it is preferred over any other source
    ///     contributing the same instance (object by key) of the same meta-type
    /// </summary>
    AuthoritativeSource = 4,

    /// <summary>
    ///     When set, cached data from this world type will be cached in the
    ///     temp db (which is subject to pruning significantly more frequently).
    /// </summary>
    CacheInTemp = 8,

    /// <summary>
    ///     Flags this world type as a mapping type, which is transient and only
    ///     maps one type to another (usually many-to-many);
    /// </summary>
    Mapping = 0x10
}

[Flags]
public enum WorldFieldFlags
{
    /// <summary>
    ///     Default behavior
    /// </summary>
    None = 0,

    /// <summary>
    ///     The source uses this field as the primary key for the type
    /// </summary>
    Key = 1,

    /// <summary>
    ///     The source indexes this field
    /// </summary>
    Index = 2,

    /// <summary>
    ///     The source includes this field in ANR
    /// </summary>
    Anr = 4,

    /// <summary>
    ///     This field is a reference within the same world
    /// </summary>
    Reference = 8,

    /// <summary>
    ///     This field may contain a referral to an external system/URI;
    ///     can only be combined with <see cref="Reference" />
    /// </summary>
    Referral = 0x10,

    /// <summary>
    ///     This field is the creation date of the object(s)
    /// </summary>
    Creation = 0x20,

    /// <summary>
    ///     This field is the last modified date of the object(s)
    /// </summary>
    ModifyDate = 0x40,

    /// <summary>
    ///     This field contains the absolute path, URI, distinguished name,
    ///     etc. for the object (full native key)
    /// </summary>
    AbsolutePath = 0x80,

    /// <summary>
    ///     This field contains the relative path fragment (name, CN, etc.)
    ///     for the object (partial native key)
    /// </summary>
    RelativePath = 0x100,

    /// <summary>
    ///     The value for this field may be NULL
    /// </summary>
    Nullable = 0x200,

    /// <summary>
    ///     The value for this field can have multiple elements.
    /// </summary>
    Multivalue = 0x400,

    /// <summary>
    ///     This field is required and cannot be null or empty
    /// </summary>
    Required = 0x800,

    /// <summary>
    ///     The field contains the row/item identity (auto-assigned)
    /// </summary>
    Identity = 0x1000,

    /// <summary>
    ///     The value is provided by the database / evaluated
    /// </summary>
    Computed = 0x2000
}