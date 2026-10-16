using System.ComponentModel;
using Silo.Converters;
using Silo.Model;

namespace Silo.DbModel.Global;

/// <summary>
///     Base for every row that belongs to a <see cref="Global.User" />. Virtual so a type with a more
///     specific notion of "who" (see <see cref="Authorization" />) can route it to its own property.
/// </summary>
public abstract class UserEntity
{
    public virtual User User { get; set; } = null!;
}

/// <summary>
///     A <see cref="UserEntity" /> that records when it was created and last modified. Both timestamps
///     default to "now", so an instance built in code or loaded from XAML is stamped on creation.
/// </summary>
public abstract class TimedUserEntity : UserEntity
{
    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime Modified { get; set; } = DateTime.UtcNow;
}

/// <summary>How a remembered window was showing when it was last closed.</summary>
public enum WindowPlacementState
{
    Normal    = 0,
    Maximized = 1
}

/// <summary>
///     Tracks window position and sizes; row per Window type
/// </summary>
/// <remarks>
///     A row is one window of one kind for one person: <see cref="Type" /> says what kind ("Main", "Table", "Tool", "Dialog"),
///     <see cref="Uri" /> which one of that kind (<c>silo:main</c>, <c>silo:table/5</c>, <c>silo:tool/drawiocanvas</c>), and
///     <see cref="UserEntity.User" /> whose it is -- a row without a user is the Silo's shared default, used by anyone who has
///     no row of their own. Position and size are in device-independent units (WPF's 1/96 inch). Writing these rows does
///     not mark the Silo as changed: they are saved along with the next save for any other reason.
/// </remarks>
public class Window : TimedUserEntity
{
    public int    Height { get; set; }
    public int    Id     { get; set; }

    /// <summary>The main window's dock layout (AvalonDock XML: where the panes sit, floating ones, their sizes); null for other windows.</summary>
    public string? Layout { get; set; }

    public WindowPlacementState State { get; set; }
    public string Type   { get; set; } = string.Empty;
    public Uri    Uri    { get; set; } = null!;
    public int    Width  { get; set; }
    public int    X      { get; set; }
    public int    Y      { get; set; }
}

/// <summary>
///     Tracks changes, one row per event
/// </summary>
public class Audit : TimedUserEntity
{
    public int      Id      { get; set; }
    public string   Message { get; set; } = string.Empty;
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
///     Configured connected system, one row per destination
/// </summary>
public class Connection : TimedUserEntity
{
    public string DisplayName { get; set; } = string.Empty;
    public int    Id          { get; set; }

    [TypeConverter(typeof(LenientUriTypeConverter))]
    public Uri? SourceUri { get; set; } = null!;
}

/// <summary>
///     Stored location
/// </summary>
public class Location : TimedUserEntity
{
    public string DisplayName { get; set; } = string.Empty;
    public int    Id          { get; set; }
    public Uri    Uri         { get; set; } = null!;
}

/// <summary>
///     The identity of each user that's authorized/opened the Silo (successfully).
/// </summary>
public class User
{
    /// <summary>User domain name</summary>
    public string Domain { get; set; } = string.Empty;

    public bool Group { get; set; }
    public int  Id    { get; set; }

    /// <summary>User name</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>OS Security identifier</summary>
    public string SID { get; set; } = string.Empty;
}

/// <summary>
///     User preference, one row per pref key
/// </summary>
public class Pref : TimedUserEntity
{
    public string Data { get; set; } = string.Empty;
    public int    Id   { get; set; }
    public string Key  { get; set; } = string.Empty;
}

/// <summary>
///     Stored secret blob;  one row per record
/// </summary>
/// <remarks>
///     <see cref="Data" /> is never plain: it is sealed by the Silo's encryption (<see cref="Silo.Contracts.Providers.Crypto.ISiloEncryption" />),
///     or, for a <c>datakey</c> row, locked by an <see cref="Silo.Contracts.Providers.Crypto.ISecretProtector" />. The rows are read and
///     written through <see cref="Silo.Contracts.Providers.Data.ISecretStore" />.
/// </remarks>
public class Secret : TimedUserEntity
{
    [TypeConverter(typeof(ByteArrayConverter))]
    public byte[] Data { get; set; } = [];

