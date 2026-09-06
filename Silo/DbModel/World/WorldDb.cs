namespace Silo.DbModel.World;

public class WorldDb
{
}

/// <summary>
/// Defines a World
/// </summary>
/// <param name="Id">Primary key</param>
/// <param name="Provider">The type name of the World provider</param>
/// <param name="Uri">The URI of the World root</param>
/// <param name="Key">Unique key within the Silo for this world (cannot be changed -- used in its file name)</param>
/// <param name="DisplayName">Renamable, human-readable display name</param>
/// <param name="Flags">World-level flags</param>
public record World(
    int        Id,
    string     Provider,
    Uri        Uri,
    string     Key,
    string     DisplayName,
    WorldFlags Flags);

/// <summary>
/// Declares a type of object that a World owns
/// </summary>
/// <param name="Id">Primary key</param>
/// <param name="World">The world that owns the object type</param>
/// <param name="Provider">The type name of the provider which handles this type</param>
/// <param name="Name">The name of the type in the Silo</param>
/// <param name="DisplayName">Human-readable display name</param>
/// <param name="NativeName">The native type name</param>
/// <param name="SchemaUri">An optional URI to the schema object which defines this</param>
/// <param name="Flags">Type-level flags</param>
public record WorldType(
    int            Id,
    World          World,
    string         Provider,
    string         Name,
    string         DisplayName,
    string         TableName,
    string         NativeName,
    Uri            SchemaUri,
    WorldTypeFlags Flags);

/// <summary>
/// Declares a field for a World Type
/// </summary>
/// <param name="Id">Primary key</param>
/// <param name="World">The world that owns the field</param>
/// <param name="WorldType">The type which owns the field</param>
/// <param name="Index">The ordinal index of the field on the type</param>
/// <param name="NativeName">Human-readable display name</param>
/// <param name="NativeType">The native type name</param>
/// <param name="DataType">The type name of the .NET type used</param>
/// <param name="SchemaUri">Optionally URI to a schema element which defines this</param>
/// <param name="Flags">Field-level flags</param>
public record WorldField(
    int             Id,
    World           World,
    WorldType       WorldType,
    int             Index,
    string          NativeName,
    string          NativeType,
    string          DataType,
    Uri             SchemaUri,
    WorldFieldFlags Flags);

public record FieldMapping(
    int               Id,
    WorldField        Field,
    string            MvTypeName,
    string            MvFieldName,
    string            Conversion,
    FieldMappingFlags Flags);

/// <summary>
/// Values indicating how a field mapping is to behave upon synchronization
/// </summary>
[Flags]
public enum FieldMappingFlags
{
    /// <summary>
    /// Default behavior
    /// </summary>
    None = 0,

    /// <summary>
    /// Value will be imported into the Metaverse from
    /// the world value
    /// </summary>
    Import = 1,

    /// <summary>
    /// Value will be exported into the World from the
    /// Metaverse value
    /// </summary>
    Export = 2,

    /// <summary>
    /// Allow NULL to flow from World to Metaverse
    /// </summary>
    ImportNulls = 4,

    /// <summary>
    /// Allow NULL to flow from Metaverse to World
    /// </summary>
    ExportNulls = 8,

    /// <summary>
    /// If set on a multivalued field, each value
    /// is treated as a keyed sub-field and thus each unique
    /// element is tracked for adds, removes, and changes.
    /// </summary>
    Reconciled = 0x10
}

/// <summary>
/// Defines a mapping between a <see cref="WorldType"/> and a meta-type (Metaverse type).
/// </summary>
/// <param name="Id">Primary Key</param>
/// <param name="World">The world owning the type being mapped</param>
/// <param name="WorldType">The world type being mapped</param>
/// <param name="MvTypeName">The meta-type name targeted</param>
/// <param name="Priority">Relative priority used during synchronization</param>
/// <param name="Joining">Optional rules for auto-joining objects to connectors</param>
/// <param name="Projection">Optional rules for auto-creating connectors for each object</param>
/// <param name="Provisioning">Optional rules for custom provisioning</param>
public record WorldMapping(
    int        Id,
    World      World,
    WorldType  WorldType,
    string     MvTypeName,
    int        Priority,
    JoinRules? Joining,
    ProjRules? Projection,
    ProvRules? Provisioning);

/// <summary>
/// Defines a synchronization job profile upon which one or all WorldTypes from a World are synchronized
/// </summary>
/// <param name="SyncInterval">Specifies the interval upon which this should take place</param>
/// <param name="World">Specifies the World which will be synced</param>
/// <param name="WorldType">Specifies a specific WorldType to sync, if null all mapped types are synced.</param>
/// <param name="ImportType">Specifies how instances are imported</param>
/// <param name="SyncType">Specifies how instances are synchronized</param>
/// <param name="Priority">Relative priority used during synchronization</param>
public record WorldSync(
    TimeSpan   SyncInterval,
    World      World,
    WorldType? WorldType,
    ImportType ImportType,
    SyncType   SyncType,
    int        Priority);

