using System.Security.Principal;
using Microsoft.Win32;
using Silo.Connectors.System;

namespace Silo.Providers.Shell;

public interface IShellPaths
{
    RegistryPath            AllowedExtensions      { get; }
    RegistryPath            ClassesRoot            { get; }
    RegistryPath            NamespaceRoot          { get; }
    RegistryPath            RegisteredApplications { get; }
    ShellConfigurationScope Scope                  { get; }
    RegistryPath            SettingsRoot           { get; }
    RegistryPath            ShellRoot              { get; }
}

/// <summary>
///     The registry locations the shell is configured through, resolved for a
///     given <see cref="ShellConfigurationScope" /> (i.e. which hive they live in).
/// </summary>
public class ShellPaths : IShellPaths
{
    /// <summary>
    ///     <see cref="ShellConfigurationScope.NewProfiles" /> targets the DEFAULT user profile
    ///     template (<c>C:\Users\Default\NTUSER.DAT</c>), which isn't a live hive: whatever writes to
    ///     it has to load that file under <c>HKEY_USERS\SiloDefaultProfile</c> first. This is the key
    ///     name those paths are rooted at.
    /// </summary>
    public const string NewProfileHiveKey = "SiloDefaultProfile";

    /// <summary>Well-known SID of NT SERVICE\TrustedInstaller.</summary>
    private static readonly SecurityIdentifier _trustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public RegistryPath            AllowedExtensions      { get; }
    public RegistryPath            ClassesRoot            { get; }
    public RegistryPath            NamespaceRoot          { get; }
    public RegistryPath            RegisteredApplications { get; }
    public ShellConfigurationScope Scope                  { get; }
    public RegistryPath            SettingsRoot           { get; }
    public RegistryPath            ShellRoot              { get; }

    /// <summary>
    ///     Builds the <see cref="RegistryPath" /> for <paramref name="path" /> (a key path below the
    ///     hive root) in whichever hive this instance's <see cref="Scope" /> writes to.
    /// </summary>
    private RegistryPath _key(string path)
    {
        path = path.Required();

        // Tattooing writes the same hive as the plain User / System scopes -- the "as though it
        // were defined in group policy" part is for whatever does the writing to apply, it
        // doesn't change where the keys live.
        (RegistryHive hive, string fullPath) = Scope switch
                                               {
                                                   ShellConfigurationScope.Current => (_currentHive(), path),
                                                   ShellConfigurationScope.User    => (RegistryHive.CurrentUser, path),
                                                   ShellConfigurationScope.NewProfiles => (RegistryHive.Users,
                                                                                           $@"{NewProfileHiveKey}\{path}"),
                                                   ShellConfigurationScope.System => (RegistryHive.LocalMachine, path),
                                                   ShellConfigurationScope.HighestAvailable => (_highestHive(), path),
                                                   ShellConfigurationScope.TattooUser => (RegistryHive.CurrentUser, path),
                                                   ShellConfigurationScope.TattooSystem => (RegistryHive.LocalMachine, path),
                                                   _ => throw new ArgumentOutOfRangeException(nameof(Scope), Scope, null)
                                               };

        return new RegistryPath(hive, fullPath, null);
    }

    /// <summary>
    ///     <see cref="ShellConfigurationScope.Current" />: the user hive for a normal user, the
    ///     machine hive when running as TrustedInstaller, SYSTEM, a batch job or a service.
    ///     (The "descendant of msiexec.exe / setup.exe" case from the scope's documentation isn't
    ///     detected -- that needs a walk up the parent-process chain.)
    /// </summary>
    private static RegistryHive _currentHive()
    {
        using var identity  = WindowsIdentity.GetCurrent();
        var       principal = new WindowsPrincipal(identity);

        bool systemContext = identity.IsSystem || principal.IsInRole(_trustedInstaller) ||
                             principal.IsInRole(new SecurityIdentifier(WellKnownSidType.ServiceSid, null)) ||
                             principal.IsInRole(new SecurityIdentifier(WellKnownSidType.BatchSid,   null));

        return systemContext ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
    }

    /// <summary>
    ///     <see cref="ShellConfigurationScope.HighestAvailable" />: the machine hive if this process
    ///     is elevated enough to write it, otherwise the user hive.
    /// </summary>
    private static RegistryHive _highestHive()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
                   ? RegistryHive.LocalMachine
                   : RegistryHive.CurrentUser;
    }

    public ShellPaths(ShellConfigurationScope scope)
    {
        Scope                  = scope;
        ShellRoot              = _key(@"Software\Microsoft\Windows\CurrentVersion\Shell");
        ClassesRoot            = _key(@"Software\Classes");
        RegisteredApplications = _key(@"Software\RegisteredApplications");
        SettingsRoot           = _key(@"Software\Microsoft\Windows\CurrentVersion\Explorer");
        NamespaceRoot          = _key(@"Software\Microsoft\Windows\CurrentVersion\Explorer");
        AllowedExtensions      = _key(@"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved");
    }
}

public static class ShellTypeConstants
{
    public const string Everything           = "*";
    public const string AllFilesystemObjects = nameof(AllFilesystemObjects);
    public const string Directory            = nameof(Directory);
    public const string Background           = Directory + @"\Background";
    public const string Drive                = nameof(Drive);
}

public static class ShellMiscConstants
{
    public const string Everything                  = "*";
    public const string ShellIconOverlayIdentifiers = nameof(ShellIconOverlayIdentifiers);
    public const string Advanced                    = nameof(Advanced);
}

public static class ShellNamespaceConstants
{
    public const string Everything          = "*";
    public const string MyComputer          = nameof(MyComputer);
    public const string Desktop             = nameof(Desktop);
    public const string NetworkNeighborhood = nameof(NetworkNeighborhood);
    public const string FolderDescriptions  = nameof(FolderDescriptions);
    public const string UserShellFolders    = "User Shell Folders";
}