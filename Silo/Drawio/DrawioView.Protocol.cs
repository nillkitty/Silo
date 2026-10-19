using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Silo.Drawio;

/// <summary>
///     The host side of draw.io's embed protocol: one method for every action the host can send (<see cref="DrawioAction" />)
///     and the plumbing that turns the editor's answers back into awaitable results. Nothing here decides what Silo
///     <i>does</i> with an event -- that is <c>DrawioView.Handlers.cs</c>.
/// </summary>
/// <remarks>
///     Call these on the UI thread (WebView2 requires it). Methods that return a <see cref="Task" /> complete when the
///     editor answers or the timeout elapses (then the result is <see langword="null" />); they return
///     <see langword="null" /> straight away if the editor is not ready yet. Fire-and-forget actions return nothing.
/// </remarks>
public sealed partial class DrawioView
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DrawioMessage?>> _pending        = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DrawioMessage?>> _pluginRequests = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DrawioMessage?>> _remoteCalls    = new();

    private long    _requestCounter;
    private string? _pluginScriptId;

    /// <summary>The plugin script last registered (options + source), handed to the host page if the editor frame lacks it.</summary>
    private string? _pluginBootstrap;

    // ---- public state and events -------------------------------------------------------------

    /// <summary>The diagram's title, shown by draw.io (sent with <c>load</c>). Set before loading.</summary>
    public string? Title { get; set; }

    /// <summary>True once the editor has sent <c>init</c> (it is ready to receive <c>load</c>).</summary>
    public bool IsEditorReady => _editorReady;

    /// <summary>True once the Silo plugin has loaded inside the editor and announced itself (<c>silo.ready</c>).</summary>
    public bool IsPluginReady { get; private set; }

    /// <summary>Raised for every message from the editor, after Silo's own handling and the <c>On…</c> stub.</summary>
    public event EventHandler<DrawioMessage>? EventReceived;

    /// <summary>The entries of the "Silo" menu the plugin adds to draw.io's menu bar. Sent when the plugin is ready; call <see cref="PluginSetMenuAsync" /> to change it live.</summary>
    public List<DrawioPluginMenuItem> PluginMenu { get; } =
    [
        new("silo.refresh", "Refresh live data"),
        new("silo.fit", "Fit diagram to window"),
        DrawioPluginMenuItem.Divider,
        new("silo.options", "Diagram options..."),
        new("silo.reset", "Reset and reload editor")
    ];

    /// <summary>Entries the plugin adds to the right-click menu of cells that carry <c>silo-*</c> data (reported as <c>OnSiloContextAction</c>). Empty by default.</summary>
    public List<DrawioPluginMenuItem> PluginContextItems { get; } = [];

    /// <summary>Sections the plugin adds to the shape sidebar. Sent when the plugin is ready; call <see cref="PluginSetPalettesAsync" /> to change them live.</summary>
    public List<DrawioPluginPalette> PluginPalettes { get; } =
    [
        new("silo", "Silo",
            [
                new DrawioPaletteEntry("rounded=1;whiteSpace=wrap;html=1;fillColor=#dae8fc;strokeColor=#6c8ebf;", 140, 60, "Silo item", "Silo item"),
                new DrawioPaletteEntry("shape=cylinder3;whiteSpace=wrap;html=1;boundedLbl=1;backgroundOutline=1;size=15;fillColor=#d5e8d4;strokeColor=#82b366;", 80, 90, "Data", "Data source"),
                new DrawioPaletteEntry("text;html=1;strokeColor=none;fillColor=none;align=center;verticalAlign=middle;whiteSpace=wrap;", 160, 30, "Note", "Note")
            ])
    ];

    // ---- host -> editor: generic -------------------------------------------------------------

    /// <summary>Sends any action, including ones not modelled here.</summary>
    public void SendAction(string action, JsonObject? fields = null)
    {
        var message = fields ?? new JsonObject();
        message["action"] = action;
        Post(message);
    }

    /// <summary>
    ///     <c>configure</c>: the editor's configuration. Silo answers draw.io's own <c>configure</c> event with this
    ///     automatically (<see cref="BuildConfig" />); sending it again later has no effect on a running editor --
    ///     use <see cref="ReloadEditorAsync" /> to apply changed options.
    /// </summary>
    public void SendConfigure(JsonObject? config = null)
    {
        Post(new JsonObject { ["action"] = DrawioAction.Configure, ["config"] = config ?? BuildConfig() });
    }

    // ---- host -> editor: diagram -------------------------------------------------------------

    /// <summary>
    ///     <c>load</c>, with every parameter draw.io accepts. Completes with the editor's <c>load</c> event (diagram size
    ///     info). For the common case of just replacing the diagram use <see cref="LoadXmlAsync" />.
    /// </summary>
    public Task<DrawioMessage?> LoadAsync(DrawioLoadOptions options, int timeoutMs = 15000)
    {
        _ignoreChangesUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
        if (options.Xml is { } xml) _currentXml = xml;

        return RequestAsync(DrawioEvent.Load, options.ToJson(), timeoutMs);
    }

    /// <summary><c>merge</c>: merges the given XML into the open diagram. The answer carries <see cref="DrawioMessage.Error" /> if draw.io rejected it.</summary>
    public Task<DrawioMessage?> MergeAsync(string xml, int timeoutMs = 10000)
    {
        return RequestAsync(DrawioEvent.Merge, new JsonObject { ["action"] = DrawioAction.Merge, ["xml"] = xml }, timeoutMs);
    }

    /// <summary><c>patch</c> (diff sync): applies a patch produced by the editor's <c>autosave</c> events elsewhere. Needs <c>diffSync</c> on <c>load</c>.</summary>
    public Task<DrawioMessage?> PatchAsync(JsonNode patch, string? checksum = null, int timeoutMs = 10000)
    {
        var message = new JsonObject { ["action"] = DrawioAction.Patch, ["patch"] = patch.DeepClone() };
        if (checksum is not null) message["checksum"] = checksum;
        return RequestAsync(DrawioEvent.Patch, message, timeoutMs);
    }

    /// <summary><c>getDiff</c> (diff sync): asks for the changes since the last sync.</summary>
    public Task<DrawioMessage?> GetDiffAsync(int timeoutMs = 10000)
    {
        return RequestAsync(DrawioEvent.GetDiff, new JsonObject { ["action"] = DrawioAction.GetDiff }, timeoutMs);
    }

    /// <summary><c>resetDiff</c> (diff sync): resynchronises after a checksum mismatch, optionally with fresh XML.</summary>
    public Task<DrawioMessage?> ResetDiffAsync(string? xml = null, int timeoutMs = 10000)
    {
        var message = new JsonObject { ["action"] = DrawioAction.ResetDiff };
        if (xml is not null) message["xml"] = xml;
        return RequestAsync(DrawioEvent.ResetDiff, message, timeoutMs);
    }

    /// <summary><c>export</c> with the full set of options (format, scale, border, embedImages, pageId, includeData, ...). Returns the payload.</summary>
    public Task<string?> ExportAsync(DrawioExportOptions options, int timeoutMs = 10000)
    {
        return RequestExportAsync(options.Format, options.ToJson(), timeoutMs);
    }

    /// <summary><c>layout</c>: runs a JSON layout specification (a layout name or an array of custom layouts) on the diagram.</summary>
    public Task<DrawioMessage?> LayoutAsync(JsonNode layouts, int timeoutMs = 15000)
    {
        return RequestAsync(DrawioEvent.Layout, new JsonObject { ["action"] = DrawioAction.Layout, ["layouts"] = layouts.DeepClone() }, timeoutMs);
    }

    /// <summary><c>textContent</c>: all the text in the diagram.</summary>
    public async Task<string?> TextContentAsync(int timeoutMs = 10000)
    {
        var r = await RequestAsync(DrawioEvent.TextContent, new JsonObject { ["action"] = DrawioAction.TextContent }, timeoutMs);
        return r?.Data;
    }

    /// <summary><c>snapshot</c>: asks the editor for a snapshot of its state. draw.io's answer is not documented; it arrives through <see cref="EventReceived" /> / <c>OnUnknownEvent</c>.</summary>
    public void RequestSnapshot()
    {
        SendAction(DrawioAction.Snapshot);
    }

    /// <summary><c>resetEditor</c>: returns the editor to its initial state.</summary>
    public void ResetEditor()
    {
        SendAction(DrawioAction.ResetEditor);
    }

    // ---- host -> editor: view ----------------------------------------------------------------

    /// <summary><c>fit</c>: zooms to fit the diagram in the window.</summary>
    public Task<DrawioMessage?> FitAsync(int border = 16, double maxScale = 1, int timeoutMs = 5000)
    {
        return RequestAsync(DrawioEvent.Fit,
                            new JsonObject { ["action"] = DrawioAction.Fit, ["border"] = border, ["maxScale"] = maxScale }, timeoutMs);
    }

    /// <summary><c>viewbox</c>: shows a region of the diagram.</summary>
    public Task<DrawioMessage?> SetViewboxAsync(double x, double y, double width, double height, int border = 0, int timeoutMs = 5000)
    {
        var box = new JsonObject { ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height, ["border"] = border };
        return RequestAsync(DrawioEvent.Viewbox, new JsonObject { ["action"] = DrawioAction.Viewbox, ["viewbox"] = box }, timeoutMs);
    }

    /// <summary><c>viewport</c>: tells the editor which part of the window it occupies (inline embeds).</summary>
    public void SetViewport(double x, double y, double width, double height)
    {
        var box = new JsonObject { ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height };
        Post(new JsonObject { ["action"] = DrawioAction.Viewport, ["viewport"] = box });
    }

    /// <summary><c>fullscreenChanged</c>: tells the editor the host went (or left) fullscreen.</summary>
    public void SetFullscreenChanged(bool fullscreen)
    {
        Post(new JsonObject { ["action"] = DrawioAction.FullscreenChanged, ["value"] = fullscreen });
    }

    /// <summary><c>invokeAction</c>: runs a named editor action (<c>zoomIn</c>, <c>undo</c>, ... or one a plugin added). Unknown names are ignored.</summary>
    public void InvokeAction(string actionName)
    {
        Post(new JsonObject { ["action"] = DrawioAction.InvokeAction, ["actionName"] = actionName });
    }

    // ---- host -> editor: UI ------------------------------------------------------------------

    /// <summary><c>dialog</c>: shows a message dialog inside the editor.</summary>
    public void ShowDialog(string title, string message, string button = "OK", bool modified = false)
    {
        Post(new JsonObject
             {
                 ["action"]   = DrawioAction.Dialog,
                 ["title"]    = title,
                 ["message"]  = message,
                 ["button"]   = button,
                 ["modified"] = modified
             });
    }

    /// <summary><c>prompt</c>: asks for a line of text inside the editor. Returns the entered text, or null if it was cancelled.</summary>
    public async Task<string?> PromptAsync(string title, string ok = "OK", string? defaultValue = null)
    {
        var message = new JsonObject { ["action"] = DrawioAction.Prompt, ["title"] = title, ["ok"] = ok };
        if (defaultValue is not null) message["defaultValue"] = defaultValue;

        var r = await RequestAsync(DrawioEvent.Prompt, message, Timeout.Infinite);
        return r is { Name: DrawioEvent.Prompt } ? r.Value : null;
    }

    /// <summary><c>template</c>: opens draw.io's template picker. The answer carries the chosen template (<c>xml</c>, <c>name</c>, ...).</summary>
    public Task<DrawioMessage?> TemplateAsync(DrawioTemplateOptions? options = null)
    {
        return RequestAsync(DrawioEvent.Template, (options ?? new DrawioTemplateOptions()).ToJson(), Timeout.Infinite);
    }

    /// <summary><c>draft</c>: offers a recovered draft; the answer's <c>result</c> is Edit, Discard or ignore.</summary>
    public Task<DrawioMessage?> DraftAsync(string xml, string name, string? editKey = null, string? discardKey = null, bool? ignore = null)
    {
        var message = new JsonObject { ["action"] = DrawioAction.Draft, ["xml"] = xml, ["name"] = name };
        if (editKey is not null) message["editKey"]       = editKey;
        if (discardKey is not null) message["discardKey"] = discardKey;
        if (ignore is { } i) message["ignore"]            = i;
        return RequestAsync(DrawioEvent.Draft, message, Timeout.Infinite);
    }

    /// <summary><c>status</c>: sets the editor's status text and/or its modified (unsaved) marker. Call with <c>modified: false</c> after saving.</summary>
    public void SetStatus(string? message, bool? modified = null)
    {
        var m = new JsonObject { ["action"] = DrawioAction.Status };
        if (message is not null) m["message"] = message;
        if (modified is { } mod) m["modified"] = mod;
        Post(m);
    }

    /// <summary><c>spinner</c>: shows or hides the editor's busy spinner.</summary>
    public void SetSpinner(bool show, string? message = null)
    {
        var m = new JsonObject { ["action"] = DrawioAction.Spinner, ["show"] = show };
        if (message is not null) m["message"] = message;
        Post(m);
    }

    // ---- host -> editor: remote invocation ---------------------------------------------------

    /// <summary><c>remoteInvokeReady</c>: tells the editor Silo can receive <c>remoteInvoke</c> calls from it.</summary>
    public void SendRemoteInvokeReady()
    {
        SendAction(DrawioAction.RemoteInvokeReady);
    }

    /// <summary>
    ///     <c>remoteInvoke</c>: calls a function inside the editor and returns its answer (<c>remoteInvokeResponse</c>).
    ///     VERIFY against the pinned draw.io release: the request field is spelled <c>funtionName</c> in draw.io, and the
    ///     correlation uses <c>msgMarkers</c>; only functions on the editor's whitelist can be called.
    /// </summary>
    public async Task<DrawioMessage?> RemoteInvokeAsync(string function, JsonArray? args = null, int timeoutMs = 10000)
    {
        if (!_editorReady) return null;

        string id  = "c" + Interlocked.Increment(ref _requestCounter);
        var    tcs = new TaskCompletionSource<DrawioMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _remoteCalls[id] = tcs;

        Post(new JsonObject
             {
                 ["action"]       = DrawioAction.RemoteInvoke,
                 ["funtionName"]  = function,
                 ["functionArgs"] = args?.DeepClone() ?? new JsonArray(),
                 ["msgMarkers"]   = new JsonObject { ["callbackId"] = id }
             });

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (finished != tcs.Task)
        {
            _remoteCalls.TryRemove(id, out _);
            return null;
        }

        return await tcs.Task;
    }

    /// <summary><c>remoteInvokeResponse</c>: answers a <c>remoteInvoke</c> the editor sent to Silo (see <c>OnRemoteInvoke</c>).</summary>
    public void SendRemoteInvokeResponse(JsonNode? response, JsonNode? msgMarkers = null, string? error = null)
    {
        var m = new JsonObject { ["action"] = DrawioAction.RemoteInvokeResponse, ["resp"] = response?.DeepClone() };
        if (msgMarkers is not null) m["msgMarkers"] = msgMarkers.DeepClone();
        if (error is not null) m["error"]           = error;
        Post(m);
    }

    // ---- host -> Silo plugin -----------------------------------------------------------------

    /// <summary>
    ///     Sends an operation to the Silo plugin inside the editor and awaits its <c>silo.result</c>. Operations are the
    ///     <c>op</c> cases of <c>SiloPlugin.receive</c> in <c>silo-plugin.js</c>.
    /// </summary>
    public async Task<DrawioMessage?> SendPluginAsync(string op, JsonObject? args = null, int timeoutMs = 5000)
    {
        if (!IsPluginReady) return null;

        string id  = "p" + Interlocked.Increment(ref _requestCounter);
        var    tcs = new TaskCompletionSource<DrawioMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pluginRequests[id] = tcs;

        var message = args ?? new JsonObject();
        message["siloPlugin"] = true; // makes the host page hand it to the plugin instead of to draw.io
        message["op"]         = op;
        message["requestId"]  = id;
        Post(message);

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (finished != tcs.Task)
        {
            _pluginRequests.TryRemove(id, out _);
            return null;
        }

        return await tcs.Task;
    }

    /// <summary>Replaces the entries of the Silo menu.</summary>
    public Task<DrawioMessage?> PluginSetMenuAsync(IEnumerable<DrawioPluginMenuItem> items)
    {
        var array = new JsonArray();
        foreach (var i in items)
            array.Add(new JsonObject { ["id"] = i.Id, ["label"] = i.Label, ["shortcut"] = i.Shortcut, ["separator"] = i.Separator });

        return SendPluginAsync("setMenu", new JsonObject { ["items"] = array });
    }

    /// <summary>Replaces the entries Silo adds to the cell context menu.</summary>
    public Task<DrawioMessage?> PluginSetContextItemsAsync(IEnumerable<DrawioPluginMenuItem> items)
    {
        var array = new JsonArray();
        foreach (var i in items.Where(x => !x.Separator))
            array.Add(new JsonObject { ["id"] = i.Id, ["label"] = i.Label });

        return SendPluginAsync("setContextItems", new JsonObject { ["items"] = array });
    }

    /// <summary>Replaces the Silo sections of the shape sidebar.</summary>
    public Task<DrawioMessage?> PluginSetPalettesAsync(IEnumerable<DrawioPluginPalette> palettes)
    {
        var array = new JsonArray();
        foreach (var p in palettes)
        {
            var entries = new JsonArray();
            foreach (var e in p.Entries)
                entries.Add(new JsonObject { ["style"] = e.Style, ["width"] = e.Width, ["height"] = e.Height, ["value"] = e.Value, ["title"] = e.Title });

            array.Add(new JsonObject { ["id"] = p.Id, ["title"] = p.Title, ["expanded"] = p.Expanded, ["entries"] = entries });
        }

        return SendPluginAsync("setPalettes", new JsonObject { ["palettes"] = array });
    }

    /// <summary>
    ///     Registers a custom shape (draw.io stencil XML: <c>&lt;shape name= w= h=&gt;...</c>). Use it in a cell style as
    ///     <c>shape=&lt;name&gt;</c>; draw.io's own stencil names look like <c>mxgraph.silo.node</c> (lower case).
    /// </summary>
    public Task<DrawioMessage?> PluginAddStencilAsync(string name, string stencilXml)
    {
        return SendPluginAsync("addStencil", new JsonObject { ["name"] = name, ["xml"] = stencilXml });
    }

    /// <summary>
    ///     Gives a built-in shape typed Format-panel properties (draw.io's <c>customProperties</c>), e.g.
    ///     <c>[{"name":"size","dispName":"Size","type":"float","defVal":20}]</c>.
    /// </summary>
    public Task<DrawioMessage?> PluginSetCustomPropertiesAsync(string shapeName, JsonArray properties)
    {
        return SendPluginAsync("customProperties", new JsonObject { ["shape"] = shapeName, ["properties"] = properties.DeepClone() });
    }

    /// <summary>Selects a cell by id and scrolls it into view.</summary>
    public Task<DrawioMessage?> PluginSelectCellAsync(string cellId)
    {
        return SendPluginAsync("selectCell", new JsonObject { ["id"] = cellId });
    }

    /// <summary>Scrolls a cell into view without selecting it.</summary>
    public Task<DrawioMessage?> PluginScrollToCellAsync(string cellId)
    {
        return SendPluginAsync("scrollToCell", new JsonObject { ["id"] = cellId });
    }

    /// <summary>Sets (or, with a null value, removes) a custom attribute on a cell -- what Edit Data shows.</summary>
    public Task<DrawioMessage?> PluginSetCellAttributeAsync(string cellId, string key, string? value)
    {
        return SendPluginAsync("setCellAttribute", new JsonObject { ["id"] = cellId, ["key"] = key, ["value"] = value });
    }

    /// <summary>The current selection: <c>result.cells[]</c> in the answer, each with id, label, style and attributes.</summary>
    public Task<DrawioMessage?> PluginGetSelectionAsync()
    {
        return SendPluginAsync("getSelection");
    }

    // ---- editor -> host: plumbing ------------------------------------------------------------

    /// <summary>The configuration object sent in reply to the editor's <c>configure</c> event: the options, then <see cref="CustomizeConfig" />.</summary>
    private JsonObject BuildConfig()
    {
        var config = Options.ToConfig();
        CustomizeConfig(config);
        return config;
    }

    /// <summary>Sends <paramref name="message" /> and waits for the editor's <paramref name="eventName" /> answer.</summary>
    private async Task<DrawioMessage?> RequestAsync(string eventName, JsonObject message, int timeoutMs)
    {
        if (!_editorReady) return null;

        var tcs = new TaskCompletionSource<DrawioMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_pending.TryGetValue(eventName, out var previous)) previous.TrySetResult(null);
        _pending[eventName] = tcs;

        Post(message);

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (finished != tcs.Task)
        {
            _pending.TryRemove(new KeyValuePair<string, TaskCompletionSource<DrawioMessage?>>(eventName, tcs));
            return null;
        }

        return await tcs.Task;
    }

    /// <summary>Completes whatever is awaiting this event (called first for every message).</summary>
    private void CompletePending(DrawioMessage m)
    {
        if (m.Name == DrawioEvent.RemoteInvokeResponse)
        {
            if (RemoteCallId(m) is { } id && _remoteCalls.TryRemove(id, out var call)) call.TrySetResult(m);
            return;
        }

        // A cancelled prompt answers the same request as an accepted one.
        string key = m.Name == DrawioEvent.PromptCancel ? DrawioEvent.Prompt : m.Name;
        if (_pending.TryRemove(key, out var tcs)) tcs.TrySetResult(m);
    }

    private static string? RemoteCallId(DrawioMessage m)
    {
        if (m.Get("msgMarkers") is { ValueKind: JsonValueKind.Object } markers && markers.TryGetProperty("callbackId", out var id))
            return id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();

        return m.GetString("callbackId");
    }

    private void CompleteAllPending()
    {
        foreach (var tcs in _pending.Values) tcs.TrySetResult(null);
        foreach (var tcs in _pluginRequests.Values) tcs.TrySetResult(null);
        foreach (var tcs in _remoteCalls.Values) tcs.TrySetResult(null);
        _pending.Clear();
        _pluginRequests.Clear();
        _remoteCalls.Clear();
    }

    // ---- the Silo plugin ---------------------------------------------------------------------

    /// <summary>
    ///     (Re)registers the plugin script to run in every document the web view creates, before the page's own scripts. The
    ///     plugin waits for draw.io's <c>Draw.loadPlugin</c> and installs itself -- see <c>Drawio/Plugin/silo-plugin.js</c>.
    /// </summary>
    private async Task RegisterPluginAsync()
    {
        IsPluginReady = false;
        if (_web.CoreWebView2 is not { } core) return;

        if (_pluginScriptId is not null)
        {
            core.RemoveScriptToExecuteOnDocumentCreated(_pluginScriptId);
            _pluginScriptId = null;
        }

        _pluginBootstrap = null;
        if (!Options.EnableSiloPlugin) return;

        try
        {
            var options = Options.ToPluginOptions();
            CustomizePluginOptions(options);
            _pluginBootstrap = DrawioRuntime.BuildPluginBootstrap(options, Options.PluginScriptPath);
            _pluginScriptId  = await core.AddScriptToExecuteOnDocumentCreatedAsync(_pluginBootstrap);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not register the Silo plugin");
        }
    }

    /// <summary>Handles the plugin's own events (<see cref="SiloPluginEvent" />): result bookkeeping first, then the stub.</summary>
    private void HandlePluginEvent(DrawioMessage m)
    {
        switch (m.Name)
        {
            case SiloPluginEvent.Ready:
                IsPluginReady = true;
                _ = PluginSetMenuAsync(PluginMenu);
                _ = PluginSetPalettesAsync(PluginPalettes);
                _ = PluginSetContextItemsAsync(PluginContextItems);
                OnSiloPluginReady(m);
                break;

            case SiloPluginEvent.Result:
                if (m.GetString("requestId") is { } id && _pluginRequests.TryRemove(id, out var tcs)) tcs.TrySetResult(m);
                break;

            case SiloPluginEvent.Menu:
                OnSiloMenuCommand(m.GetString("id") ?? "", m);
                break;

            case SiloPluginEvent.ContextAction:
                OnSiloContextAction(m.GetString("id") ?? "", m);
                break;

            case SiloPluginEvent.Selection:
                OnSiloSelection(m);
                break;

            case SiloPluginEvent.Click:
                OnSiloClick(m);
                break;

            case SiloPluginEvent.DoubleClick:
                OnSiloDoubleClick(m);
                break;

            case SiloPluginEvent.Log:
                _log.Debug("[draw.io plugin] {message}", m.GetString("message"));
                break;

            case SiloPluginEvent.NeedPlugin:
                // The document-created script did not reach the editor iframe: the host page injects it instead.
                if (_pluginBootstrap is not null) Post(new JsonObject { ["siloBootstrap"] = _pluginBootstrap });
                break;

            case SiloPluginEvent.Error:
                _log.Warning("[draw.io plugin] {section}: {message}", m.GetString("section"), m.GetString("message"));
                OnSiloPluginError(m);
                break;

            default:
                OnUnknownEvent(m);
                break;
        }
    }
}
