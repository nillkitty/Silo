using System.Security.AccessControl;
using Microsoft.Win32;

namespace Silo.Connectors.System;

/// <summary>
///     Holds a reference to a native registry hive and native path (e.g.
///     'Software\Microsoft\Windows\CurrentVersion'.
/// </summary>
/// <param name="Hive">The registry hive the path is a child of</param>
/// <param name="Path">The \-separated path of registry keys</param>
/// <param name="ValueName">Optional name of the value being read/written</param>
public record RegistryPath(RegistryHive Hive, string Path, string? ValueName)
{
    public static RegistryPath LocalRoot => new(RegistryHive.LocalMachine, "*", null);

    /// <summary>
    ///     Returns the key name (e.g. 'CurrentVersion') if a sub key,
    ///     hive name (e.g. 'HKEY_LOCAL_MACHINE') if a root key,
    ///     or "Local Registry" if hklm:///*
    /// </summary>
    public string Name
    {
        get
        {
            if (Path == "*" || Path.Nill())
                return Hive == RegistryHive.LocalMachine ? "Local Registry" : HiveDisplayName(Hive);

            var segments = Path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > 0 ? segments[^1] : HiveDisplayName(Hive);
        }
    }

    public RegistryKey? Open(RegistryKeyPermissionCheck perms = RegistryKeyPermissionCheck.ReadSubTree, RegistryRights rights = default)
    {
        var b = RegistryKey.OpenBaseKey(Hive, default);
        if (b is null) return null;

        return b.OpenSubKey(Path, perms, rights);
    }

    public RegistryKey? OpenOrCreate()
    {
        var b = RegistryKey.OpenBaseKey(Hive, default);
        if (b is null) return null;

        return b.CreateSubKey(Path, RegistryKeyPermissionCheck.ReadWriteSubTree);
    }

    private static string HiveDisplayName(RegistryHive hive)
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
}