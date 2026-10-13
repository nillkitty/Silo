namespace Silo.DbModel.Universal;

/// <summary>
///     Defines a type of universal data which is installed or present.
/// </summary>
public class UniversalType
{
    /// <summary>The UTC date and time when the currently stored data expires</summary>
    public DateTime? DataExpires { get; set; }

    /// <summary>The UTC date and time when the current data was last updated (if ever)</summary>
    public DateTime? DataUpdated { get; set; }

    /// <summary>Human-readable Display Name for the data type</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Unique Id</summary>
    public int Id { get; set; }

    /// <summary>The UTC date and time when this item type was added to the Silo</summary>
    public DateTime Introduced { get; set; } = DateTime.UtcNow;

    /// <summary>Unique key for the data type</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The name of the provider type which supplies this data</summary>
    public string? SourceProvider { get; set; }

    /// <summary>The URI from which the data was obtained/is synchronized</summary>
    public Uri? SourceUri { get; set; }

    /// <summary>The table name which stores this data</summary>
    public string TableName { get; set; } = string.Empty;
}

/// <summary>
///     Used in a template to create a dependency on a registered/well-known universal type id
///     (provider can be determined at bind time)
/// </summary>
public class IncludeUniversal
{
    public bool   Critical  { get; set; }
    public string Id        { get; set; }
    public string LocalName { get; set; }
}

/// <summary>
///     Defines a possible source for a type of Universal data, which can be included.
/// </summary>
public class UniversalProvider
{
    /// <summary>Human-readable description</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Unique Id</summary>
    public int Id { get; set; }

    /// <summary>Whether it has been included in this Silo</summary>
    public bool Included { get; set; }

    /// <summary>The type name of the source provider</summary>
    public string SourceProvider { get; set; } = string.Empty;
}