/// <summary>
/// Values indicating how an import job is to be handled
/// </summary>
public enum ImportType
{
    /// <summary>
    /// Do not import any data, read it live as it is required
    /// </summary>
    None,

    /// <summary>
    /// Only cache data locally for performance; do not stage object instance in
    /// the world database.
    /// </summary>
    Cache,

    /// <summary>
    /// Cache the data, but do so in the temp database.
    /// </summary>
    CacheTemp,

    /// <summary>
    /// Import changes, adds, and deletes since the last import (if possible);
    /// will be upgraded to <see cref="Full" /> if a delta import is not possible.
    /// </summary>
    Delta,

    /// <summary>
    /// Performs a full import of all qualified objects.
    /// </summary>
    Full
}

/// <summary>
/// Values indicating how imported (or loaded) data is synchronized from
/// world space into the metaverse.
/// </summary>
[Flags]
public enum SyncType
{
    /// <summary>
    /// The data is not synchronized at all
    /// </summary>
    NoSync = 0,

    /// <summary>
    /// Sync objects on this sync cycle, by default delta (only changed objects)
    /// </summary>
    Sync = 1,

    /// <summary>
    /// A full synchronization is forced across all objects
    /// </summary>
    Full = 2,

    /// <summary>
    /// Whether to enable Provisioning 
    /// </summary>
    Provision = 4,

    /// <summary>
    /// Whether to enable Joins 
    /// </summary>
    Join = 8,

    /// <summary>
    /// Whether to enable Projection
    /// </summary>
    Project = 0x10,

    /// <summary>
    /// Whether to enable metaverse attribute flow
    /// </summary>
    FlowToMv = 0x20,
    Default  = Sync | Provision | Join | Project | FlowToMv
}

public enum WorldFlags
{
    /// <summary>
    /// Default behavior
    /// </summary>
    None = 0,

    /// <summary>
    /// When set, data from this world type will never be mirrored in
    /// the world db.
    /// </summary>
    NeverMirror = 1,

    /// <summary>
    /// When set, data from this world type will never be mirrored in
    /// the world db.
    /// </summary>
    NeverCache = 2,
}

[Flags]
public enum WorldTypeFlags
{
    /// <summary>
    /// Default behavior
    /// </summary>
    None = 0,

    /// <summary>
    /// When set, data from this world type will never be mirrored in
    /// the world db.
    /// </summary>
    NeverMirror = 1,

    /// <summary>
    /// When set, data from this world type will never be cached in the
    /// world db.
    /// </summary>
    NeverCache = 2,

    /// <summary>
    /// Flags this (world's view of this data) as the authoritative source
    /// for the meta-type -- meaning, it is preferred over any other source
    /// contributing the same instance (object by key) of the same meta-type
    /// </summary>
    AuthoritativeSource = 4,

    /// <summary>
    /// When set, cached data from this world type will be cached in the
    /// temp db (which is subject to pruning significantly more frequently).
    /// </summary>
    CacheInTemp = 8,

    /// <summary>
    /// Flags this world type as a mapping type, which is transient and only
    /// maps one type to another (usually many-to-many);  
    /// </summary>
    Mapping = 8,
}

[Flags]
public enum WorldFieldFlags
{
    /// <summary>
    /// Default behavior
    /// </summary>
    None = 0,

    /// <summary>
    /// The source uses this field as the primary key for the type
    /// </summary>
    Key = 1,

    /// <summary>
    /// The source indexes this field
    /// </summary>
    Index = 2,

    /// <summary>
    /// The source includes this field in ANR
    /// </summary>
    Anr = 4,

    /// <summary>
    /// This field is a reference within the same world
    /// </summary>
    Reference = 8,

    /// <summary>
    /// This field may contain a referral to an external system/URI;
    /// can only be combined with <see cref="Reference" />
    /// </summary>
    Referral = 0x10,

    /// <summary>
    /// This field is the creation date of the object(s)
    /// </summary>
    Creation = 0x20,

    /// <summary>
    /// This field is the last modified date of the object(s)
    /// </summary>
    ModifyDate = 0x40,

    /// <summary>
    /// This field contains the absolute path, URI, distinguished name,
    /// etc. for the object (full native key)
    /// </summary>
    AbsolutePath = 0x80,

    /// <summary>
    /// This field contains the relative path fragment (name, CN, etc.)
    /// for the object (partial native key)
    /// </summary>
    RelativePath = 0x100,

    /// <summary>
    /// The value for this field may be NULL
    /// </summary>
    Nullable = 0x200,

    /// <summary>
    /// The value for this field can have multiple elements.
    /// </summary>
    Multivalue = 0x400
}