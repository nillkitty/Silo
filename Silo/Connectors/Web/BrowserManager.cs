using System.Collections.Concurrent;
using Serilog;
using Silo.Platform;
using Telefrag.DI;

namespace Silo.Connectors.Web;

/// <summary>
///     Knows every browser there is (Silo's own, the ones registered with the container and the ones the providers found)
///     and which
///     of them the shell or Silo itself would use for an address.
/// </summary>
[Singleton(typeof(IBrowserManager))]
public class BrowserManager : ManagerBase<IBrowser, IBrowserProvider>, IBrowserManager
{
    /// <summary>An address used to ask the shell for "the browser", when the address that will be opened is not known.</summary>
    public static readonly Uri WebProbe = new("https://www.example.com/");

    private readonly ConcurrentDictionary<string, IBrowser?> _found = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the shell has registered for addresses.</summary>
    protected virtual IUrlInfo? Urls => App.Resolve<IUrlInfo>();

    protected override IEnumerable<IBrowser> Provide(IBrowserProvider provider)
    {
        return provider.GetBrowsers() ?? [];
    }

    protected override string? KeyOf(IBrowser item)
    {
        return string.IsNullOrWhiteSpace(item.ExePath) ? null : ShellCommand.FullPath(item.ExePath);
    }

    /// <summary>The browser that a registered command runs; null when it runs something that is not there.</summary>
    private IBrowser? Lookup(string command)
    {
        if (ShellCommand.Split(command) is not { } split) return null;

        string program = split.Program;

        if (InternalBrowsers.IsSilo(program)) return InternalBrowsers.Auto;

        foreach (var browser in All)
            if (ShellCommand.SameProgram(browser.ExePath, program))
                return browser;

        // not one that was found: a program the person set up for the scheme, described by its file
        return _found.GetOrAdd(ShellCommand.FullPath(program),
                               _ => global::System.IO.File.Exists(program) ? ShellBrowser.FromCommand(command) : null);
    }

    public override void Refresh()
    {
        base.Refresh();
        _found.Clear();
    }

    public IEnumerable<IBrowser> GetBrowsers()
    {
        return InternalBrowsers.Listed.Concat(All).ToList();
    }

    public IBrowser? Find(string handlerCommand, BrowserFallback fallback = BrowserFallback.InternalDefault, Uri? destination = null)
    {
        IBrowser? browser = null;
        if (!string.IsNullOrWhiteSpace(handlerCommand))
            try
            {
                browser = Lookup(handlerCommand);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Looking for the browser that runs {command} failed", handlerCommand);
            }

        if (browser is not null) return browser;

        switch (fallback)
        {
            case BrowserFallback.None:
                throw new BrowserNotFoundException($"No browser could be found for the command '{handlerCommand}'.");
            case BrowserFallback.Null:
                return null;
            case BrowserFallback.UserDefault:
                return GetUserDefault(destination ?? WebProbe);
            default:
                return destination is null ? InternalBrowsers.Auto : GetInternalDefault(destination);
        }
    }

    public IBrowser GetUserDefault(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        try
        {
            if (Urls?.GetInfo(destination) is { } info && Lookup(info.HandlerCommand) is { } browser) return browser;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Asking the shell for the browser of {destination} failed", destination);
        }

        return GetInternalDefault(destination);
    }

    public IBrowser GetInternalDefault(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        return (IBrowser?)InternalBrowsers.For(destination) ?? InternalBrowsers.Auto;
    }
}