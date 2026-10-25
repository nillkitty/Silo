using System.IO;
using System.Linq;
using System.Threading;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Silo.Design;
using Silo.Drawio.Host;

namespace Silo.Drawio.Options;

/// <summary>Leave-it-to-draw.io / force-on / force-off, for settings where draw.io has its own default.</summary>
public enum DrawioToggle
{
    Default,
    On,
    Off
}

/// <summary>draw.io's <c>ui=</c> theme.</summary>
public enum DrawioTheme
{
    Default,
    Kennedy,
    Min,
    Atlas,
    Dark,
    Sketch,
    Simple
}

/// <summary>draw.io's <c>dark=</c> parameter (Sketch, Minimal and Simple themes).</summary>
public enum DrawioDarkMode
{
    Default,
    Light,
    Dark,
    Auto
}

/// <summary>How a pane pushes changed model/bound data into an already-open diagram.</summary>
public enum DrawioLiveUpdate
{
    /// <summary>
    ///     Export the current diagram, patch it on the .NET side (regenerated cells and binding
    ///     values), and <c>load</c> it back. Uses only documented embed-protocol actions, at the
    ///     price of resetting undo history and (apart from zoom) the view.
    /// </summary>
    Reload,

    /// <summary>
    ///     Send the patched diagram with the embed protocol's <c>merge</c> action instead. Gentler
    ///     on the view if draw.io merges same-id cells in place, but that behaviour is not
    ///     documented, so treat it as experimental.
    /// </summary>
    Merge
}

/// <summary>
///     Marks a <see cref="DrawioOptions" /> property as a user-facing option. Every restriction the
///     embedded draw.io (or the WebView2 hosting it) can be put under is one of these, and the
///     Author-level options window (see <see cref="DrawioOptionsWindow" />) and design menu are
///     generated from them by reflection -- adding an option means adding a property here and its
///     translation in <see cref="DrawioOptions.ToQueryString" />/<see cref="DrawioOptions.ToConfig" />,
///     nothing else.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DrawioOptionAttribute(string category, string title, string description = "") : Attribute
{
    public string Category    { get; } = category;
    public string Description { get; } = description;

    /// <summary>Inclusive upper bound for numeric options.</summary>
    public double Max { get; init; } = double.MaxValue;

    /// <summary>Inclusive lower bound for numeric options.</summary>
    public double Min { get; init; } = double.MinValue;

    /// <summary>The lowest <see cref="DesignLevel" /> at which the option is shown and editable.</summary>
    public DesignLevel MinLevel { get; init; } = DesignLevel.Author;

    public string Title { get; } = title;
}

/// <summary>
///     Everything about how an embedded draw.io pane is restricted, as plain .NET properties.
///     Properties named <c>Hide…</c>/<c>Disable…</c> remove something when <see langword="true" />;
///     the all-defaults instance is an unrestricted editor.
/// </summary>
/// <remarks>
///     Translated to draw.io via URL parameters (<see cref="ToQueryString" />) and the embed
///     <c>configure</c> message (<see cref="ToConfig" />); the WebView2-level options
///     (<see cref="AllowDevTools" /> and friends) are applied by <see cref="DrawioView" />.
///     Serializes to JSON (enums as strings) for the profile file and per-diagram overrides.
/// </remarks>
public sealed class DrawioOptions
{
    public const string CatInterface = "Interface";
    public const string CatViewer    = "Chromeless viewer";
    public const string CatLibraries = "Libraries & templates";
    public const string CatEditing   = "Editing";
    public const string CatData      = "Data & network";
    public const string CatSaving    = "Buttons & saving";
    public const string CatDisplay   = "Display";
    public const string CatLive      = "Live data";
    public const string CatHost      = "Host (WebView2)";
    public const string CatDefaults  = "Defaults (set by Silo)";
    public const string CatPlugin    = "Silo plugin";

    internal static readonly JsonSerializerOptions JsonOptions = new()
                                                                {
                                                                    WriteIndented = true,
                                                                    Converters    = { new JsonStringEnumConverter() }
                                                                };

    // ---- Interface ---------------------------------------------------------------------------

    [DrawioOption(CatInterface, "Theme", "draw.io's ui= theme. Default leaves draw.io's own choice.")]
    public DrawioTheme Theme { get; set; }

    [DrawioOption(CatInterface, "Dark mode", "dark= (Sketch, Minimal and Simple themes).")]
    public DrawioDarkMode DarkMode { get; set; }

    [DrawioOption(CatInterface, "Language", "lang= code, e.g. en, de, ja. Empty = draw.io's default.")]
    public string Language { get; set; } = "";

    [DrawioOption(CatInterface, "Sketch style", "sketch= for the whole editor.")]
    public DrawioToggle SketchStyle { get; set; }

