using Microsoft.Win32;
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
        var       handlers    = new List<UrlHandler>();
        using var classesRoot = Registry.ClassesRoot;

        foreach (var scheme in classesRoot.GetSubKeyNames())
        {
            using var key = classesRoot.OpenSubKey(scheme);
            if (key?.GetValue("URL Protocol") == null) continue;

            var command    = GetCommand(key);
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

    private static bool IsWebProtocol(string scheme, string command)
    {
        if (WebSchemes.Contains(scheme)) return true;

        return BrowserExecutables.Any(browser => command.Contains(browser, StringComparison.OrdinalIgnoreCase));
    }
}

[Singleton(typeof(IUrlInfo))]
public class SystemUrlHandler : IUrlInfo
{
    public IReadOnlyList<UrlHandler> Handlers => field ??= UrlHandlerResolver.GetAllHandlers();

    public UrlInfo? GetInfo(string scheme)
    {
        var h = Handlers.FirstOrDefault(x => x.Scheme.Is(scheme));
        if (h is null) return null;
        if (h.Command is null) return null;
        var ours = h.Command.ToLower().Contains("silo.exe") is true;
        return new UrlInfo(h.Scheme, h.Command, h.IsWebBased, ours);
    }
}

public record UrlInfo(string Scheme, string HandlerCommand, bool IsWebUrl, bool IsOurs);

public interface IUrlInfo
{
    public bool IsRegistered(string scheme)
    {
        return GetInfo(scheme) != null;
    }

    UrlInfo? GetInfo(string scheme);
}