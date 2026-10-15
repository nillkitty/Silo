using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Serilog;
using Telefrag.DI;

// Note: this namespace sits under Silo.Connectors, which has a child namespace named System -- so code here never writes
// "System.Something"; it uses the usings above (or global::System).
namespace Silo.Connectors.Discovery;

/// <summary>Shared plumbing: turns a list that was built (and may have failed part way) into the discovery stream.</summary>
internal static class DiscoveryStream
{
    /// <summary>
    ///     Yields the entries as <see cref="Discovered{TComponent}" /> after one asynchronous hop, so the caller's thread
    ///     is never held up.
    /// </summary>
    public static async IAsyncEnumerable<Discovered<DestinationEntry>> Of(IDiscovery<DestinationEntry>               discovery,
                                                                          Func<List<DestinationEntry>>               build,
                                                                          [EnumeratorCancellation] CancellationToken cancel)
    {
        await Task.Yield();

        List<DestinationEntry> entries;
        try
        {
            entries = build();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "{discovery} failed", discovery.DisplayName);
            entries = [];
        }

        foreach (var entry in entries)
        {
            cancel.ThrowIfCancellationRequested();
            yield return new Discovered<DestinationEntry>(discovery, entry.Uri, entry);
        }
    }

    /// <summary>A file: URI for a folder or drive path, or null when it cannot be made.</summary>
    public static Uri? FileUri(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile ? uri : null;
    }
}

/// <summary>The drives of this computer (not the network ones: <see cref="MappedNetworkDiscovery" /> lists those).</summary>
[Singleton(typeof(IDiscovery<DestinationEntry>))]
public sealed class DriveDiscovery : IDiscovery<DestinationEntry>
{
    public string        DisplayName => "Drives";
    public DiscoveryType Type        => DiscoveryType.Quick;

    private static List<DestinationEntry> Build()
    {
        var result = new List<DestinationEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown) continue;

            string root   = drive.Name;
            string kind   = Describe(drive.DriveType);
            string label  = string.Empty;
            string detail = kind;
            try
            {
                if (drive.IsReady)
                {
                    label  = drive.VolumeLabel;
                    detail = $"{kind}, {FormatSize(drive.TotalSize)}";
                }
                else
                {
                    detail = $"{kind}, not ready";
                }
            }
            catch (Exception)
            {
                /* a drive that cannot be asked is listed by its letter and kind */
            }

            var uri = DiscoveryStream.FileUri(root);
            if (uri is null) continue;

            string letter = root.TrimEnd('\\', '/');
            result.Add(new DestinationEntry(string.IsNullOrWhiteSpace(label) ? letter : $"{label} ({letter})", uri, detail));
        }

        return result;
    }

    private static string Describe(DriveType type)
    {
        return type switch
               {
                   DriveType.Fixed     => "Local disk",
                   DriveType.Removable => "Removable drive",
                   DriveType.CDRom     => "Optical drive",
                   DriveType.Ram       => "RAM disk",
                   _                   => "Drive"
               };
    }

    /// <summary>1536 bytes read as "1.5 KB"; used for drive sizes.</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
        double   size  = bytes;
        int      unit  = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} bytes" : $"{size:0.#} {units[unit]}";
    }

    public IAsyncEnumerable<Discovered<DestinationEntry>> DiscoverAsync(CancellationToken cancel)
    {
        return DiscoveryStream.Of(this, Build, cancel);
    }
}

