using System.IO;
using System.Runtime.InteropServices;
using Telefrag.DI;
using FileConnection = Silo.Connectors.File.FileConnection;
using IoDirectory = System.IO.Directory;
using IoFile = System.IO.File;

namespace Silo.Space;

/// <summary>
///     The local filesystem as a namespace. A native path is whatever the OS accepts (<c>C:\Users\me\a.txt</c>,
///     <c>\\server\share\a.txt</c>, <c>/home/me/a.txt</c>); relative paths resolve against the process's current
///     directory. Resolves to <see cref="FileSystemInfo" /> objects.
/// </summary>
[Singleton(typeof(INamespace))]
public class FileNamespace : INamespace
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string? DisplayName   => "Filesystem";
    public char    PathSeparator => Path.DirectorySeparatorChar;

    public bool IsPathValid(string path)
    {
        return PathValidator.IsPathValid(path);
    }

    /// <summary>
    ///     True when both paths are valid and, once made absolute and tidied (<c>.</c>/<c>..</c> folded, trailing separators
    ///     and a <c>\\?\</c> prefix dropped), are the same. Case is ignored on Windows and macOS. Links are not followed.
    /// </summary>
    public bool PathsEqual(string p1, string p2)
    {
        var a = Normalize(p1);
        var b = Normalize(p2);
        return a is not null && b is not null && string.Equals(a, b, PathComparison);
    }

    /// <summary>A <see cref="DirectoryInfo" /> or <see cref="FileInfo" /> for an existing path; null if the path is invalid or nothing is there.</summary>
    public object? Resolve(string path)
    {
        var full = Normalize(path);
        if (full is null) return null;

        try
        {
            if (IoDirectory.Exists(full)) return new DirectoryInfo(full);
            if (IoFile.Exists(full)) return new FileInfo(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an unreadable location is treated as not in the namespace
        }

        return null;
    }

    /// <summary>The full path of a <see cref="FileSystemInfo" />, a file connection's target, or a <c>file:</c> <see cref="Uri" />; null for anything else.</summary>
    public string? GetPath(object o)
    {
        return o switch
               {
                   FileSystemInfo f       => f.FullName,
                   FileConnection c       => c.Data?.FullName,
                   Uri { IsFile: true } u => u.LocalPath,
                   _                      => null
               };
    }

    /// <summary>
    ///     Silo has no private scheme for files, so this is the same <c>file:</c> URI as <see cref="PathToPublicUri" />.
    /// </summary>
    public Uri? PathToInternalUri(string path)
    {
        return PathToPublicUri(path);
    }

    /// <summary>The <c>file:</c> URI of the path (UNC paths become <c>file://server/share/...</c>); null if the path is invalid.</summary>
    public Uri? PathToPublicUri(string path)
    {
        var full = Normalize(path);
        return full is not null && Uri.TryCreate(full, UriKind.Absolute, out var uri) && uri.IsFile ? uri : null;
    }

    /// <summary>Validates, makes absolute and tidies a path; null if it is not a usable path.</summary>
    private static string? Normalize(string? path)
    {
        if (path is null || !PathValidator.IsPathValid(path.Trim())) return null;

        try
        {
            var full = Path.GetFullPath(path.Trim());

            if (OperatingSystem.IsWindows())
            {
                if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    full = @"\\" + full[8..];
                else if (full.StartsWith(@"\\?\", StringComparison.Ordinal) && full.Length > 5 && full[5] == ':')
                    full = full[4..];
            }

            var rootLength = Path.GetPathRoot(full)?.Length ?? 0;
            while (full.Length > rootLength && (full[^1] == Path.DirectorySeparatorChar || full[^1] == Path.AltDirectorySeparatorChar))
                full = full[..^1];

            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }
}

public static class PathValidator
{
    private static readonly char[] WindowsInvalidChars = new[]
                                                         {
                                                             '\"', '<', '>', '|', '\0',
                                                             (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7,
                                                             (char)8, (char)9, (char)10, (char)11, (char)12, (char)13, (char)14,
                                                             (char)15, (char)16, (char)17, (char)18, (char)19, (char)20, (char)21,
                                                             (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28,
                                                             (char)29, (char)30, (char)31
                                                         };

    public static bool IsPathValid(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty || path.IsWhiteSpace())
            return false;

        if (path.Length > 32767)
            return false;

        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        return isWindows ? IsWindowsPathValid(path) : IsUnixPathValid(path);
    }

    private static bool IsUnixPathValid(ReadOnlySpan<char> path)
    {
        // POSIX filesystems disallow the null character
        if (path.IndexOf('\0') >= 0)
            return false;

        int start = 0;
        while (start < path.Length)
        {
            int nextSlash     = path.Slice(start).IndexOf('/');
            int segmentLength = nextSlash < 0 ? path.Length - start : nextSlash;

            // Maximum POSIX filename length (NAME_MAX)
            if (segmentLength > 255)
                return false;

            if (nextSlash < 0)
                break;

            start += nextSlash + 1;
        }

        return true;
    }

    private static bool IsWindowsPathValid(ReadOnlySpan<char> path)
    {
        // 1. Check for illegal characters anywhere in the path
        if (path.IndexOfAny(WindowsInvalidChars) >= 0)
            return false;

        ReadOnlySpan<char> remainder = path;

        // 2. Strip Win32 device prefixes: \\?\ or \??\
        if (remainder.StartsWith(@"\\?\", StringComparison.Ordinal) || remainder.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            remainder = remainder.Slice(4);
            if (remainder.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                remainder = remainder.Slice(4);
            }
            else if (remainder.Length >= 2 && IsAsciiLetter(remainder[0]) && remainder[1] == ':')
            {
                // \\?\C:\... -- a drive after the device prefix
                remainder = remainder.Slice(2);
                if (!remainder.IsEmpty && (remainder[0] == '\\' || remainder[0] == '/'))
                    remainder = remainder.Slice(1);
            }
        }
        // UNC paths: \\server\share
        else if (remainder.StartsWith(@"\\", StringComparison.Ordinal) || remainder.StartsWith("//", StringComparison.Ordinal))
        {
            remainder = remainder.Slice(2);
            int nextSep = remainder.IndexOfAny('\\', '/');
            if (nextSep <= 0)
                return false;

            remainder = remainder.Slice(nextSep + 1);
        }
        // Drive roots: C: or C:\
        else if (remainder.Length >= 2 && IsAsciiLetter(remainder[0]) && remainder[1] == ':')
        {
            remainder = remainder.Slice(2);
            if (!remainder.IsEmpty && (remainder[0] == '\\' || remainder[0] == '/'))
                remainder = remainder.Slice(1);
        }

        // Wildcards are patterns, not names (checked here so the "?" in a \\?\ prefix is not mistaken for one)
        if (remainder.IndexOfAny('*', '?') >= 0)
            return false;

        // Colon is invalid after the volume designator
        if (remainder.IndexOf(':') >= 0)
            return false;

        // 3. Validate each path segment
        int start = 0;
        while (start < remainder.Length)
        {
            int nextSep       = remainder.Slice(start).IndexOfAny('\\', '/');
            int segmentLength = nextSep < 0 ? remainder.Length - start : nextSep;

            ReadOnlySpan<char> segment = remainder.Slice(start, segmentLength);

            if (!IsWindowsSegmentValid(segment))
                return false;

            if (nextSep < 0)
                break;

            start += nextSep + 1;
        }

        return true;
    }

    private static bool IsWindowsSegmentValid(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty)
            return true;

        // "." and ".." are directory references, not names ending in a period
        if (segment.SequenceEqual(".") || segment.SequenceEqual(".."))
            return true;

        // Segments cannot end with spaces or trailing periods on NTFS/FAT
        char last = segment[segment.Length - 1];
        if (last == ' ' || last == '.')
            return false;

        // Standard NTFS/FAT component length limit
        if (segment.Length > 255)
            return false;

        // Check for reserved DOS names: NUL, CON, AUX, PRN, COM1-9, LPT1-9
        int                dotIdx   = segment.IndexOf('.');
        ReadOnlySpan<char> baseName = dotIdx >= 0 ? segment.Slice(0, dotIdx) : segment;

        if (baseName.Length == 3 || baseName.Length == 4)
            if (IsReservedDeviceName(baseName))
                return false;

        return true;
    }

    private static bool IsReservedDeviceName(ReadOnlySpan<char> name)
    {
        if (name.Length == 3)
            return name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase);

        if (name.Length == 4)
        {
            bool isCom = name.StartsWith("COM", StringComparison.OrdinalIgnoreCase);
            bool isLpt = name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase);

            if (isCom || isLpt)
            {
                char ch = name[3];
                return ch >= '1' && ch <= '9';
            }
        }

        return false;
    }

    private static bool IsAsciiLetter(char c)
    {
        return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    }
}