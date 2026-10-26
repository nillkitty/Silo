using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Silo.Labs.Web;

namespace Silo.Labs;

// The Web Lab as a model (see LabModel.cs):
//   subject  a page (its address)
//   parts    the page; the frames in it (nested as they are); the DOM node that is selected
//   aspects  DOM, Console, Network (panes); Page, Frame (commands); Node (the Style pane, and commands on the selected node)
//   tools    Script (run JavaScript in the page), Zoom
public sealed partial class WebLabSession
{
    private const string SubjectId = "page";

    private static readonly LabPartType PageType = new("page", "Page", "Link", "The document the browser shows");

    private static readonly LabPartType FrameType = new("frame", "Frame", "Window", "A document inside the page (an iframe)",
                                                        [new("blank", "Blank frame", "about:blank", "An empty frame")],
                                                        [new LabTemplate("iframe", "Frame of an address", "{{address}}", [new("address", "Address of the frame", "https://example.com/")], "A frame, put at the end of the page")], "page");

    private static readonly LabPartType NodeType = new("domnode", "DOM node", "Nodes", "The element selected in the DOM tree",
                                                       [new("box", "Box", "<div></div>", "An empty div"), new("note", "Note", "<p>Note</p>", "A paragraph"), new("rule", "Line", "<hr>", "A horizontal line")],
                                                       [new LabTemplate("element", "Element", "<{{tag}}>{{text}}</{{tag}}>", [new("tag", "Tag", "div", ["div", "p", "span", "section", "button", "h1", "h2", "ul"]), new("text", "What is in it", "Hello")], "An element, put at the end of the page")], "page");

    /// <summary>One frame of the page, as the browser reports it.</summary>
    internal sealed record FrameInfo(string Id, string Name, string Url, IReadOnlyList<FrameInfo> Children);

    private DomInspector? _dom;
    private FrameInfo?    _frames;

    protected override string SubjectNoun => "page";

    // ---- subject: prefabs and templates ---------------------------------------------------------------

    protected override IEnumerable<LabSubjectType> DefineSubjectTypes()
    {
        var prefabs = new List<LabPrefab>
                      {
                          new("blank", "Blank page", "about:blank", "An empty page", "Link"),
                          new("silo", "Silo on GitHub", "https://github.com/nillkitty/Silo", "Where Silo lives", "Link", "Sites"),
                          new("drawio", "draw.io", "https://app.diagrams.net/", "The diagram editor", "Link", "Sites"),
                          new("example", "Example page", "https://example.com/", "A page that is always there", "Link", "Sites")
                      };
        var templates = new List<LabTemplate>
                        {
                            new("home", "Home address", "{{address}}", [new("address", "Address", string.Empty, Bind: _ => LabOf.Parameters[WebLabParameters.HomeUrl].Text)], "The Lab's home address (a parameter)", "Link"),
                            new("address", "Address", "https://{{address}}", [new("address", "Address", "example.com")], "A web address", "Link"),
                            new("search", "Search", "https://duckduckgo.com/?q={{query}}", [new("query", "What to look for", "silo")], "A web search", "Search"),
                            new("local", "Local server", "http://localhost:{{port}}/{{path}}", [new("port", "Port", "8080"), new("path", "Path", string.Empty)], "A server on this computer", "Link")
                        };
        yield return new LabSubjectType(SubjectId, "Web page", "Link", "A page, shown in a browser of its own, and what it does", prefabs, templates);
    }

    // ---- parts: the page, its frames, the selected node --------------------------------------------------

    protected override IEnumerable<LabPartType> DefinePartTypes() => [PageType, FrameType, NodeType];

    protected override IEnumerable<LabPart> BuildParts()
    {
        string address = _current?.AbsoluteUri ?? _pending?.AbsoluteUri ?? "about:blank";
        var page = new LabPart(PageType, "page", string.IsNullOrWhiteSpace(_pageTitle) ? LabUi.Shorten(address, 40) : _pageTitle!, _current, LabUi.Shorten(address, 60));
        if (_frames is { } root)
            foreach (var child in root.Children)
                page.Add(FramePart(child));

        if (_dom?.Selected is { NodeId: not 0 } node) page.Add(new LabPart(NodeType, "node", node.Label, node, node.Kind));

        yield return page;
    }

    private static LabPart FramePart(FrameInfo frame)
    {
        var part = new LabPart(FrameType, "f:" + frame.Id, frame.Name.Length > 0 ? frame.Name : LabUi.Shorten(frame.Url, 40), frame, LabUi.Shorten(frame.Url, 60));
        foreach (var child in frame.Children) part.Add(FramePart(child));
        return part;
    }

