using System.Runtime.CompilerServices;

namespace Silo.Connectors;

/// <summary>
///     Represents an object which manifests through local configuration that can be read
///     at any time.
/// </summary>
public interface ILocalConfig
{
    /// <summary>
    ///     Gets the virtual URI at which this object can be managed.
    /// </summary>
    Uri? GetManagementUri();
}

public interface IBrowser : ILocalConfig
{
    /// <summary>
    ///     Gets the URL schemes this Browser is registered for
    /// </summary>
    string[] DefaultFor { get; }

    /// <summary>
    ///     Gets the path to the ExePath where the browser's binary is
    /// </summary>
    string ExePath { get; }

    /// <summary>
    ///     Gets the browser name
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Gets the browser's publisher name
    /// </summary>
    string Publisher { get; }

    /// <summary>
    ///     Gets the browser's version number if available
    /// </summary>
    Version? Version { get; }

    /// <summary>
    ///     Launches an instance of the browser, using the provdied URL as the destination
    /// </summary>
    void Launch(Uri destination);
}

/// <summary>
///     A service which can provide any number of Browsers
/// </summary>
public interface IBrowserProvider : IDiscovery<IBrowser>
{
    DiscoveryType IDiscovery<IBrowser>.Type => DiscoveryType.Quick;
    IEnumerable<IBrowser>              GetBrowsers();

    async IAsyncEnumerable<Discovered<IBrowser>> IDiscovery<IBrowser>.DiscoverAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        // one asynchronous hop, so the caller's thread is never held up by reading the browsers
        await Task.Yield();

        var bb = GetBrowsers();
        if (bb is null)
            throw new InvalidOperationException($"Received a null from a GetBrowsers() call to {GetType()}");

        foreach (var b in bb)
        {
            if (b is null) continue;

            cancel.ThrowIfCancellationRequested();

            // a browser with nothing to manage is found at the place its program is
            var u  = b.GetManagementUri() ?? (Uri.TryCreate(b.ExePath, UriKind.Absolute, out var program) ? program : new Uri("about:blank"));
            var bd = new Discovered<IBrowser>(this, u, b);
            yield return bd;
        }
    }
}

/// <summary>
///     A service used to discover, find, and invoke browsers
/// </summary>
public interface IBrowserManager
{
    /// <summary>
    ///     Every browser there is, to choose from: Silo's own (its internal browser and the Web Lab) first, then those that were
    ///     registered with the container and those the <see cref="IBrowserProvider" />s found (installed ones are counted once
    ///     however many ways they are found).
    /// </summary>
    IEnumerable<IBrowser> GetBrowsers();

    /// <summary>
    ///     Finds the browser a registered command (<c>"C:\...\browser.exe" %1</c>, what the shell has for a URL protocol) runs. A
    ///     command that runs Silo is the internal browser; a program no provider knows is described from its file.
    /// </summary>
    /// <param name="handlerCommand">The command registered with the shell.</param>
    /// <param name="fallback">What to do when no browser can be found for the command.</param>
    /// <param name="destination">
    ///     The address that is going to be opened, which a fallback to the internal or the user's default browser needs to
    ///     choose one; without it, the internal default is Silo's own internal browser and the user's default is
    ///     the one for <c>https</c>.
    /// </param>
    IBrowser? Find(string handlerCommand, BrowserFallback fallback = BrowserFallback.InternalDefault, Uri? destination = null);

    /// <summary>
    ///     The browser (or program) the person chose in Windows for the address's scheme. A scheme nothing is registered for
    ///     is opened by <see cref="GetInternalDefault" />, so this never fails to give something to open the address with.
    /// </summary>
    IBrowser GetUserDefault(Uri destination);

    /// <summary>
    ///     The browser inside Silo that takes the address: the Web Lab for web pages, navigation for <c>silo:</c> addresses; Silo's own
    ///     internal browser when none of those do, which hands what is left to the person's browser.
    /// </summary>
    IBrowser GetInternalDefault(Uri destination);
}

public enum BrowserFallback
{
    Default = InternalDefault,

    /// <summary>
    ///     Throws an exception if the browser could not be found.
    /// </summary>
    None = 1,

    /// <summary>
    ///     Returns null if the browser could not be found.
    /// </summary>
    Null,

    /// <summary>
    ///     Returns the user's explorer-registered default browser (for the given URL),
    ///     obviously causes URLs to open external to Silo.
    /// </summary>
    UserDefault,

    /// <summary>
    ///     Returns the internal default, which -- depending on the destination URL --
    ///     is either the local silo browser (streamlined), Web Lab, the draw.io pane,
    ///     or a navigation link directly into Silo.
    /// </summary>
    InternalDefault = 0
}

/// <summary>Thrown when an address has to be opened and no browser can be found for it (<see cref="BrowserFallback.None" />, or nothing left to fall back to).</summary>
public class BrowserNotFoundException(string message, Exception? inner = null) : Exception(message, inner);