    [DrawioOption(CatInterface, "Rough (hand-drawn) shapes", "rough= for shapes and connectors.")]
    public DrawioToggle RoughShapes { get; set; }

    [DrawioOption(CatInterface, "High contrast", "high-contrast=1.")]
    public bool HighContrast { get; set; }

    [DrawioOption(CatInterface, "Force touch mode", "touch=1.")]
    public bool TouchMode { get; set; }

    [DrawioOption(CatInterface, "Hide splash screen", "splash=0.")]
    public bool HideSplash { get; set; } = true;

    [DrawioOption(CatInterface, "Hide shape sidebar", "sidebar=0.")]
    public bool HideSidebar { get; set; }

    [DrawioOption(CatInterface, "Hide format panel", "format=0.")]
    public bool HideFormatPanel { get; set; }

    [DrawioOption(CatInterface, "Hide toolbar", "toolbar=0 (takes effect in the chromeless viewer).")]
    public bool HideToolbar { get; set; }

    [DrawioOption(CatInterface, "Hide top-level menus",
                  "Names to hide (config hideMenus): file; edit; view; arrange; extras; help.")]
    public List<string> HideMenus { get; set; } = [];

    [DrawioOption(CatInterface, "Hide menu items", "Menu item names to hide (config hideMenuItems), semicolon separated.")]
    public List<string> HideMenuItems { get; set; } = [];

    [DrawioOption(CatInterface, "Hide inline toolbar", "config enableInlineToolbar=false (the small toolbar next to a selection).")]
    public bool HideInlineToolbar { get; set; }

    [DrawioOption(CatInterface, "Disable floating windows", "windows=0 (Minimal theme).")]
    public bool DisableFloatingWindows { get; set; }

    [DrawioOption(CatInterface, "Disable shape picker", "shape-picker=0.")]
    public bool DisableShapePicker { get; set; }

    [DrawioOption(CatInterface, "Hide page tabs", "pages=0.")]
    public bool HidePageTabs { get; set; }

    [DrawioOption(CatInterface, "Hide scrollbars", "sb=0.")]
    public bool HideScrollbars { get; set; }

    [DrawioOption(CatInterface, "Ruler", "ruler=1 starts with the ruler on.")]
    public bool ShowRuler { get; set; }

    [DrawioOption(CatInterface, "Grid", "grid= on/off, overriding the file.")]
    public DrawioToggle Grid { get; set; }

    [DrawioOption(CatInterface, "Page view", "pv= on/off, overriding the file.")]
    public DrawioToggle PageView { get; set; }

    // ---- Chromeless viewer -------------------------------------------------------------------

    [DrawioOption(CatViewer, "Chromeless (read-only viewer)",
                  "chrome=0: no menus, toolbar, sidebar or format panel. Combine with 'Lock generated cells'.")]
    public bool Chromeless { get; set; }

    [DrawioOption(CatViewer, "Lightbox", "lightbox=1.")]
    public bool Lightbox { get; set; }

    [DrawioOption(CatViewer, "Layer control", "layers=1.")]
    public bool ViewerLayers { get; set; }

    [DrawioOption(CatViewer, "Folding", "nav=1.")]
    public bool ViewerFolding { get; set; }

    [DrawioOption(CatViewer, "Hide print button", "noPrint=1.")]
    public bool HideViewerPrint { get; set; }

    [DrawioOption(CatViewer, "Hide export button", "noExport=1.")]
    public bool HideViewerExport { get; set; }

    [DrawioOption(CatViewer, "Transparent background", "transparent=1.")]
    public bool ViewerTransparent { get; set; }

    // ---- Libraries & templates ---------------------------------------------------------------

    [DrawioOption(CatLibraries, "Show shape libraries panel", "libraries=1 (embed mode hides it by default).")]
    public bool ShowLibraries { get; set; } = true;

    [DrawioOption(CatLibraries, "Libraries shown", "libs= keys, semicolon separated, e.g. general; flowchart; uml. Empty = draw.io's default set.")]
    public List<string> Libraries { get; set; } = [];

    [DrawioOption(CatLibraries, "Libraries available in More Shapes",
                  "config enabledLibraries: only these keys are offered. Empty = all (unless 'Hide all' is on).")]
    public List<string> EnabledLibraries { get; set; } = [];

    [DrawioOption(CatLibraries, "Hide all More Shapes libraries", "config enabledLibraries = [].")]
    public bool HideAllMoreShapes { get; set; }

    [DrawioOption(CatLibraries, "Disable custom libraries", "config enableCustomLibraries=false (no open/new library).")]
    public bool DisableCustomLibraries { get; set; }

    [DrawioOption(CatLibraries, "Hide templates", "config enabledTemplateSections = [].")]
    public bool HideTemplates { get; set; }

