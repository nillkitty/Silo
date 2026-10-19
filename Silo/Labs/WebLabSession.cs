using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;
using Silo.Commanding;

namespace Silo.Labs;

/// <summary>
///     One Web Lab window: a WebView2 showing the page, the Lab's parameters applied to everything the page does (what it can reach, what
///     it sends, what it is given, what it may ask for), and the inspectors that read the page back through the DevTools protocol.
/// </summary>
public sealed class WebLabSession : LabSession<Uri>
{
    /// <summary>Its own browser profile, apart from the Draw.io editor's: what a page stores here stays here.</summary>
    private static readonly string UserDataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "webview2-lab");

    private readonly ConsoleInspector                   _console;
    private readonly ConcurrentDictionary<string, string> _flags = new(StringComparer.Ordinal);
    private readonly List<LabInspector>                 _inspectors;
    private readonly Queue<PendingPermission>           _permissions = new();
    private readonly List<WebInspector>                 _webInspectors;

    private bool                          _canBack;
    private bool                          _canForward;
    private Uri?                          _current;
    private DevTools?                     _dev;
    private string                        _defaultUserAgent = string.Empty;
    private List<HeaderRule>              _headerRules   = [];
    private List<(Regex Pattern, bool WholeAddress)> _blocked = [];
    private bool                          _navigating;
    private bool                          _noDownloads;
    private bool                          _noPopups;
    private string?                       _pageTitle;
    private Dictionary<string, string>    _permissionPolicy = new(StringComparer.OrdinalIgnoreCase);
    private Uri?                          _pending;
    private bool                          _ready;
    private List<ReplaceRule>             _replacements = [];
    private bool                          _siloSchemes;
    private bool                          _started;
    private WebView2?                     _view;

    internal WebLabSession(WebLab lab) : base(lab)
    {
        var dom = new DomInspector(this);
        _console       = new ConsoleInspector(this);
        _webInspectors = [dom, _console, new NetworkInspector(this), new StyleInspector(this, dom)];
        _inspectors    = _webInspectors.Cast<LabInspector>().ToList();

        ParseParameters();
    }

    /// <summary>Raised when a page follows a <c>silo:</c> / <c>silo-uri:</c> link while the Lab's Silo URI schemes parameter is on.</summary>
    public static event Action<Uri>? SiloUriRequested;

    /// <summary>Raised when the Lab did something to a request (blocked, replaced, rewrote): the address and what was done.</summary>
    internal event Action<string, string>? FlagChanged;

    public override Uri? CurrentValue => _current ?? _pending;

    public override string Title => string.IsNullOrWhiteSpace(_pageTitle) ? base.Title : $"{_pageTitle} - {Lab.Name}";

    public override IReadOnlyList<LabInspector> Inspectors => _inspectors;

    private LabParameterSet Parameters => LabOf.Parameters;

    // ---- the page ----------------------------------------------------------------------------

    public override UIElement BuildMainView()
    {
        _view = new WebView2 { CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = UserDataFolder } };
        _view.Loaded += async (_, _) => await StartAsync();
        return _view;
    }

    private async Task StartAsync()
    {
        if (_started || _view is null) return;

        _started = true;
        try
        {
            await _view.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The Web Lab's browser could not start");
            Status = "The browser could not start: " + ex.Message;
            return;
        }

        var core = _view.CoreWebView2;
        _defaultUserAgent = core.Settings.UserAgent;

        core.NavigationStarting    += OnNavigationStarting;
        core.NavigationCompleted   += OnNavigationCompleted;
        core.SourceChanged         += (_, _) => UpdateCurrent();
        core.HistoryChanged        += (_, _) =>
                                      {
                                          _canBack    = core.CanGoBack;
                                          _canForward = core.CanGoForward;
                                          RaiseActionsChanged();
                                      };
        core.DocumentTitleChanged  += (_, _) =>
                                      {
                                          _pageTitle = core.DocumentTitle;
                                          Raise(nameof(Title));
                                      };
        core.NewWindowRequested    += OnNewWindowRequested;
        core.PermissionRequested   += OnPermissionRequested;
        core.DownloadStarting      += OnDownloadStarting;
        core.WebResourceRequested  += OnWebResourceRequested;
        core.ProcessFailed         += (_, e) => Say("The page's process failed: " + e.ProcessFailedKind);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

        _dev = new DevTools(core);
        foreach (var inspector in _webInspectors) inspector.Attach(_dev);

        Parameters.Changed += OnParametersChanged;
        ParseParameters();
        ApplyParameters();

        _ready = true;
        RaiseActionsChanged();

        if (_pending is { } pending) Navigate(pending);
    }

    protected override Task OpenValueAsync(Uri value)
    {
        _pending = value;
        if (_ready)
        {
            Navigate(value);
        }
        else
        {
            RaiseValueChanged();
            Status = "Starting the browser...";
        }

        return Task.CompletedTask;
    }

    protected override Task OpenDefaultAsync()
    {
        string home = Parameters[WebLabParameters.HomeUrl].Text;
        return OpenValueAsync(LabOf.TryParse(home, out var uri) && uri is not null ? uri : new Uri("about:blank"));
    }

    private void Navigate(Uri uri)
    {
        try
        {
            _view!.CoreWebView2.Navigate(uri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            Say("Could not go to " + uri + ": " + ex.Message);
        }
    }

    private void UpdateCurrent()
    {
        if (_view?.CoreWebView2 is not { } core) return;

        if (Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) && !Equals(uri, _current))
        {
            _current = uri;
            _pending = null;
            RaiseValueChanged();
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            if (_siloSchemes && uri.Scheme is "silo" or "silo-uri")
            {
                e.Cancel = true;
                Say("Silo address: " + uri);
                SiloUriRequested?.Invoke(uri);
                return;
            }

            foreach (var inspector in _webInspectors) inspector.OnNavigating(uri);
        }

        _navigating = true;
        Say("Loading " + e.Uri);
        RaiseActionsChanged();
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _navigating = false;
        Say(e.IsSuccess ? "Loaded" : "Failed: " + e.WebErrorStatus);
        foreach (var inspector in _webInspectors) inspector.OnNavigated(CurrentValue);
        RaiseActionsChanged();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // one view: a page that opens another window opens it here, unless pop-ups are switched off
        e.Handled = true;
        if (_noPopups)
        {
            Say("Blocked a pop-up: " + e.Uri);
            return;
        }

        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) Navigate(uri);
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (!_noDownloads) return;

        e.Cancel = true;
        Say("Blocked a download: " + e.DownloadOperation.Uri);
    }

    // ---- permissions -------------------------------------------------------------------------

    private sealed record PendingPermission(CoreWebView2PermissionRequestedEventArgs Args, CoreWebView2Deferral Deferral, string Kind, string Origin);

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        string kind  = e.PermissionKind.ToString().ToLowerInvariant();
        string state = _permissionPolicy.GetValueOrDefault(kind) ?? _permissionPolicy.GetValueOrDefault("*") ?? "ask";

        switch (state.ToLowerInvariant())
        {
            case "allow":
                e.State = CoreWebView2PermissionState.Allow;
                Say($"Allowed {kind} for {e.Uri}");
                break;
            case "deny":
                e.State = CoreWebView2PermissionState.Deny;
                Say($"Denied {kind} for {e.Uri}");
                break;
            default:
                // hold the page's request until Allow or Deny on the toolbar answers it
                _permissions.Enqueue(new PendingPermission(e, e.GetDeferral(), kind, e.Uri));
                Say($"{e.Uri} asks for {kind}: Allow or Deny");
                RaiseActionsChanged();
                break;
        }
    }

    private void AnswerPermission(bool allow)
    {
        if (!_permissions.TryDequeue(out var pending)) return;

        pending.Args.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        pending.Deferral.Complete();
        Say($"{(allow ? "Allowed" : "Denied")} {pending.Kind} for {pending.Origin}");
        RaiseActionsChanged();
    }

    // ---- parameters --------------------------------------------------------------------------

    private sealed record HeaderRule(Regex? Host, string Name, string? Value);

    private sealed record ReplaceRule(Regex Pattern, string Target);

    private void OnParametersChanged(LabParameter? changed)
    {
        if (_view is null) return;

        _view.Dispatcher.InvokeAsync(() =>
                                     {
                                         ParseParameters();
                                         ApplyParameters();
                                         RaiseActionsChanged();
                                         Say("Parameters changed; Refresh shows the page with them");
                                     });
    }

    /// <summary>Reads the parameters into the rules the request handlers use.</summary>
    private void ParseParameters()
    {
        _blocked = Parameters[WebLabParameters.BlockedHosts].Lines
                              .Where(l => !l.StartsWith('#'))
                              .Select(l => (Pattern: Wildcard(l), WholeAddress: l.Contains('/')))
                              .ToList();

        _headerRules = Parameters[WebLabParameters.HeaderRewrite].Lines.Where(l => !l.StartsWith('#')).Select(ParseHeaderRule).OfType<HeaderRule>().ToList();

        _replacements = Parameters[WebLabParameters.ResourceReplacement].Lines
                                   .Where(l => !l.StartsWith('#'))
                                   .Select(ParseReplacement)
                                   .OfType<ReplaceRule>()
                                   .ToList();

        _permissionPolicy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in Parameters[WebLabParameters.Permissions].Lines.Where(l => !l.StartsWith('#')))
        {
            int equals = line.IndexOf('=');
            if (equals > 0) _permissionPolicy[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        _siloSchemes = Parameters[WebLabParameters.SiloUriSchemes].Bool;

        var disabled = Parameters[WebLabParameters.DisabledFeatures].Lines;
        _noPopups    = disabled.Contains("Popups", StringComparer.OrdinalIgnoreCase);
        _noDownloads = disabled.Contains("Downloads", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Puts the parameters that are settings of the browser itself to the browser.</summary>
    private void ApplyParameters()
    {
        if (_view?.CoreWebView2 is not { } core) return;

        var disabled = Parameters[WebLabParameters.DisabledFeatures].Lines.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = core.Settings;
        settings.IsScriptEnabled                = !disabled.Contains("JavaScript");
        settings.IsWebMessageEnabled            = !disabled.Contains("WebMessages");
        settings.AreDevToolsEnabled             = !disabled.Contains("DevTools");
        settings.AreDefaultContextMenusEnabled  = !disabled.Contains("ContextMenus");
        settings.IsGeneralAutofillEnabled       = !disabled.Contains("AutoFill");
        settings.IsZoomControlEnabled           = !disabled.Contains("Zoom");
        settings.IsStatusBarEnabled             = !disabled.Contains("StatusBar");
        settings.AreDefaultScriptDialogsEnabled = !disabled.Contains("ScriptDialogs");
        settings.AreBrowserAcceleratorKeysEnabled = !disabled.Contains("AcceleratorKeys");

        string agent = Parameters[WebLabParameters.UserAgent].Text.Trim();
        settings.UserAgent = agent.Length > 0 ? agent : _defaultUserAgent;
    }

    private static Regex Wildcard(string pattern)
    {
        return new Regex("^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static HeaderRule? ParseHeaderRule(string line)
    {
        Regex? host = null;
        line = line.Trim();

        if (line.StartsWith('@'))
        {
            int space = line.IndexOf(' ');
            if (space < 0) return null;

            host = Wildcard(line[1..space]);
            line = line[(space + 1)..].Trim();
        }

        if (line.StartsWith('-')) return line.Length > 1 ? new HeaderRule(host, line[1..].Trim(), null) : null;

        int colon = line.IndexOf(':');
        return colon > 0 ? new HeaderRule(host, line[..colon].Trim(), line[(colon + 1)..].Trim()) : null;
    }

    private static ReplaceRule? ParseReplacement(string line)
    {
        int arrow = line.IndexOf("=>", StringComparison.Ordinal);
        if (arrow <= 0) return null;

        string pattern = line[..arrow].Trim();
        string target  = line[(arrow + 2)..].Trim();
        return pattern.Length == 0 || target.Length == 0 ? null : new ReplaceRule(Wildcard(pattern), target);
    }

    // ---- what the page asks for --------------------------------------------------------------

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var    request = e.Request;
        string url     = request.Uri;
        Uri.TryCreate(url, UriKind.Absolute, out var uri);

        var environment = _view!.CoreWebView2.Environment;

        foreach (var (pattern, whole) in _blocked)
        {
            if (!pattern.IsMatch(whole ? url : uri?.Host ?? string.Empty)) continue;

            Flag(url, "blocked");
            e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked by Web Lab", "Content-Type: text/plain");
            return;
        }

        foreach (var rule in _replacements)
        {
            if (!rule.Pattern.IsMatch(url)) continue;

            if (Replace(e, environment, rule.Target, url))
            {
                Flag(url, "replaced");
                return;
            }
        }

        bool rewritten = false;
        foreach (var rule in _headerRules)
        {
            if (rule.Host is not null && (uri is null || !rule.Host.IsMatch(uri.Host))) continue;

            if (rule.Value is null) request.Headers.RemoveHeader(rule.Name);
            else request.Headers.SetHeader(rule.Name, rule.Value);
            rewritten = true;
        }

        if (rewritten) Flag(url, "headers");
    }

    /// <summary>Answers the request with the replacement: some text, a redirect, or a file.</summary>
    private bool Replace(CoreWebView2WebResourceRequestedEventArgs e, CoreWebView2Environment environment, string target, string url)
    {
        if (target.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = Encoding.UTF8.GetBytes(target[5..]);
            e.Response = environment.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", $"Content-Type: {MimeFor(url)}; charset=utf-8\r\nAccess-Control-Allow-Origin: *");
            return true;
        }

        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            e.Response = environment.CreateWebResourceResponse(null, 302, "Found", "Location: " + target);
            return true;
        }

        string path = target.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? target[5..].Trim() : target;
        if (!File.Exists(path))
        {
            Say("Replacement file not found: " + path);
            return false;
        }

        try
        {
            e.Response = environment.CreateWebResourceResponse(File.OpenRead(path), 200, "OK", $"Content-Type: {MimeFor(path)}\r\nAccess-Control-Allow-Origin: *");
            return true;
        }
        catch (Exception ex)
        {
            Say($"Could not read {path}: {ex.Message}");
            return false;
        }
    }

    private static string MimeFor(string nameOrUrl)
    {
        string path = nameOrUrl;
        int    cut  = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];

        return Path.GetExtension(path).ToLowerInvariant() switch
               {
                   ".js" or ".mjs"  => "application/javascript",
                   ".css"           => "text/css",
                   ".json"          => "application/json",
                   ".html" or ".htm" => "text/html",
                   ".svg"           => "image/svg+xml",
                   ".png"           => "image/png",
                   ".jpg" or ".jpeg" => "image/jpeg",
                   ".gif"           => "image/gif",
                   ".webp"          => "image/webp",
                   ".ico"           => "image/x-icon",
                   ".woff2"         => "font/woff2",
                   ".woff"          => "font/woff",
                   ".xml"           => "application/xml",
                   _                => "text/plain"
               };
    }

    private void Flag(string url, string what)
    {
        if (_flags.Count > 5000) _flags.Clear();

        string flags = _flags.AddOrUpdate(url, what, (_, old) => old.Contains(what, StringComparison.Ordinal) ? old : old + "," + what);
        FlagChanged?.Invoke(url, flags);
    }

    /// <summary>What the Lab did to the requests for this address (blocked, replaced, headers); empty when nothing.</summary>
    internal string FlagsFor(string url) => _flags.TryGetValue(url, out var flags) ? flags : string.Empty;

    /// <summary>Puts a line on the Lab's status bar (from any inspector or handler).</summary>
    internal void Say(string text)
    {
        if (_view is { } view && !view.Dispatcher.CheckAccess()) view.Dispatcher.InvokeAsync(() => Status = text);
        else Status = text;
    }

    // ---- actions -----------------------------------------------------------------------------

    public override IEnumerable<ProvidedCommand> GlobalActions
    {
        get
        {
            yield return ProvidedCommand.From("home", "Go _Home", () => RunAsync(OpenDefaultAsync), "Launch", () => _ready, "Go to the home address (a parameter)") with { Gesture = "Alt+Home" };
            yield return ProvidedCommand.From("blank", "Open _Blank Page", () => RunAsync(() => OpenValueAsync(new Uri("about:blank"))), null, () => _ready, "Show an empty page");
            yield return ProvidedCommand.From("settings", "Open _Settings", () => Host?.ShowInspector(LabParametersInspector.InspectorId), "Settings", null, "Show the Lab's parameters: blocked hosts, header rewrite, user agent, permissions...");
            yield return ProvidedCommand.From("devtools", "Open _DevTools Window", () => _view?.CoreWebView2?.OpenDevToolsWindow(), "Code", () => _ready, "The browser's own developer tools, in a window");
            yield return ProvidedCommand.From("clear-data", "_Clear Browsing Data", () => RunAsync(ClearBrowsingDataAsync), null, () => _ready, "Forget this Lab's cookies, cache and stored site data");
        }
    }

    public override IEnumerable<ProvidedCommand> ContextActions
    {
        get
        {
            yield return ProvidedCommand.From("back", "_Back", () => _view?.CoreWebView2?.GoBack(), "Undo", () => _ready && _canBack, "Go back one page") with { Gesture = "Alt+Left" };
            yield return ProvidedCommand.From("forward", "_Forward", () => _view?.CoreWebView2?.GoForward(), "TbRedo", () => _ready && _canForward, "Go forward one page") with { Gesture = "Alt+Right" };

            if (_navigating)
                yield return ProvidedCommand.From("cancel", "_Cancel", () => _view?.CoreWebView2?.Stop(), null, null, "Stop loading the page");

            if (_permissions.TryPeek(out var pending))
            {
                yield return ProvidedCommand.From("accept", $"_Allow {pending.Kind}", () => AnswerPermission(true), null, null, $"Let {pending.Origin} use {pending.Kind}");
                yield return ProvidedCommand.From("deny", "_Deny", () => AnswerPermission(false), null, null, $"Refuse {pending.Origin} {pending.Kind}");
            }
        }
    }

    public override IEnumerable<ProvidedCommand> ValueActions
    {
        get
        {
            foreach (var standard in StandardValueActions()) yield return standard;

            yield return ProvidedCommand.From("refresh", "_Refresh", () => _view?.CoreWebView2?.Reload(), "Refresh", () => _ready, "Load the page again, with the current parameters") with { Gesture = "F5" };
            yield return ProvidedCommand.From("hard-refresh", "Refresh Ignoring _Cache", () => RunAsync(HardRefreshAsync), null, () => _ready, "Empty the browser's cache and load the page again") with { Gesture = "Ctrl+F5" };
            yield return ProvidedCommand.From("view-source", "View _Source", () => RunAsync(ViewSourceAsync), "Document", () => _ready && _current is not null, "The page's HTML as the server sent it");
            yield return ProvidedCommand.From("open-external", "Open in _Browser", OpenExternal, null, () => _current is { Scheme: "http" or "https" }, "Open the address in your own web browser");
        }
    }

    public override IReadOnlyList<LabTerminalCommand> TerminalCommands =>
    [
        new LabTerminalCommand("js", "js <code>            evaluate JavaScript in the page", async call => await _console.EvaluateAsync(call.Rest)),
        new LabTerminalCommand("cookies", "cookies [clear]     list the page's cookies, or clear them", CookiesAsync),
        new LabTerminalCommand("block", "block <host>        add a host to the blocked hosts", call => Task.FromResult<string?>(Block(call.Rest, true))),
        new LabTerminalCommand("unblock", "unblock <host>      take a host off the blocked hosts", call => Task.FromResult<string?>(Block(call.Rest, false))),
        new LabTerminalCommand("zoom", "zoom <percent>       zoom the page", call => Task.FromResult<string?>(Zoom(call.Rest)))
    ];

    private async Task RunAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "A Web Lab action failed");
            Say(ex.Message);
        }
    }

    private async Task ClearBrowsingDataAsync()
    {
        await _view!.CoreWebView2.Profile.ClearBrowsingDataAsync();
        Say("Cleared the browsing data");
    }

    private async Task HardRefreshAsync()
    {
        var core = _view!.CoreWebView2;
        await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
        core.Reload();
    }

    private async Task ViewSourceAsync()
    {
        if (_dev is null) return;

        var response = await _dev.CallAsync("Runtime.evaluate", new { expression = "fetch(location.href).then(r => r.text())", awaitPromise = true, returnByValue = true });
        string source = (string?)response["result"]?["value"] ?? "The source could not be read: " + ((string?)response["exceptionDetails"]?["text"] ?? "no answer");
        LabTextWindow.ShowText("Source of " + ValueText, source);
    }

    private void OpenExternal()
    {
        if (_current is null) return;

        try
        {
            Process.Start(new ProcessStartInfo(_current.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Say("Could not open the browser: " + ex.Message);
        }
    }

    private async Task<string?> CookiesAsync(LabTerminalCall call)
    {
        var core = _view?.CoreWebView2 ?? throw new InvalidOperationException("The browser is not up yet.");
        if (call.Args.Count > 0 && call.Args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            core.CookieManager.DeleteAllCookies();
            return "Cookies cleared.";
        }

        var cookies = await core.CookieManager.GetCookiesAsync(null);
        return cookies.Count == 0 ? "No cookies." : string.Join(Environment.NewLine, cookies.Select(c => $"{c.Domain}{c.Path}  {c.Name}={c.Value}"));
    }

    private string Block(string host, bool block)
    {
        host = host.Trim();
        if (host.Length == 0) throw new InvalidOperationException((block ? "block" : "unblock") + " <host>");

        var parameter = Parameters[WebLabParameters.BlockedHosts];
        var lines     = parameter.Lines.ToList();
        if (block)
        {
            if (!lines.Contains(host, StringComparer.OrdinalIgnoreCase)) lines.Add(host);
        }
        else if (lines.RemoveAll(l => l.Equals(host, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            throw new InvalidOperationException($"{host} is not blocked.");
        }

        parameter.Value = lines;
        return $"{lines.Count} blocked host{(lines.Count == 1 ? string.Empty : "s")}.";
    }

    private string Zoom(string percent)
    {
        if (_view is null) throw new InvalidOperationException("The browser is not up yet.");

        if (!double.TryParse(percent.TrimEnd('%'), out double value) || value < 25 || value > 500)
            throw new InvalidOperationException("zoom <percent>, between 25 and 500");

        _view.ZoomFactor = value / 100;
        return $"Zoom {value:0}%";
    }

    protected override void OnDispose()
    {
        Parameters.Changed -= OnParametersChanged;

        // a page waiting for an answer is not left hanging
        while (_permissions.TryDequeue(out var pending))
        {
            try
            {
                pending.Args.State = CoreWebView2PermissionState.Deny;
                pending.Deferral.Complete();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Answering a permission request as the Web Lab closed failed");
            }
        }

        try
        {
            _view?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Disposing the Web Lab's browser failed");
        }
    }
}
