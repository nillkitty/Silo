using System.Net;
using System.Net.Sockets;
using Silo.Connectors.File;
using Silo.Providers.Tree;
using S = Silo.ExternalTools.ExternalToolSection;
using I = Silo.ExternalTools.ExternalToolIcons;
using Run = Silo.ExternalTools.ExternalToolRun;

namespace Silo.ExternalTools;

/// <summary>
///     The external tools Silo comes with, for IP addresses, domain names and files, and the Explorer nodes that stand for
///     them (a host of the network tree, a name of the DNS tree, a resource record, a file). Registered by
///     <see cref="ExternalTools" /> the first time it is used.
/// </summary>
/// <remarks>
///     Targets go on command lines as they are: an <see cref="IPAddress" /> prints only hex digits, dots, colons and a scope
///     number, and a <see cref="DomainName" /> only letters, digits, hyphens, underscores and dots.
/// </remarks>
public static class BuiltInExternalTools
{
    private const string Source = "Silo";

    internal static void Register()
    {
        RegisterAddressTools();
        RegisterNameTools();
        RegisterFileTools();
        RegisterTargets();
    }

    // ---- IP addresses -----------------------------------------------------------------------------------------------

    private static void RegisterAddressTools()
    {
        // in a terminal
        ExternalTools.Register<IPAddress>("ping", "_Ping", I.Ping, S.Terminal, "Pings the address",
                                          a => new Run.Terminal($"ping {a}", $"ping {a}"), featured: true);
        ExternalTools.Register<IPAddress>("nbtstat", "_NetBIOS Names (nbtstat)", I.NetBiosNames, S.Terminal,
                                          "Lists the NetBIOS names the computer at the address has registered",
                                          a => new Run.Terminal($"nbtstat {a}", $"nbtstat -A {a}"), IsV4);
        ExternalTools.Register<IPAddress>("tracert", "_Trace Route (tracert)", I.Traceroute, S.Terminal,
                                          "Shows the routers between this computer and the address",
                                          a => new Run.Terminal($"tracert {a}", $"tracert {a}"));
        ExternalTools.Register<IPAddress>("arp", "_ARP Lookup", I.ArpLookup, S.Terminal,
                                          "Shows the hardware (MAC) address this computer knows for the address (IPv6: the neighbor cache)",
                                          a => new Run.Terminal($"arp {a}", IsV4(a)
                                                                                ? $"arp -a {a}"
                                                                                : $"netsh interface ipv6 show neighbors | findstr /I /C:\"{a}\""));
        ExternalTools.Register<IPAddress>("nslookup.server", "nslookup with this as _Server", I.NslookupAsServer, S.Terminal,
                                          "Starts nslookup asking the DNS server at this address (type the names to look up)",
                                          a => new Run.Terminal($"nslookup @{a}", $"nslookup - {a}"));
        ExternalTools.Register<IPAddress>("nslookup.target", "nslookup this _Address", I.NslookupAsTarget, S.Terminal,
                                          "Asks DNS for the name of the address (a reverse lookup)",
                                          a => new Run.Terminal($"nslookup {a}", $"nslookup {a}"));
        ExternalTools.Register<IPAddress>("ssh", "_SSH", I.Ssh, S.Terminal, "Opens a secure shell session to the address",
                                          a => new Run.Terminal($"ssh {a}", $"ssh {a}"), requiresProgram: "ssh.exe");

        // in a browser
        ExternalTools.Register<IPAddress>("geoip", "_Geo-IP", I.GeoIp, S.Browser, "Shows where the address is and who runs its network (ipinfo.io)",
                                          a => new Run.Browser(new Uri($"https://ipinfo.io/{Bare(a)}")), IsPublic);
        ExternalTools.Register<IPAddress>("whois", "_WHOIS", I.WhoisAddress, S.Browser, "Shows the registration of the address's network (who.is)",
                                          a => new Run.Browser(new Uri($"https://who.is/whois-ip/ip-address/{Bare(a)}")), IsPublic);
        ExternalTools.Register<IPAddress>("arin", "_ARIN Lookup", I.ArinLookup, S.Browser,
                                          "Looks the address up in the ARIN registry (RDAP), which refers it to the regional registry that has it",
                                          a => new Run.Browser(new Uri($"https://search.arin.net/rdap/?query={Bare(a)}")), IsPublic);
        ExternalTools.Register<IPAddress>("browse", "Browse IP (_HTTPS)", I.BrowseHttps, S.Browser, "Opens https:// at the address",
                                          a => new Run.Browser(new Uri($"https://{UrlHost(a)}/")));

        // other
        ExternalTools.Register<IPAddress>("rdp", "_Remote Desktop to IP", I.RemoteDesktop, S.Other, "Connects to the address with Remote Desktop",
                                          a => new Run.Program("mstsc.exe", $"/v:{UrlHost(a)}"));
        ExternalTools.Register<IPAddress>("nmap", "_nmap", I.Nmap, S.Other, "Scans the address's common ports with nmap",
                                          a => new Run.Terminal($"nmap {a}", IsV4(a) ? $"nmap {a}" : $"nmap -6 {a}"), requiresProgram: "nmap.exe");
        ExternalTools.Register<IPAddress>("sessions", "Show TCP/UDP _Sessions", I.Sessions, S.Other,
                                          "Lists this computer's TCP connections and UDP endpoints with the address, and the processes that own them",
                                          a => new Run.Terminal($"sessions {a}", $"netstat -ano | findstr /C:\"{a}\""));
        ExternalTools.Register<IPAddress>("firewall.log", "Search _Firewall Log", I.FirewallLog, S.Other,
                                          "Searches Windows Firewall's log for the address (as administrator)",
                                          a => new Run.PowerShell($"firewall log {a}", FirewallLogScript(a.ToString()), true));
        ExternalTools.Register<IPAddress>("security.log", "Search Security _Log", I.SecurityLog, S.Other,
                                          "Lists the Security event log's events (logons...) from the address (as administrator)",
                                          a => new Run.PowerShell($"security log {a}", AddressSecurityLogScript(a), true));
    }

