# Diagram extension points

The Diagram Canvas embeds draw.io and adds a Silo plugin to it. Extensions never talk to draw.io directly: they implement
the contracts below, and Silo turns what they return into menus, sidebar shapes, cells and live values. How the plugin is
injected and how it drives the editor are implementation details of `Drawio/Host` and `Plugin/silo-plugin.js` and are not
part of the extension surface.

## At a glance

| You want to… | Implement / use | Register with |
| --- | --- | --- |
| Add shapes to the sidebar | `IPaletteProvider` | `[Singleton(typeof(IPaletteProvider))]` |
| Add a new kind of data-bound node | `DiagramNodeBinder` | `[Singleton(typeof(IPaletteProvider))]` |
| Show your objects in Tree / Table / List nodes | `ITreeSource`, `IRowSource` (or an `IDiagramSourceAdapter`) | adapter: `[Singleton(typeof(IDiagramSourceAdapter))]` |
| Let nodes bind to a file type | `IBindableFormat` | the file-format registry |
| Let nodes bind to a URI scheme | `IUriResolver` | the service registry |
| Feed `{scheme:path}` tokens in labels and styles | `IDiagramDataProvider`, or `DiagramData.Push` | `DiagramData.Register` |
| Generate a layer of cells | `IDiagramContentSource` / `IIncrementalDiagramSource` | `DrawioView.Source` |
| Act when a cell link is clicked | `IDiagramLinkHandler` | `DiagramLinks.Handlers` |
| Store diagrams in a Silo | `IDiagramStore` | on the Silo's `IDatabaseProvider` |
| Add menu / context-menu items | `DrawioPluginMenuItem` | `DrawioView.PluginMenu`, `PluginContextItems` |

## Sidebar shapes — `IPaletteProvider`

`GetPalettes()` returns `DrawioPluginPalette`s (an `Id`, a `Title` and `DrawioPaletteEntry`s). Every registered provider is
asked when a diagram pane is ready. Palettes that share an `Id` merge into one sidebar section, so several providers can add
to the same section.

A `DrawioPaletteEntry` is either a single cell (`Style`, `Width`, `Height`, optional `Value` / `Title`) or, with `Xml`, an
`mxGraphModel` of several cells (a container with children, cells with custom attributes).

## Data-bound nodes — `DiagramNodeBinder`

A data-bound node is a cell whose `silo-node` attribute names a kind (`status`, `table`, `list`, `tree`, …) and whose
`silo-uri` names a source. Each kind is one stateless `DiagramNodeBinder`:

* `Kind` — the `silo-node` value it handles.
* `SettingsType` — its settings, a `DynamicNode` subclass.
* `CreatePaletteEntry()` — the node as dropped from the shared "Silo Data" section (`DiagramNodeBinder.PaletteId`).
* `Bind(BindContext)` — given the node's current cells and what its URI resolved to, returns the changes (`BindResult`).
  The pane does the reading, resolving and applying; the binder only works out the difference.

What a node creates is ordinary diagram content: it is saved with the diagram and marked as belonging to the node. When the
person deletes or moves one of those cells, Silo respects it on later refreshes.

### What a node can bind to

A node's URI is resolved, in order, by:

1. a live value scheme registered with `DiagramData` (`silo:`, `bind:`, `push:`, your own);
2. the registered `IUriResolver`s;
3. `file:` — a folder as itself, a file through the `IBindableFormat` that claims its extension (`Bind(Uri)` returns the
   object to bind, or null), any other file as itself.

`silo-path` is then followed into the result.

### Shaping objects for Tree, Table and List nodes

* **`ITreeSource` / `ITreeItem`** — roots and lazily read children (`HasChildren = null` means "unknown, read on expand").
* **`IRowSource` / `IRowItem`** — columns plus pages of rows (`RowPage.Total` adds a "… N more" row).
* **`IDiagramSourceAdapter`** — turns objects you don't own into either of the above. Adapters run by descending
  `Priority`; objects that already implement `ITreeSource` / `IRowSource` are used as they are. The built-in heuristics
  (directories, JSON/XML, tabular and list data sources, any enumerable) run last.
