using System;
using System.Collections.Generic;
using System.Text;

namespace Silo.DbModel.Universal;

public class UniversalDb
{
}

/// <summary>
/// Defines a type of universal data which is installed or present.
/// </summary>
/// <param name="Id">Unique Id</param>
/// <param name="Name">Unique key for the data type</param>
/// <param name="DisplayName">Human-readable Display Name for the data type</param>
/// <param name="TableName">The table name which stores this data</param>
/// <param name="Introduced">The UTC date and time when this item type was added to the Silo</param>
/// <param name="DataExpires">The UTC date and time when the currently stored data expires</param>
/// <param name="DataUpdated">The UTC date and time when the current data was last updated (if ever)</param>
/// <param name="SourceProvider">The name of the provider type which supplies this data</param>
/// <param name="SourceUri">The URI from which the data was obtained/is synchronized</param>
public record UniversalType(
    int       Id,
    string    Name,
    string    DisplayName,
    string    TableName,
    DateTime  Introduced,
    DateTime? DataExpires,
    DateTime? DataUpdated,
    string?   SourceProvider,
    Uri?      SourceUri);

/// <summary>
/// Defines a possible source for a type of Universal data, which can be included.
/// </summary>
/// <param name="Id">Unique Id</param>
/// <param name="SourceProvider">The type name of the source provider</param>
/// <param name="Included">Whether it has been included in this Silo</param>
/// <param name="Description">Human-readable description</param>
public record UniversalProvider(
    int    Id,
    string SourceProvider,
    bool   Included,
    string Description);