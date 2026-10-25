using System.Diagnostics.CodeAnalysis;
using System.DirectoryServices;
using System.Globalization;
using System.Runtime.InteropServices;
using Silo.Model;
using Silo.ViewModel;

namespace Silo.Connectors.Ldap;

public class LdapModel : ModelBase, INodeProvider, INodeDetails
{
    protected internal const uint BuiltinLocal = 0x00000001; // GROUP_TYPE_BUILTIN_LOCAL_GROUP
    protected internal const uint Global       = 0x00000002; // GROUP_TYPE_ACCOUNT_GROUP
    protected internal const uint DomainLocal  = 0x00000004; // GROUP_TYPE_RESOURCE_GROUP
    protected internal const uint Universal    = 0x00000008; // GROUP_TYPE_UNIVERSAL_GROUP
    protected internal const uint Security     = 0x80000000; // GROUP_TYPE_SECURITY_ENABLED

    // what is asked for when the entries below an entry are read: only what the nodes and the details show
    private static readonly string[] ChildAttributes =
    [
        "name", "objectCategory", "distinguishedName", "groupType", "description",
        "sAMAccountName", "userPrincipalName", "mail", "whenCreated",
        "whenChanged"
    ];

    private static readonly (string Label, string Attribute)[] DetailAttributes =
    [
        ("Description", "description"),
        ("Account name", "sAMAccountName"),
        ("User principal name", "userPrincipalName"),
        ("E-mail", "mail"),
        ("Created", "whenCreated"), ("Changed", "whenChanged")
    ];

    public enum GroupScope
    {
        Unknown = 0,
        Global,
        DomainLocal,
        Universal,
        BuiltinLocal
    }

    /// <summary>
    ///     The name of the object's category (<c>Person</c>, <c>Computer</c>, <c>Organizational-Unit</c>), which is the first
    ///     part of the <c>objectCategory</c> distinguished name without its <c>CN=</c>.
    /// </summary>
    public string? Category { get; private set; }

    /// <summary>
    ///     The connection this entry was read through (the root has the connection itself); null for a model made on its
    ///     own.
    /// </summary>
    public new virtual LdapConnection? Connection { get; }

    public ICredential? Credential { get; set; }

    public string?        DN        { get; private set; }
    public DirectoryEntry Entry     { get; private set; }
    public int            GroupType { get; private set; }

    /// <summary>Whether the entry can have entries below it: a container is read when it is expanded, the rest are leaves.</summary>
    public virtual bool HasChildren => HasChildrenOfKind(Kind);

    /// <summary>
    ///     What the entry is, in one lower-case word with no hyphens (<c>user</c>, <c>group</c>,
    ///     <c>organizationalunit</c>); see <see cref="KindOf" />.
    /// </summary>
    public string Kind { get; private set; }

    /// <summary>The text of the entry's node.</summary>
    public virtual string Label => Name;

    /// <summary>The entry's node in the Explorer, once it has been made (see <see cref="GetNode" />).</summary>
    public SiloNode? Node { get; protected set; }

    public string? ObjectCategory { get; private set; }

    /// <summary>The entry's class in the directory schema (<c>user</c>, <c>group</c>, <c>organizationalUnit</c>...).</summary>
    public string? SchemaClass { get; private set; }

    /// <summary>
    ///     Whether the entry has been read, so that asking it for an attribute does not go to the directory. Always, but
    ///     for a connection that has not connected yet.
    /// </summary>
    protected virtual bool IsLoaded => true;

    /// <summary>
    ///     The icon (a key in <c>Resources/NavIcons.xaml</c>).
    /// </summary>
    public IconRef Icon { get; private set; }

    /// <summary>
    ///     The entry's name as people write it (<c>Smith, John</c>), not its relative distinguished name (
    ///     <c>CN=Smith\, John</c>).
    /// </summary>
    public string Name { get; private set; }

    public string TypeName { get; private set; }
    public Uri    Uri      { get; private set; }

    /// <summary>
    ///     The directory entry itself, for code that wants the data and not the model (see
    ///     <see cref="INodeProvider.Data" />).
    /// </summary>
    public object? Data => Entry;