    // ---- domain names -----------------------------------------------------------------------------------------------

    private static void RegisterNameTools()
    {
        // in a browser
        ExternalTools.Register<DomainName>("whois", "_WHOIS", I.WhoisDomain, S.Browser, "Shows the domain's registration (who.is)",
                                           n => new Run.Browser(new Uri($"https://who.is/whois/{n}")));
        ExternalTools.Register<DomainName>("archive", "_archive.org", I.WaybackMachine, S.Browser,
                                           "Shows the snapshots of the site the Wayback Machine has (web.archive.org)",
                                           n => new Run.Browser(new Uri($"https://web.archive.org/web/*/{n}")));
        ExternalTools.Register<DomainName>("browse", "Browse Host (_HTTPS)", I.BrowseHttps, S.Browser, "Opens https:// at the name",
                                           n => new Run.Browser(new Uri($"https://{n}/")));

        // in a terminal
        ExternalTools.Register<DomainName>("ping", "_Ping", I.Ping, S.Terminal, "Pings the host",
                                           n => new Run.Terminal($"ping {n}", $"ping {n}"), featured: true);
        ExternalTools.Register<DomainName>("tracert", "_Trace Route (tracert)", I.Traceroute, S.Terminal,
                                           "Shows the routers between this computer and the host",
                                           n => new Run.Terminal($"tracert {n}", $"tracert {n}"));
        ExternalTools.Register<DomainName>("nslookup.target", "nslookup this _Name", I.NslookupAsTarget, S.Terminal,
                                           "Asks DNS for the addresses of the name",
                                           n => new Run.Terminal($"nslookup {n}", $"nslookup {n}"));
        ExternalTools.Register<DomainName>("nslookup.server", "nslookup with this as _Server", I.NslookupAsServer, S.Terminal,
                                           "Starts nslookup asking the DNS server of this name (type the names to look up)",
                                           n => new Run.Terminal($"nslookup @{n}", $"nslookup - {n}"));
        ExternalTools.Register<DomainName>("ssh", "_SSH", I.Ssh, S.Terminal, "Opens a secure shell session to the host",
                                           n => new Run.Terminal($"ssh {n}", $"ssh {n}"), requiresProgram: "ssh.exe");

        // other
        ExternalTools.Register<DomainName>("rdp", "_Remote Desktop to Name", I.RemoteDesktop, S.Other, "Connects to the host with Remote Desktop",
                                           n => new Run.Program("mstsc.exe", $"/v:{n}"));
        ExternalTools.Register<DomainName>("nmap", "_nmap", I.Nmap, S.Other, "Scans the host's common ports with nmap",
                                           n => new Run.Terminal($"nmap {n}", $"nmap {n}"), requiresProgram: "nmap.exe");
        ExternalTools.Register<DomainName>("security.log", "Search Security _Log", I.SecurityLog, S.Other,
                                           "Lists the Security event log's recent events that name the host (as administrator)",
                                           n => new Run.PowerShell($"security log {n}", NameSecurityLogScript(n), true));
        ExternalTools.Register<DomainName>("adsi", "Reveal A_DSI Connections", I.AdsiConnections, S.Other,
                                           "Lists this computer's LDAP connections (ADSI, Active Directory) to the host, and the processes that own them",
                                           n => new Run.PowerShell($"ADSI connections {n}", AdsiConnectionsScript(n)));
        ExternalTools.Register<DomainName>("smb", "Show SM_B Shares", I.SmbShares, S.Other, "Lists the folders and printers the host shares (net view)",
                                           n => new Run.Terminal($"shares {n}", $@"net view \\{n} /all"));
    }