* **`IDiagramItemHints`** (optional, on items, rows or a Status node's object) — a click-through `Link`, `StyleEntries`
  and a `Tooltip` for the cell the item becomes.
* **`INotifySourceChanged`** (optional) — raise `Changed` so Live nodes refresh without polling. Sources implementing
  `IObservableDataSource` are watched through that instead.

## Live values in labels and styles — `IDiagramDataProvider`

Any cell can bind with custom attributes: `silo-label`, `silo-tooltip`, `silo-style.<key>` and `silo-attr.<name>`. Each is
a template of literal text and `{scheme:path}` tokens, optionally formatted:

* `{push:cpu|fmt:0.0}` — a .NET format string
* `{silo:c1.state|map:connected=#d5e8d4,*=#f5f5f5}` — value lookup (`*` = anything else)
* `{bind:CpuPercent|range:0=#d5e8d4,80=#f8cecc}` — the highest threshold reached

Inside data-bound nodes, scoped tokens also work: `{.Path}`, `{item:Path}`, `{row:Column}`, `{node:Path}`.

To add a scheme, implement `IDiagramDataProvider` (`Scheme`, `TryGet(path)`, and raise `Changed` when values may have
changed) and call `DiagramData.Register`. For one-off values, `DiagramData.Push("cpu", 42)` updates every `{push:cpu}`
cell in every open pane.

## Generated layers — `IDiagramContentSource`

Assign a source to `DrawioView.Source` to give a pane a layer of cells Silo owns (the person's own cells are left alone).

* **`IDiagramContentSource`** — `Build()` returns a `DiagramModel` (nodes, edges); raise `Changed` to have it rebuilt and
  reloaded.
* **`IIncrementalDiagramSource`** — also raises `EditsReady` with `DiagramEdit`s (`AddNode`, `AddEdge`, `Update`,
  `Remove`) that are applied while the diagram is shown, without a reload and without touching the person's undo history.
  `DiagramDiff.Between(before, after)` computes the edits from two models; when it returns null, raise `Changed` instead.

Generated cells the person deletes or moves stay deleted or where they put them. Call `DrawioView.ForgetGeneratedEdits()`
when the layer starts over and ids are reused. `LiveEditsApplied` is raised after each batch.

## Click-through — `IDiagramLinkHandler`

`DiagramLink(Kind, Id)` is a reference from a cell to a Silo construct; `ToUrl()` gives the value for the cell's `link`.
When one is clicked, `DiagramLinks.Handlers` are asked in order and the first `Handle(link, view)` that returns true wins.
Add your handler to that list. `DrawioView.LinkActivated` is raised as well.

## Menus and cell events

`DrawioView.PluginMenu` (the Silo menu in the menu bar) and `PluginContextItems` (entries on the context menu of Silo cells)
are lists of `DrawioPluginMenuItem(Id, Label, Shortcut?, Separator?)`; `PluginSetMenuAsync` / `PluginSetContextItemsAsync`
change them while the editor is open.

The chosen `Id` and cell events (selection, click, double-click) arrive in the stubs in `DrawioView.Handlers.cs`, which is
the place to add handling. They run on the UI thread after Silo's own bookkeeping, so they can stay empty.

For acting on the open diagram, `DrawioView` offers async methods such as `PluginSelectCellAsync`,
`PluginScrollToCellAsync`, `PluginSetCellAttributeAsync`, `PluginGetSelectionAsync`, `PluginAddStencilAsync` (custom
shapes, used as `shape=<name>`), `GetXmlAsync`, `ExportAsync` and `FitAsync`.

## Storing diagrams — `IDiagramStore`

A Silo can hold diagrams when its `IDatabaseProvider` implements `IDiagramStore` (`ListDiagrams`, `GetDiagram`,
`SaveDiagram`, `DeleteDiagram`). Raise `DiagramEvents.RaiseChanged()` after changing diagrams outside the canvas so every
diagram list stays current; `DiagramActions` covers open, new, rename and delete.

## Compatibility

The contracts above are Silo's and are the stable surface. draw.io's internals are not: Silo pins the draw.io release and
each plugin feature is guarded, so an unsupported feature is reported (`silo.error`, *Plugin debug log*) rather than
breaking the editor.
