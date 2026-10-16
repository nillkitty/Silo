using System.IO;
using System.Runtime.InteropServices;
using ComFileTime = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Silo.Connectors.File;

/// <summary>What a file system path is.</summary>
public enum FileKind
{
    /// <summary>Nothing is there (or it cannot be read at all).</summary>
    Missing,

    /// <summary>An ordinary file.</summary>
    File,

    /// <summary>An ordinary folder.</summary>
    Directory,

    /// <summary>
    ///     The root of a volume: a drive (<c>C:\</c>), a network share (<c>\\server\share</c>), or a volume mounted in a folder.
    ///     <see cref="FileKindInfo.DriveType" /> says what sort.
    /// </summary>
    Volume,

    /// <summary>A directory junction (Windows): a folder that is really another folder on the same computer.</summary>
    Junction,

    /// <summary>A symbolic link to a file.</summary>
    FileSymlink,

    /// <summary>A symbolic link to a folder.</summary>
    DirectorySymlink
}

/// <summary>What <see cref="FileKinds.Classify" /> found out about a path.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="DriveType">For a <see cref="FileKind.Volume" />: fixed disk, removable, network, optical...; otherwise null.</param>
/// <param name="LinkTarget">For a link (or junction): where it points, as stored; otherwise null.</param>
/// <param name="IsCloudPlaceholder">A file or folder whose content is in the cloud and is fetched when opened (OneDrive and the like).</param>
public sealed record FileKindInfo(FileKind Kind, DriveType? DriveType = null, string? LinkTarget = null, bool IsCloudPlaceholder = false)
{
    /// <summary>True when the path can have things in it: a folder, a volume, a junction or a link to a folder.</summary>
    public bool IsContainer => Kind is FileKind.Directory or FileKind.Volume or FileKind.Junction or FileKind.DirectorySymlink;

    /// <summary>True for a junction or a symbolic link.</summary>
    public bool IsLink => Kind is FileKind.Junction or FileKind.FileSymlink or FileKind.DirectorySymlink;
}

/// <summary>Works out what a path is, and how to show it.</summary>
public static class FileKinds
{
    private const uint ReparseTagSymlink    = 0xA000000C;
    private const uint ReparseTagMountPoint = 0xA0000003; // a junction, or a volume mounted in a folder
    private const uint ReparseTagCloud      = 0x9000001A; // OneDrive and other cloud-file providers (the low nibble varies)
    private const uint ReparseTagCloudMask  = 0xFFFF0FFF;

    private const FileAttributes RecallOnOpen       = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    /// <summary>
    ///     Looks at the path itself (not at what a link points to). Reads the file system, so it can be slow for a network
    ///     path: call it off the UI thread for one that might be. Never throws; a path that cannot be read is
    ///     <see cref="FileKind.Missing" />.
    /// </summary>
    public static FileKindInfo Classify(FileSystemInfo fsi)
    {
        ArgumentNullException.ThrowIfNull(fsi);

        try
        {
            fsi.Refresh();
            var attributes = fsi.Attributes;
            if ((int)attributes == -1) return new FileKindInfo(FileKind.Missing);

            bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
            bool cloud       = (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                string? target = TryGetLinkTarget(fsi);
                uint?   tag    = ReparseTag(fsi.FullName);

                if (tag is ReparseTagMountPoint)
                    return IsVolumeTarget(target)
                               ? new FileKindInfo(FileKind.Volume, DriveType.Fixed, target)
                               : new FileKindInfo(FileKind.Junction, null, target);

                if (tag is ReparseTagSymlink || (tag is null && target is not null))
                    return new FileKindInfo(isDirectory ? FileKind.DirectorySymlink : FileKind.FileSymlink, null, target);

                if (tag is { } other && (other & ReparseTagCloudMask) == (ReparseTagCloud & ReparseTagCloudMask))
                    cloud = true;
            }

            if (!isDirectory) return new FileKindInfo(FileKind.File, IsCloudPlaceholder: cloud);

            if (IsRoot(fsi.FullName)) return new FileKindInfo(FileKind.Volume, DriveTypeOf(fsi.FullName));

            return new FileKindInfo(FileKind.Directory, IsCloudPlaceholder: cloud);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FileKindInfo(FileKind.Missing);
        }
    }

    /// <summary>True when <paramref name="path" /> is the root of a volume (<c>C:\</c>, <c>\\server\share</c>, <c>/</c>).</summary>
    public static bool IsRoot(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        string? root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && Trimmed(root).Equals(Trimmed(path), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     What sort of volume the root of <paramref name="path" /> is. Asks the system for the drive type only, which does not
    ///     touch the disk, so it is safe for a disconnected network drive.
    /// </summary>
    public static DriveType DriveTypeOf(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return DriveType.Unknown;
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) return DriveType.Network;

            return new DriveInfo(root).DriveType;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return DriveType.Unknown;
        }
    }

    /// <summary>The resource key (in <c>Resources/NavIcons.xaml</c>) of the icon for what was found.</summary>
    public static string IconKey(FileKindInfo info)
    {
        return info.Kind switch
               {
                   FileKind.Missing          => "FsFileMissing",
                   FileKind.File             => "Document",
                   FileKind.Directory        => "Folder17",
                   FileKind.Junction         => "FsFolderJunction",
                   FileKind.DirectorySymlink => "FsFolderLink",
                   FileKind.FileSymlink      => "FsFileLink",
                   FileKind.Volume => info.DriveType switch
                                      {
                                          DriveType.Removable => "FsRemovable",
                                          DriveType.CDRom     => "FsOptical",
                                          DriveType.Network   => "FsNetworkDrive",
                                          _                   => "FsDrive"
                                      },
                   _ => "Document"
               };
    }

    /// <summary>The kind in words, for the Properties pane.</summary>
    public static string Describe(FileKindInfo info)
    {
        return info.Kind switch
               {
                   FileKind.Missing          => "Missing",
                   FileKind.File             => "File",
                   FileKind.Directory        => "Folder",
                   FileKind.Junction         => "Junction",
                   FileKind.DirectorySymlink => "Symbolic link to a folder",
                   FileKind.FileSymlink      => "Symbolic link to a file",
                   FileKind.Volume => info.DriveType switch
                                      {
                                          DriveType.Removable => "Removable drive",
                                          DriveType.CDRom     => "Optical drive",
                                          DriveType.Network   => "Network drive or share",
                                          DriveType.Ram       => "RAM disk",
                                          _                   => "Drive"
                                      },
                   _ => "File system item"
               };
    }

    private static string Trimmed(string path) => path.TrimEnd('\\', '/');

    private static bool IsVolumeTarget(string? target)
    {
        return target is not null && target.Contains("Volume{", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetLinkTarget(FileSystemInfo fsi)
    {
        try
        {
            return fsi.LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The reparse tag of a path that is a reparse point (Windows only); null when it cannot be read.</summary>
    private static uint? ReparseTag(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            IntPtr handle = FindFirstFileW(Trimmed(path), out var data);
            if (handle == InvalidHandle) return null;

            FindClose(handle);
            return (data.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ? data.Reserved0 : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
    }

    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint        Attributes;
        public ComFileTime Created;
        public ComFileTime Accessed;
        public ComFileTime Written;
        public uint        SizeHigh;
        public uint        SizeLow;
        public uint        Reserved0; // for a reparse point, its tag
        public uint        Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string fileName, out FindData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
}