    // ---- files ------------------------------------------------------------------------------------------------------

    private static void RegisterFileTools()
    {
        (string Id, string Header, string Icon, FileHashKind Kind)[] hashes =
        [
            ("md5", "_MD5", I.Md5, FileHashKind.Md5),
            ("crc32", "_CRC32", I.Crc32, FileHashKind.Crc32),
            ("sha1", "SHA_1", I.Sha1, FileHashKind.Sha1),
            ("sha256", "SHA_256", I.Sha256, FileHashKind.Sha256),
            ("sha512", "SHA_512", I.Sha512, FileHashKind.Sha512)
        ];

        foreach (var (id, header, icon, kind) in hashes)
            ExternalTools.Register<FileInfo>("hash." + id, header, icon, S.CopyHash,
                                             $"Computes the file's {FileHashes.NameOf(kind)} checksum and copies it (as hex)",
                                             f => new Run.InApp($"compute the {FileHashes.NameOf(kind)} of {f.FullName}", () => FileHashes.CopyAsync(f, kind)),
                                             f => f.Exists);
    }

    // ---- what Explorer nodes stand for -------------------------------------------------------------------------------

    private static void RegisterTargets()
    {
        // a host of the network tree (a /32 or /128), not a subnet
        ExternalTools.RegisterTarget<NetTreeNode>(n => !n.IsUnexplored && n.Subnet.PrefixLength == n.Subnet.MaxPrefixLength ? n.Subnet.Network : null);

        ExternalTools.RegisterTarget<DnsNode>(n => DomainName.From(n.Name));
        ExternalTools.RegisterTarget<DnsRecordLeaf>(r => RecordTarget(r.Record));

        ExternalTools.RegisterTarget<FileModel>(f => f.Kind is FileKind.File or FileKind.FileSymlink ? new FileInfo(f.Path) : null);
    }

    /// <summary>What a resource record points at: the address of an A or AAAA, the name of an NS, CNAME, PTR, MX or SRV.</summary>
    public static object? RecordTarget(DnsRecord record)
    {
        string data = record.Data.Trim();
        return record.Type switch
               {
                   DnsType.A or DnsType.AAAA                  => IPAddress.TryParse(data, out var address) ? address : null,
                   DnsType.NS or DnsType.CNAME or DnsType.PTR => DomainName.From(data),
                   DnsType.MX or DnsType.SRV                  => DomainName.From(data.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()),
                   _                                          => null
               };
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static bool IsV4(IPAddress a) => a.AddressFamily == AddressFamily.InterNetwork;