    [DrawioOption(CatLibraries, "Sidebar titles", "config sidebarTitles.")]
    public bool SidebarTitles { get; set; }

    // ---- Editing -----------------------------------------------------------------------------

    [DrawioOption(CatEditing, "Lock generated cells",
                  "Cells Silo generates (model feed, pushed nodes) get movable/resizable/deletable/editable/connectable=0 styles.")]
    public bool LockGeneratedCells { get; set; } = true;

    [DrawioOption(CatEditing, "Disable connecting", "config defaultConnectable=false and defaultConnectionArrowsEnabled=false.")]
    public bool DisableConnecting { get; set; }

    [DrawioOption(CatEditing, "Disable folding", "config defaultFoldingEnabled=false.")]
    public bool DisableFolding { get; set; }

    [DrawioOption(CatEditing, "Disable size guides", "config enableSizeGuides=false.")]
    public bool DisableSizeGuides { get; set; }

    [DrawioOption(CatEditing, "Simple labels", "config simpleLabels=true: no word wrap or rich text labels.")]
    public bool SimpleLabels { get; set; }

    [DrawioOption(CatEditing, "Math typesetting", "math=0 when set to Off.")]
    public DrawioToggle MathJax { get; set; }

    [DrawioOption(CatEditing, "Mouse wheel zooms", "config zoomWheel=true.")]
    public bool ZoomWheel { get; set; }

    // ---- Data & network ----------------------------------------------------------------------

    [DrawioOption(CatData, "Device storage only", "local=1.")]
    public bool StorageLocalOnly { get; set; }

    [DrawioOption(CatData, "Disable cloud storage", "gapi=0, db=0, od=0, tr=0, gh=0, gl=0 and ms365=0.")]
    public bool DisableCloudStorage { get; set; } = true;

    [DrawioOption(CatData, "Stealth", "stealth=1: no features that need external web services (e.g. PDF export).")]
    public bool Stealth { get; set; } = true;

    [DrawioOption(CatData, "Lockdown", "lockdown=1: block all data transmission except browser-to-storage.")]
    public bool Lockdown { get; set; } = true;

    [DrawioOption(CatData, "Disable draw.io's own plugin loading",
                  "Hides Extras > Plugins and never passes p=. Silo's own plugin (see the Silo plugin category) is separate: " +
                  "plugins=0 is only sent when this is on AND the Silo plugin is off, because plugins=0 stops draw.io running any plugin.")]
    public bool DisablePlugins { get; set; } = true;

    [DrawioOption(CatData, "Disable drafts", "drafts=0: no draft saving to IndexedDB.")]
    public bool DisableDrafts { get; set; } = true;

    [DrawioOption(CatData, "Disable collaboration", "sync=none.")]
    public bool DisableCollaboration { get; set; } = true;

    [DrawioOption(CatData, "Disable PWA registration", "pwa=0.")]
    public bool DisablePwa { get; set; } = true;

    [DrawioOption(CatData, "Disable export URL", "config enableExportUrl=false.")]
    public bool DisableExportUrl { get; set; } = true;

    [DrawioOption(CatData, "Restrict export", "config restrictExport=true.")]
    public bool RestrictExport { get; set; }

    [DrawioOption(CatData, "Suppress new windows", "suppressNewWindows=1: links are reported to Silo instead of opening.")]
    public bool SuppressNewWindows { get; set; } = true;

    [DrawioOption(CatData, "Open external links in the browser",
                  "When a link reaches Silo and no Silo handler claims it, open http(s) links in the default browser.")]
    public bool OpenExternalLinks { get; set; }

    // ---- Buttons & saving --------------------------------------------------------------------

    [DrawioOption(CatSaving, "Hide Save button", "noSaveBtn=1 (draw.io then shows Save and Exit unless that is hidden too).")]
    public bool HideSaveButton { get; set; } = true;

    [DrawioOption(CatSaving, "Hide Save and Exit button", "saveAndExit=0.")]
    public bool HideSaveAndExitButton { get; set; } = true;

    [DrawioOption(CatSaving, "Hide Exit button", "noExitBtn=1.")]
    public bool HideExitButton { get; set; } = true;

    [DrawioOption(CatSaving, "Autosave to Silo", "load autosave=1: every change is reported to Silo (and saved to the Silo document when one is bound).")]
    public bool AutoSave { get; set; } = true;

    [DrawioOption(CatSaving, "Autosave delay (ms)", "config autosaveDelay. 0 = draw.io's default.", Min = 0, Max = 60000,
                  MinLevel = DesignLevel.Developer)]
    public int AutosaveDelayMs { get; set; }

    [DrawioOption(CatSaving, "Fit diagram on load", "Fit the diagram into the pane when it is loaded.")]
    public bool FitOnLoad { get; set; }

