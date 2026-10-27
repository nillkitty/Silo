using Serilog;
using Silo.Labs;
using Silo.Labs.Web;
using Silo.Platform;

namespace Silo.Connectors.Web;

/// <summary>
///     A browser inside Silo: it opens an address in a window of Silo instead of in a program of its own. It is Silo's own
///     program (<see cref="IBrowser.ExePath" />
///     is the one that is running), so it is not found by looking at what is installed.
/// </summary>
public interface IInternalBrowser : IBrowser
{
    /// <summary>Whether this browser opens the address itself.</summary>
    bool Accepts(Uri destination);
}

/// <summary>What the internal browsers have in common: they are Silo, and they open an address on Silo's UI thread.</summary>
public abstract class InternalBrowser : IInternalBrowser
{
    private string[]? _defaultFor;

    public abstract string Name { get; }

    /// <summary>
    ///     The schemes registered to Silo (<c>silo:</c> when it was set up to open those), asked of the shell the first
    ///     time.
    /// </summary>
    public string[] DefaultFor => _defaultFor ??= App.Resolve<IUrlInfo>()?.GetProtocolsFor(ExePath) ?? [];

    public string ExePath => InternalBrowsers.ProcessPath;

    public string Publisher => "Silo";

    public Version? Version => App.Version;

    /// <summary>Opens the address, on the UI thread.</summary>
    protected abstract void Open(Uri destination);

    public override string ToString()
    {
        return Name;
    }

    public virtual Uri? GetManagementUri()
    {
        return null;
    }

    public abstract bool Accepts(Uri destination);

    public void Launch(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        // windows are made on the UI thread; this may be called from another
        App.RunSafe(() => Open(destination));
    }
}

/// <summary>
///     Silo's browser as a whole, the one an address goes to when no particular browser is asked for: the Web Lab opens
///     web pages, navigation
///     follows <c>silo:</c> addresses, and what neither takes is handed to the person's own browser (a local file to the
///     program that
///     has its type).
/// </summary>
public sealed class SiloInternalBrowser : InternalBrowser
{
    public override string Name => "Silo Internal";

    /// <summary>Everything: what Silo cannot show itself it passes on.</summary>
    public override bool Accepts(Uri destination)
    {
        return true;
    }

    protected override void Open(Uri destination)
    {
        if (InternalBrowsers.For(destination) is { } own)
        {
            own.Launch(destination);
            return;
        }

        // not for a window of Silo: the person's own program. Asking for it must not come back to this browser (nothing
        // registered for the scheme falls back to the internal default, which is this one).
        var theirs = Browser.Manager.GetUserDefault(destination);
        if (theirs is not IInternalBrowser)
        {
            theirs.Launch(destination);
            return;
        }

        if (destination.IsAbsoluteUri && destination.IsFile && App.Resolve<IShellExecute>() is { } shell)
        {
            _ = shell.ShellExecute(destination.LocalPath);
            return;
        }

        throw new BrowserNotFoundException($"Nothing is registered to open '{destination}'.");
    }
}

/// <summary>
///     Opens web pages (and the local web files and <c>about:</c>/<c>data:</c> pages the Web Lab studies) in the Web
///     Lab.
/// </summary>
public sealed class WebLabBrowser : InternalBrowser
{
    public override string Name => "Silo Web Lab";

    public override Uri? GetManagementUri()
    {
        return new Uri("silo:lab/web");
    }

    public override bool Accepts(Uri destination)
    {
        return Taking(destination) is not null;
    }

    protected override void Open(Uri destination)
    {
        if (Taking(destination) is not { } take)
            throw new NotSupportedException($"The Web Lab does not open '{destination}'.");

        LabHost.Open(take.Lab, take.Value);
    }

    private static (ILab Lab, object Value)? Taking(Uri destination)
    {
        foreach (var (lab, value) in LabHost.LabsAccepting(destination))
            if (lab is WebLab)
                return (lab, value);

        return null;
    }
}

/// <summary>
///     Follows <c>silo:</c> addresses that name a place in Silo (see <see cref="SiloRoute" />): a Lab, a tool, the
///     main window.
/// </summary>
public sealed class SiloNavigationBrowser : InternalBrowser
{
    public override string Name => "Silo Navigation";

    public override bool Accepts(Uri destination)
    {
        return SiloRoute.TryParse(destination, out _);
    }

