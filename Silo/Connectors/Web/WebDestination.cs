using System.Data;

namespace Silo.Connectors.Web;

public record WebDestination(Uri Url, IBrowser OpenInBrowser) : IDestination
{
    Uri IDestination.Uri
    {
        get => Url;
        set => throw new ReadOnlyException("This destination object is read-only.");
    }

    /// <summary>
    ///     Launches the destination in the configured browser (or internally within Silo)
    /// </summary>
    public Task LaunchAsync(CancellationToken cancel = default)
    {
        return Browser.LaunchAsync(Url, OpenInBrowser, cancel);
    }

    public WebDestination(Uri url) : this(url, Browser.SiloAuto)
    {
    }

    /// <summary>A destination opened by the browser a registered command runs; Silo's browser when there is none for it.</summary>
    public WebDestination(Uri url, string handlerCommand) : this(url, Browser.Find(handlerCommand, BrowserFallback.Default) ?? Browser.SiloAuto)
    {
    }
}