    // ---- Display -----------------------------------------------------------------------------

    [DrawioOption(CatDisplay, "Link icons", "Show an icon on cells with links. Needed for click-through on a locked diagram.")]
    public bool ShowLinkIcons { get; set; } = true;

    [DrawioOption(CatDisplay, "Tooltip icons", "Show an icon on cells with tooltips.")]
    public bool ShowTooltipIcons { get; set; }

    [DrawioOption(CatDisplay, "Disable tooltips", "tooltips=0.")]
    public bool DisableTooltips { get; set; }

    // ---- Live data ---------------------------------------------------------------------------

    [DrawioOption(CatLive, "Enable data bindings", "Apply silo-label / silo-style.* / silo-tooltip cell bindings from the live data providers.")]
    public bool EnableBindings { get; set; } = true;

    [DrawioOption(CatLive, "Update strategy", "How changes are pushed into an open diagram. See DrawioLiveUpdate.", MinLevel = DesignLevel.Developer)]
    public DrawioLiveUpdate LiveUpdate { get; set; } = DrawioLiveUpdate.Reload;

    [DrawioOption(CatLive, "Throttle (ms)", "Minimum time between live updates.", Min = 100, Max = 60000, MinLevel = DesignLevel.Developer)]
    public int LiveThrottleMs { get; set; } = 750;

    [DrawioOption(CatLive, "Defer while editing (ms)",
                  "Postpone a live update until the person has made no change for this long, so a reload never lands mid-edit.",
                  Min = 0, Max = 60000, MinLevel = DesignLevel.Developer)]
    public int LiveDeferWhileEditingMs { get; set; } = 2500;

    // ---- Host (WebView2) ---------------------------------------------------------------------

    [DrawioOption(CatHost, "Block external requests", "Any request that is not to the local draw.io runtime is refused.")]
    public bool BlockExternalRequests { get; set; } = true;

    [DrawioOption(CatHost, "Allow downloads", "Let the page start browser downloads.")]
    public bool AllowDownloads { get; set; }

    [DrawioOption(CatHost, "Allow pop-up windows", "Let the page open new browser windows.")]
    public bool AllowPopups { get; set; }

    [DrawioOption(CatHost, "Allow browser context menu", "WebView2 default context menus.")]
    public bool AllowContextMenu { get; set; }

    [DrawioOption(CatHost, "Allow browser accelerator keys", "F5, Ctrl+P, Ctrl+F and the like.")]
    public bool AllowBrowserKeys { get; set; }

    [DrawioOption(CatHost, "Allow zoom control", "WebView2's own Ctrl+wheel zoom.")]
    public bool AllowZoomControl { get; set; }

    [DrawioOption(CatHost, "Show status bar", "WebView2's link-hover status bar.")]
    public bool ShowStatusBar { get; set; }

    [DrawioOption(CatHost, "Allow dev tools", "F12 / Inspect.", MinLevel = DesignLevel.Developer)]
    public bool AllowDevTools { get; set; }

    // ---- Defaults (set by Silo) --------------------------------------------------------------
    // draw.io keeps per-user settings in the browser profile; these make Silo, not the profile, decide.

    [DrawioOption(CatDefaults, "Host overrides client settings", "config override=true: ignore draw.io's remembered per-user settings so these options always win.")]
    public bool HostOverridesClientSettings { get; set; } = true;

    [DrawioOption(CatDefaults, "Settings name", "config settingsName: the key draw.io stores its own settings under, so Silo's never mix with another draw.io.",
                  MinLevel = DesignLevel.Developer)]
    public string SettingsName { get; set; } = "silo";

    [DrawioOption(CatDefaults, "Compress diagram XML", "config compressXml. Off keeps the XML readable and patchable on the .NET side.", MinLevel = DesignLevel.Developer)]
    public bool CompressXml { get; set; }

    [DrawioOption(CatDefaults, "Default shape style", "config defaultVertexStyle, as a style string, e.g. rounded=1;fillColor=#dae8fc;strokeColor=#6c8ebf;")]
    public string DefaultVertexStyle { get; set; } = "";

    [DrawioOption(CatDefaults, "Default connector style", "config defaultEdgeStyle, as a style string, e.g. edgeStyle=orthogonalEdgeStyle;rounded=1;")]
    public string DefaultEdgeStyle { get; set; } = "";

    [DrawioOption(CatDefaults, "Default libraries", "config defaultLibraries: libraries open in the sidebar to start with, e.g. general; flowchart.")]
    public List<string> DefaultLibraries { get; set; } = [];

    [DrawioOption(CatDefaults, "Custom libraries", "config defaultCustomLibraries: library file ids or URLs loaded into the sidebar (U-prefixed URL, e.g. Uhttps://drawio.silo.local/silo-shapes.xml).")]
    public List<string> CustomLibraries { get; set; } = [];