    /// <summary>
    ///     The <c>ldap://</c> address an <c>adsi://</c> one stands for: the same host, port and distinguished name under the
    ///     <c>ldap</c> scheme (<c>adsi://dc1/DC=contoso,DC=com</c> is <c>ldap://dc1/DC=contoso,DC=com</c>). A URI of any other
    ///     scheme is returned as it is. The path is kept as written (still escaped), a port only when one was given (an
    ///     <c>adsi</c> address has no default of its own; <c>ldap</c>'s 389 is implied), and the user info, query and fragment
    ///     are dropped, which an <c>adsi</c> address has no use for.
    /// </summary>
    /// <remarks>
    ///     <see cref="ToAdsPath" /> (what <see cref="Connect" /> uses) reads an <c>adsi://</c> address itself and does not go
    ///     through this.
    /// </remarks>
    public static Uri AdsiToLdap(Uri uri)
    {
        uri.Required();
        if (!uri.Scheme.Equals("adsi", StringComparison.OrdinalIgnoreCase)) return uri;

        string port = uri.Port > 0 && !uri.IsDefaultPort ? ":" + uri.Port : string.Empty;
        string path = uri.AbsolutePath; // "/" when there is none

        return new Uri($"ldap://{uri.Host}{port}{(path.StartsWith('/') ? path : "/" + path)}", UriKind.Absolute);
    }