    protected override void Open(Uri destination)
    {
        if (!SiloRoute.TryParse(destination, out var route))
            throw new NotSupportedException($"'{destination}' does not name a place in Silo that can be opened.");

        switch (route.Kind)
        {
            case SiloRoute.Main:
                if (Application.Current?.MainWindow is { } main)
                {
                    if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
                    main.Activate();
                }

                break;

            case SiloRoute.Lab:
                var lab = LabHost.Labs.FirstOrDefault(l => l.Id.Equals(route.Id, StringComparison.OrdinalIgnoreCase)) ??
                          throw new InvalidOperationException($"Silo has no Lab called '{route.Id}'.");
                LabHost.Open(lab);
                break;

            case SiloRoute.Tool:
                var tool = App.ResolveAll<ITool>().FirstOrDefault(t => t.Name.Equals(route.Id, StringComparison.OrdinalIgnoreCase)) ??
                           throw new InvalidOperationException($"Silo has no tool called '{route.Id}'.");
                if (!tool.IsChecked) tool.Toggle();
                break;
        }
    }
}

/// <summary>
///     A place in Silo a <c>silo:</c> address names: <c>silo:main</c>, <c>silo:lab/web</c>,
///     <c>silo:tool/unicodeexplorer</c> (the addresses the
///     windows are kept under). The addresses of things inside a Silo (<c>silo:///active/...</c>, <c>silo:table/5</c>) are
///     not routes: nothing
///     opens those from an address yet.
/// </summary>
public readonly record struct SiloRoute(string Kind, string Id)
{
    public const string Main = "main";
    public const string Lab  = "lab";
    public const string Tool = "tool";

    /// <summary>Reads the address; false when it is not one that names a route. The kind is lower case, the id as written.</summary>
    public static bool TryParse(Uri? uri, out SiloRoute route)
    {
        route = default;
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!uri.Scheme.Equals("silo",     StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("silo-uri", StringComparison.OrdinalIgnoreCase)) return false;

        // "silo:lab/web" has no authority, "silo:///lab/web" an empty one: the path is the same
        string path  = uri.GetComponents(UriComponents.Path, UriFormat.Unescaped).Trim('/');
        var    parts = path.Split('/', 2, StringSplitOptions.TrimEntries);
        string kind  = parts[0].ToLowerInvariant();
        string id    = parts.Length > 1 ? parts[1].Trim('/') : string.Empty;

        switch (kind)
        {
            case Main when id.Length        == 0:
            case Lab or Tool when id.Length > 0:
                route = new SiloRoute(kind, id);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>Silo's own browsers, and the one to use for an address.</summary>
public static class InternalBrowsers
{
    /// <summary>Silo's browser as a whole.</summary>
    public static SiloInternalBrowser Auto { get; } = new();

    /// <summary>Navigation inside Silo as a browser (not one to choose: it takes addresses that are not pages).</summary>
    public static SiloNavigationBrowser InSilo { get; } = new();

    /// <summary>The Web Lab as a browser.</summary>
    public static WebLabBrowser InWebLab { get; } = new();

    // declared after the browsers it lists: static initializers run in order
    private static readonly IInternalBrowser[] Specific = [InWebLab, InSilo];

    /// <summary>The browsers listed among those to choose from.</summary>
    public static IReadOnlyList<IBrowser> Listed { get; } = [Auto, InWebLab];

    /// <summary>The path of the program that is running.</summary>
    public static string ProcessPath => Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0].Trim('"');

    /// <summary>
    ///     The browser inside Silo that opens the address by itself (not <see cref="Auto" />, which passes on what is
    ///     left); null when none does.
    /// </summary>
    public static IInternalBrowser? For(Uri destination)
    {
        foreach (var browser in Specific)
            try
            {
                if (browser.Accepts(destination)) return browser;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "{browser} failed to say whether it opens {destination}", browser.Name, destination);
            }

        return null;
    }

    /// <summary>
    ///     Whether a program path is Silo's own (the program that is running, or a copy of <c>silo.exe</c>, which is what
    ///     the shell is told to run for <c>silo:</c>).
    /// </summary>
    public static bool IsSilo(string? program)
    {
        if (string.IsNullOrWhiteSpace(program)) return false;

        return ShellCommand.SameProgram(program, ProcessPath) ||
               ShellCommand.FileNameOf(program).Equals("silo.exe", StringComparison.OrdinalIgnoreCase);
    }
}