using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Silo.Drawio;

// The draw.io embed protocol (proto=json), in one place. Everything below was checked against the draw.io
// embed-mode documentation (https://www.drawio.com/doc/faq/embed-mode) except where a comment says VERIFY:
// those field names come from reading the draw.io source and should be re-checked against the pinned release
// (DrawioRuntimeSettings.Version) before something depends on them.

/// <summary>Host -> editor <c>action</c> names.</summary>
public static class DrawioAction
{
    public const string Configure          = "configure";
    public const string Dialog             = "dialog";
    public const string Draft              = "draft";
    public const string Export             = "export";
    public const string Fit                = "fit";
    public const string FullscreenChanged  = "fullscreenChanged";
    public const string GetDiff            = "getDiff";
    public const string InvokeAction       = "invokeAction";
    public const string Layout             = "layout";
    public const string Load               = "load";
    public const string Merge              = "merge";
    public const string Patch              = "patch";
    public const string Prompt             = "prompt";
    public const string RemoteInvoke       = "remoteInvoke";
    public const string RemoteInvokeReady  = "remoteInvokeReady";
    public const string RemoteInvokeResponse = "remoteInvokeResponse";
    public const string ResetDiff          = "resetDiff";
    public const string ResetEditor        = "resetEditor";
    public const string Snapshot           = "snapshot";
    public const string Spinner            = "spinner";
    public const string Status             = "status";
    public const string Template           = "template";
    public const string TextContent        = "textContent";
    public const string Viewbox            = "viewbox";
    public const string Viewport           = "viewport";
}

/// <summary>Editor -> host <c>event</c> names.</summary>
public static class DrawioEvent
{
    public const string Autosave             = "autosave";
    public const string Configure            = "configure";
    public const string Draft                = "draft";
    public const string Error                = "error";
    public const string Exit                 = "exit";
    public const string Export               = "export";
    public const string Fit                  = "fit";
    public const string GetDiff              = "getDiff";
    public const string Init                 = "init";
    public const string Layout               = "layout";
    public const string Load                 = "load";
    public const string Merge                = "merge";
    public const string OpenLink             = "openLink";
    public const string Patch                = "patch";
    public const string Prompt               = "prompt";
    public const string PromptCancel         = "prompt-cancel";
    public const string Ready                = "ready";
    public const string RemoteInvoke         = "remoteInvoke";
    public const string RemoteInvokeResponse = "remoteInvokeResponse";
    public const string ResetDiff            = "resetDiff";
    public const string Resize               = "resize";
    public const string Save                 = "save";
    public const string ScrollWheel          = "scrollWheel";
    public const string Shortcut             = "shortcut";
    public const string Template             = "template";
    public const string TextContent          = "textContent";
    public const string Viewbox              = "viewbox";
    public const string Zoom                 = "zoom";

    /// <summary>Raised by Silo's own host page (Ctrl+right-click on the editor), not by draw.io.</summary>
    public const string SiloDesignMenu = "siloDesignMenu";
}

/// <summary>Events posted by the Silo plugin running inside the editor (see <c>Drawio/Plugin/silo-plugin.js</c>).</summary>
public static class SiloPluginEvent
{
    public const string ContextAction = "silo.contextAction";
    public const string Click         = "silo.click";
    public const string DoubleClick   = "silo.dblclick";
    public const string Error         = "silo.error";
    public const string Log           = "silo.log";
    public const string Menu          = "silo.menu";
    public const string Ready         = "silo.ready";
    public const string Result        = "silo.result";
    public const string Selection     = "silo.selection";
}

/// <summary>
///     One message from the editor: its event name and the raw JSON payload, with typed accessors for
///     the fields draw.io documents. The payload is a detached copy, safe to keep.
/// </summary>
public sealed class DrawioMessage
{
    public DrawioMessage(string name, JsonElement root)
    {
        Name = name;
        Root = root;
    }

    /// <summary>The event name, e.g. <c>save</c> (see <see cref="DrawioEvent" />); empty if the message had none.</summary>
    public string Name { get; }

    /// <summary>The whole payload.</summary>
    public JsonElement Root { get; }

    // ---- generic accessors -------------------------------------------------------------------

    public JsonElement? Get(string property)
    {
        return Root.ValueKind == JsonValueKind.Object && Root.TryGetProperty(property, out var v) ? v : null;
    }

    public string? GetString(string property)
    {
        return Get(property) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    }

    public double? GetNumber(string property)
    {
        return Get(property) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;
    }

    /// <summary>Accepts JSON booleans and the 1/0 draw.io sometimes uses.</summary>
    public bool? GetBool(string property)
    {
        if (Get(property) is not { } v) return null;

        return v.ValueKind switch
        {
            JsonValueKind.True   => true,
            JsonValueKind.False  => false,
            JsonValueKind.Number => v.GetDouble() != 0,
            _                    => null
        };
    }