    public int    Id  { get; set; }

    /// <summary>A name that is not secret: a credential's name, or <c>$datakey</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>What the row holds: <c>credential</c>, <c>datakey</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>For a <c>datakey</c>, the id of the protector that locked it.</summary>
    public string Provider { get; set; } = string.Empty;
}

/// <summary>
///     Navigation tree nodes as configured by the user, one row per node
/// </summary>
/// <remarks>
///     Besides nodes the person configured, the Explorer's remembered state (<see cref="Silo.Contracts.Providers.Data.INodeStateStore" />):
///     one row per node that was expanded, open or selected, whose <see cref="Uri" /> is <c>silo:node?key=</c> and the node's Explorer
///     key, escaped.
/// </remarks>
public class Node : TimedUserEntity
{
    public int Expanded { get; set; }
    public Uri IconUri  { get; set; } = null!;
    public int Id       { get; set; }

    /// <summary>The node's document was open as a permanent tab when the Silo was last used.</summary>
    public bool IsOpen { get; set; }

    /// <summary>The node was the selected one in the Explorer.</summary>
    public bool IsSelected { get; set; }

    public bool   IsVisible { get; set; }
    public string Name      { get; set; } = string.Empty;
    public Uri    Uri       { get; set; } = null!;
}

/// <summary>
///     Other files in this silo, one row per file.
/// </summary>
public class File : TimedUserEntity
{
    [TypeConverter(typeof(ByteArrayConverter))]
    public byte[] Crc32 { get; set; } = [];

    public int          ExpectedSize { get; set; }
    public string       Filename     { get; set; } = string.Empty;
    public int          Id           { get; set; }
    public SiloFileType Type         { get; set; }
}

public enum SiloFileType
{
    /// <summary>
    ///     Arbitrary content file
    /// </summary>
    Content,

    /// <summary>
    ///     The (semi-)unencrypted stub db for an encrypted Silo
    /// </summary>
    StubDb,

    /// <summary>
    ///     The Silo's global DB
    /// </summary>
    GlobalDb,

    /// <summary>
    ///     The Universal database stores universal common data such as Zip codes, US States,
    ///     Units of Measure, and Conversions
    /// </summary>
    UniversalDb,

    /// <summary>
    ///     The metaverse database stores the state of aggregate composite objects
    /// </summary>
    MetaverseDb,

    /// <summary>
    ///     The world database stores the last read state of a connected data source
    /// </summary>
    WorldDb,

    /// <summary>
    ///     The temp database store session info and MRU data.
    /// </summary>
    TempDb
}

/// <summary>
///     Stores a delegated authorization to a user in the user table.
/// </summary>
/// <remarks>
///     The row's <see cref="UserEntity.User" /> is the authorizing user, exactly as before; setting
///     <see cref="AuthorizingUser" /> is all a template needs to do.
/// </remarks>
public class Authorization : TimedUserEntity
{
    public User          AuthorizedUser  { get; set; } = null!;
    public User          AuthorizingUser { get; set; } = null!;
    public int           Id              { get; set; }
    public SiloUserLevel Level           { get; set; }

    public override User User
    {
        get => AuthorizingUser;
        set => AuthorizingUser = value;
    }
}

/// <summary>
///     Stores a reference to an assembly which is to be loaded at load-time.
/// </summary>
public class Assembly : TimedUserEntity
{
    public string AssemblyName { get; set; } = string.Empty;
    public bool   Critical     { get; set; }
    public int    Id           { get; set; }
    public Uri    SourceUri    { get; set; } = null!;
}

[Flags]
public enum SiloUserLevel
{
    /// <summary>
    ///     User level which has no access to the Silo
    /// </summary>
    None = 0,

    /// <summary>
    ///     Read access enabled to the Silo
    /// </summary>
    Read = 1,

    /// <summary>
    ///     Write access enabled to the Silo
    /// </summary>
    Write = 2,

    /// <summary>
    ///     Author-mode access to the Silo;
    /// </summary>
    Author = 4,

    /// <summary>
    ///     Developer mode access to the silo;
    /// </summary>
    Develop = 8
}