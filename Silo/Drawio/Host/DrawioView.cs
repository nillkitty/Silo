using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;
using Silo.Design;
using Silo.Drawio.Live;
using Silo.Drawio.Mx;
using Silo.Drawio.Options;

namespace Silo.Drawio.Host;

/// <summary>
///     An embedded draw.io editor: WebView2 hosting the locally installed draw.io web app (see
///     <see cref="DrawioRuntime" />) in embed mode, configured by a <see cref="DrawioProfile" />
///     (optionally overridden for one diagram) and driven over draw.io's JSON <c>postMessage</c>
///     protocol.
/// </summary>
/// <remarks>
///     <para>
///         <b>Restrictions.</b> Every restriction is a property of <see cref="DrawioOptions" />. URL
///         parameters and the <c>configure</c> handshake carry the draw.io-side ones; the WebView2-side ones
///         (dev tools, context menu, downloads, pop-ups, external requests, ...) are applied here. Changing
///         options re-configures the pane live: host-side settings immediately, draw.io-side ones by
///         reloading the editor with the current diagram carried across.
///     </para>
///     <para>
///         <b>Live data.</b> A pane can have a <see cref="Source" /> (the generated layer, e.g. the live Silo
///         model) and cells can carry <c>silo-…</c> bindings (see <see cref="DiagramBindings" />). Whenever
///         the source or any data provider changes, the pane re-exports the diagram, patches it on the .NET
///         side -- regenerated cells replaced, binding values re-evaluated, the person's own cells untouched --
///         and loads it back, throttled and deferred while the person is mid-edit
///         (<see cref="DrawioOptions.LiveUpdate" />).
///     </para>
///     <para>
///         Other code can draw into a pane with <see cref="ApplyEditsAsync" /> and
///         <see cref="DiagramData.Push" />.
///     </para>
/// </remarks>
public sealed partial class DrawioView : UserControl, IDisposable
{
    private static readonly string UserDataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "webview2");

    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _exports = new();
    private readonly DispatcherTimer                                            _liveTimer;
    private readonly DispatcherTimer                                            _reconfigureTimer;
    private readonly ILogger                                                    _log = Log.ForContext<DrawioView>();
    private readonly Button                                                     _overlayButton;
    private readonly TextBlock                                                  _overlayText;
    private readonly Border                                                     _overlay;
    private readonly ProgressBar                                                _overlayBar;
    private readonly WebView2                                                   _web;

    private string?        _appliedJson;
    private string?        _currentXml;
    private bool           _disposed;
    private bool           _editorReady;
    private DateTime       _ignoreChangesUntilUtc;
    private DateTime       _lastUserChangeUtc = DateTime.MinValue;
    private double?        _lastScale;
    private bool           _loadedOnce;
    private DrawioOptions? _override;
    private DrawioProfile  _profile;
    private bool           _refreshing;
    private bool           _sourceDirty;
    private bool           _started;
    private IDiagramContentSource? _source;
    private bool           _webInitialized;

    public DrawioView(string profileName = DrawioProfiles.Canvas, DrawioOptions? optionsOverride = null)
    {
        _profile  = DrawioProfiles.Instance.Get(profileName);
        _override = optionsOverride;

        _web = new WebView2
               {
                   CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = UserDataFolder },
                   DefaultBackgroundColor = System.Drawing.Color.Transparent
               };

        _overlayText = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 420 };
        _overlayBar  = new ProgressBar { IsIndeterminate = true, Height = 4, Width = 240, Margin = new Thickness(0, 10, 0, 0) };
        _overlayButton = new Button { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14, 4, 14, 4), Visibility = Visibility.Collapsed };

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(_overlayText);
        stack.Children.Add(_overlayBar);
        stack.Children.Add(_overlayButton);

        _overlay = new Border { Background = SystemColors.WindowBrush, Child = stack, Visibility = Visibility.Visible };

        var grid = new Grid();
        grid.Children.Add(_web);
        grid.Children.Add(_overlay);
        Content = grid;

        _liveTimer        = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(750) };
        _liveTimer.Tick   += async (_, _) => { _liveTimer.Stop(); await RefreshAsync(); };
        _reconfigureTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
        _reconfigureTimer.Tick += async (_, _) => { _reconfigureTimer.Stop(); await ReconfigureAsync(); };

        _profile.Changed          += OnProfileChanged;
        DiagramData.Changed       += OnLiveDataChanged;
        Loaded                    += async (_, _) => await StartAsync();
        ShowStatus("Starting the diagram editor…", true);
    }

    // ---- public surface ----------------------------------------------------------------------

    /// <summary>The options in force: the per-diagram override if there is one, otherwise the profile's.</summary>
    public DrawioOptions Options => _override ?? _profile.Options;

    /// <summary>Per-diagram options that replace the profile's for this pane (null = use the profile).</summary>
    public DrawioOptions? Override
    {
        get => _override;
        set
        {
            _override = value;
            ScheduleReconfigure();
        }
    }

    public DrawioProfile Profile => _profile;

    /// <summary>The generated layer for this pane, if any (e.g. <see cref="SiloModelDiagramSource" />).</summary>
    public IDiagramContentSource? Source
    {
        get => _source;
        set
        {
            if (_source is not null) _source.Changed -= OnSourceChanged;
            _source = value;
            if (_source is not null) _source.Changed += OnSourceChanged;
            _sourceDirty = true;
            RequestRefresh();
        }
    }

    /// <summary>The diagram as last reported by the editor, including any generated cells.</summary>
    public string? Xml => _currentXml;

    /// <summary>The diagram without the cells Silo generates -- what is worth persisting.</summary>
    public string? UserXml
    {
        get
        {
            if (_currentXml is null) return null;

            var doc = DrawioDocument.Parse(_currentXml);
            doc.RemoveGenerated();
            return doc.ToXml();
        }
    }

    /// <summary>Raised when the person changes the diagram (the editor's autosave).</summary>
    public event EventHandler? DocumentChanged;

    /// <summary>Raised when a cell link to a Silo construct is activated, after the registered handlers have had their turn.</summary>
    public event EventHandler<DiagramLink>? LinkActivated;

    /// <summary>Raised when <see cref="Override" /> is edited through <see cref="BeginOverride" />, so its owner can persist it.</summary>
    public event EventHandler? OverrideChanged;

    /// <summary>Raised when the editor has loaded a diagram and is ready for edits.</summary>
    public event EventHandler? EditorReady;

    /// <summary>Switches to another profile (and so to that profile's restrictions).</summary>
    public void SetProfile(string name)
    {
        _profile.Changed -= OnProfileChanged;
        _profile         =  DrawioProfiles.Instance.Get(name);
        _profile.Changed += OnProfileChanged;
        ScheduleReconfigure();
    }

    /// <summary>Starts a per-diagram override (a copy of the profile's options) if there isn't one, and returns it for editing.</summary>
    public DrawioOptions BeginOverride()
    {
        return _override ??= _profile.Options.Clone();
    }

    /// <summary>Drops the per-diagram override so the pane follows its profile again.</summary>
    public void ClearOverride()
    {
        if (_override is null) return;

        _override = null;
        OverrideChanged?.Invoke(this, EventArgs.Empty);
        ScheduleReconfigure();
    }

    /// <summary>
    ///     Edits the options in force -- the per-diagram override if there is one, otherwise the
    ///     profile (and so every pane that shares it) -- and tells the right party about it.
    /// </summary>
    public void EditOptions(Action<DrawioOptions> edit)
    {
        edit(Options);

        if (_override is not null) NotifyOverrideChanged();
        else _profile.NotifyChanged();
    }

    /// <summary>Call after editing the object <see cref="BeginOverride" /> returned.</summary>
    public void NotifyOverrideChanged()
    {
        OverrideChanged?.Invoke(this, EventArgs.Empty);
        ScheduleReconfigure();
    }

    /// <summary>Replaces the diagram's content.</summary>
    public Task LoadXmlAsync(string? xml)
    {
        _currentXml = string.IsNullOrWhiteSpace(xml) ? null : xml;
        if (_editorReady) PostLoad(Compose(_currentXml), false);
        return Task.CompletedTask;
    }

    /// <summary>Asks the editor for its current diagram XML (falls back to the last known XML if it does not answer in time).</summary>
    public async Task<string?> GetXmlAsync(int timeoutMs = 3000)
    {
        if (!_editorReady) return _currentXml;

        string? xml = await RequestExportAsync("xml", null, timeoutMs);
        if (xml is not null && xml.StartsWith('<')) _currentXml = xml;
        return _currentXml;
    }

    /// <summary>Asks the editor to export the diagram (svg, png, ...); returns the data URI / payload, or null if it did not answer.</summary>
    public Task<string?> ExportAsync(string format, int timeoutMs = 10000)
    {
        return RequestExportAsync(format, null, timeoutMs);
    }

    /// <summary>
    ///     Applies code-driven changes to the diagram -- add / update / remove cells -- on top of whatever the
    ///     person currently has on the canvas.
    /// </summary>
    public async Task ApplyEditsAsync(IEnumerable<DiagramEdit> edits)
    {
        var doc = DrawioDocument.Parse(await GetXmlAsync());
        doc.Apply(edits, Options.LockGeneratedCells);
        await LoadXmlAsync(doc.ToXml());
    }

    /// <summary>Re-evaluates live data now (still subject to the throttle).</summary>
    public void RequestRefresh()
    {
        if (!_editorReady || _disposed) return;

        if (!_liveTimer.IsEnabled)
        {
            _liveTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, Options.LiveThrottleMs));
            _liveTimer.Start();
        }
    }

    /// <summary>Reloads the editor page (carrying the diagram across), e.g. to apply changed draw.io-side restrictions.</summary>
    public async Task ReloadEditorAsync()
    {
        if (!_webInitialized || _web.CoreWebView2 is null) return;

        if (_editorReady) _currentXml = await GetXmlAsync() ?? _currentXml;

        _appliedJson = Options.ToJson();
        ApplyHostSettings();
        await RegisterPluginAsync();
        Navigate();
    }

    /// <summary>
    ///     Reloads the editor from scratch, carrying the diagram across: clears WebView2's caches and service workers (so
    ///     draw.io's files, the host page and the plugin are fetched fresh), rewrites the host page, re-registers the plugin
    ///     and navigates again. draw.io's own saved settings and scratchpad (local storage) are kept.
    /// </summary>
    public async Task ResetEditorAsync()
    {
        if (!_webInitialized || _web.CoreWebView2 is not { } core) return;

        if (_editorReady) _currentXml = await GetXmlAsync() ?? _currentXml;

        ShowStatus("Resetting the editor…", true);
        try
        {
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache |
                                                      CoreWebView2BrowsingDataKinds.CacheStorage |
                                                      CoreWebView2BrowsingDataKinds.ServiceWorkers);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Clearing the editor's caches failed");
        }

        try
        {
            DrawioRuntime.WriteHostPage();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Rewriting the draw.io host page failed");
        }

        _sourceDirty = true;
        await ReloadEditorAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _liveTimer.Stop();
        _reconfigureTimer.Stop();
        _profile.Changed    -= OnProfileChanged;
        DiagramData.Changed -= OnLiveDataChanged;
        if (_source is not null) _source.Changed -= OnSourceChanged;
        foreach (var tcs in _exports.Values) tcs.TrySetResult(null);
        CompleteAllPending();

        _web.Dispose();
    }

    // ---- startup -----------------------------------------------------------------------------

    private async Task StartAsync()
    {
        if (_started || _disposed) return;
        _started = true;

        try
        {
            if (!DrawioRuntime.IsInstalled)
            {
                if (!DrawioRuntime.Settings.AutoInstall)
                {
                    _started = false;
                    ShowStatus($"The draw.io editor (v{DrawioRuntime.Settings.Version}) has not been downloaded yet.\n" +
                               "It is downloaded once and then runs entirely from your computer.", false,
                               $"Download draw.io v{DrawioRuntime.Settings.Version}", async () => await InstallAndStartAsync());
                    return;
                }

                await InstallAsync();
            }
            else
            {
                DrawioRuntime.WriteHostPage();
            }

            await InitializeWebViewAsync();
        }
        catch (Exception ex)
        {
            _started = false;
            _log.Error(ex, "Could not start the draw.io pane");
            ShowStatus("The diagram editor could not start.\n" + ex.Message, false, "Try again", async () => await StartAsync());
        }
    }

    private async Task InstallAndStartAsync()
    {
        try
        {
            await InstallAsync();
            await StartAsync();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "draw.io download failed");
            ShowStatus("Downloading draw.io failed.\n" + ex.Message, false, "Try again", async () => await InstallAndStartAsync());
        }
    }

    private async Task InstallAsync()
    {
        ShowStatus("Downloading draw.io…", true);
        await DrawioRuntime.EnsureInstalledAsync(new Progress<string>(s => _overlayText.Text = s));
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webInitialized) return;

        ShowStatus("Starting the diagram editor…", true);
        Directory.CreateDirectory(UserDataFolder);

        try
        {
            await _web.EnsureCoreWebView2Async();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowStatus("The Microsoft Edge WebView2 runtime is not installed, and the diagram editor needs it.\n" +
                       "Install the \"WebView2 Runtime\" from Microsoft and reopen this pane.", false);
            return;
        }

        var core = _web.CoreWebView2;
        core.ClearVirtualHostNameToFolderMapping(DrawioRuntime.VirtualHost);
        core.SetVirtualHostNameToFolderMapping(DrawioRuntime.VirtualHost, DrawioRuntime.InstallDirectory,
                                               CoreWebView2HostResourceAccessKind.DenyCors);

        core.WebMessageReceived  += OnWebMessageReceived;
        core.NavigationStarting  += OnNavigationStarting;
        core.NewWindowRequested  += OnNewWindowRequested;
        core.DownloadStarting    += OnDownloadStarting;
        core.ProcessFailed       += OnProcessFailed;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;

        _webInitialized = true;
        _appliedJson    = Options.ToJson();
        ApplyHostSettings();
        await RegisterPluginAsync();
        Navigate();
    }

    private void Navigate()
    {
        _editorReady = false;
        _loadedOnce  = false;
        ShowStatus("Loading the editor…", true);
        _web.CoreWebView2.Navigate(DrawioRuntime.HostUrl(CustomizeQuery(Options.ToQueryString())));
    }

    // ---- WebView2 restrictions ---------------------------------------------------------------

    private void ApplyHostSettings()
    {
        if (_web.CoreWebView2 is not { } core) return;

        var o = Options;
        var s = core.Settings;
        s.AreDevToolsEnabled               = o.AllowDevTools;
        s.AreDefaultContextMenusEnabled    = o.AllowContextMenu;
        s.AreBrowserAcceleratorKeysEnabled = o.AllowBrowserKeys;
        s.IsStatusBarEnabled               = o.ShowStatusBar;
        s.IsZoomControlEnabled             = o.AllowZoomControl;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is "about" or "data" or "blob") return;

        if (!string.Equals(uri.Host, DrawioRuntime.VirtualHost, StringComparison.OrdinalIgnoreCase))
        {
            _log.Debug("Blocked navigation to {uri}", e.Uri);
            e.Cancel = true;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (Options.AllowPopups) return;

        _log.Debug("Blocked pop-up to {uri}", e.Uri);
        e.Handled = true;
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (Options.AllowDownloads) return;

        _log.Debug("Blocked download of {uri}", e.DownloadOperation.Uri);
        e.Cancel = true;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Options.BlockExternalRequests) return;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is "data" or "blob" or "about") return;
        if (string.Equals(uri.Host, DrawioRuntime.VirtualHost, StringComparison.OrdinalIgnoreCase)) return;

        _log.Debug("Blocked external request {uri}", e.Request.Uri);
        e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked by Silo", "");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _log.Warning("WebView2 process failed: {kind}", e.ProcessFailedKind);
        ShowStatus("The diagram editor stopped unexpectedly.", false, "Reload", async () => await ReloadEditorAsync());
    }

    // ---- protocol ----------------------------------------------------------------------------

    private void Post(JsonObject message)
    {
        if (_web.CoreWebView2 is null) return;
        _web.CoreWebView2.PostWebMessageAsString(message.ToJsonString());
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string text;
        try
        {
            text = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return; // not part of the JSON protocol
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            string name = root.TryGetProperty("event", out var evp) && evp.ValueKind == JsonValueKind.String ? evp.GetString() ?? "" : "";

            // Detached copy: handlers may keep the message after the document is gone.
            var message = new DrawioMessage(name, root.Clone());

            try
            {
                DispatchEvent(message);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Handling the draw.io event {event} failed", name);
            }
        }
    }

    /// <summary>
    ///     Routes every message from the editor. Order: complete anything awaiting this event, run Silo's own
    ///     handling (state tracking, saving, links), raise <see cref="EventReceived" />, then call the matching
    ///     <c>On…</c> stub in <c>DrawioView.Handlers.cs</c> -- the place to add behaviour.
    /// </summary>
    private void DispatchEvent(DrawioMessage m)
    {
        CompletePending(m);

        switch (m.Name)
        {
            case DrawioEvent.Ready:
                OnReady(m);
                break;

            case DrawioEvent.Configure:
                Post(new JsonObject { ["action"] = DrawioAction.Configure, ["config"] = BuildConfig() });
                OnConfigure(m);
                break;

            case DrawioEvent.Init:
                _editorReady = true;
                PostLoad(Compose(_currentXml), false);
                OnInit(m);
                break;

            case DrawioEvent.Load:
                _loadedOnce = true;
                HideOverlay();
                EditorReady?.Invoke(this, EventArgs.Empty);
                OnLoad(m);
                break;

            case DrawioEvent.Zoom:
                if (m.Scale is { } scale) _lastScale = scale;
                OnZoom(m);
                break;

            case DrawioEvent.Autosave:
                HandleDocumentEvent(m);
                OnAutosave(m);
                break;

            case DrawioEvent.Save:
                HandleDocumentEvent(m);
                OnSave(m);
                break;

            case DrawioEvent.Exit:
                OnExit(m);
                break;

            case DrawioEvent.Export:
                CompleteExport(m.Root);
                OnExport(m);
                break;

            case DrawioEvent.OpenLink:
                if (m.Href is { } href) HandleLink(href);
                OnOpenLink(m);
                break;

            case DrawioEvent.Merge:
                if (m.Error is { } mergeError) _log.Warning("draw.io rejected a merge: {error}", mergeError);
                OnMerge(m);
                break;

            case DrawioEvent.Resize:
                OnResize(m);
                break;

            case DrawioEvent.Fit:
                OnFit(m);
                break;

            case DrawioEvent.Viewbox:
                OnViewbox(m);
                break;

            case DrawioEvent.Layout:
                OnLayout(m);
                break;

            case DrawioEvent.Patch:
                OnPatch(m);
                break;

            case DrawioEvent.GetDiff:
                OnGetDiff(m);
                break;

            case DrawioEvent.ResetDiff:
                OnResetDiff(m);
                break;

            case DrawioEvent.Prompt:
                OnPrompt(m);
                break;

            case DrawioEvent.PromptCancel:
                OnPromptCancel(m);
                break;

            case DrawioEvent.Template:
                OnTemplate(m);
                break;

            case DrawioEvent.Draft:
                OnDraft(m);
                break;

            case DrawioEvent.TextContent:
                OnTextContent(m);
                break;

            case DrawioEvent.RemoteInvoke:
                OnRemoteInvoke(m);
                break;

            case DrawioEvent.RemoteInvokeResponse:
                OnRemoteInvokeResponse(m);
                break;

            case DrawioEvent.ScrollWheel:
                OnScrollWheel(m);
                break;

            case DrawioEvent.Shortcut:
                OnShortcut(m);
                break;

            case DrawioEvent.Error:
                _log.Warning("draw.io reported an error: {message}", m.ToString());
                OnError(m);
                break;

            case DrawioEvent.SiloDesignMenu:
                Dispatcher.BeginInvoke(new Action(() => DesignMenu.ShowAt(this)));
                break;

            default:
                if (m.Name.StartsWith("silo.", StringComparison.Ordinal)) HandlePluginEvent(m);
                else OnUnknownEvent(m);
                break;
        }

        EventReceived?.Invoke(this, m);
    }

    /// <summary>Common handling of <c>autosave</c> and <c>save</c>: remember the XML, tell the owner the person changed it.</summary>
    private void HandleDocumentEvent(DrawioMessage m)
    {
        if (m.Xml is not { } payload) return;

        _currentXml = payload;
        if (DateTime.UtcNow >= _ignoreChangesUntilUtc)
        {
            _lastUserChangeUtc = DateTime.UtcNow;
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CompleteExport(JsonElement root)
    {
        string format = root.TryGetProperty("format", out var f) ? f.GetString() ?? "" : "";

        string? payload = null;
        if (root.TryGetProperty("xml", out var x) && x.ValueKind == JsonValueKind.String) payload = x.GetString();
        if (payload is null && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String) payload = d.GetString();

        if (_exports.TryRemove(format, out var tcs)) tcs.TrySetResult(payload);
    }

    private async Task<string?> RequestExportAsync(string format, JsonObject? extra, int timeoutMs)
    {
        if (!_editorReady) return null;

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_exports.TryGetValue(format, out var previous)) previous.TrySetResult(null);
        _exports[format] = tcs;

        var msg = extra ?? new JsonObject();
        msg["action"] = "export";
        msg["format"] = format;
        Post(msg);

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (finished != tcs.Task)
        {
            _exports.TryRemove(new KeyValuePair<string, TaskCompletionSource<string?>>(format, tcs));
            return null;
        }

        return await tcs.Task;
    }

    private void PostLoad(string xml, bool merge)
    {
        _ignoreChangesUntilUtc = DateTime.UtcNow.AddMilliseconds(900);

        if (merge && _loadedOnce)
        {
            Post(new JsonObject { ["action"] = DrawioAction.Merge, ["xml"] = xml });
            return;
        }

        var o = Options;
        var load = new DrawioLoadOptions
                   {
                       Xml        = xml,
                       AutoSave   = o.AutoSave,
                       ZoomEvents = true,
                       Title      = Title
                   };

        if (o.FitOnLoad && !_loadedOnce) load.Fit = true;
        else if (_loadedOnce && _lastScale is { } s) load.Scale = s;

        CustomizeLoad(load);
        Post(load.ToJson());
    }

    private void HandleLink(string? href)
    {
        if (string.IsNullOrEmpty(href)) return;

        if (DiagramLink.TryParse(href, out var link))
        {
            if (!DiagramLinks.Dispatch(link, this)) _log.Debug("No handler for diagram link {kind}/{id}", link.Kind, link.Id);
            LinkActivated?.Invoke(this, link);
            return;
        }

        if (Options.OpenExternalLinks && Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        else
            _log.Debug("Ignored diagram link {href}", href);
    }

    // ---- live data ---------------------------------------------------------------------------

    /// <summary>
    ///     Builds the XML to show: the person's diagram, with the generated layer replaced by the source's
    ///     current model and the bindings re-evaluated.
    /// </summary>
    private string Compose(string? xml)
    {
        var doc = DrawioDocument.Parse(xml);

        if (_source is { } src)
        {
            doc.RemoveGenerated();
            doc.AddModel(src.Build(), true, Options.LockGeneratedCells);
        }

        if (Options.EnableBindings) DiagramBindings.Apply(doc);

        return doc.ToXml();
    }

    /// <summary>
    ///     Asks the plugin for the cells carrying bindings, evaluates them, and has the plugin write the values into those
    ///     cells in place. False when the plugin did not answer (the caller falls back to a reload).
    /// </summary>
    private async Task<bool> ApplyBindingsInPlaceAsync()
    {
        var found = await SendPluginAsync("bindingCells", null, 3000);
        if (found?.GetBool("ok") != true || found.Get("result") is not { } result ||
            !result.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
            return false;

        var updates = new JsonArray();
        foreach (var cell in cells.EnumerateArray())
        {
            if (!cell.TryGetProperty("id", out var id) || !cell.TryGetProperty("attributes", out var attributes) ||
                attributes.ValueKind != JsonValueKind.Object)
                continue;

            var bindings = attributes.EnumerateObject()
                                     .Where(a => a.Value.ValueKind == JsonValueKind.String)
                                     .Select(a => (a.Name, a.Value.GetString() ?? ""));
            if (DiagramBindings.Evaluate(id.ToString(), bindings) is { } update) updates.Add(update);
        }

        if (updates.Count == 0) return true;

        var applied = await SendPluginAsync("applyBindings", new JsonObject { ["cells"] = updates }, 3000);
        return applied?.GetBool("ok") == true;
    }

    private void OnLiveDataChanged()
    {
        if (_disposed) return;
        Dispatcher.BeginInvoke(RequestRefresh);
    }

    /// <summary>The generated layer changed: the next refresh must rebuild the diagram, not just re-evaluate bindings.</summary>
    private void OnSourceChanged()
    {
        _sourceDirty = true;
        OnLiveDataChanged();
    }

    private async Task RefreshAsync()
    {
        if (!_editorReady || _refreshing || _disposed) return;
        if (_source is null && !Options.EnableBindings) return;
        if (_source is null && !IsPluginReady && !DiagramBindings.HasBindings(_currentXml)) return;

        // Only binding values changed and the plugin is there: update the cells in place, which keeps the selection,
        // the undo history and the view (and does not need to wait for the person to stop editing).
        if (!_sourceDirty && IsPluginReady && Options.EnableBindings)
        {
            _refreshing = true;
            try
            {
                if (await ApplyBindingsInPlaceAsync()) return;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "In-place binding update failed; reloading the diagram instead");
            }
            finally
            {
                _refreshing = false;
            }
        }

        var o = Options;
        double sinceEdit = (DateTime.UtcNow - _lastUserChangeUtc).TotalMilliseconds;
        if (o.LiveDeferWhileEditingMs > 0 && sinceEdit < o.LiveDeferWhileEditingMs)
        {
            _liveTimer.Interval = TimeSpan.FromMilliseconds(o.LiveDeferWhileEditingMs - sinceEdit + 50);
            _liveTimer.Start();
            return;
        }

        _refreshing = true;
        try
        {
            string? current  = await GetXmlAsync();
            string  baseline = DrawioDocument.Parse(current).ToXml();
            string  next     = Compose(current);
            _sourceDirty = false;
            if (next == baseline) return;

            _currentXml = next;
            PostLoad(next, o.LiveUpdate == DrawioLiveUpdate.Merge);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Live diagram update failed");
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ---- reconfiguration ---------------------------------------------------------------------

    private void OnProfileChanged(object? sender, EventArgs e)
    {
        if (_override is null) Dispatcher.BeginInvoke(ScheduleReconfigure);
    }

    private void ScheduleReconfigure()
    {
        if (_disposed) return;

        _reconfigureTimer.Stop();
        _reconfigureTimer.Start();
    }

    private async Task ReconfigureAsync()
    {
        if (!_webInitialized) return;

        string json = Options.ToJson();
        if (json == _appliedJson) return;

        // Only the host-side (WebView2) options changed: no need to reload the editor.
        var before = _appliedJson is null ? new DrawioOptions() : DrawioOptions.FromJson(_appliedJson);
        bool editorChanged = before.ToQueryString() != Options.ToQueryString() ||
                             before.ToConfig().ToJsonString() != Options.ToConfig().ToJsonString() ||
                             before.EnableSiloPlugin != Options.EnableSiloPlugin ||
                             before.PluginScriptPath != Options.PluginScriptPath ||
                             before.ToPluginOptions().ToJsonString() != Options.ToPluginOptions().ToJsonString();

        if (editorChanged)
        {
            await ReloadEditorAsync();
        }
        else
        {
            _appliedJson = json;
            ApplyHostSettings();
            RequestRefresh();
        }
    }

    // ---- overlay -----------------------------------------------------------------------------

    private void ShowStatus(string text, bool busy, string? buttonText = null, Func<Task>? buttonAction = null)
    {
        _overlayText.Text           = text;
        _overlayBar.Visibility      = busy ? Visibility.Visible : Visibility.Collapsed;
        _overlayButton.Visibility   = buttonText is null ? Visibility.Collapsed : Visibility.Visible;
        _overlayButton.Content      = buttonText;
        _overlayButton.Tag          = buttonAction;
        _overlayButton.Click       -= OnOverlayButtonClick;
        _overlayButton.Click       += OnOverlayButtonClick;
        _overlay.Visibility         = Visibility.Visible;
    }

    private async void OnOverlayButtonClick(object sender, RoutedEventArgs e)
    {
        if (_overlayButton.Tag is Func<Task> action) await action();
    }

    private void HideOverlay()
    {
        _overlay.Visibility = Visibility.Collapsed;
    }
}