/// <summary>The well-known folders of the current user and the computer (Desktop, Documents, Downloads, Program Files...).</summary>
[Singleton(typeof(IDiscovery<DestinationEntry>))]
public sealed class KnownFolderDiscovery : IDiscovery<DestinationEntry>
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    private static readonly (string Name, Environment.SpecialFolder Folder)[] Folders =
    [
        ("Desktop",
         Environment.SpecialFolder.DesktopDirectory),
        ("Documents",
         Environment.SpecialFolder.MyDocuments),
        ("Music", Environment.SpecialFolder.MyMusic),
        ("Pictures",
         Environment.SpecialFolder.MyPictures),
        ("Videos", Environment.SpecialFolder.MyVideos),
        ("Home folder",
         Environment.SpecialFolder.UserProfile),
        ("Application data (roaming)",
         Environment.SpecialFolder.ApplicationData),
        ("Application data (local)",
         Environment.SpecialFolder
                    .LocalApplicationData),
        ("Application data (all users)",
         Environment.SpecialFolder
                    .CommonApplicationData),
        ("Program Files",
         Environment.SpecialFolder.ProgramFiles),
        ("Program Files (x86)",
         Environment.SpecialFolder.ProgramFilesX86),
        ("Windows", Environment.SpecialFolder.Windows),
        ("Start menu",
         Environment.SpecialFolder.StartMenu),
        ("Startup", Environment.SpecialFolder.Startup)
    ];

    public string        DisplayName => "Known folders";
    public DiscoveryType Type        => DiscoveryType.Quick;

    private static List<DestinationEntry> Build()
    {
        var result = new List<DestinationEntry>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || !seen.Add(path.TrimEnd('\\', '/'))) return;
            if (DiscoveryStream.FileUri(path) is { } uri) result.Add(new DestinationEntry(name, uri, path));
        }

        Add(Folders[0].Name, Environment.GetFolderPath(Folders[0].Folder));
        Add(Folders[1].Name, Environment.GetFolderPath(Folders[1].Folder));
        Add("Downloads",     DownloadsFolder());
        foreach (var (name, folder) in Folders.Skip(2))
            Add(name, Environment.GetFolderPath(folder));

        return result;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pointer);

    /// <summary>The user's Downloads folder (it has no <see cref="Environment.SpecialFolder" />), or null.</summary>
    private static string? DownloadsFolder()
    {
        try
        {
            var id = DownloadsId;
            if (SHGetKnownFolderPath(in id, 0, IntPtr.Zero, out var pointer) != 0) return null;

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                CoTaskMemFree(pointer);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    public IAsyncEnumerable<Discovered<DestinationEntry>> DiscoverAsync(CancellationToken cancel)
    {
        return DiscoveryStream.Of(this, Build, cancel);
    }
}

/// <summary>The registry hives (<c>reg:///hkey_current_user</c> and the others).</summary>
[Singleton(typeof(IDiscovery<DestinationEntry>))]
public sealed class RegistryHiveDiscovery : IDiscovery<DestinationEntry>
{
    private static readonly (string Name, string Detail)[] Hives =
    [
        ("HKEY_CURRENT_USER", "Settings of the current user"),
        ("HKEY_LOCAL_MACHINE", "Settings of this computer"),
        ("HKEY_CLASSES_ROOT", "File types and COM classes"),
        ("HKEY_USERS", "Every loaded user profile"),
        ("HKEY_CURRENT_CONFIG", "The current hardware profile")
    ];

    public string        DisplayName => "Registry hives";
    public DiscoveryType Type        => DiscoveryType.Quick;

    private static List<DestinationEntry> Build()
    {
        return Hives.Select(h => new DestinationEntry(h.Name, new Uri("reg:///" + h.Name.ToLowerInvariant()), h.Detail)).ToList();
    }

    public IAsyncEnumerable<Discovered<DestinationEntry>> DiscoverAsync(CancellationToken cancel)
    {
        return DiscoveryStream.Of(this, Build, cancel);
    }
}

/// <summary>
///     Mapped network drives (the connected ones, and those Windows remembers from the last sign-in:
///     <c>HKCU\Network</c>), listed by the share they point at.
/// </summary>
[Singleton(typeof(IDiscovery<DestinationEntry>))]
public sealed class MappedNetworkDiscovery : IDiscovery<DestinationEntry>
{
    public string        DisplayName => "Mapped network drives";
    public DiscoveryType Type        => DiscoveryType.Quick;

    private static List<DestinationEntry> Build()
    {
        // drive letter ("Z:") -> where it points
        var drives = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var network = Registry.CurrentUser.OpenSubKey("Network");
            foreach (string name in network?.GetSubKeyNames() ?? [])
            {
                using var key = network!.OpenSubKey(name);
                if (key?.GetValue("RemotePath") is string { Length: > 0 } remote)
                    drives[name.TrimEnd(':').ToUpperInvariant() + ":"] = remote;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Remembered network drives could not be read");
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Network) continue;

            string letter = drive.Name.TrimEnd('\\', '/');
            drives.TryAdd(letter.ToUpperInvariant(), drive.Name); // no way to ask for the share here: fall back to the drive itself
        }

        var result = new List<DestinationEntry>();
        foreach (var (letter, remote) in drives)
            if (DiscoveryStream.FileUri(remote) is { } uri)
                result.Add(new DestinationEntry($"{letter} ({remote})", uri, "Mapped network drive"));

        return result;
    }

    public IAsyncEnumerable<Discovered<DestinationEntry>> DiscoverAsync(CancellationToken cancel)
    {
        return DiscoveryStream.Of(this, Build, cancel);
    }
}

/// <summary>
///     The Active Directory domain this computer has joined, when it has joined one (<c>ldap://domain</c>). There is no
///     LDAP connector yet, so the Destinations tab lists it as unavailable until there is.
/// </summary>
[Singleton(typeof(IDiscovery<DestinationEntry>))]
public sealed class DomainDiscovery : IDiscovery<DestinationEntry>
{
    private const int NetSetupDomainName = 3;

    public string        DisplayName => "Joined domain";
    public DiscoveryType Type        => DiscoveryType.Quick;

    /// <summary>The destination for a domain name (<c>corp.example.com</c>), or null when there is none.</summary>
    public static DestinationEntry? EntryFor(string? domainName)
    {
        domainName = domainName?.Trim().Trim('.');
        if (string.IsNullOrEmpty(domainName) || !Uri.TryCreate($"ldap://{domainName}", UriKind.Absolute, out var uri)) return null;

        return new DestinationEntry($"Domain {domainName}", uri, "Active Directory domain this computer has joined");
    }

    private static List<DestinationEntry> Build()
    {
        string? name = JoinedDomain();
        return EntryFor(name) is { } entry ? [entry] : [];
    }

    /// <summary>The DNS name of the joined domain (else its short name), or null if the computer is not on a domain.</summary>
    private static string? JoinedDomain()
    {
        string? shortName = null;
        try
        {
            if (NetGetJoinInformation(null, out var buffer, out int status) == 0)
                try
                {
                    if (status != NetSetupDomainName) return null;

                    shortName = Marshal.PtrToStringUni(buffer);
                }
                finally
                {
                    NetApiBufferFree(buffer);
                }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not ask Windows whether this computer is joined to a domain");
            return null;
        }

        try
        {
            string dns = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            if (!string.IsNullOrWhiteSpace(dns)) return dns;
        }
        catch (Exception)
        {
            /* the short name will do */
        }

        return shortName;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr nameBuffer, out int joinStatus);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    public IAsyncEnumerable<Discovered<DestinationEntry>> DiscoverAsync(CancellationToken cancel)
    {
        return DiscoveryStream.Of(this, Build, cancel);
    }
}