using System.IO;
using Microsoft.Win32;
using Serilog;
using Telefrag.DI;

namespace Silo.Platform;

public record UrlHandler(string Scheme, string Command, bool IsWebBased);

/// <summary>
///     Gets URL Protocol handlers from Shell
/// </summary>
public static class UrlHandlerResolver
{
    private static readonly HashSet<string> WebSchemes = new(StringComparer.OrdinalIgnoreCase)
                                                         {
                                                             "http", "https", "ftp"
                                                         };

    private static readonly string[] BrowserExecutables =
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe",
        "iexplore.exe"
    };

    public static IReadOnlyList<UrlHandler> GetAllHandlers()
    {
        var handlers    = new List<UrlHandler>();
        var classesRoot = Registry.ClassesRoot;

        foreach (var scheme in classesRoot.GetSubKeyNames())
        {
            using var key = classesRoot.OpenSubKey(scheme);
            if (key?.GetValue("URL Protocol") == null) continue;

            // what the person chose for the scheme (the default browser, the mail program) is what opens it; the handler
            // the scheme's own key names is only what was there before they chose
            var command    = GetUserChoiceCommand(classesRoot, scheme) ?? GetCommand(key);
            var isWebBased = IsWebProtocol(scheme, command);

            handlers.Add(new UrlHandler(scheme, command, isWebBased));
        }

        return handlers;
    }

    private static string GetCommand(RegistryKey key)
    {
        using var cmdKey = key.OpenSubKey(@"shell\open\command");
        return cmdKey?.GetValue(null)?.ToString() ?? string.Empty;
    }

    /// <summary>
    ///     The command of the program the person picked for <paramref name="scheme" /> in Windows' default apps (kept under the
    ///     user's <c>UrlAssociations</c> as a ProgId, whose own key has the command); null when they never picked one.
    /// </summary>
    private static string? GetUserChoiceCommand(RegistryKey classesRoot, string scheme)
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{scheme}\UserChoice");
            if (choice?.GetValue("ProgId") is not string { Length: > 0 } progId) return null;

            using var cmdKey = classesRoot.OpenSubKey($@"{progId}\shell\open\command");
            return cmdKey?.GetValue(null)?.ToString() is { Length: > 0 } command ? command : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            Log.Debug(ex, "Could not read the chosen handler of {scheme}", scheme);
            return null;
        }
    }

    private static bool IsWebProtocol(string scheme, string command)
    {
        if (WebSchemes.Contains(scheme)) return true;

        return BrowserExecutables.Any(browser => command.Contains(browser, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
///     What the Windows shell says about URL protocols: who opens a scheme, and -- the other way round -- which schemes a
///     program is registered for. What is read is kept until <see cref="Refresh" />.
/// </summary>
[Singleton(typeof(IUrlInfo))]
public class SystemUrlHandler : IUrlInfo
{
    private readonly object                    _gate = new();
    private          IReadOnlyList<UrlHandler>? _handlers;

    public IReadOnlyList<UrlHandler> Handlers
    {
        get
        {
            lock (_gate)
                return _handlers ??= LoadHandlers();
        }
    }

    /// <summary>Reads the handlers the shell has (a test hands it its own).</summary>
    protected virtual IReadOnlyList<UrlHandler> LoadHandlers()
    {
        return UrlHandlerResolver.GetAllHandlers();
    }

    /// <summary>Forgets what was read, so the next question reads the shell again (a program was installed, a default changed).</summary>
    public void Refresh()
    {
        lock (_gate)
            _handlers = null;
    }

    public UrlInfo? GetInfo(Uri url)
    {
        string scheme = url.Required().Scheme;
        var    h      = Handlers.FirstOrDefault(x => x.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase));
        if (h is null || string.IsNullOrWhiteSpace(h.Command)) return null;

        return Describe(h, url);
    }

    public UrlInfo? GetInfo(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;

        // a web scheme first: a person asking about a browser wants to know about the web
        var best = HandlersFor(exePath).OrderBy(Rank).ThenBy(h => h.Scheme, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return best is null ? null : Describe(best, null);
    }

    public string[]? GetProtocolsFor(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;

        return HandlersFor(exePath).Select(h => h.Scheme).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    ///     The handlers registered to the program at <paramref name="exePath" />; when none is, those registered to a program
    ///     of the same file name (another copy of it: a debug build is not where the installed one is).
    /// </summary>
    private List<UrlHandler> HandlersFor(string exePath)
    {
        var exact = Handlers.Where(h => ShellCommand.SameProgram(ShellCommand.ProgramOf(h.Command), exePath)).ToList();
        return exact.Count > 0 ? exact : Handlers.Where(h => ShellCommand.SameProgramName(ShellCommand.ProgramOf(h.Command), exePath)).ToList();
    }

    private static int Rank(UrlHandler h)
    {
        if (h.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return 0;
        if (h.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) return 1;
        return h.IsWebBased ? 2 : 3;
    }

    private static UrlInfo Describe(UrlHandler h, Uri? url)
    {
        string? program = ShellCommand.ProgramOf(h.Command);
        bool    ours    = h.Command.Contains("silo.exe", StringComparison.OrdinalIgnoreCase) || ShellCommand.SameProgram(program, Environment.ProcessPath);
        return new UrlInfo(h.Scheme, url, h.Command, h.IsWebBased, ours);
    }
}

/// <param name="Scheme">The URL protocol.</param>
/// <param name="CanonicalUri">The address that was asked about; null when the question was about a program and not an address.</param>
/// <param name="HandlerCommand">The command the shell runs to open it (<c>"C:\...\browser.exe" %1</c>).</param>
/// <param name="IsWebUrl">Whether a browser opens it.</param>
/// <param name="IsOurs">Whether Silo itself is what opens it.</param>
public record UrlInfo(string Scheme, Uri? CanonicalUri, string HandlerCommand, bool IsWebUrl, bool IsOurs);

public interface IUrlInfo
{
    /// <summary>
    ///     Gets whether a URL scheme is registered with the shell
    /// </summary>
    public bool IsRegistered(Uri uri)
    {
        return GetInfo(uri) != null;
    }

    /// <summary>
    ///     Get the shell runtime information for who is handling a given URL Protocol
    ///     (based on the current user's shell)
    /// </summary>
    UrlInfo? GetInfo(Uri uri);

    /// <summary>
    ///     Gets the shell runtime information for the URL protocol registered to a given executable path (a web scheme in
    ///     preference to another); null when it handles none. The address in it is null: it answers about a program, not an
    ///     address.
    /// </summary>
    UrlInfo? GetInfo(string exePath);

    /// <summary>
    ///     Reverse lookup; gets the URL protocols (schemes) that are registered to a specific
    ///     executable path or one analogous to it (the same file name, in another folder). Null when no path was given.
    /// </summary>
    string[]? GetProtocolsFor(string exePath);
}
