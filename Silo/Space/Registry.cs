using System.IO;
using System.Security;
using Microsoft.Win32;
using Silo.Connectors.System;
using Telefrag.DI;

namespace Silo.Space;

#if WINDOWS

/// <summary>
///     The Windows registry as a namespace. A native path is the form regedit shows:
///     <c>HKEY_CURRENT_USER\Software\Microsoft</c>. Also accepted on the way in: the short hive names (<c>HKCU\...</c>),
///     PowerShell's <c>HKCU:\...</c> and <c>Registry::</c> prefix, and regedit's leading <c>Computer\</c>. A trailing
///     <c>?ValueName</c> points at a value inside the key rather than at the key itself (the same convention as the
///     <c>reg:</c> URIs in <see cref="RegistryUriProvider" />). Registry names are case-insensitive. Only the backslash
///     separates keys: a forward slash is an ordinary character, because real key names contain them.
/// </summary>
[Singleton(typeof(INamespace))]
public class Registry : INamespace
{
    private const int MaxKeyNameLength   = 255;
    private const int MaxValueNameLength = 16383;

    private static readonly Dictionary<string, RegistryHive> Hives = new(StringComparer.OrdinalIgnoreCase)
                                                                     {
                                                                         ["HKEY_CLASSES_ROOT"] =
                                                                             RegistryHive.ClassesRoot,
                                                                         ["HKCR"] = RegistryHive
                                                                            .ClassesRoot,
                                                                         ["HKEY_CURRENT_USER"] =
                                                                             RegistryHive.CurrentUser,
                                                                         ["HKCU"] = RegistryHive
                                                                            .CurrentUser,
                                                                         ["HKEY_LOCAL_MACHINE"] =
                                                                             RegistryHive
                                                                                .LocalMachine,
                                                                         ["HKLM"] = RegistryHive
                                                                            .LocalMachine,
                                                                         ["HKEY_USERS"] =
                                                                             RegistryHive.Users,
                                                                         ["HKU"] = RegistryHive.Users,
                                                                         ["HKEY_CURRENT_CONFIG"] =
                                                                             RegistryHive
                                                                                .CurrentConfig,
                                                                         ["HKCC"] = RegistryHive
                                                                            .CurrentConfig,
                                                                         ["HKEY_PERFORMANCE_DATA"] =
                                                                             RegistryHive
                                                                                .PerformanceData,
                                                                         ["HKPD"] = RegistryHive
                                                                            .PerformanceData
                                                                     };

    public string? DisplayName   => "Registry";
    public char    PathSeparator => '\\';

    private static bool IsRoot(RegistryPath p)
    {
        return p.Path == "*";
    }

    private static bool HasValue(RegistryKey key, string name)
    {
        return key.GetValue(name) is not null;
    }

