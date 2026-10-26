# draw.io embedding and the Silo plugin — architecture notes

## Is draw.io's plugin architecture proprietary?

No. draw.io (jgraph/drawio) is Apache-2.0 open source, so its source — including the plugin loader — can be read, bundled and
modified. What it lacks is a *formal* plugin specification: there is no versioned API reference. The plugin mechanism is
documented by (1) the embed-mode FAQ, (2) the example plugins under `src/main/webapp/plugins`, and (3) the source itself.
A comment in the source calls `Draw.loadPlugin` the only long-term supported solution; everything a plugin touches inside
(`EditorUi`, `Graph`, `Menus`, `Sidebar`, mxGraph classes) is internal and can change between releases. Silo therefore **pins the
draw.io release** (`DrawioRuntimeSettings.Version`) and guards every plugin feature separately.

## The two channels

| Channel | Direction | Used for | Stability |
| --- | --- | --- | --- |
| Embed protocol (`embed=1&proto=json`) | host ⇄ editor via `postMessage` | load/save/export/merge/prompt/status, … | Documented. All actions/events are in `DrawioProtocol.cs`, all host methods in `DrawioView.Protocol.cs`, all event stubs in `DrawioView.Handlers.cs`. |
| Silo plugin (`Plugin/silo-plugin.js`) | plugin → host via `parent.postMessage`; host → plugin via the host page | menus, context menu, palettes, shapes, custom properties, selection/click events, cell operations | Internal API, pinned and guarded. |

## How the plugin gets in

1. `DrawioView.RegisterPluginAsync` calls WebView2 `AddScriptToExecuteOnDocumentCreatedAsync` with
   `DrawioRuntime.BuildPluginBootstrap` — `window.__SILO_PLUGIN_OPTIONS__ = {…};` followed by the plugin source (the copy
   embedded in the assembly as resource `silo-plugin.js`, or the file named by the *Plugin script file* option).
2. The script runs in every document before draw.io's own scripts, polls for `Draw.loadPlugin`, then registers its
   callback. draw.io runs the callback in the `App` constructor with the `EditorUi`.
3. `Draw.loadPlugin` exists only while plugins are enabled. Silo's `plugins=0` URL parameter is therefore sent only when
   *Disable plugins* is on **and** the Silo plugin is off.
4. The plugin posts `silo.ready`; the host answers by sending the menu, palettes and context items.

draw.io's own plugin registry (`p=` parameter) and custom-plugin gate (`ALLOW_CUSTOM_PLUGINS`) are bypassed on purpose;
users cannot load arbitrary plugins this way, only the script Silo injects.

## Host page routing

`silo-host.html` wraps the editor in an iframe and relays strings both ways. Host→editor messages containing
`"siloPlugin":true` are handed to `frame.contentWindow.SiloPlugin.receive(...)` instead of being posted to draw.io (which would
answer `unknownMessage`). Plugin events are ordinary `parent.postMessage` JSON with `event` starting `silo.`, relayed to
`DrawioView`, which routes them in `HandlePluginEvent`.

## What the plugin provides

