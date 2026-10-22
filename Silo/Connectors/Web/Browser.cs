using Serilog;

namespace Silo.Connectors.Web;

/// <summary>
///     The way to a browser for code that has no reason to hold an <see cref="IBrowserManager" />: Silo's own, the person's, the one for an address, and
///     opening an address with one of them.
/// </summary>
public static class Browser
{
    /// <summary>Silo's internal browser: it opens what it can inside Silo and hands the rest to the person's browser.</summary>
    public static IBrowser SiloAuto => InternalBrowsers.Auto;

    /// <summary>The browser (or program) the person chose in Windows for web addresses.</summary>
    public static IBrowser UserDefault => Manager.GetUserDefault(BrowserManager.WebProbe);

    /// <summary>The service that knows the browsers.</summary>
    public static IBrowserManager Manager => App.Require<IBrowserManager>();

    /// <summary>Every browser there is to choose from.</summary>
    public static IEnumerable<IBrowser> GetBrowsers()
    {
        return Manager.GetBrowsers();
    }

    /// <summary>The browser the shell has registered for an address's scheme; null when none is (or it is not a program there is).</summary>
    public static IBrowser? Find(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var info = App.Resolve<Platform.IUrlInfo>()?.GetInfo(url);
        return info is null ? null : Manager.Find(info.HandlerCommand, BrowserFallback.Null, url);
    }

    /// <summary>The browser a registered command runs (<c>"C:\...\browser.exe" %1</c>), or what <paramref name="fallback" /> says.</summary>
    public static IBrowser? Find(string handlerCommand, BrowserFallback fallback = BrowserFallback.Default)
    {
        return Manager.Find(handlerCommand, fallback);
    }

    /// <summary>Opens an address with a browser, off the caller's thread. A browser that cannot open it is logged and the failure is passed on.</summary>
    public static Task LaunchAsync(Uri url, IBrowser? browser = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var use = browser ?? SiloAuto;
        return Task.Run(() =>
                        {
                            try
                            {
                                use.Launch(url);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "{browser} could not open {url}", use.Name, url);
                                throw;
                            }
                        }, cancel);
    }
}