    public bool Has(string property)
    {
        return Get(property) is not null;
    }

    // ---- fields draw.io documents ------------------------------------------------------------

    /// <summary>Diagram XML (<c>save</c>, <c>autosave</c>, <c>export</c> of xml, <c>merge</c>/<c>template</c> results).</summary>
    public string? Xml => GetString("xml");

    /// <summary>Exported data (data URI, svg, ...).</summary>
    public string? Data => GetString("data");

    public string? Format => GetString("format");

    /// <summary><c>save</c>/<c>exit</c>: Save and Exit was used / the diagram had unsaved changes.</summary>
    public bool Exit => GetBool("exit") ?? false;

    public bool Modified => GetBool("modified") ?? false;

    public string? Href => GetString("href");

    public string? Target => GetString("target");

    public double? Scale => GetNumber("scale");

    /// <summary><c>prompt</c> result.</summary>
    public string? Value => GetString("value");

    /// <summary><c>shortcut</c>: the key chord that matched <c>passThroughKeys</c>.</summary>
    public string? Command => GetString("command");

    /// <summary>Set when draw.io rejected something (e.g. a <c>merge</c> it could not apply).</summary>
    public string? Error => GetString("error");

    public string? Checksum => GetString("checksum");

    public override string ToString()
    {
        return Root.GetRawText();
    }
}

/// <summary>
///     Everything the <c>load</c> action accepts. Leave a property <see langword="null" /> to leave draw.io's own
///     behaviour for it; <see cref="Extra" /> carries any field that is not modelled here.
/// </summary>
public sealed class DrawioLoadOptions
{
    /// <summary>Diagram XML (also SVG/PNG with embedded XML, data URIs, Visio, Lucidchart/Gliffy JSON).</summary>
    public string? Xml { get; set; }

    /// <summary>Alternative to <see cref="Xml" />: a descriptor such as <c>{"format":"csv","data":...}</c> or mermaid.</summary>
    public JsonNode? Descriptor { get; set; }

    public bool?   AutoSave      { get; set; }
    public bool?   Modified      { get; set; }
    public bool?   SaveAndExit   { get; set; }
    public bool?   NoSaveBtn     { get; set; }
    public bool?   NoExitBtn     { get; set; }
    public string? Title         { get; set; }

    /// <summary>Semicolon-separated library keys for this diagram.</summary>
    public string? Libs { get; set; }

    public bool?   Dark          { get; set; }
    public string? Theme         { get; set; }
    public bool?   Rough         { get; set; }
    public bool?   ToSketch      { get; set; }
    public int?    Border        { get; set; }
    public string? Background    { get; set; }
    public JsonNode? Viewport    { get; set; }
    public JsonNode? Rect        { get; set; }
    public int?    MinWidth      { get; set; }
    public int?    MinHeight     { get; set; }
    public JsonNode? Viewbox     { get; set; }
    public double? Scale         { get; set; }
    public double? ScaleBorder   { get; set; }
    public bool?   Fit           { get; set; }
    public double? MaxFitScale   { get; set; }
    public JsonNode? Scroll      { get; set; }
    public bool?   PublishClose  { get; set; }
    public JsonNode? SourceMetadata { get; set; }
    public string? ExportProtocol { get; set; }
    public bool?   DiffSync      { get; set; }

    /// <summary>Ask the editor to report zoom changes (<c>zoom</c> events).</summary>
    public bool? ZoomEvents { get; set; }

    /// <summary>A layout (or custom layout array) to run when the diagram is loaded.</summary>
    public JsonNode? Layout { get; set; }

    /// <summary>Any <c>load</c> field not modelled above.</summary>
    public JsonObject Extra { get; } = new();

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["action"] = DrawioAction.Load };

        void Set(string key, JsonNode? value)
        {
            if (value is not null) o[key] = value;
        }

        void Flag(string key, bool? value)
        {
            if (value is { } b) o[key] = b ? 1 : 0;
        }

        Set("xml", Xml);
        Set("descriptor", Descriptor?.DeepClone());
        Flag("autosave", AutoSave);
        Flag("modified", Modified);
        Flag("saveAndExit", SaveAndExit);
        Flag("noSaveBtn", NoSaveBtn);
        Flag("noExitBtn", NoExitBtn);
        Set("title", Title);
        Set("libs", Libs);
        Flag("dark", Dark);
        Set("theme", Theme);
        Flag("rough", Rough);
        Flag("toSketch", ToSketch);
        if (Border is { } border) o["border"] = border;
        Set("background", Background);
        Set("viewport", Viewport?.DeepClone());
        Set("rect", Rect?.DeepClone());
        if (MinWidth is { } mw) o["minWidth"]   = mw;
        if (MinHeight is { } mh) o["minHeight"] = mh;
        Set("viewbox", Viewbox?.DeepClone());
        if (Scale is { } sc) o["scale"]             = sc;
        if (ScaleBorder is { } sb) o["scaleBorder"] = sb;
        Flag("fit", Fit);
        if (MaxFitScale is { } mfs) o["maxFitScale"] = mfs;
        Set("scroll", Scroll?.DeepClone());
        Flag("publishClose", PublishClose);
        Set("sourceMetadata", SourceMetadata?.DeepClone());
        Set("exportProtocol", ExportProtocol);
        Flag("diffSync", DiffSync);
        if (ZoomEvents is { } ze) o["zoomEvents"] = ze;
        Set("layout", Layout?.DeepClone());

        foreach (var (key, value) in Extra) o[key] = value?.DeepClone();
        return o;
    }
}