    /// <summary>Whether the address is one the Internet's registries know: not private, loopback, link-local or the like.</summary>
    internal static bool IsPublic(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal || a.IsIPv6Multicast) return false;
        if (!IsV4(a)) return !a.Equals(IPAddress.IPv6Any);

        byte[] b = a.GetAddressBytes();
        return b[0] switch
               {
                   0 or 10 or 127 or >= 224 => false,
                   100                      => b[1] is < 64 or > 127, // 100.64.0.0/10, carrier-grade NAT
                   169                      => b[1] != 254,
                   172                      => b[1] is < 16 or > 31,
                   192                      => b[1] != 168,
                   _                        => true
               };
    }

    /// <summary>The address without an IPv6 scope ("%12"), which means nothing to another computer.</summary>
    private static string Bare(IPAddress a) => IsV4(a) || a.ScopeId == 0 ? a.ToString() : new IPAddress(a.GetAddressBytes()).ToString();

    /// <summary>The address as the host of a URL: an IPv6 address in brackets.</summary>
    private static string UrlHost(IPAddress a) => IsV4(a) ? a.ToString() : $"[{Bare(a)}]";

    internal static string FirewallLogScript(string target)
    {
        return $$$"""
                 $log = "$env:SystemRoot\System32\LogFiles\Firewall\pfirewall.log"
                 if (-not (Test-Path $log)) { "Windows Firewall is not logging to $log (turn logging on in the firewall's settings)."; return }
                 $p = '(?<![\w.:])' + [regex]::Escape('{{{target}}}') + '(?![\w.:])'
                 $found = Select-String -Path $log -Pattern $p | ForEach-Object Line
                 if ($found) { $found } else { "Nothing in $log mentions {{{target}}}." }
                 """;
    }

    internal static string AddressSecurityLogScript(IPAddress a)
    {
        string mapped = IsV4(a) ? $" or Data[@Name='IpAddress']='::ffff:{a}'" : "";
        return $$$"""
                 $xpath = "*[EventData[Data[@Name='IpAddress']='{{{a}}}'{{{mapped}}}]]"
                 try {
                     Get-WinEvent -LogName Security -FilterXPath $xpath -MaxEvents 500 -ErrorAction Stop |
                         Format-Table -AutoSize TimeCreated, Id, TaskDisplayName, @{n='Summary';e={($_.Message -split "`r?`n")[0]}}
                 } catch { "No events in the Security log from {{{a}}} ($($_.Exception.Message))" }
                 """;
    }

    internal static string NameSecurityLogScript(DomainName n)
    {
        return $$$"""
                 $name = [regex]::Escape('{{{n}}}'); $label = [regex]::Escape('{{{n.FirstLabel}}}')
                 $p = "(?i)(\b$name\b|\b$label\$|Workstation Name:\s+$label\b)"
                 "Searching the last 20000 events of the Security log for {{{n}}}..."
                 $found = Get-WinEvent -LogName Security -MaxEvents 20000 -ErrorAction SilentlyContinue | Where-Object { $_.Message -match $p } | Select-Object -First 500
                 if ($found) { $found | Format-Table -AutoSize TimeCreated, Id, TaskDisplayName, @{n='Summary';e={($_.Message -split "`r?`n")[0]}} }
                 else { "No recent events in the Security log name {{{n}}}." }
                 """;
    }

    internal static string AdsiConnectionsScript(DomainName n)
    {
        return $$$"""
                 try { $ips = [System.Net.Dns]::GetHostAddresses('{{{n}}}') | ForEach-Object { $_.IPAddressToString } }
                 catch { "{{{n}}} could not be resolved: $($_.Exception.Message)"; return }
                 "{{{n}}}: $($ips -join ', ')"
                 $found = Get-NetTCPConnection -RemotePort 389, 636, 3268, 3269 -ErrorAction SilentlyContinue | Where-Object { $ips -contains $_.RemoteAddress }
                 if ($found) {
                     $found | Format-Table -AutoSize LocalAddress, LocalPort, RemoteAddress, RemotePort, State, OwningProcess,
                         @{n='Process';e={(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName}}
                 } else { "No LDAP connections (ports 389, 636, 3268, 3269) to {{{n}}}." }
                 """;
    }
}