    private static FrameInfo ReadFrame(JObject tree)
    {
        var frame    = tree["frame"] as JObject;
        var children = (tree["childFrames"] as JArray)?.OfType<JObject>().Select(ReadFrame).ToList() ?? [];
        return new FrameInfo((string?)frame?["id"] ?? string.Empty, (string?)frame?["name"] ?? string.Empty, (string?)frame?["url"] ?? string.Empty, children);
    }

    /// <summary>Asks the browser which frames the page has (they are not announced: after a page loads, and when asked).</summary>
    private async Task RefreshFramesAsync()
    {
        if (_dev is null) return;

        var response = await _dev.TryCallAsync("Page.getFrameTree");
        if (response?["frameTree"] is not JObject tree) return;

        _frames = ReadFrame(tree);
        _view?.Dispatcher.Invoke(RaisePartsChanged);
    }

    /// <summary>A page finished loading: its frames are read, and read again a moment later for the ones that are added by its scripts.</summary>
    private async Task ReadFramesAfterLoadAsync()
    {
        await RefreshFramesAsync();
        await Task.Delay(1500);
        await RefreshFramesAsync();
    }

    /// <summary>The DOM tree's selection changed: the node is the page's last part and the active one.</summary>
    private void OnDomSelectionChanged()
    {
        RaisePartsChanged();
        if (_dom?.Selected is { NodeId: not 0 }) SetActivePart("node");
        else if (ActivePart is null) SetActivePart("page");
    }

    private FrameInfo? ActiveFrame => ActivePart?.Tag as FrameInfo;

    private DomNode? ActiveNode => ActivePart?.Tag as DomNode;

    // ---- adding parts: put something into the page ----------------------------------------------------------

    protected override async Task OnAddPartAsync(LabPartType type, LabPart? parent, string content)
    {
        if (_dev is null) throw new InvalidOperationException("The browser is not up yet.");

        string script = type.Id switch
                        {
                            "frame"   => $"(function(){{var f=document.createElement('iframe');f.src={JsonConvert.SerializeObject(content)};f.style.cssText='width:100%;height:240px;border:1px solid #888';(document.body||document.documentElement).appendChild(f);return 'ok';}})()",
                            "domnode" => $"(function(){{(document.body||document.documentElement).insertAdjacentHTML('beforeend',{JsonConvert.SerializeObject(content)});return 'ok';}})()",
                            _         => throw new InvalidOperationException($"{type.Name} cannot be added to a page.")
                        };
        var response = await _dev.CallAsync("Runtime.evaluate", new { expression = script, returnByValue = true });
        if (response["exceptionDetails"] is JObject problem) throw new InvalidOperationException((string?)problem["text"] ?? "The page refused it.");

        Say(type.Id == "frame" ? "Added the frame to the end of the page" : "Added the element to the end of the page");
        await ReadFramesAfterLoadAsync();
        if (_dom is not null) await _dom.RefreshAsync();
    }

    // ---- aspects, tools and commands ------------------------------------------------------------------------

    protected override IEnumerable<LabAspect> DefineAspects()
    {
        var page = LabOwner.OfSubjectType(SubjectId);
        yield return new LabAspect("dom", "DOM", page)
                     {
                         IconKey     = "Nodes",
                         Description = "The page's elements, as a tree",
                         Commands    = [ProvidedCommand.From("dom-refresh", "_Refresh DOM Tree", () => { if (_dom is not null) _ = RunAsync(_dom.RefreshAsync); }, "Refresh", () => _ready && _dom is not null, "Read the page's elements again")]
                     };
        yield return new LabAspect("console", "Console", page) { IconKey = "Terminal", Description = "What the page says in its console, and a line to run JavaScript in it" };
        yield return new LabAspect("network", "Network", page) { IconKey = "Network", Description = "The requests the page makes, as a waterfall" };

        yield return new LabAspect("page", "Page", LabOwner.OfPartType(PageType.Id))
                     {
                         IconKey     = "Link",
                         Description = "The page itself",
                         Commands =
                         [
                             ProvidedCommand.From("page-frames", "Read _Frames Again", () => _ = RunAsync(RefreshFramesAsync), "Refresh", () => _ready && _dev is not null, "Ask the browser which frames the page has"),
                             ProvidedCommand.From("page-copy-title", "Copy _Title", () => CopyOf(_pageTitle, "title"), "TbCopy", () => !string.IsNullOrWhiteSpace(_pageTitle), "Put the page's title on the clipboard")
                         ]
                     };

        yield return new LabAspect("frame", "Frame", LabOwner.OfPartType(FrameType.Id))
                     {
                         IconKey     = "Window",
                         Description = "The active frame",
                         Commands =
                         [
                             ProvidedCommand.From("frame-open", "_Open Frame as the Page", OpenFrame, "Launch", () => ActiveFrame is { Url: { Length: > 0 } u } && u != "about:blank" && Uri.TryCreate(u, UriKind.Absolute, out _), "Go to the frame's address in this window"),
                             ProvidedCommand.From("frame-copy", "Copy _Address", () => CopyOf(ActiveFrame?.Url, "address"), "TbCopy", () => ActiveFrame is { Url.Length: > 0 }, "Put the frame's address on the clipboard")
                         ]
                     };

        yield return new LabAspect("node", "DOM node", LabOwner.OfPartType(NodeType.Id))
                     {
                         IconKey     = "Nodes",
                         Description = "The selected element: its style, and what can be done to it",
                         Commands =
                         [
                             ProvidedCommand.From("node-copy-html", "Copy _HTML", () => _ = RunAsync(CopyNodeHtmlAsync), "TbCopy", () => _ready && ActiveNode is { NodeId: not 0 }, "Put the element, with what is in it, on the clipboard"),
                             ProvidedCommand.From("node-scroll", "_Scroll Into View", () => _ = RunAsync(ScrollToNodeAsync), "Search", () => _ready && ActiveNode is { NodeId: not 0 }, "Show the element in the page"),
                             ProvidedCommand.From("node-remove", "_Remove from Page", () => _ = RunAsync(RemoveNodeAsync), "TbDelete", () => _ready && ActiveNode is { NodeId: not 0 }, "Take the element out of the page (until it is loaded again)") with { IsDestructive = true }
                         ]
                     };
    }