    [DrawioOption(CatDefaults, "Custom libraries after built-in ones", "config appendCustomLibraries.")]
    public bool AppendCustomLibraries { get; set; }

    [DrawioOption(CatDefaults, "Template file", "config templateFile: a template index to use for the Templates dialog. Empty = draw.io's own.")]
    public string TemplateFile { get; set; } = "";

    [DrawioOption(CatDefaults, "Custom fonts", "config customFonts: a family name, or Name=https://font-url, one per entry.")]
    public List<string> CustomFonts { get; set; } = [];

    [DrawioOption(CatDefaults, "Custom CSS", "config css: CSS injected into the editor's own UI.")]
    public string CustomCss { get; set; } = "";

    [DrawioOption(CatDefaults, "Preset colours", "config customPresetColors: hex colours (without #) added to the colour dialog.")]
    public List<string> PresetColors { get; set; } = [];

    [DrawioOption(CatDefaults, "Page width (px)", "config pageFormat width. 0 = draw.io's default.", Min = 0, Max = 100000)]
    public int PageWidth { get; set; }

    [DrawioOption(CatDefaults, "Page height (px)", "config pageFormat height. 0 = draw.io's default.", Min = 0, Max = 100000)]
    public int PageHeight { get; set; }

    [DrawioOption(CatDefaults, "Grid size", "config defaultGridSize. 0 = draw.io's default.", Min = 0, Max = 1000)]
    public int GridSize { get; set; }

    [DrawioOption(CatDefaults, "Grid on by default", "config defaultGridEnabled.")]
    public DrawioToggle GridDefault { get; set; }

    [DrawioOption(CatDefaults, "Page visible by default", "config defaultPageVisible.")]
    public DrawioToggle PageVisibleDefault { get; set; }

    [DrawioOption(CatDefaults, "Zoom step (%)", "config zoomFactor as a percentage, e.g. 120 = 1.2. 0 = draw.io's default.", Min = 0, Max = 500)]
    public int ZoomFactorPercent { get; set; }

    [DrawioOption(CatDefaults, "Key chords handled by Silo", "config passThroughKeys: chords draw.io hands to Silo as 'shortcut' events instead of acting on.")]
    public List<string> PassThroughKeys { get; set; } = [];

    [DrawioOption(CatDefaults, "Global placeholder variables (JSON)", "config globalVars, e.g. {\"env\":\"prod\"}: usable in labels and tooltips as %env%.")]
    public string GlobalVarsJson { get; set; } = "";

    [DrawioOption(CatDefaults, "Keyboard shortcuts (JSON)", "config keyboardShortcuts: custom key bindings for editor actions.")]
    public string KeyboardShortcutsJson { get; set; } = "";

    [DrawioOption(CatDefaults, "UI string overrides (JSON)", "config resources: replaces/adds editor UI strings, e.g. {\"save\":\"Save to Silo\"}.")]
    public string ResourcesJson { get; set; } = "";

    [DrawioOption(CatDefaults, "Extra config (JSON)", "Any other config key. Merged last, so it can override anything above.", MinLevel = DesignLevel.Developer)]
    public string ExtraConfigJson { get; set; } = "";

    [DrawioOption(CatDefaults, "Extra URL parameters", "Any other URL parameter, as a=1&b=2. Appended last.", MinLevel = DesignLevel.Developer)]
    public string ExtraQuery { get; set; } = "";

    // ---- Silo plugin -------------------------------------------------------------------------

    [DrawioOption(CatPlugin, "Enable the Silo plugin",
                  "Loads Silo's own plugin (Drawio/Plugin/silo-plugin.js) into the editor: the Silo menu, context-menu entries, selection/click events and Silo palettes.")]
    public bool EnableSiloPlugin { get; set; } = true;

    [DrawioOption(CatPlugin, "Silo menu", "Adds a 'Silo' menu to the menu bar (items come from DrawioView.PluginMenu).")]
    public bool PluginSiloMenu { get; set; } = true;

    [DrawioOption(CatPlugin, "Context-menu entries", "Adds Silo entries to the right-click menu of cells that carry silo-* data.")]
    public bool PluginContextMenu { get; set; } = true;

    [DrawioOption(CatPlugin, "Selection events", "Reports selection changes to Silo (silo.selection).")]
    public bool PluginSelectionEvents { get; set; } = true;

    [DrawioOption(CatPlugin, "Click events", "Reports cell clicks and double-clicks to Silo (silo.click / silo.dblclick).")]
    public bool PluginClickEvents { get; set; } = true;