    /// <summary>
    ///     The ADSI path (<c>LDAP://host:port/distinguished name</c>) that a URI stands for. <c>ldap://</c> and <c>adsi://</c>
    ///     are the same; <c>ldaps://</c> adds the port 636 (which ADSI would not assume) and is secured by
    ///     <see cref="IsSecure" />. The part after the host is the distinguished name, unescaped. A user name and password in
    ///     the URI are not carried over, and neither is a query (ADSI has no use for the attributes, scope and filter of an
    ///     RFC 4516
    ///     URL). Nothing after the host (or after <c>adsi://</c>) means the directory's default naming context; see
    ///     <see cref="Connect" />.
    /// </summary>
    /// <remarks>
    ///     The scheme is written in capitals because ADSI is particular about it: <c>ldap://</c> is an unknown provider to it,
    ///     which is
    ///     also why this returns text and not a <see cref="System.Uri" /> (a <c>Uri</c> writes its scheme in lower case).
    /// </remarks>
    /// <exception cref="ArgumentException">The URI is not an <c>ldap</c>, <c>ldaps</c> or <c>adsi</c> one.</exception>
    public static string ToAdsPath(Uri uri)
    {
        uri.Required();
        string scheme = uri.Scheme;
        bool   ldaps  = scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase);
        if (!ldaps && !scheme.Equals("ldap", StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals("adsi",           StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{scheme}' is not an LDAP address (ldap://, ldaps:// or adsi://).", nameof(uri));

        string dn   = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        var    path = new StringBuilder("LDAP://");

        if (uri.Host.Length > 0)
        {
            int port = uri.Port > 0 ? uri.Port : ldaps ? 636 : -1;

            path.Append(uri.Host);
            if (port > 0 && (ldaps || !uri.IsDefaultPort)) path.Append(':').Append(port);
            if (dn.Length > 0) path.Append('/').Append(dn);
        }
        else
        {
            path.Append(dn); // serverless: LDAP://DC=contoso,DC=com
        }

        return path.ToString();
    }

    /// <summary>Whether a URI asks for the connection to be made over TLS (<c>ldaps://</c>).</summary>
    public static bool IsSecure(Uri uri)
    {
        return uri.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Splits an ADSI path (<c>LDAP://host:port/CN=Users,DC=x</c>) into the server (<c>host:port</c>; empty for a
    ///     serverless
    ///     path) and the distinguished name (empty when there is none).
    /// </summary>
    public static (string Server, string DN) SplitAdsPath(string path)
    {
        string rest                                                              = path.Required();
        if (rest.StartsWith("LDAP://", StringComparison.OrdinalIgnoreCase)) rest = rest["LDAP://".Length..];

        // a server never has an '=' in it, a distinguished name always does (its first '/' would be an escaped one: CN=a\/b)
        int slash = rest.IndexOf('/');
        if (slash >= 0 && !rest[..slash].Contains('=')) return (rest[..slash], rest[(slash + 1)..]);

        return rest.Contains('=') || rest.Equals("RootDSE", StringComparison.OrdinalIgnoreCase)
                   ? (string.Empty, rest)
                   : (rest, string.Empty);
    }

    /// <summary>
    ///     A path with no distinguished name is the directory itself: ask its root DSE where its default naming context is
    ///     (<c>DC=contoso,DC=com</c>) and use that.
    /// </summary>
    private static string WithDefaultNamingContext(string path, string? userName, string? password, AuthenticationTypes auth)
    {
        var (server, dn) = SplitAdsPath(path);
        if (dn.Length > 0) return path;

        string    prefix = server.Length > 0 ? "LDAP://" + server + "/" : "LDAP://";
        using var root   = new DirectoryEntry(prefix + "RootDSE", userName, password, auth);

        string? context = root.GetValue<string>("defaultNamingContext");
        if (string.IsNullOrEmpty(context))
            throw new
                InvalidOperationException("The directory did not say what its default naming context is, so there is nothing to open. Give a distinguished name (adsi://host/DC=contoso,DC=com).");

        return prefix + context;
    }

    /// <summary>
    ///     Makes the connection a request asks for, without touching the directory: its URI (<see cref="ToAdsPath" />; the
    ///     connector's default, the current domain, when there is none) and the credential in the request's metadata (the
    ///     Windows
    ///     user's own when there is none) are kept for when it connects (<see cref="LdapConnection.Connect" />), which is what
    ///     binds, finds the default naming context and applies the connector's paging, referral and password encoding, off the
    ///     calling thread.
    /// </summary>
    public static LdapConnection Create(LdapConnector connector, ConnectionRequest? crq)
    {
        connector.Required();
        crq ??= new ConnectionRequest(connector.DefaultUri);
        Uri uri = crq.RequestedUri ?? connector.DefaultUri;

        // the credential hands over what it has to a sink; nothing is read out of it
        var credential = crq.Metadata.OfType<ICredential>().FirstOrDefault();
        var sink       = new LdapCredentialSink();
        credential?.Configure(sink);

        var auth = AuthenticationTypes.Secure | (IsSecure(uri) ? AuthenticationTypes.SecureSocketsLayer : AuthenticationTypes.None);
        var bind = new LdapBind(ToAdsPath(uri), sink.UserName, sink.Password, auth);

        return new LdapConnection(bind, uri, connector, crq.Metadata ?? [], credential, crq.ParentNode, crq.DisplayName);
    }

    /// <summary>
    ///     <see cref="Create" />s the connection and connects it, without blocking the caller. If it cannot connect the
    ///     connection is
    ///     discarded and the reason thrown.
    /// </summary>
    public static async Task<LdapConnection> ConnectAsync(LdapConnector     connector, ConnectionRequest? crq,
                                                          CancellationToken cancel = default)
    {
        var connection = Create(connector, crq);
        try
        {
            await connection.Connect(cancel);
        }
        catch
        {
            connection.Entry.Dispose();
            throw;
        }

        return connection;
    }

    /// <summary><see cref="WithDefaultNamingContext" /> off the calling thread: it asks the directory.</summary>
    internal static Task<string> WithDefaultNamingContextAsync(string path, string? userName, string? password, AuthenticationTypes auth,
                                                               CancellationToken cancel)
    {
        return Task.Run(() => WithDefaultNamingContext(path, userName, password, auth), cancel);
    }

    /// <summary>
    ///     Whether entries of this kind (see <see cref="KindOf" />) hold other entries: organizational units, containers
    ///     and the like. A group does not: its members are a list of references, not entries below it.
    /// </summary>
    public static bool HasChildrenOfKind(string kind)
    {
        return kind is "organizationalunit" or "container" or "builtindomain" or "domaindns" or "domain" or "configuration"
                    or "lostandfound" or "sitescontainer" or "site" or "subnetcontainer";
    }

    // the first value of an attribute as text; whenCreated is a date and description can have several values
    private static string? AttributeText(DirectoryEntry entry, string attribute)
    {
        return entry.Properties[attribute] is { Count: > 0 } values ? Convert.ToString(values[0], CultureInfo.CurrentCulture) : null;
    }

    /// <summary>
    ///     The entries below this one, as nodes: containers first, then by name. Read off the UI thread, through the
    ///     connection so it is tracked.
    /// </summary>
    internal Task<IReadOnlyList<SiloNode>> LoadChildNodesAsync(CancellationToken cancel)
    {
        var connection = Connection ?? throw new InvalidOperationException("The entry was not read through a connection.");
        return connection.RunAsync($"Reading {Name}", () => ReadChildren(connection, cancel), cancel);
    }

    private IReadOnlyList<SiloNode> ReadChildren(LdapConnection connection, CancellationToken cancel)
    {
        var models = new List<LdapModel>();

        foreach (DirectoryEntry child in Entry.Children)
        {
            cancel.ThrowIfCancellationRequested();

            try
            {
                child.RefreshCache(ChildAttributes);
                models.Add(new LdapModel(child, connection) { Credential = Credential });
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // one entry that cannot be read (no right to it, deleted meanwhile) is not a reason to show none of the others
                Logger.Debug(ex, "Could not read {Child} below {Entry}", child.Path, Entry.Path);
                child.Dispose();
            }
        }

        return models.OrderByDescending(m => m.HasChildren)
                     .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Select(m => m.GetNode()!)
                     .ToList();
    }

    public string GetGroupTypeName()
    {
        return FromGroupType(GroupType).ToString();
    }

    public static AdGroupInfo FromGroupType(int rawGroupType)
    {
        uint flags      = (uint)rawGroupType;
        bool isSecurity = (flags & Security) != 0;

        // A built-in group is also a domain local one (its type has both bits: 0x80000005), so it has to be asked about first.
        GroupScope scope = flags switch
                           {
                               _ when (flags & BuiltinLocal) != 0 => GroupScope.BuiltinLocal,
                               _ when (flags & Universal)    != 0 => GroupScope.Universal,
                               _ when (flags & Global)       != 0 => GroupScope.Global,
                               _ when (flags & DomainLocal)  != 0 => GroupScope.DomainLocal,
                               _                                  => GroupScope.Unknown
                           };

        return new AdGroupInfo(scope, isSecurity);
    }

    /// <summary>
    ///     The first relative distinguished name of a distinguished name (<c>CN=Person</c> of
    ///     <c>CN=Person,CN=Schema,...</c>); a comma written <c>\,</c> does not end it.
    /// </summary>
    public static string FirstRdn(string? dn)
    {
        if (dn is null) return string.Empty;

        for (int i = 0; i < dn.Length; i++)
            if (dn[i] == '\\')
                i++; // the character after a backslash is part of the name
            else if (dn[i] == ',')
                return dn[..i];

        return dn;
    }

    /// <summary>
    ///     What an RDN names, without its type: <c>Person</c> for <c>CN=Person</c>. Text with no <c>=</c> is returned as
    ///     it is.
    /// </summary>
    public static string RdnValue(string? rdn)
    {
        if (rdn is null) return string.Empty;

        int eq = rdn.IndexOf('=');
        return eq >= 0 ? rdn[(eq + 1)..] : rdn;
    }

    /// <summary>
    ///     What an entry is, as one lower-case word with no hyphens, from its schema class (<c>organizationalUnit</c>) or,
    ///     when it has none, its category (<c>Organizational-Unit</c>); <c>object</c> when neither says.
    /// </summary>
    public static string KindOf(string? schemaClass, string? category)
    {
        string name = !string.IsNullOrWhiteSpace(schemaClass) ? schemaClass : category ?? string.Empty;
        name = name.Replace("-", string.Empty).Trim().ToLowerInvariant();

        return name.Length > 0 ? name : "object";
    }

    /// <summary>The words for what an entry is. A group says what kind of group (<c>Global Security Group</c>).</summary>
    public static string TypeNameOf(string kind, int groupType, string? fallback = null)
    {
        return kind switch
               {
                   "computer"                                            => "Computer Account",
                   "user"                                                => "User Account",
                   "person" or "organizationalperson" or "inetorgperson" => "Person",
                   "contact"                                             => "Contact",
                   "group"                                               => FromGroupType(groupType) + " Group",
                   "organizationalunit"                                  => "OU",
                   "domaindns" or "domain"                               => "Domain",
                   "rootdse" or "dse"                                    => "Root DSE",
                   "container"                                           => "Container",
                   "builtindomain"                                       => "Built-in Domain",
                   "foreignsecurityprincipal"                            => "Foreign Security Principal",
                   "subnet"                                              => "Subnet",
                   "site"                                                => "Site",
                   _                                                     => fallback is { Length: > 0 } ? fallback : "Object"
               };
    }

    /// <summary>The key of the icon (see <c>Resources/NavIcons.xaml</c>) for a kind of entry (see <see cref="KindOf" />).</summary>
    public static string IconKeyOf(string kind)
    {
        return kind switch
               {
                   "computer"                                                                                    => "PcMonitor",
                   "user" or "person" or "organizationalperson" or "inetorgperson" or "foreignsecurityprincipal" => "Person",
                   "contact"                                                                                     => "Credentials",
                   "group"                                                                                       => "UserGroup",
                   "organizationalunit" or "container" or "builtindomain"                                        => "Folder17",
                   "domaindns" or "domain"                                                                       => "Network",
                   "rootdse" or "dse"                                                                            => "Server",
                   "site"                                                                                        => "HierarchyStructure",
                   "subnet"                                                                                      => "Nodes",
                   _                                                                                             => "Record"
               };
    }

    // some entries (the root DSE) cannot say what class they are
    private static string? SchemaClassOf(DirectoryEntry entry)
    {
        try
        {
            return entry.SchemaClassName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads what the entry is from the entry (which asks the directory, unless it was loaded already).</summary>
    /// <param name="entry">The entry the model stands for from now on.</param>
    /// <param name="keepUri">Keep the URI the model has (the one it was asked for) instead of the entry's own.</param>
    [MemberNotNull(nameof(Entry), nameof(Name), nameof(Kind), nameof(TypeName), nameof(Icon), nameof(Uri))]
    protected void Populate(DirectoryEntry entry, bool keepUri = false)
    {
        Entry          = entry.Required();
        ObjectCategory = entry.GetValue<string>("objectCategory");
        Category       = ObjectCategory is null ? null : RdnValue(FirstRdn(ObjectCategory));
        DN             = entry.GetValue<string>("distinguishedName");
        Name           = entry.GetValue<string>("name") ?? RdnValue(entry.Name);
        if (!keepUri || Uri is null) Uri = new Uri(entry.Path, UriKind.RelativeOrAbsolute);
        GroupType   = entry.GetValue<int>("groupType");
        SchemaClass = SchemaClassOf(entry);
        Kind        = KindOf(SchemaClass, Category);
        TypeName    = TypeNameOf(Kind, GroupType, Category ?? SchemaClass);
        Icon        = IconRef.FromKey(IconKeyOf(Kind));
    }

    /// <summary>
    ///     A model of an entry that is already bound: its properties are read now (which asks the directory). With no
    ///     <paramref name="connection" /> it belongs to the app's local connection (<see cref="App.LocalConnection" />), as
    ///     a model of anything that was not read through a connection does; <see cref="Connection" /> stays null.
    /// </summary>
    public LdapModel(DirectoryEntry entry, LdapConnection? connection = null) : base(connection ?? App.LocalConnection)
    {
        Connection = this is LdapConnection cn ? cn : connection;
        Populate(entry);
    }

    /// <summary>
    ///     A model of an entry that has not been opened yet (<paramref name="requested" /> is where it was asked to go): it
    ///     knows only
    ///     that, and says what it is once <see cref="Populate" /> has read the entry. Nothing is asked of the directory.
    /// </summary>
    protected LdapModel(DirectoryEntry unbound, Uri requested, LdapConnection? connection = null) : base(connection)
    {
        Connection = connection;
        Entry      = unbound.Required();
        Uri        = requested.Required();
        Kind       = "domaindns";
        Name       = requested.Host.Length > 0 ? requested.Host : "Directory";
        TypeName   = TypeNameOf(Kind, 0);
        Icon       = IconRef.FromKey(IconKeyOf(Kind));
    }

    /// <summary>
    ///     The rows of the Properties pane: what the entry is and the attributes people look for. Nothing is asked of the
    ///     directory (they were read with the entry).
    /// </summary>
    public virtual IEnumerable<(string Name, string Value)> GetDetails()
    {
        yield return ("Type", TypeName);
        yield return ("Name", Name);

        if (DN is not null) yield return ("Distinguished name", DN);
        if (SchemaClass is not null) yield return ("Class", SchemaClass);
        if (Category is not null) yield return ("Category", Category);
        if (Kind == "group") yield return ("Group type", GetGroupTypeName());

        if (!IsLoaded) yield break; // not read yet: the rows below would bind the entry on whoever asked

        foreach (var (label, attribute) in DetailAttributes)
            if (AttributeText(Entry, attribute) is { Length: > 0 } value)
                yield return (label, value);
    }

    /// <summary>The node for the entry; the entries below it are read when it is expanded.</summary>
    public virtual SiloNode? GetNode()
    {
        Node ??= new LdapNode(this);
        return Node;
    }

    public readonly record struct AdGroupInfo(GroupScope Scope, bool IsSecurityEnabled)
    {
        public override string ToString()
        {
            return $"{Scope} {(IsSecurityEnabled ? "Security" : "Distribution")}";
        }
    }
}

/// <summary>What a connection needs to open its directory when it connects: where, as whom, and how.</summary>
/// <param name="Path">
///     The ADSI path (<c>LDAP://host:port/distinguished name</c>), perhaps without a distinguished name
///     (see <see cref="LdapModel.ToAdsPath" />).
/// </param>
/// <param name="UserName">The name to sign in as, domain included; null for the Windows user's own.</param>
/// <param name="Password">The password.</param>
/// <param name="Auth">The authentication types, TLS included.</param>
public sealed record LdapBind(string Path, string? UserName, string? Password, AuthenticationTypes Auth);