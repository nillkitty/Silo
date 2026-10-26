namespace Silo.DbModel.Worlds;

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