    [DrawioOption(CatPlugin, "Silo palettes", "Lets Silo add its own sections to the shape sidebar (DrawioView.PluginPalettes).")]
    public bool PluginPalettes { get; set; } = true;

    [DrawioOption(CatPlugin, "Plugin debug log", "Writes the plugin's activity to the editor's console and Silo's log.", MinLevel = DesignLevel.Developer)]
    public bool PluginDebug { get; set; }

    [DrawioOption(CatPlugin, "Plugin script file", "Developer: load the plugin from this file instead of the copy built into Silo, so edits apply on the next editor reload.",
                  MinLevel = DesignLevel.Developer)]
    public string PluginScriptPath { get; set; } = "";

    // ---- Translation -------------------------------------------------------------------------

    /// <summary>The URL query string (no leading '?') for the draw.io editor page.</summary>
    public string ToQueryString()
    {
        var q = new List<KeyValuePair<string, string>>();

        void Add(string key, string value) => q.Add(new KeyValuePair<string, string>(key, value));

        void Toggle(string key, DrawioToggle t)
        {
            if (t == DrawioToggle.On) Add(key, "1");
            else if (t == DrawioToggle.Off) Add(key, "0");
        }

        // Always: embed mode with the JSON protocol, driven by the configure handshake.
        Add("embed", "1");
        Add("proto", "json");
        Add("configure", "1");
        Add("spin", "1");

        if (Theme != DrawioTheme.Default) Add("ui", Theme.ToString().ToLowerInvariant());
        if (DarkMode != DrawioDarkMode.Default)
            Add("dark", DarkMode switch { DrawioDarkMode.Light => "0", DrawioDarkMode.Dark => "1", _ => "auto" });
        if (!string.IsNullOrWhiteSpace(Language)) Add("lang", Language.Trim());
        Toggle("sketch", SketchStyle);
        Toggle("rough", RoughShapes);
        if (HighContrast) Add("high-contrast", "1");
        if (TouchMode) Add("touch", "1");
        if (HideSplash) Add("splash", "0");
        if (HideSidebar) Add("sidebar", "0");
        if (HideFormatPanel) Add("format", "0");
        if (HideToolbar) Add("toolbar", "0");
        if (DisableFloatingWindows) Add("windows", "0");
        if (DisableShapePicker) Add("shape-picker", "0");
        if (HidePageTabs) Add("pages", "0");
        if (HideScrollbars) Add("sb", "0");
        if (ShowRuler) Add("ruler", "1");
        Toggle("grid", Grid);
        Toggle("pv", PageView);

        if (Chromeless) Add("chrome", "0");
        if (Lightbox) Add("lightbox", "1");
        if (ViewerLayers) Add("layers", "1");
        if (ViewerFolding) Add("nav", "1");
        if (HideViewerPrint) Add("noPrint", "1");
        if (HideViewerExport) Add("noExport", "1");
        if (ViewerTransparent) Add("transparent", "1");

        if (ShowLibraries) Add("libraries", "1");
        if (Libraries.Count > 0) Add("libs", string.Join(";", Libraries));

        if (MathJax == DrawioToggle.Off) Add("math", "0");

        if (StorageLocalOnly) Add("local", "1");
        if (DisableCloudStorage)
            foreach (string k in new[] { "gapi", "db", "od", "tr", "gh", "gl", "ms365" })
                Add(k, "0");
        if (Stealth) Add("stealth", "1");
        if (Lockdown) Add("lockdown", "1");
        // plugins=0 would stop draw.io running any plugin, Silo's included.
        if (DisablePlugins && !EnableSiloPlugin) Add("plugins", "0");
        if (DisableDrafts) Add("drafts", "0");
        if (DisableCollaboration) Add("sync", "none");
        if (DisablePwa) Add("pwa", "0");
        if (SuppressNewWindows) Add("suppressNewWindows", "1");

        if (HideSaveButton) Add("noSaveBtn", "1");
        if (HideSaveAndExitButton) Add("saveAndExit", "0");
        if (HideExitButton) Add("noExitBtn", "1");

        if (ShowLinkIcons) Add("link-icons", "1");
        if (ShowTooltipIcons) Add("tooltip-icons", "1");
        if (DisableTooltips) Add("tooltips", "0");

        foreach (string pair in (ExtraQuery ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = pair.IndexOf('=');
            string key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            string val = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
            q.RemoveAll(p => p.Key == key);
            Add(key, val);
        }

        return string.Join("&", q.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    }

    /// <summary>The configuration object sent in reply to draw.io's <c>configure</c> event.</summary>
    public JsonObject ToConfig()
    {
        // Plain (uncompressed) XML is what lets Silo read and patch diagrams on the .NET side.
        var c = new JsonObject { ["compressXml"] = CompressXml, ["version"] = ConfigVersion };

        if (HostOverridesClientSettings) c["override"] = true;
        if (!string.IsNullOrWhiteSpace(SettingsName)) c["settingsName"] = SettingsName.Trim();

        if (HideMenus.Count > 0) c["hideMenus"] = ToArray(HideMenus);

        // Extras > Plugins is the way draw.io's own (third-party) plugins get loaded.
        var hiddenItems = HideMenuItems.ToList();
        if (DisablePlugins && !hiddenItems.Contains("plugins")) hiddenItems.Add("plugins");
        if (hiddenItems.Count > 0) c["hideMenuItems"] = ToArray(hiddenItems);
        if (HideInlineToolbar) c["enableInlineToolbar"] = false;
        if (HideAllMoreShapes) c["enabledLibraries"]    = new JsonArray();
        else if (EnabledLibraries.Count > 0) c["enabledLibraries"] = ToArray(EnabledLibraries);
        if (DisableCustomLibraries) c["enableCustomLibraries"] = false;
        if (HideTemplates) c["enabledTemplateSections"]        = new JsonArray();
        if (SidebarTitles) c["sidebarTitles"]                  = true;
        if (DisableConnecting)
        {
            c["defaultConnectable"]              = false;
            c["defaultConnectionArrowsEnabled"] = false;
        }

        if (DisableFolding) c["defaultFoldingEnabled"] = false;
        if (DisableSizeGuides) c["enableSizeGuides"]   = false;
        if (SimpleLabels) c["simpleLabels"]            = true;
        if (ZoomWheel) c["zoomWheel"]                  = true;
        if (DisableExportUrl) c["enableExportUrl"]     = false;
        if (RestrictExport) c["restrictExport"]        = true;
        if (Lockdown) c["lockdown"]                    = true;
        if (SuppressNewWindows) c["suppressNewWindows"] = true;
        if (ShowLinkIcons) c["showLinkIcons"]          = true;
        if (ShowTooltipIcons) c["showTooltipIcons"]    = true;
        if (AutosaveDelayMs > 0) c["autosaveDelay"]    = AutosaveDelayMs;
        if (FitOnLoad) c["fitDiagramOnLoad"]           = true;

        // ---- defaults the host decides ----
        if (!string.IsNullOrWhiteSpace(DefaultVertexStyle)) c["defaultVertexStyle"] = DrawioJson.StyleToObject(DefaultVertexStyle);
        if (!string.IsNullOrWhiteSpace(DefaultEdgeStyle)) c["defaultEdgeStyle"]     = DrawioJson.StyleToObject(DefaultEdgeStyle);
        if (DefaultLibraries.Count > 0) c["defaultLibraries"]                       = string.Join(";", DefaultLibraries);
        if (CustomLibraries.Count > 0) c["defaultCustomLibraries"]                  = ToArray(CustomLibraries);
        if (AppendCustomLibraries) c["appendCustomLibraries"]                       = true;
        if (!string.IsNullOrWhiteSpace(TemplateFile)) c["templateFile"]             = TemplateFile.Trim();
        if (!string.IsNullOrWhiteSpace(CustomCss)) c["css"]                         = CustomCss;
        if (PresetColors.Count > 0) c["customPresetColors"]                         = ToArray(PresetColors.Select(x => x.TrimStart('#')));
        if (PageWidth > 0 && PageHeight > 0) c["pageFormat"]                        = new JsonObject { ["width"] = PageWidth, ["height"] = PageHeight };
        if (GridSize > 0) c["defaultGridSize"]                                      = GridSize;
        if (GridDefault != DrawioToggle.Default) c["defaultGridEnabled"]            = GridDefault == DrawioToggle.On;
        if (PageVisibleDefault != DrawioToggle.Default) c["defaultPageVisible"]     = PageVisibleDefault == DrawioToggle.On;
        if (ZoomFactorPercent > 0) c["zoomFactor"]                                  = ZoomFactorPercent / 100.0;
        if (PassThroughKeys.Count > 0) c["passThroughKeys"]                         = ToArray(PassThroughKeys);

        if (CustomFonts.Count > 0)
        {
            var fonts = new JsonArray();
            foreach (string f in CustomFonts)
            {
                int eq = f.IndexOf('=');
                if (eq > 0) fonts.Add(new JsonObject { ["fontFamily"] = f[..eq].Trim(), ["fontUrl"] = f[(eq + 1)..].Trim() });
                else fonts.Add(f.Trim());
            }

            c["customFonts"] = fonts;
        }

        if (DrawioJson.ParseObject(GlobalVarsJson) is { } vars) c["globalVars"]                   = vars;
        if (DrawioJson.ParseObject(KeyboardShortcutsJson) is { } keys) c["keyboardShortcuts"]     = keys;
        if (DrawioJson.ParseObject(ResourcesJson) is { } res) c["resources"]                      = res;

        // Last, so it can override anything above.
        if (DrawioJson.ParseObject(ExtraConfigJson) is { } extra)
            foreach (var (key, value) in extra.ToList())
            {
                extra.Remove(key);
                c[key] = value;
            }

        return c;
    }

    /// <summary>Bump when the defaults above change in a way draw.io's remembered settings should not survive.</summary>
    public const int ConfigVersion = 1;

    /// <summary>The options object the Silo plugin reads as <c>window.__SILO_PLUGIN_OPTIONS__</c>.</summary>
    public JsonObject ToPluginOptions()
    {
        return new JsonObject
               {
                   ["debug"]           = PluginDebug,
                   ["menu"]            = PluginSiloMenu,
                   ["contextMenu"]     = PluginContextMenu,
                   ["selectionEvents"] = PluginSelectionEvents,
                   ["clickEvents"]     = PluginClickEvents,
                   ["palettes"]        = PluginPalettes
               };
    }

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (string s in items) a.Add(s);
        return a;
    }

    // ---- Persistence helpers -----------------------------------------------------------------

    public DrawioOptions Clone()
    {
        return FromJson(ToJson());
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public static DrawioOptions FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new DrawioOptions();

        try
        {
            return JsonSerializer.Deserialize<DrawioOptions>(json, JsonOptions) ?? new DrawioOptions();
        }
        catch (JsonException)
        {
            return new DrawioOptions();
        }
    }

    /// <summary>Whether two option sets would produce the same editor (and so need no reload).</summary>
    public bool SameAs(DrawioOptions other)
    {
        return ToJson() == other.ToJson();
    }
}

/// <summary>
///     One option as the UI sees it: its metadata plus typed get/set on a <see cref="DrawioOptions" />.
/// </summary>
public sealed class DrawioOptionDescriptor
{
    public enum ValueKind
    {
        Bool,
        Enum,
        Int,
        Text,
        List
    }

