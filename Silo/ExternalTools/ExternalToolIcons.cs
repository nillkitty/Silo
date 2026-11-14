namespace Silo.ExternalTools;

/// <summary>
///     The icon keys of the external tools and their sections. Most are shapes from the root <c>Paths.xaml</c>, copied into
///     <c>Resources/ExternalToolIcons.xaml</c> (the root set is not merged into the app); add a shape there when a key here
///     needs one. A tool offered for several target types (ping, say) keeps the same icon for each; no two tools of one
///     target type share one.
/// </summary>
public static class ExternalToolIcons
{
    // sections
    public const string Terminal = "Terminal";
    public const string Browser  = "Icon007Browser";
    public const string Other    = "Icon083Hammer";
    public const string CopyHash = "Icon005BinaryCode";

    // in a terminal
    public const string Ping              = "Icon065Radar";
    public const string Traceroute        = "Icon115Tracking";
    public const string NetBiosNames      = "IconNameTag";
    public const string ArpLookup         = "Icon176EthernetPort";
    public const string NslookupAsServer  = "Icon038Dns";
    public const string NslookupAsTarget  = "Icon003MagnifyingGlass";
    public const string Ssh               = "Icon021Key";

    // in a browser
    public const string GeoIp             = "Icon130MapPointer";
    public const string WhoisAddress      = "Icon089AddressBook";
    public const string ArinLookup        = "Icon209LegalDocument";
    public const string WhoisDomain       = "Icon051DomainRegistration";
    public const string WaybackMachine    = "Archive";
    public const string BrowseHttps       = "Icon142WebBrowser";

    // other
    public const string RemoteDesktop     = "Icon106Monitor";
    public const string Nmap              = "Icon020Scan";
    public const string Sessions          = "Icon160Connector";
    public const string FirewallLog       = "Icon059Firewall";
    public const string SecurityLog       = "Icon026Security";
    public const string AdsiConnections   = "Icon191Hierarchy";
    public const string SmbShares         = "Icon153Share";

    // hashes
    public const string Md5               = "Icon046Fingerprint";
    public const string Crc32             = "Icon101Checking";
    public const string Sha1              = "Icon234Token";
    public const string Sha256            = "Icon037DataEncryption";
    public const string Sha512            = "Icon022Encryption";
}