    private void CopyOf(string? text, string what)
    {
        if (string.IsNullOrEmpty(text)) return;

        LabUi.Copy(text);
        Say($"Copied the {what}");
    }

    private void OpenFrame()
    {
        if (ActiveFrame is { } frame && Uri.TryCreate(frame.Url, UriKind.Absolute, out var uri)) _ = RunAsync(() => OpenValueAsync(uri));
    }

    private async Task CopyNodeHtmlAsync()
    {
        if (_dev is null || ActiveNode is not { NodeId: not 0 } node) return;

        var outer = await _dev.TryCallAsync("DOM.getOuterHTML", new { nodeId = node.NodeId });
        string html = (string?)outer?["outerHTML"] ?? string.Empty;
        _view?.Dispatcher.Invoke(() => CopyOf(html, "element"));
    }

    private async Task ScrollToNodeAsync()
    {
        if (_dev is null || ActiveNode is not { NodeId: not 0 } node) return;

        await _dev.TryCallAsync("DOM.scrollIntoViewIfNeeded", new { nodeId = node.NodeId });
    }

    private async Task RemoveNodeAsync()
    {
        if (_dev is null || ActiveNode is not { NodeId: not 0 } node) return;

        await _dev.TryCallAsync("DOM.removeNode", new { nodeId = node.NodeId });
        if (_dom is not null) await _dom.RefreshAsync();
        Say("Took the element out of the page");
    }

    protected override IEnumerable<LabTool> DefineTools()
    {
        var page = LabOwner.OfSubjectType(SubjectId);
        yield return new LabTool("script", "_Script", page)
                     {
                         IconKey     = "Code",
                         Description = "Run JavaScript in the page and see what it gives",
                         Parameters  = LabParameterSet.For(LabOf.Id, "tool:script", "Script", [new LabParameter("code", "Code", LabParameterKind.Lines, null, "What to run in the page (the last expression is what it gives).", hint: "document.title")]),
                         Commands    = [ProvidedCommand.From("script-run", "_Run in Page", () => _ = RunAsync(RunScriptAsync), "Play", () => _ready && _dev is not null && ToolText("script", "code").Length > 0, "Run the code in the page")]
                     };
        yield return new LabTool("zoom", "_Zoom", page)
                     {
                         IconKey     = "ZoomIn",
                         Description = "Make the page bigger or smaller",
                         Parameters  = LabParameterSet.For(LabOf.Id, "tool:zoom", "Zoom", [new LabParameter("percent", "Percent", LabParameterKind.Text, "100", "How big the page is drawn (25 to 500).", hint: "100")]),
                         Commands    = [ProvidedCommand.From("zoom-apply", "_Zoom", () => Say(Zoom(ToolText("zoom", "percent"))), "ZoomIn", () => _ready, "Zoom the page to that percent")]
                     };
    }

    private string ToolText(string tool, string name)
    {
        var set = Tools.FirstOrDefault(t => t.Id == tool)?.Parameters;
        return set is null ? string.Empty : (set[name].Text ?? string.Empty).Trim();
    }

    private async Task RunScriptAsync()
    {
        string code = string.Join('\n', Tools.FirstOrDefault(t => t.Id == "script")?.Parameters?["code"].Lines ?? []);
        if (string.IsNullOrWhiteSpace(code)) return;

        string result = await _console.EvaluateAsync(code);
        Say(LabUi.Shorten(result.Replace('\n', ' '), 200));
    }
}
