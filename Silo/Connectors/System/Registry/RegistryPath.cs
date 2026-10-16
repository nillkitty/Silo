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
    /// <summary>
    ///     The root of the local registry (all the hives). It belongs to no hive, so it has the hive value 0, which is not a
    ///     <see cref="RegistryHive" />: a hive's own root (<c>HKEY_LOCAL_MACHINE</c> alone) is <c>(hive, "*")</c> and must not be mistaken for it.
    /// </summary>
    public static RegistryPath LocalRoot => new(LocalRootHive, "*", null);

    private const RegistryHive LocalRootHive = 0;

    /// <summary>The root of the local registry (all the hives), which is what <see cref="LocalRoot" /> stands for; not a hive's own root.</summary>
    public bool IsLocalRoot => Hive == LocalRootHive && Path == "*";

    /// <summary>
    ///     The key's full name as <see cref="RegistryKey.Name" /> gives it (<c>HKEY_CURRENT_USER\Software\Silo</c>); just the hive's
    ///     name for a hive, and an empty string for the local registry's root, which is not a key.
    /// </summary>
    public string FullName
    {
        get
        {
            if (IsLocalRoot) return string.Empty;
            if (Path == "*" || Path.Nill()) return HiveDisplayName(Hive);

            return HiveDisplayName(Hive) + "\\" + Path.Trim('\\');
        }
    }

    /// <summary>
    ///     Returns the key name (e.g. 'CurrentVersion') if a sub key,
    ///     hive name (e.g. 'HKEY_LOCAL_MACHINE') if a root key,
    ///     or "Local Registry" for <see cref="LocalRoot" /> (reg:///)
    /// </summary>
    public string Name
    {
        get
        {
            if (IsLocalRoot) return "Local Registry";
            if (Path == "*" || Path.Nill()) return HiveDisplayName(Hive);

            var segments = Path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > 0 ? segments[^1] : HiveDisplayName(Hive);
        }
    }

    /// <summary>The key, or null if there is none (it does not exist, or is the local registry's root, which is not a key). A hive opens as itself.</summary>
    public RegistryKey? Open(RegistryKeyPermissionCheck perms = RegistryKeyPermissionCheck.ReadSubTree, RegistryRights rights = default)
    {
        if (IsLocalRoot) return null;

        var b = RegistryKey.OpenBaseKey(Hive, default);
        if (b is null) return null;
        if (Path == "*" || Path.Nill()) return b;

        return b.OpenSubKey(Path, perms, rights);
    }

    public RegistryKey? OpenOrCreate()
    {
        if (IsLocalRoot) return null;

        var b = RegistryKey.OpenBaseKey(Hive, default);
        if (b is null) return null;

        return b.CreateSubKey(Path, RegistryKeyPermissionCheck.ReadWriteSubTree);
    }

    /// <summary>The hive's name as the registry spells it: HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE...</summary>
    internal static string HiveDisplayName(RegistryHive hive)
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