    private static List<DrawioOptionDescriptor>? _all;

    private readonly PropertyInfo _property;

    private DrawioOptionDescriptor(PropertyInfo property, DrawioOptionAttribute meta)
    {
        _property = property;
        Meta      = meta;
        Name      = property.Name;
        Type      = property.PropertyType;
        Kind = Type == typeof(bool)             ? ValueKind.Bool :
               Type.IsEnum                      ? ValueKind.Enum :
               Type == typeof(int)              ? ValueKind.Int :
               Type == typeof(List<string>)     ? ValueKind.List : ValueKind.Text;
    }

    /// <summary>Every option, in declaration order (which is also the display order).</summary>
    public static IReadOnlyList<DrawioOptionDescriptor> All => _all ??= Build();

    public ValueKind Kind { get; }

    public DrawioOptionAttribute Meta { get; }

    public string Name { get; }

    public Type Type { get; }

    private static List<DrawioOptionDescriptor> Build()
    {
        var list = new List<DrawioOptionDescriptor>();
        foreach (var p in typeof(DrawioOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.MetadataToken))
            if (p.GetCustomAttribute<DrawioOptionAttribute>() is { } meta)
                list.Add(new DrawioOptionDescriptor(p, meta));
        return list;
    }

    public static DrawioOptionDescriptor? Find(string name)
    {
        return All.FirstOrDefault(d => d.Name == name);
    }

