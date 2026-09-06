using System;
using System.Collections.Generic;
using System.Text;

namespace Silo.DbModel.Global;

public class GlobalDb
{
}

public record UserEntity(User User);

public record TimedUserEntity : UserEntity
{
    public TimedUserEntity(User      user, DateTime? Created = null,
                           DateTime? Modified = null) : base(user)
    {
        this.User     = user;
        this.Created  = Created  ?? DateTime.UtcNow;
        this.Modified = Modified ?? DateTime.UtcNow;
    }

    public DateTime Creation { get; init; }
    public DateTime Created  { get; init; }
    public DateTime Modified { get; init; }
}

/// <summary>
/// Tracks window position and sizes; row per Window type
/// </summary>
public record Window(
    int    Id,
    string Type,
    Uri    Uri,
    int    X,
    int    Y,
    int    Width,
    int    Height,
    User   User) : TimedUserEntity(User);

/// <summary>
/// Tracks changes, one row per event
/// </summary>
public record Audit(int Id, DateTime TimeUtc, User User, string Message);

/// <summary>
/// Configured connected system, one row per destination
/// </summary>
public record Connection(int Id, string DisplayName, Uri SourceUri);

/// <summary>
/// Stored location
/// </summary>
public record Location(int Id, Uri Uri, string DisplayName);

/// <summary>
/// The identity of each user that's authorized/opened the Silo (successfully).
/// </summary>
/// <param name="Domain">User domain name</param>
/// <param name="Name">User name</param>
/// <param name="SID">OS Security identifier</param>
public record User(int Id, string Domain, string Name, string SID, bool Group);

/// <summary>
/// User preference, one row per pref key
/// </summary>
public record Pref(int Id, User User, string Key, string Data);

/// <summary>
/// Stored secret blob;  one row per record
/// </summary>
public record Secret(int Id, string Key, byte[] Data);

/// <summary>
/// Navigation tree nodes as configured by the user, one row per node
/// </summary>
public record Node(int Id, string Name, Uri Uri, Uri IconUri, int Expanded);

/// <summary>
/// Other files in this silo, one row per file.
/// </summary>
public record File(
    int          Id,
    string       Filename,
    SiloFileType Type,
    int          ExpectedSize,
    byte[]       Crc32);

public enum SiloFileType
{
    /// <summary>
    /// Arbitrary content file
    /// </summary>
    Content,

    /// <summary>
    /// The (semi-)unencrypted stub db for an encrypted Silo
    /// </summary>
    StubDb,

    /// <summary>
    /// The Silo's global DB
    /// </summary>
    GlobalDb,

    /// <summary>
    /// The Universal database stores universal common data such as Zip codes, US States,
    /// Units of Measure, and Conversions
    /// </summary>
    UniversalDb,

    /// <summary>
    /// The metaverse database stores the state of aggregate composite objects
    /// </summary>
    MetaverseDb,

    /// <summary>
    /// The world database stores the last read state of a connected data source
    /// </summary>
    WorldDb,

    /// <summary>
    /// The temp database store session info and MRU data.
    /// </summary>
    TempDb
}

/// <summary>
/// Stores a delegated authorization to a user in the user table.
/// </summary>
public record Authorization(
    int           Id,
    User          AuthorizedUser,
    User          AuthorizingUser,
    SiloUserLevel Level);

[Flags]
public enum SiloUserLevel
{
    /// <summary>
    /// User level which has no access to the Silo
    /// </summary>
    None = 0,

    /// <summary>
    /// Read access enabled to the Silo
    /// </summary>
    Read = 1,

    /// <summary>
    /// Write access enabled to the Silo
    /// </summary>
    Write = 2,

    /// <summary>
    /// Author-mode access to the Silo;  
    /// </summary>
    Author = 4,

    /// <summary>
    /// Developer mode access to the silo;  
    /// </summary>
    Develop = 8,
}