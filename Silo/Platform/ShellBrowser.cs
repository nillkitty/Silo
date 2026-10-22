using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Serilog;
using Silo.Connectors;
using Silo.Connectors.System;
using Telefrag.DI;

namespace Silo.Platform;

/// <summary>
///     A program installed on this computer that opens web addresses, as the shell describes it: where it is, what it is called
///     (from the file's own version information, unless it was told), who made it and what schemes it is registered for.
///     Opening an address runs the program with it, the way the command registered for it says (a command with no place for the
///     address gets it added in quotes at the end).
/// </summary>
public sealed class ShellBrowser : IBrowser
{
    private readonly string _arguments;
    private readonly Uri?   _managementUri;

    public ShellBrowser(string exePath, string? name = null, string? publisher = null, Version? version = null, IEnumerable<string>? defaultFor = null,
                        Uri? managementUri = null, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("A browser needs the path of its program.", nameof(exePath));

        var (fileName, fileCompany, fileVersion) = ReadFile(exePath);

        ExePath        = exePath;
        Name           = !string.IsNullOrWhiteSpace(name) ? name : fileName ?? Path.GetFileNameWithoutExtension(exePath);
        Publisher      = !string.IsNullOrWhiteSpace(publisher) ? publisher : fileCompany ?? string.Empty;
        Version        = version ?? fileVersion;
        DefaultFor     = (defaultFor ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToLowerInvariant()).Distinct().Order().ToArray();
        _managementUri = managementUri;
        _arguments     = arguments ?? string.Empty;
    }

    public string[] DefaultFor { get; }
    public string   ExePath    { get; }
    public string   Name       { get; }
    public string   Publisher  { get; }
    public Version? Version    { get; }

    public Uri? GetManagementUri()
    {
        return _managementUri;
    }

    public void Launch(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var start = new ProcessStartInfo(ExePath) { UseShellExecute = false, Arguments = ShellCommand.Substitute(_arguments, destination) };

        // null when the program handed the address to a copy of itself that was already running: nothing to dispose
        using var process = Process.Start(start);
    }

    /// <summary>
    ///     The program a registered command runs (<c>"C:\...\browser.exe" --single-argument %1</c>), keeping the arguments so an
    ///     address is passed the way the registration says; null when the command names no program.
    /// </summary>
    public static ShellBrowser? FromCommand(string? command, IEnumerable<string>? defaultFor = null)
    {
        return ShellCommand.Split(command) is { } split ? new ShellBrowser(split.Program, defaultFor: defaultFor, arguments: split.Arguments) : null;
    }

    public override string ToString()
    {
        return $"{Name} ({ExePath})";
    }

    private static (string? Name, string? Company, Version? Version) ReadFile(string exePath)
    {
        try
        {
            if (!new FileInfo(exePath).Exists) return (null, null, null);

            var info    = FileVersionInfo.GetVersionInfo(exePath);
            var version = (info.FileMajorPart | info.FileMinorPart | info.FileBuildPart | info.FilePrivatePart) != 0
                              ? new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart)
                              : null;
            return (Clean(info.FileDescription) ?? Clean(info.ProductName), Clean(info.CompanyName), version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Debug(ex, "Could not read the version information of {path}", exePath);
            return (null, null, null);
        }
    }

    private static string? Clean(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}

/// <summary>
///     The browsers Windows lists as installed: the clients registered under <c>Start Menu Internet</c> (Edge, Chrome, Firefox, Brave...),
///     for the whole computer in both its registry views and for the current user.
/// </summary>
[Singleton(typeof(IBrowserProvider))]
[Singleton(typeof(IDiscovery<IBrowser>))]
public sealed class RegisteredBrowserProvider : IBrowserProvider
{
    /// <summary>Where the browsers are registered, under each hive.</summary>
    public const string ClientsKey = @"SOFTWARE\Clients\StartMenuInternet";

    private static readonly (RegistryHive Hive, RegistryView View, string Name)[] Roots =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, "HKEY_LOCAL_MACHINE"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "HKEY_LOCAL_MACHINE"),
        (RegistryHive.CurrentUser, RegistryView.Default, "HKEY_CURRENT_USER")
    ];

    public string DisplayName => "Installed browsers";

    public IEnumerable<IBrowser> GetBrowsers()
    {
        var found = new List<IBrowser>();
        foreach (var (hive, view, hiveName) in Roots)
        {
            try
            {
                using var root    = RegistryKey.OpenBaseKey(hive, view);
                using var clients = root.OpenSubKey(ClientsKey);
                if (clients is null) continue;

                foreach (string clientName in clients.GetSubKeyNames())
                {
                    if (ReadClient(clients, hiveName, clientName) is { } browser)
                        found.Add(browser);
                }
            }
            catch (Exception ex) when (IsRegistryProblem(ex))
            {
                Log.Debug(ex, "Could not list the browsers under {hive}", hiveName);
            }
        }

        return found;
    }

    /// <summary>
    ///     A browser from what its registration says: the program from its <c>open</c> command, the name it is listed under, and the
    ///     schemes it handles. No file is looked at, and nothing is registered, so a test can make one; null when the command names no
    ///     program.
    /// </summary>
    /// <param name="hiveName">The hive the client was found in (<c>HKEY_LOCAL_MACHINE</c>), for the address of its key.</param>
    /// <param name="clientName">The name of the client's key.</param>
    /// <param name="displayName">The name the client is listed under (its key's default value).</param>
    /// <param name="command">The client's <c>shell\open\command</c>.</param>
    /// <param name="schemes">The schemes of its <c>Capabilities\URLAssociations</c>.</param>
    public static ShellBrowser? FromClient(string hiveName, string clientName, string? displayName, string? command, IEnumerable<string>? schemes)
    {
        if (ShellCommand.Split(command) is not { } split) return null;

        var management = RegistryUris.FromKeyName($@"{hiveName}\{ClientsKey}\{clientName}");
        return new ShellBrowser(split.Program, displayName, null, null, schemes, management, split.Arguments);
    }

    private static ShellBrowser? ReadClient(RegistryKey clients, string hiveName, string clientName)
    {
        try
        {
            using var client = clients.OpenSubKey(clientName);
            if (client is null) return null;

            using var openCommand = client.OpenSubKey(@"shell\open\command");
            using var urls        = client.OpenSubKey(@"Capabilities\URLAssociations");

            string? command = openCommand?.GetValue(null) as string;
            string? program = ShellCommand.ProgramOf(command);
            if (program is null || !new FileInfo(program).Exists) return null;

            return FromClient(hiveName, clientName, client.GetValue(null) as string, command, urls?.GetValueNames());
        }
        catch (Exception ex) when (IsRegistryProblem(ex))
        {
            Log.Debug(ex, "Could not read the browser {client}", clientName);
            return null;
        }
    }

    private static bool IsRegistryProblem(Exception ex)
    {
        return ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException or System.Security.SecurityException;
    }
}