* **Silo menu** in the menu bar (`DrawioView.PluginMenu`, edit live with `PluginSetMenuAsync`) → `OnSiloMenuCommand`.
* **Context-menu entries** for cells carrying `silo-*` attributes (`PluginContextItems`) → `OnSiloContextAction`.
* **Selection / click / double-click events** → `OnSiloSelection`, `OnSiloClick`, `OnSiloDoubleClick`.
* **Sidebar palettes** (`PluginPalettes`) of ready-made shapes.
* **Custom shapes**: `PluginAddStencilAsync(name, stencilXml)`; use as `shape=<name>` in a style.
* **Custom properties** (typed Format-panel fields on built-in shapes): `PluginSetCustomPropertiesAsync`.
* **Cell operations**: select, scroll to, set attribute, read selection.
* **Live bindings in place**: `bindingCells` / `applyBindings` re-evaluate `silo-*` bindings without reloading the diagram.
* **The person's edits of generated cells** (2026-10-26): a model listener reports, as `silo.generatedEdited { removed, restored,
  moved }`, every generated cell (`siloGenerated="1"`) the person deletes, puts back (undo) or moves/resizes/re-routes. Silo's own
  changes (`upsertCells` sets `state.applying`), diagram loads (root changes) and folding (+/-) are not reported. `DrawioView.Live.cs`
  keeps the ids: deleted cells are not added again (in place or on a rebuild), moved ones keep their geometry.
* **Live generated cells in place** (`upsertCells`, 2026-10-26): adds, updates and removes generated cells while the diagram is
  shown, for an `IIncrementalDiagramSource` (`DrawioView.PluginApplyEditsAsync`, queued and batched in `DrawioView.Live.cs`). One
  model update whose undo entry is taken off again, so the person's undo history keeps only their own edits; removing a vertex
  removes its connectors. Removal goes through `model.remove`, never `graph.removeCells`, which silently keeps cells styled
  `deletable=0` (every cell of a locked generated layer). Checked headless against mxGraph 4.2.2 (not yet against the pinned draw.io build). VERIFY on a draw.io
  bump: `ui.editor.undoManager` is an `mxUndoManager` with `history` / `indexOfNextAdd`.
* **Silo Properties** section in the Format panel's Style tab, above draw.io's own Property grid (`StyleFormatPanel.prototype.init`
  wrapped).
  * **Rows:** the selection's typed properties, each with its own icon (draw.io's `Editor.*Image` / `Format.*Image`, see
    `TOKEN_ICONS`).
  * **Grouping:** by category, or by definition scope (where each value comes from). Group headings are icon bands; clicking
    one folds the group.
  * **Toolbar:** show every token, group by category or by scope, layering buttons, what each token applies to (shape or
    label), token names, and Silo Copy Style / Paste Style.
  * **Advanced Property Menu:** each row's layering button opens it.
    * Copy Style mode per token: Not included / Present values only / Include effective defaults.
    * Paste Style mode per token: Not affected / Copied styles only / Replace non-copied with default.
    * Sources: Data Binding (edit or remove the `silo-style.<key>` attribute), then Current Default Style, Diagram Default
      (named style), Theme Default, Configuration Default and Factory Default. Each source shows its value for the cell;
      clicking one sets the cell to that value. Dragging the handles reorders the sources, and that order decides the default
      used by "Replace non-copied with default" and Use Default.
    * Use Default and Remove from Cell.
  * Edits go through `graph.setCellStyles` / `setAttributeForCell`, so they can be undone.
  * Settings are kept in the editor's `localStorage`: `silo.propertiesPanel.view` and `silo.propertiesPanel.tokens`. Plugin
    option `propertiesPanel: false` turns the section off.

## Verified vs. not verified

Checked against draw.io documentation/source: the embed actions and events listed in `DrawioProtocol.cs`, `configure`
handshake, `load` parameters, `Draw.loadPlugin` semantics, `Menu`/`menubar.addMenu`/`actions.addAction`, popup-menu
wrapping, selection-model and graph click events, `sidebar.addPalette`/`createVertexTemplate`, `mxStencilRegistry`,
`customProperties`.

**VERIFY** (could not be confirmed in the sources available; each is isolated and reports `silo.error` instead of
breaking the editor): the exact field names of `remoteInvoke`/`remoteInvokeResponse` (`funtionName`, `msgMarkers`), the
answer to `snapshot`, the `sidebar.palettes[id]` structure used to rebuild a palette in place, and whether
`Draw.loadPlugin` is already defined when the injected script starts polling on a given draw.io version.

When bumping `DrawioRuntimeSettings.Version`, enable *Plugin debug log*, open a diagram and check that `silo.ready`
reports every feature as `true`.

## Sources

* https://www.drawio.com/doc/faq/embed-mode — embed protocol
* https://www.drawio.com/doc/faq/configure-diagram-editor — `configure` options
* https://github.com/jgraph/drawio — source (Apache-2.0), `src/main/webapp/js/diagramly/App.js`, `Menus.js`, `Sidebar.js`, `plugins/`