/// <summary>Options for the <c>export</c> action. <see cref="Format" /> is xml, json, svg, xmlsvg, png, xmlpng, html or html2.</summary>
public sealed class DrawioExportOptions
{
    public DrawioExportOptions(string format)
    {
        Format = format;
    }

    public string Format { get; }

    public double? Scale        { get; set; }
    public int?    Border       { get; set; }
    public bool?   EmbedImages  { get; set; }
    public string? PageId       { get; set; }
    public bool?   IncludeData  { get; set; }
    public bool?   Transparent  { get; set; }

    /// <summary>Export this XML instead of the open diagram.</summary>
    public string? Xml { get; set; }

    /// <summary>Any other export field (see the embed-mode documentation).</summary>
    public JsonObject Extra { get; } = new();

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["action"] = DrawioAction.Export, ["format"] = Format };
        if (Scale is { } s) o["scale"]             = s;
        if (Border is { } b) o["border"]           = b;
        if (EmbedImages is { } e) o["embedImages"] = e;
        if (PageId is not null) o["pageId"]        = PageId;
        if (IncludeData is { } i) o["includeData"] = i;
        if (Transparent is { } t) o["transparent"] = t;
        if (Xml is not null) o["xml"]              = Xml;
        foreach (var (key, value) in Extra) o[key] = value?.DeepClone();
        return o;
    }
}

/// <summary>Options for the <c>template</c> action (draw.io's template picker).</summary>
public sealed class DrawioTemplateOptions
{
    public bool? Callback        { get; set; }
    public bool? TemplatesOnly   { get; set; }
    public bool? EnableRecent    { get; set; }
    public bool? EnableSearch    { get; set; }
    public bool? EnableCustomTemp { get; set; }
    public bool? WithoutType     { get; set; }
    public bool? ToSketch        { get; set; }
    public bool? NoExitOnCancel  { get; set; }

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["action"] = DrawioAction.Template };
        if (Callback is { } a) o["callback"]               = a;
        if (TemplatesOnly is { } b) o["templatesOnly"]     = b;
        if (EnableRecent is { } c) o["enableRecent"]       = c;
        if (EnableSearch is { } d) o["enableSearch"]       = d;
        if (EnableCustomTemp is { } e) o["enableCustomTemp"] = e;
        if (WithoutType is { } f) o["withoutType"]         = f;
        if (ToSketch is { } g) o["toSketch"]               = g;
        if (NoExitOnCancel is { } h) o["noExitOnCancel"]   = h;
        return o;
    }
}

/// <summary>An entry of the "Silo" menu the plugin adds to draw.io's menu bar.</summary>
/// <param name="Id">Reported back in <see cref="SiloPluginEvent.Menu" /> when the item is chosen.</param>
/// <param name="Label">Menu text.</param>
/// <param name="Shortcut">Optional draw.io shortcut text, e.g. <c>Ctrl+Alt+R</c>.</param>
/// <param name="Separator">When true the entry is a separator and the other fields are ignored.</param>
public sealed record DrawioPluginMenuItem(string Id, string Label, string? Shortcut = null, bool Separator = false)
{
    public static DrawioPluginMenuItem Divider => new("-", "-", null, true);
}

/// <summary>A shape in a plugin-supplied sidebar palette.</summary>
public sealed record DrawioPaletteEntry(string Style, double Width, double Height, string? Value = null, string? Title = null);

/// <summary>A sidebar section ("palette") the plugin adds next to draw.io's own shape libraries.</summary>
public sealed record DrawioPluginPalette(string Id, string Title, IReadOnlyList<DrawioPaletteEntry> Entries, bool Expanded = true);

/// <summary>Small JSON helpers shared by the protocol code.</summary>
internal static class DrawioJson
{
    public static JsonArray ToArray(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (string s in items) a.Add(s);
        return a;
    }

    /// <summary>Parses a JSON object, returning null for empty/invalid text.</summary>
    public static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Turns a draw.io style string (<c>fillColor=#fff;rounded=1</c>) into the object form config expects.</summary>
    public static JsonObject StyleToObject(string style)
    {
        var o = new JsonObject();
        foreach (string part in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) o[part[..eq]] = part[(eq + 1)..];
        }

        return o;
    }
}