    /// <summary>
    ///     Parses the forms described on the class. The resulting <see cref="RegistryPath.Path" /> is "*" for a hive
    ///     root.
    /// </summary>
    private static bool TryParse(string? path, out RegistryPath result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var text = path.Trim().Trim('"');

        string? valueName = null;
        var     q         = text.IndexOf('?');
        if (q >= 0)
        {
            valueName = text[(q + 1)..];
            text      = text[..q];
            if (valueName.Length == 0 || valueName.Length > MaxValueNameLength) return false;
        }

        if (text.StartsWith("Registry::", StringComparison.OrdinalIgnoreCase)) text = text["Registry::".Length..];

        var segments = text.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var skip     = segments.Length > 1 && segments[0].Equals("Computer", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (segments.Length - skip == 0) return false;

        // PowerShell drive form: HKCU:\Software -> hive token "HKCU:"
        if (!Hives.TryGetValue(segments[skip].TrimEnd(':'), out var hive)) return false;

        var keys = segments[(skip + 1)..];
        foreach (var k in keys)
            if (k.Length > MaxKeyNameLength || k.Contains('\0'))
                return false;

        result = new RegistryPath(hive, keys.Length == 0 ? "*" : string.Join('\\', keys), valueName);
        return true;
    }

    private static string Format(RegistryPath p)
    {
        var s                          = HiveName(p.Hive);
        if (!IsRoot(p)) s              += "\\" + p.Path;
        if (p.ValueName is not null) s += "?"  + p.ValueName;
        return s;
    }

    private static string HiveName(RegistryHive hive)
    {
        return hive switch
               {
                   RegistryHive.ClassesRoot     => "HKEY_CLASSES_ROOT",
                   RegistryHive.CurrentUser     => "HKEY_CURRENT_USER",
                   RegistryHive.LocalMachine    => "HKEY_LOCAL_MACHINE",
                   RegistryHive.Users           => "HKEY_USERS",
                   RegistryHive.CurrentConfig   => "HKEY_CURRENT_CONFIG",
                   RegistryHive.PerformanceData => "HKEY_PERFORMANCE_DATA",
                   _                            => hive.ToString()
               };
    }

    public bool IsPathValid(string path)
    {
        return TryParse(path, out _);
    }

    /// <summary>
    ///     Two registry paths are equal when they name the same hive, key and (if any) value, ignoring case, short vs long
    ///     hive names, and a leading <c>Computer\</c>. Paths that do not parse are never equal to anything.
    /// </summary>
    public bool PathsEqual(string p1, string p2)
    {
        if (!TryParse(p1, out var a) || !TryParse(p2, out var b)) return false;

        return a.Hive == b.Hive && string.Equals(a.Path, b.Path,      StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.ValueName,                b.ValueName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     For a key path, returns the opened <see cref="RegistryKey" /> (read-only; the caller disposes it). For a path that
    ///     names a value, returns the <see cref="RegistryPath" /> of that value when it exists. Returns null when the path
    ///     is invalid, does not exist, or cannot be read with the current permissions.
    /// </summary>
    public object? Resolve(string path)
    {
        if (!TryParse(path, out var rp)) return null;

        try
        {
            var root = RegistryKey.OpenBaseKey(rp.Hive, RegistryView.Default);
            if (IsRoot(rp))
            {
                if (rp.ValueName is null) return root;
                using (root)
                {
                    return HasValue(root, rp.ValueName) ? rp : null;
                }
            }

            using (root)
            {
                var key = root.OpenSubKey(rp.Path, false);
                if (key is null || rp.ValueName is null) return key;

                using (key)
                {
                    return HasValue(key, rp.ValueName) ? rp : null;
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The native path of an open <see cref="RegistryKey" /> or of a <see cref="RegistryPath" />; null for anything
    ///     else.
    /// </summary>
    public string? GetPath(object o)
    {
        try
        {
            return o switch
                   {
                       RegistryKey k  => k.Name,
                       RegistryPath p => Format(p),
                       _              => null
                   };
        }
        catch (ObjectDisposedException)
        {
            return null; // a closed key no longer knows its name
        }
    }

    /// <summary>
    ///     <c>reg:///hkey_current_user/Software/Microsoft?ValueName</c> -- the form <see cref="RegistryUriProvider" />
    ///     parses back into the same <see cref="RegistryPath" />.
    /// </summary>
    public Uri? PathToInternalUri(string path)
    {
        if (!TryParse(path, out var rp)) return null;

        var uri                           = "reg:///" + HiveName(rp.Hive).ToLowerInvariant();
        if (!IsRoot(rp)) uri              += "/" + string.Join('/', rp.Path.Split('\\').Select(s => Uri.EscapeDataString(s)));
        if (rp.ValueName is not null) uri += "?" + Uri.EscapeDataString(rp.ValueName);

        return Uri.TryCreate(uri, UriKind.Absolute, out var result) ? result : null;
    }

    /// <summary>The registry has no public URI scheme: nothing outside Silo understands <c>reg:</c>, so this is always null.</summary>
    public Uri? PathToPublicUri(string path)
    {
        return null;
    }
}

#endif