    public object? Get(DrawioOptions o)
    {
        return _property.GetValue(o);
    }

    /// <summary>The value as editable text (lists are joined with "; ").</summary>
    public string GetText(DrawioOptions o)
    {
        object? v = Get(o);
        return v switch
        {
            List<string> l => string.Join("; ", l),
            null           => "",
            _              => v.ToString() ?? ""
        };
    }

    public void Set(DrawioOptions o, object? value)
    {
        _property.SetValue(o, value);
    }

    /// <summary>Parses <paramref name="text"/> for this option's type and stores it. Returns false if it did not parse.</summary>
    public bool TrySetText(DrawioOptions o, string text)
    {
        switch (Kind)
        {
            case ValueKind.Text:
                Set(o, text.Trim());
                return true;

            case ValueKind.List:
                Set(o, text.Split([';', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
                return true;

            case ValueKind.Int:
                if (!int.TryParse(text.Trim(), out int n)) return false;
                Set(o, (int)Math.Clamp(n, Meta.Min, Meta.Max));
                return true;

            case ValueKind.Bool:
                if (!bool.TryParse(text.Trim(), out bool b)) return false;
                Set(o, b);
                return true;

            case ValueKind.Enum:
                if (!Enum.TryParse(Type, text.Trim(), true, out object? e)) return false;
                Set(o, e);
                return true;

            default:
                return false;
        }
    }
}
