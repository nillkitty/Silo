using System.DirectoryServices;
using Silo.Model;

namespace Silo.Connectors.Ldap;

public class LdapModel : ModelBase
{
    protected internal const uint BuiltinLocal = 0x00000001; // GROUP_TYPE_BUILTIN_LOCAL_GROUP
    protected internal const uint Global       = 0x00000002; // GROUP_TYPE_ACCOUNT_GROUP
    protected internal const uint DomainLocal  = 0x00000004; // GROUP_TYPE_RESOURCE_GROUP
    protected internal const uint Universal    = 0x00000008; // GROUP_TYPE_UNIVERSAL_GROUP
    protected internal const uint Security     = 0x80000000; // GROUP_TYPE_SECURITY_ENABLED

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
    public string? Category { get; }

    public ICredential? Credential { get; set; }

    public string?        DN        { get; }
    public DirectoryEntry Entry     { get; init; }
    public int            GroupType { get; }

    /// <summary>
    ///     The key of the icon (see <c>Resources/NavIcons.xaml</c>) as a relative URI, which is how the Explorer reads one.
    /// </summary>
    public Uri Icon { get; }

    /// <summary>
    ///     What the entry is, in one lower-case word with no hyphens (<c>user</c>, <c>group</c>,
    ///     <c>organizationalunit</c>); see <see cref="KindOf" />.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    ///     The entry's name as people write it (<c>Smith, John</c>), not its relative distinguished name (
    ///     <c>CN=Smith\, John</c>).
    /// </summary>
    public string? Name { get; }

    public string? ObjectCategory { get; }

    /// <summary>The entry's class in the directory schema (<c>user</c>, <c>group</c>, <c>organizationalUnit</c>...).</summary>
    public string? SchemaClass { get; }

    public string TypeName { get; }
    public Uri    Uri      { get; }

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
    ///     Opens the directory a request names: its URI (<see cref="ToAdsPath" />; the connector's default, the current
    ///     domain, when there is none), signed in with the credential in the request's metadata (the Windows user's own when
    ///     there is none), with the connector's paging, referral and password encoding.
    /// </summary>
    public static LdapConnection Connect(LdapConnector connector, ConnectionRequest? crq)
    {
        connector.Required();
        crq ??= new ConnectionRequest(connector.DefaultUri);
        Uri uri = crq.RequestedUri ?? connector.DefaultUri;

        // the credential hands over what it has to a sink; nothing is read out of it
        var credential = crq.Metadata.OfType<ICredential>().FirstOrDefault();
        var sink       = new LdapCredentialSink();
        credential?.Configure(sink);

        var    auth = AuthenticationTypes.Secure | (IsSecure(uri) ? AuthenticationTypes.SecureSocketsLayer : AuthenticationTypes.None);
        // the entry binds as soon as its options are touched, so it has to know who it is by then
        string path = WithDefaultNamingContext(ToAdsPath(uri), sink.UserName, sink.Password, auth);

        var entry = new DirectoryEntry(path, sink.UserName, sink.Password, auth);
        try
        {
            entry.Options.PageSize         = connector.PageSize ?? 10_000;
            entry.Options.Referral         = connector.Referral;
            entry.Options.PasswordEncoding = connector.PasswordEncoding;
            entry.RefreshCache();
        }
        catch
        {
            entry.Dispose();
            throw;
        }

        return new LdapConnection(entry, connector, crq.Metadata ?? [], credential);
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

    public LdapModel(DirectoryEntry entry)
    {
        Entry          = entry.Required();
        ObjectCategory = entry.GetValue<string>("objectCategory");
        Category       = ObjectCategory is null ? null : RdnValue(FirstRdn(ObjectCategory));
        DN             = entry.GetValue<string>("distinguishedName");
        Name           = entry.GetValue<string>("name") ?? RdnValue(entry.Name);
        Uri            = new Uri(entry.Path, UriKind.RelativeOrAbsolute);
        GroupType      = entry.GetValue<int>("groupType");
        SchemaClass    = SchemaClassOf(entry);
        Kind           = KindOf(SchemaClass, Category);
        TypeName       = TypeNameOf(Kind, GroupType, Category ?? SchemaClass);
        Icon           = new Uri(IconKeyOf(Kind), UriKind.Relative);
    }

    public readonly record struct AdGroupInfo(GroupScope Scope, bool IsSecurityEnabled)
    {
        public override string ToString()
        {
            return $"{Scope} {(IsSecurityEnabled ? "Security" : "Distribution")}";
        }
    }
}