using System.Collections.ObjectModel;
using Silo.Model;

namespace Silo.DbModel.Global;

public class GlobalDb
{
}

public record UserEntity(User User);

public record TimedUserEntity : UserEntity
{
    public TimedUserEntity(User user, DateTime? Created = null, DateTime? Modified = null) : base(user)
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
public record Window(int Id, string Type, Uri Uri, int X, int Y, int Width, int Height, User User) : TimedUserEntity(User);

/// <summary>
/// Tracks changes, one row per event
/// </summary>
public record Audit(int Id, DateTime TimeUtc, User User, string Message) : TimedUserEntity(User, TimeUtc, TimeUtc);

/// <summary>
/// Configured connected system, one row per destination
/// </summary>
public record Connection(int Id, User User, string DisplayName, Uri SourceUri) : TimedUserEntity(User);

/// <summary>
/// Stored location
/// </summary>
public record Location(int Id, User User, Uri Uri, string DisplayName) : TimedUserEntity(User);

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
public record Pref(int Id, User User, string Key, string Data) : TimedUserEntity(User);

/// <summary>
/// Stored secret blob;  one row per record
/// </summary>
public record Secret(int Id, User User, string Key, byte[] Data) : TimedUserEntity(User)
{
    public OpenSilo UpdateCredential(string user, string pass, string? domain)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Navigation tree nodes as configured by the user, one row per node
/// </summary>
public record Node(int Id, User User, string Name, Uri Uri, Uri IconUri, int Expanded) : TimedUserEntity(User)
{
    public bool IsVisible { get; set; }

    public SiloNode CreateViewModel() => new SiloNode(this);
}

public record SiloNode
{
    public SiloNode(Node src) : this(src.Required(), src.Name, src.Uri, src.IconUri, src.Expanded)
    {
    }

    public SiloNode(Node dataContext, string text, Uri uri, Uri? icon, int? expand)
    {
        this.DataContext = dataContext;
        this.Text        = text;
        this.Uri         = uri;
        this.Icon        = icon;
        this.Expand      = expand;
        this.Children    = [];
        this.Children.CollectionChanged += (sender, args) =>
                                           {
                                               if (args?.NewItems is null)
                                                   return;

                                               foreach (var x in args.NewItems)
                                                   if (x is SiloNode n)
                                                       n.Parent = this;
                                               if (args.OldItems != null)
                                                   foreach (var x in args.OldItems)
                                                       if (x is SiloNode n)
                                                           n.Parent = null;
                                           };
    }

    public bool IsVisible  => Expand > 0;
    public bool IsSelected { get; set; }

    public bool IsExpanded
    {
        get => Expand > 0;
        set
        {
            if (value)
                Expand = 1;
            else
                Expand = 0;
        }
    }

    public Node                           DataContext { get; protected init; }
    public string                         Text        { get; protected init; }
    public Uri                            Uri         { get; protected init; }
    public Uri?                           Icon        { get; protected init; }
    public int?                           Expand      { get; protected set; }
    public object?                        Parent      { get; protected set; }
    public ObservableCollection<SiloNode> Children    { get; }

    public bool Remove()
    {
        if (IsVisible && Parent is SiloNode { } p)
        {
            return p.Children.Remove(this);
        }

        return false;
    }
}

/// <summary>
/// Other files in this silo, one row per file.
/// </summary>
public record File(int Id, User User, string Filename, SiloFileType Type, int ExpectedSize, byte[] Crc32) : TimedUserEntity(User);

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
public record Authorization(int Id, User AuthorizedUser, User AuthorizingUser, SiloUserLevel Level) : TimedUserEntity(AuthorizingUser);

/// <summary>
/// Stores a reference to an assembly which is to be loaded at load-time.
/// </summary>
public record Assembly(int Id, User User, string AssemblyName, Uri SourceUri, bool Critical) : TimedUserEntity(User);

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