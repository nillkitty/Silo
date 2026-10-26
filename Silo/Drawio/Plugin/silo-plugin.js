/*
 * Silo plugin for draw.io
 * ========================
 *
 * Injected by Silo (WebView2 AddScriptToExecuteOnDocumentCreated) into every document the editor view creates, BEFORE
 * draw.io's own scripts run. Options arrive as window.__SILO_PLUGIN_OPTIONS__ (see DrawioOptions.ToPluginOptions).
 *
 * How it hooks in
 *   draw.io's only long-term supported extension point is `Draw.loadPlugin(function (ui) { ... })`: the callback runs once,
 *   in the App constructor, with the EditorUi. Normally plugins are listed in the `p=` URL parameter (registry ids only) or
 *   loaded from a URL when custom plugins are allowed; Silo skips that and polls for `Draw.loadPlugin` itself.
 *   (draw.io is Apache-2.0 open source, but this API is documented only by its source and the examples under
 *   src/main/webapp/plugins -- see Drawio/PLUGIN-ARCHITECTURE.md.)
 *
 * Host <-> plugin messages
 *   plugin -> Silo : parent.postMessage(JSON.stringify({ event: 'silo.*', ... }), '*'); the Silo host page relays it.
 *   Silo -> plugin : the host page calls window.SiloPlugin.receive({ siloPlugin: true, op, requestId, ... }); the answer is
 *                    { event: 'silo.result', requestId, ok, result | error }.
 *
 * Every feature is installed inside its own guard: if one fails (draw.io changed an internal), the others still work and a
 * silo.error is reported. Features can be switched off with the options in DrawioOptions (category "Silo plugin").
 *
 * Quick map of the draw.io internals used here (all VERIFY against the pinned release if something stops working):
 *   ui.menubar.addMenu / ui.menus.put(new Menu(fn))   menu bar menus
 *   ui.actions.addAction(name, fn)                     named actions (also reachable via invokeAction)
 *   ui.menus.createPopupMenu                           context menu (wrapped)
 *   graph.getSelectionModel() mxEvent.CHANGE           selection
 *   graph.addListener(mxEvent.CLICK / DOUBLE_CLICK)    clicks
 *   ui.sidebar.addPalette(id, title, expanded, fn)     shape sidebar sections
 *   mxStencilRegistry.addStencil(name, new mxStencil)  custom shapes
 *   mxCellRenderer.defaultShapes[name].prototype.customProperties   typed Format-panel properties
 *   StyleFormatPanel.prototype.init (wrapped)          the Silo Properties section above draw.io's Property grid
 *   ui.formatContainer / ui.formatWidth / ui.refresh   the drag handle that resizes the Format panel
 */
(function () {
    'use strict';

    // The host page (silo-host.html) is not the editor.
    if (/silo-host\.html$/i.test(location.pathname)) return;
    if (window.SiloPlugin) return;

    var options = window.__SILO_PLUGIN_OPTIONS__ || {};
    var state = { ui: null, graph: null, menu: [], palettes: [], paletteIds: {}, contextItems: [] };

    // ---- messaging -----------------------------------------------------------------------------------------------

    function post(event, data) {
        var message = data || {};
        message.event = event;
        try {
            parent.postMessage(JSON.stringify(message), '*');
        } catch (e) { /* no host: running outside Silo */ }
    }

    function log(text) {
        if (options.debug) {
            try { console.log('[silo-plugin] ' + text); } catch (e) { }
            post('silo.log', { message: String(text) });
        }
    }

    function guard(section, fn) {
        try {
            return fn();
        } catch (e) {
            post('silo.error', { section: section, message: e && e.message ? e.message : String(e) });
            log('section "' + section + '" failed: ' + e);
            return undefined;
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    function cellInfo(cell) {
        var model = state.graph.getModel();
        var attributes = {};
        var value = cell.value;
        if (value && value.attributes) {
            for (var i = 0; i < value.attributes.length; i++) {
                var a = value.attributes[i];
                attributes[a.nodeName] = a.nodeValue;
            }
        }

        return {
            id: cell.id,
            label: state.graph.getLabel(cell),
            style: cell.style || '',
            vertex: model.isVertex(cell),
            edge: model.isEdge(cell),
            attributes: attributes
        };
    }

    function getAttribute(cell, key) {
        return cell && cell.value && cell.value.getAttribute ? cell.value.getAttribute(key) : null;
    }

    function hasSiloData(cell) {
        if (!cell || !cell.value || !cell.value.attributes) return false;
        for (var i = 0; i < cell.value.attributes.length; i++)
            if (/^silo[-_]/i.test(cell.value.attributes[i].nodeName)) return true;
        return false;
    }

    function findCell(id) {
        return state.graph.getModel().getCell(id);
    }

    // ---- features ------------------------------------------------------------------------------------------------

    function installMenu(ui) {
        if (options.menu === false) return;

        var mxResources = window.mxResources;
        mxResources.parse('silo=Silo');

        ui.menus.put('silo', new window.Menu(function (menu, parent) {
            for (var i = 0; i < state.menu.length; i++) {
                var item = state.menu[i];
                if (item.separator) {
                    menu.addSeparator(parent);
                    continue;
                }

                (function (entry) {
                    var actionName = 'silo.' + entry.id;
                    if (!ui.actions.get(actionName))
                        ui.actions.addAction(actionName, function () { post('silo.menu', { id: entry.id }); });

                    menu.addItem(entry.label, null, function () { post('silo.menu', { id: entry.id }); }, parent, null, true);
                })(item);
            }
        }));

        // Add the menu bar entry once; its contents are rebuilt from state.menu every time it opens. draw.io keeps its
        // status area (and in some themes its buttons) at the end of the bar, and an entry added after them wraps out of
        // sight below the bar, so move it in front of them, next to Help.
        var elt = ui.menubar.addMenu('Silo', ui.menus.get('silo').funct);
        [ui.statusContainer, ui.buttonContainer].some(function (after) {
            if (elt && after && after.parentNode === elt.parentNode) {
                elt.parentNode.insertBefore(elt, after);
                return true;
            }
            return false;
        });

        // Look like draw.io's own entries (EditorUi.createMenu restyles them after addMenu; a bare addMenu entry is
        // styled as a whole bar and fills the width): copy the class of the entry now before it (Help).
        var sibling = elt && elt.previousElementSibling;
        if (elt && sibling && sibling.className) elt.className = sibling.className;
    }

    function installContextMenu(ui) {
        if (options.contextMenu === false) return;

        var original = ui.menus.createPopupMenu;
        ui.menus.createPopupMenu = function (menu, cell, evt) {
            original.apply(this, arguments);

            guard('contextMenu', function () {
                var target = cell || state.graph.getSelectionCell();
                if (!hasSiloData(target) || state.contextItems.length === 0) return;

                menu.addSeparator();
                for (var i = 0; i < state.contextItems.length; i++) {
                    (function (entry) {
                        menu.addItem(entry.label, null, function () {
                            var info = cellInfo(target);
                            post('silo.contextAction', {
                                id: entry.id,
                                cellId: info.id,
                                label: info.label,
                                style: info.style,
                                attributes: info.attributes
                            });
                        });
                    })(state.contextItems[i]);
                }
            });
        };
    }

    function installSelectionEvents(graph) {
        if (options.selectionEvents === false) return;

        graph.getSelectionModel().addListener(window.mxEvent.CHANGE, function () {
            guard('selection', function () {
                var cells = graph.getSelectionCells().map(cellInfo);
                post('silo.selection', { cells: cells });
            });
        });
    }

    function installClickEvents(graph) {
        if (options.clickEvents === false) return;

        function report(name, evt) {
            guard(name, function () {
                var cell = evt.getProperty('cell');
                if (!cell) return;

                var info = cellInfo(cell);
                var me = evt.getProperty('event');
                post(name, {
                    cellId: info.id,
                    label: info.label,
                    style: info.style,
                    attributes: info.attributes,
                    ctrl: !!(me && me.ctrlKey),
                    shift: !!(me && me.shiftKey)
                });
            });
        }

        graph.addListener(window.mxEvent.CLICK, function (sender, evt) { report('silo.click', evt); });
        graph.addListener(window.mxEvent.DOUBLE_CLICK, function (sender, evt) { report('silo.dblclick', evt); });
    }

    var paletteFunctions = {};

    function applyPalettes() {
        if (options.palettes === false || !state.ui || !state.ui.sidebar) return;

        var sidebar = state.ui.sidebar;
        state.palettes.forEach(function (palette) {
            var id = 'silo-' + palette.id;

            // The palette function reads the current entries at the time the section is (re)built.
            paletteFunctions[id] = palette;

            if (!state.paletteIds[id]) {
                state.paletteIds[id] = true;
                sidebar.addPalette(id, palette.title, palette.expanded !== false, function (content) {
                    var current = paletteFunctions[id];
                    (current.entries || []).forEach(function (entry) {
                        content.appendChild(sidebar.createVertexTemplate(
                            entry.style, entry.width, entry.height, entry.value || '', entry.title || '', true, null, true));
                    });
                });
            } else if (sidebar.palettes && sidebar.palettes[id]) {
                // Already added: rebuild its content in place.
                var wrapper = sidebar.palettes[id][1];
                if (wrapper && wrapper.firstChild) {
                    while (wrapper.firstChild) wrapper.removeChild(wrapper.firstChild);
                    paletteFunctions[id].entries.forEach(function (entry) {
                        wrapper.appendChild(sidebar.createVertexTemplate(
                            entry.style, entry.width, entry.height, entry.value || '', entry.title || '', true, null, true));
                    });
                }
            }
        });
    }

    // ---- properties panel ----------------------------------------------------------------------------------------
    //
    // A Silo section in the Format panel's Style tab, put ABOVE draw.io's own Property grid (which stays, so the two can be
    // compared). It lists the typed properties draw.io knows for the selection (selection state customProperties: the shape's
    // own list plus Editor.commonVertexProperties / commonEdgeProperties), grouped by category or by where each value comes
    // from, each with an icon, with search, the style key names on request, and a reset for values set on the cell itself.
    //
    // Every row starts with its layering button: it shows how the token takes part in Silo's Copy Style / Paste Style (the two
    // toolbar buttons), and opens the token's Advanced Property Menu:
    //   Copy Style   Not included | Present values only | Include effective defaults
    //   Paste Style  Not affected | Copied styles only | Replace non-copied with default
    //   Sources      Data binding (silo-style.<key>, edit or remove) and the defaults below, each with its value for this
    //                cell; click one to set the cell to that value; drag the handles to reorder them. The order is the order
    //                "a default" is looked up in: by Replace non-copied with default, and by Use default.
    //
    // Where a value comes from (for the first selected cell):
    //   binding   a silo-style.<key> attribute on the cell (Silo's data bindings write the value)
    //   local     the key=value is in the cell's own style string
    //   current   draw.io's current default style (what new shapes get; Set as Default Style changes it)
    //   diagram   a named style the cell's style starts with (e.g. "swimlane;"), from the diagram's theme stylesheet
    //   theme     the stylesheet's default vertex / edge style
    //   config    the configured default (Graph.prototype.defaultVertexStyle / defaultEdgeStyle, from the configure handshake)
    //   factory   nothing sets it: the shape's own default (the property's defVal / getDefaultValue)
    //
    // draw.io internals used (VERIFY on a version change): StyleFormatPanel.prototype.init (wrapped, as draw.io's own
    // diagramly/Editor.js does), this.editorUi.getSelectionState().customProperties / .style / .cells, this.createPanel(),
    // the grid's table.geProperties (to find where to insert), graph.getStylesheet().styles / getDefaultVertexStyle(),
    // graph.currentVertexStyle / currentEdgeStyle, Graph.prototype.defaultVertexStyle, graph.setCellStyles,
    // graph.setAttributeForCell, FilenameDialog, Editor.*Image / Format.*Image icons and the geAdaptiveAsset class (dark
    // mode inverts those icons).

    var PROPS_VIEW_KEY   = 'silo.propertiesPanel.view';
    var PROPS_TOKENS_KEY = 'silo.propertiesPanel.tokens';

    function readStored(key, fallback) {
        try {
            var kept = JSON.parse(localStorage.getItem(key) || 'null');
            return kept === null ? fallback : kept;
        } catch (e) {
            return fallback;
        }
    }

    function writeStored(key, value) {
        try { localStorage.setItem(key, JSON.stringify(value)); } catch (e) { /* kept for this session only */ }
    }

    var propsView = (function () {
        var view = { open: true, all: false, group: 'category', names: false, layering: true, target: false, collapsed: {}, filter: '' };
        var kept = readStored(PROPS_VIEW_KEY, {});
        for (var k in view) if (kept[k] !== undefined && k !== 'filter') view[k] = kept[k];
        return view;
    })();

    function saveView() {
        var copy = {};
        for (var k in propsView) if (k !== 'filter') copy[k] = propsView[k];
        writeStored(PROPS_VIEW_KEY, copy);
    }

    // per token: { copy: 'none' | 'present' | 'effective', paste: 'none' | 'copied' | 'replace', sources: [...] }
    var tokenSettings = readStored(PROPS_TOKENS_KEY, {});

    var COPY_MODES = [
        { id: 'none', label: 'Not included', icon: 'copyImage', dim: true, tip: 'Copy Style leaves this token out' },
        { id: 'present', label: 'Present values only', icon: 'copyImage', tip: 'Copy Style takes it only when it is set on the cell itself' },
        { id: 'effective', label: 'Include effective defaults', icon: 'shadowImage', tip: 'Copy Style takes the value in effect, wherever it comes from' }
    ];

    var PASTE_MODES = [
        { id: 'none', label: 'Not affected', icon: 'lockedImage', tip: 'Paste Style never changes this token' },
        { id: 'copied', label: 'Copied styles only', icon: 'pasteImage', tip: 'Paste Style sets it when the copied style has it' },
        { id: 'replace', label: 'Replace non-copied with default', icon: 'removeFormatImage', tip: 'Paste Style sets it when copied, else puts the first default from Sources' }
    ];

    var SOURCE_LIST = {
        binding: { label: 'Data Binding', icon: 'thinDataImage', tip: 'A silo-style.<key> attribute on the cell: Silo writes the value from the bound data' },
        current: { label: 'Current Default Style', icon: 'formatImage', tip: 'What new shapes get (Set as Default Style changes it)' },
        diagram: { label: 'Diagram Default', icon: 'thinNoteImage', tip: 'A named style the cell uses, from the diagram\'s stylesheet' },
        theme: { label: 'Theme Default', icon: 'contrastImage', tip: 'The theme\'s default vertex or edge style' },
        config: { label: 'Configuration Default', icon: 'gearImage', tip: 'The default set by the editor\'s configuration' },
        factory: { label: 'Factory Default (per Shape)', icon: 'thinShapesImage', tip: 'The shape\'s own default' }
    };
    var DEFAULT_SOURCE_ORDER = ['binding', 'current', 'diagram', 'theme', 'config', 'factory'];

    // the groups when grouping by where values come from
    var SCOPES = {
        binding: { heading: 'Bound to data', icon: 'thinDataImage', order: 0 },
        local: { heading: 'Set on this cell', icon: 'editImage', order: 1 },
        current: { heading: 'Current default style', icon: 'formatImage', order: 2 },
        diagram: { heading: 'Diagram default (named style)', icon: 'thinNoteImage', order: 3 },
        theme: { heading: 'Theme default', icon: 'contrastImage', order: 4 },
        config: { heading: 'Configuration default', icon: 'gearImage', order: 5 },
        factory: { heading: 'Shape (factory) default', icon: 'thinShapesImage', order: 6 }
    };

    function settingsOf(key) {
        var s = tokenSettings[key] || {};
        return {
            copy: s.copy || 'present',
            paste: s.paste || 'copied',
            sources: Array.isArray(s.sources) && s.sources.length === DEFAULT_SOURCE_ORDER.length ? s.sources.slice() : DEFAULT_SOURCE_ORDER.slice()
        };
    }

    function saveSettings(key, settings) {
        tokenSettings[key] = settings;
        writeStored(PROPS_TOKENS_KEY, tokenSettings);
    }

    // Categories by style key, checked in order (the first match wins), each with its icon.
    var CATEGORY_RULES = [
        ['Image', 'imageImage', /^image|^imageAspect$/i],
        ['Label', 'textFormatImage', /^(noLabel|overflow|labelPadding|labelPosition|verticalLabelPosition|labelBackgroundColor|labelBorderColor|labelWidth|textDirection|textOpacity|horizontal|autosize|autosizeText|shapeInside.*|spacing.*|whiteSpace|html|placeholders|movableLabel|boundedLbl|fontColor|fontSize|fontFamily|fontStyle|align|verticalAlign)$/i],
        ['Connections', 'linkImage', /^(portConstraint|portConstraintRotation|connectable|allowArrows|snapToPoint|outlineConnect|perimeterSpacing|sourcePortConstraint|targetPortConstraint|jettySize|orthogonal|orthogonalLoop|edgeStyle|elbow|curved|bezier|bendable|segment|noJump|jumpLayers|jumpStyle|jumpSize|anchorPointDirection|ignoreEdge|flowAnimation.*|arrowSize|arrowWidth|startWidth|endWidth|start(Arrow|Fill|Size|FillColor|StrokeColor)|end(Arrow|Fill|Size|FillColor|StrokeColor)|sourcePerimeterSpacing|targetPerimeterSpacing|fixedPointSpacing)$/i],
        ['Container', 'addBoxImage', /^(container|containerType|dropTarget|collapsible|collapsedPoints|foldingIcon.*|recursiveResize|resizeParent.*|resizeLast.*|expand|expandToFront|contract|part|childLayout|stackLayout|horizontalStack|resizeChildren|moveCells|treeFolding|treeMoving|marginBottom|marginTop|marginLeft|marginRight|fixedRows|rowLines|columnLines|colspan|rowspan|tableRender|autosizeGrid)$/i],
        ['Behavior', 'selectImage', /^(editable|movable|resizable|resizeWidth|resizeHeight|rotatable|cloneable|deletable|locked|lockedGroup|lockedGroupIcon|pointerEvents|selectParentFirst|editIcon|moveIcon|connectIcon|metaEdit|backgroundOutline|enumerate|enumerateValue)$/i],
        ['Fill and Line', 'fillColorImage', /(Color|Opacity|^darkOpacity2$|^fillStyle$|^fillWeight$|^hachure.*|^jiggle$|^curveFitting$|^simplification$|^disableMultiStroke.*|^sketch.*|^comic$|^dashed$|^dash.*|^strokeWidth$|^linecap$|^linejoin$|^miterlimit$|^shadow|^glass$|^fixDash$|^fixedDash$)/i],
        ['Shape', 'shapesImage', /.*/]
    ];

    // An icon per token (draw.io's own where it has one; '#side-*' are drawn here). Tokens not listed get their category's icon.
    var TOKEN_ICONS = {
        arcSize: 'Format.roundedBendImage', absoluteArcSize: 'Format.sharpBendImage', rounded: 'Format.curvedBendImage',
        roundedPerimeter: 'Format.ovalMarkerImage', perimeter: 'zoomFitImage', size: 'fullscreenImage', fixedSize: 'thinRectangleImage',
        fixedWidth: 'alignHorizontalCenterImage', double: 'duplicateImage', margin: 'indentImage', footerSize: 'horizontalRuleImage',
        footerColor: 'Format.filledEdgeImage', startSize: 'alignTopImage', top: '#side-top', bottom: '#side-bottom', left: '#side-left',
        right: '#side-right', dx: '#side-left', dy: '#side-top', direction: '#compass', swimlaneHead: 'alignVerticalTopImage',
        swimlaneBody: 'alignVerticalBottomImage', separatorColor: 'Format.pipeEdgeImage', fixedHeader: 'alignBottomImage',
        base: 'Format.taperedArrowImage', position: 'Format.boxMarkerImage', position2: 'Format.diamondThinMarkerImage', tipX: 'arrowRightImage',
        tipY: 'arrowDownImage', width: 'alignHorizontalRightImage', zigzagOffset: 'Format.horizontalIsometricImage',
        centerRadius: 'Format.circleMarkerImage', centerColor: 'Format.ovalFilledMarkerImage', tabHeight: 'addRowAboveImage',
        tabWidth: 'addColumnLeftImage', tabPosition: 'alignHorizontalLeftImage', noLabel: 'hiddenImage', overflow: 'thinExpandImage',
        shapeInsideShape: 'thinShapesImage', shapeInsidePadding: 'outdentImage', autosize: 'autosizeImage', movableLabel: 'textFormatImage',
        horizontal: 'verticalTextImage', boundedLbl: 'Format.connectionImage', imageAspect: 'imageImage', imageAlign: 'alignLeftImage',
        imageVerticalAlign: 'alignMiddleImage', imageWidth: 'alignJustifyImage', imageHeight: 'alignCenterImage', fillOpacity: 'opacityImage',
        strokeOpacity: 'strokeColorImage', fixDash: 'Format.dashMarkerImage', dashGap: 'Format.halfCircleMarkerImage',
        dashOffset: 'Format.baseDashMarkerImage', dashPattern: 'Format.crossMarkerImage', linecap: 'Format.circlePlusMarkerImage',
        linejoin: 'Format.blockThinMarkerImage', miterlimit: 'Format.diamondMarkerImage', fillWeight: 'backgroundColorImage',
        hachureAngle: 'Format.verticalIsometricImage', hachureGap: 'Format.ERmanyMarkerImage', jiggle: 'freehandImage',
        curveFitting: 'Format.curvedImage', simplification: 'removeFormatImage', disableMultiStroke: 'strikethroughImage',
        disableMultiStrokeFill: 'underlineImage', sketchStyle: 'sparklesImage', darkOpacity: 'contrastImage', darkOpacity2: 'thinDarkImage',
        comic: 'thinGestureImage', portConstraint: 'Format.EROneMarkerImage', portConstraintRotation: 'syncImage', connectable: 'linkImage',
        allowArrows: 'Format.arrowImage', snapToPoint: 'Format.ERmandOneMarkerImage', outlineConnect: 'outlineImage',
        sourcePortConstraint: 'arrowUpImage', targetPortConstraint: 'Format.ERzeroToOneMarkerImage', bezier: 'Format.ERoneToManyMarkerImage',
        noJump: 'Format.straightImage', jumpLayers: 'layersImage', orthogonal: 'Format.orthogonalImage', bendable: 'Format.wireEdgeImage',
        segment: 'Format.horizontalElbowImage', anchorPointDirection: 'sendImage', ignoreEdge: 'Format.noMarkerImage',
        flowAnimationDirection: 'doubleArrowDownImage', flowAnimationDuration: 'calendarImage', flowAnimationTimingFunction: 'undoImage',
        arrowSize: 'Format.blockFilledMarkerImage', arrowWidth: 'Format.simpleArrowImage', startWidth: 'Format.classicThinMarkerImage',
        endWidth: 'Format.doubleBlockMarkerImage', startFill: 'Format.openFilledMarkerImage', endFill: 'Format.diamondFilledMarkerImage',
        startFillColor: 'Format.classicFilledMarkerImage', endFillColor: 'Format.blockThinFilledMarkerImage',
        startStrokeColor: 'Format.classicMarkerImage', endStrokeColor: 'Format.blockMarkerImage', perimeterSpacing: 'spacingImage',
        orthogonalLoop: 'Format.verticalElbowImage', jettySize: 'Format.linkEdgeImage', fixedPointSpacing: 'Format.asyncMarkerImage',
        container: 'addBoxImage', containerType: 'tableImage', dropTarget: 'toBackImage', collapsible: 'expandLessImage',
        foldingIconSize: 'minusImage', recursiveResize: 'fullscreenExitImage', expand: 'expandMoreImage', expandToFront: 'toFrontImage',
        contract: 'zoomOutImage', collapsedPoints: 'Format.ERzeroToManyMarkerImage', part: 'extensionImage', resizeLast: 'addColumnRightImage',
        resizeLastRow: 'addRowBelowImage', rowLines: 'Format.doubleBlockFilledMarkerImage', columnLines: 'removeColumnImage',
        fixedRows: 'removeRowImage', colspan: 'dockRightImage', rowspan: 'rightPanelOpenImage', tableRender: 'thinGridImage',
        autosizeGrid: 'zoomInImage', moveCells: 'moveImage', treeFolding: 'layoutImage', treeMoving: 'shareImage', editable: 'editImage',
        movable: 'thinOpenImage', resizable: 'compareImage', resizeWidth: 'Format.asyncFilledMarkerImage', resizeHeight: 'doubleArrowUpImage',
        rotatable: 'refreshImage', cloneable: 'copyImage', deletable: 'trashImage', lockedGroup: 'lockedImage', lockedGroupIcon: 'unlockedImage',
        pointerEvents: 'selectImage', selectParentFirst: 'groupImage', editIcon: 'thinFormatImage', moveIcon: 'thinSparklesImage',
        connectIcon: 'plusImage', metaEdit: 'tooltipImage', backgroundOutline: 'thinNoteImage', enumerate: 'orderedListImage',
        enumerateValue: 'tagsImage'
    };

    // icons draw.io does not have, drawn as data URIs in its line style
    var DRAWN = {
        'side-top': 'M4 4h16', 'side-bottom': 'M4 20h16', 'side-left': 'M4 4v16', 'side-right': 'M20 4v16'
    };

    function drawnIcon(name) {
        var svg;
        if (DRAWN[name])
            svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="#000"><path d="M4 4h16v16H4z" stroke-width="1.2" stroke-dasharray="2 2" opacity=".5"/><path d="' + DRAWN[name] + '" stroke-width="2.6" stroke-linecap="square"/></svg>';
        else if (name === 'compass')
            svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9" fill="none" stroke="#000" stroke-width="1.6"/><path d="M12 5l3 7h-6z" fill="#000"/><path d="M12 19l-3-7h6z" fill="none" stroke="#000" stroke-width="1.2"/></svg>';
        return svg ? 'data:image/svg+xml;base64,' + btoa(svg) : null;
    }

    function iconSource(name) {
        if (!name) return null;
        if (name.charAt(0) === '#') return drawnIcon(name.substring(1));
        if (name.indexOf('Format.') === 0) {
            var f = window.Format && window.Format[name.substring(7)];
            return f ? (f.src || f) : null;
        }
        var e = window.Editor && window.Editor[name];
        return e ? (e.src || e) : null;
    }

    function categoryOf(key) {
        for (var i = 0; i < CATEGORY_RULES.length; i++)
            if (CATEGORY_RULES[i][2].test(key)) return CATEGORY_RULES[i];
        return CATEGORY_RULES[CATEGORY_RULES.length - 1];
    }

    function tokenIcon(key) {
        return TOKEN_ICONS[key] || categoryOf(key)[1];
    }

    /** The cell's own key=value pairs and the names of the named styles its style starts with. */
    function parseStyle(style) {
        var own = {}, named = [];
        (style || '').split(';').forEach(function (token) {
            if (!token) return;
            var eq = token.indexOf('=');
            if (eq < 0) named.push(token);
            else own[token.substring(0, eq)] = token.substring(eq + 1);
        });
        return { own: own, named: named };
    }

    /** The value a source gives a token for a cell (undefined: it gives none). */
    function sourceValue(graph, cell, key, prop, sstate, panel, source) {
        var model = graph.getModel();
        var edge = model.isEdge(cell);
        switch (source) {
            case 'binding': {
                var template = getAttribute(cell, 'silo-style.' + key);
                return template === null || template === undefined || template === '' ? undefined : template;
            }
            case 'local':
                return parseStyle(model.getStyle(cell)).own[key];
            case 'current': {
                var current = edge ? graph.currentEdgeStyle : graph.currentVertexStyle;
                return current ? current[key] : undefined;
            }
            case 'diagram': {
                var sheet = graph.getStylesheet();
                var named = parseStyle(model.getStyle(cell)).named;
                for (var i = named.length - 1; i >= 0; i--) {
                    var style = sheet.styles && sheet.styles[named[i]];
                    if (style && style[key] !== undefined) return style[key];
                }
                return undefined;
            }
            case 'theme': {
                var theme = edge ? graph.getStylesheet().getDefaultEdgeStyle() : graph.getStylesheet().getDefaultVertexStyle();
                return theme ? theme[key] : undefined;
            }
            case 'config': {
                var config = window.Graph && (edge ? window.Graph.prototype.defaultEdgeStyle : window.Graph.prototype.defaultVertexStyle);
                return config ? config[key] : undefined;
            }
            case 'factory':
                if (!prop) return undefined;
                return typeof prop.getDefaultValue === 'function'
                    ? guard('propertiesPanel.default', function () { return prop.getDefaultValue(sstate, panel); })
                    : prop.defVal;
        }
        return undefined;
    }

    /** Where the value in effect comes from: binding, local, diagram (named style), theme, or factory. */
    function scopeOf(graph, cell, key) {
        if (sourceValue(graph, cell, key, null, null, null, 'binding') !== undefined) return 'binding';
        if (sourceValue(graph, cell, key, null, null, null, 'local') !== undefined) return 'local';
        if (sourceValue(graph, cell, key, null, null, null, 'diagram') !== undefined) return 'diagram';
        if (sourceValue(graph, cell, key, null, null, null, 'theme') !== undefined) return 'theme';
        return 'factory';
    }

    /** The first default, in the token's source order, that has a value (the binding is not a default; undefined: none). */
    function firstDefault(graph, cell, key, prop, sstate, panel) {
        var order = settingsOf(key).sources;
        for (var i = 0; i < order.length; i++) {
            if (order[i] === 'binding') continue;
            var v = sourceValue(graph, cell, key, prop, sstate, panel, order[i]);
            if (v !== undefined && v !== null) return v;
        }
        return undefined;
    }

    function icon(name, title, size, opacity) {
        var img = document.createElement('img');
        var src = iconSource(name);
        if (src) img.src = src;
        img.className = 'geAdaptiveAsset';
        img.title = title || '';
        img.alt = title || '';
        img.style.cssText = 'width:' + (size || 16) + 'px;height:' + (size || 16) + 'px;vertical-align:middle;flex:0 0 auto;' +
            (opacity !== undefined ? 'opacity:' + opacity + ';' : '');
        return img;
    }

    function toolButton(name, title, on, click, size) {
        var b = document.createElement('button');
        b.type = 'button';
        b.title = title;
        b.setAttribute('aria-label', title);
        b.setAttribute('aria-pressed', on ? 'true' : 'false');
        var px = size || 24;
        b.style.cssText = 'width:' + (px + 2) + 'px;height:' + px + 'px;padding:0;margin-left:2px;border-radius:4px;cursor:pointer;display:inline-flex;' +
            'align-items:center;justify-content:center;flex:0 0 auto;border:1px solid ' + (on ? '#7a4fd6' : 'transparent') + ';background:' +
            (on ? 'rgba(122,79,214,0.18)' : 'transparent') + ';';
        b.appendChild(icon(name, title, px - 8));
        b.addEventListener('click', function (e) { e.preventDefault(); e.stopPropagation(); click(e); });
        return b;
    }

    /** The editor for one property: checkbox, list, number, color or text, by the property's type. */
    function valueEditor(prop, value, apply) {
        var input;
        var type = prop.type || 'string';
        if (type === 'bool') {
            input = document.createElement('input');
            input.type = 'checkbox';
            input.checked = value === true || value === '1' || value === 1 || value === 'true';
            input.addEventListener('change', function () { apply(input.checked ? '1' : '0'); });
        } else if (type === 'enum' && prop.enumList) {
            input = document.createElement('select');
            prop.enumList.forEach(function (item) {
                var option = document.createElement('option');
                option.value = item.val;
                option.textContent = item.dispName || item.val;
                if (String(item.val) === String(value)) option.selected = true;
                input.appendChild(option);
            });
            input.addEventListener('change', function () { apply(input.value); });
        } else if (type === 'int' || type === 'float') {
            input = document.createElement('input');
            input.type = 'number';
            if (prop.min !== undefined) input.min = prop.min;
            if (prop.max !== undefined) input.max = prop.max;
            input.step = type === 'int' ? '1' : 'any';
            input.value = value === undefined || value === null ? '' : value;
            input.addEventListener('change', function () { if (input.value !== '') apply(input.value); });
        } else if (type === 'color' && /^#[0-9a-f]{6}$/i.test(String(value || ''))) {
            input = document.createElement('input');
            input.type = 'color';
            input.value = value;
            input.addEventListener('change', function () { apply(input.value); });
        } else if (type === 'readOnly' || type === 'staticArr' || type === 'dynamicArr' || prop.dependentProps) {
            input = document.createElement('span');
            input.textContent = value === undefined || value === null ? '' : String(value);
            input.style.opacity = '0.7';
        } else {
            input = document.createElement('input');
            input.type = 'text';
            input.value = value === undefined || value === null ? '' : value;
            input.addEventListener('change', function () { apply(input.value); });
        }

        input.setAttribute('aria-label', prop.dispName || prop.name);
        // draw.io styles the Format panel's own inputs (positions, widths, alignment): ours undo that and fill their cell
        if (input.tagName !== 'SPAN' && input.type !== 'checkbox')
            input.style.cssText = 'position:static;float:none;display:block;margin:0;width:100%;max-width:100%;box-sizing:border-box;' +
                'text-align:left;font-size:11px;height:20px;padding:0 3px;';
        else if (input.type === 'checkbox')
            input.style.cssText = 'position:static;float:none;margin:0;';
        return input;
    }

    // ---- Silo Copy Style / Paste Style ------------------------------------------------------------------------------

    var styleClipboard = null; // { values: { key: value }, from: cell id }

    /** Copies the tokens of the first selected cell the way each token's Copy Style setting says. */
    function siloCopyStyle(graph, rows) {
        var cell = graph.getSelectionCell();
        if (!cell) return;

        var values = {};
        rows.forEach(function (row) {
            var mode = settingsOf(row.key).copy;
            if (mode === 'present') {
                var own = parseStyle(graph.getModel().getStyle(cell)).own[row.key];
                if (own !== undefined) values[row.key] = own;
            } else if (mode === 'effective' && row.value !== undefined && row.value !== null) {
                values[row.key] = String(row.value);
            }
        });

        styleClipboard = { values: values, from: cell.id };
        post('silo.log', { message: 'Silo Copy Style: ' + Object.keys(values).length + ' tokens' });
    }

    /** Pastes onto every selected cell the way each token's Paste Style setting says. */
    function siloPasteStyle(graph, rows, sstate, panel) {
        if (!styleClipboard) return;

        var cells = graph.getSelectionCells();
        var keys = {};
        rows.forEach(function (row) { keys[row.key] = row.prop; });
        for (var k in styleClipboard.values) if (!(k in keys)) keys[k] = null;

        graph.getModel().beginUpdate();
        try {
            cells.forEach(function (cell) {
                for (var key in keys) {
                    var mode = settingsOf(key).paste;
                    if (mode === 'none') continue;

                    if (Object.prototype.hasOwnProperty.call(styleClipboard.values, key))
                        graph.setCellStyles(key, styleClipboard.values[key], [cell]);
                    else if (mode === 'replace') {
                        var fallback = firstDefault(graph, cell, key, keys[key], sstate, panel);
                        graph.setCellStyles(key, fallback === undefined ? null : String(fallback), [cell]);
                    }
                }
            });
        } finally {
            graph.getModel().endUpdate();
        }
    }

    // ---- the Advanced Property Menu --------------------------------------------------------------------------------

    var openMenu = null;

    function closeMenu() {
        if (!openMenu) return;
        openMenu.remove();
        document.removeEventListener('mousedown', openMenu._outside, true);
        document.removeEventListener('keydown', openMenu._escape, true);
        openMenu = null;
    }

    function menuColors(anchor) {
        var host = anchor.closest('.geFormatContainer') || document.body;
        var style = getComputedStyle(host);
        var bg = style.backgroundColor && style.backgroundColor !== 'rgba(0, 0, 0, 0)' ? style.backgroundColor : '#ffffff';
        return { bg: bg, fg: style.color || '#000', dark: document.body.classList.contains('geDarkMode') || /\bgeDark/.test(document.documentElement.className) };
    }

    /** The token's menu: how Copy Style and Paste Style treat it, and its sources (each with its value here). */
    function showTokenMenu(anchor, ctx) {
        closeMenu();
        var colors = menuColors(anchor);
        var settings = settingsOf(ctx.key);
        var menu = document.createElement('div');
        menu.setAttribute('role', 'menu');
        menu.style.cssText = 'position:fixed;z-index:100000;min-width:270px;max-width:340px;padding:0 0 6px;border-radius:8px;overflow:hidden;' +
            'font:12px Helvetica,Arial,sans-serif;box-shadow:0 6px 24px rgba(0,0,0,0.25);border:1px solid rgba(128,128,128,0.45);' +
            'background:' + colors.bg + ';color:' + colors.fg + ';';

        var head = document.createElement('div');
        head.style.cssText = 'padding:8px 10px;font-weight:bold;font-size:13px;border-bottom:2px solid rgba(128,128,128,0.35);display:flex;align-items:center;gap:6px;';
        head.appendChild(icon(tokenIcon(ctx.key), '', 18));
        head.appendChild(document.createTextNode('Advanced Property Menu — ' + ctx.label));
        menu.appendChild(head);

        function section(title, iconName) {
            var s = document.createElement('div');
            s.style.cssText = 'padding:7px 10px 3px;font-weight:bold;font-style:italic;display:flex;align-items:center;gap:6px;' +
                'border-top:1px solid rgba(128,128,128,0.25);margin-top:2px;';
            s.appendChild(icon(iconName, '', 16));
            s.appendChild(document.createTextNode(title));
            menu.appendChild(s);
        }

        function item(parts, click, tip, extra) {
            var row = document.createElement('div');
            row.setAttribute('role', 'menuitem');
            row.tabIndex = -1;
            row.title = tip || '';
            row.style.cssText = 'display:flex;align-items:center;gap:7px;padding:4px 10px;cursor:pointer;' + (extra || '');
            row.addEventListener('mouseenter', function () { row.style.background = 'rgba(122,79,214,0.16)'; });
            row.addEventListener('mouseleave', function () { row.style.background = ''; });
            parts.forEach(function (p) { row.appendChild(typeof p === 'string' ? document.createTextNode(p) : p); });
            if (click) row.addEventListener('click', function (e) { e.stopPropagation(); click(e, row); });
            menu.appendChild(row);
            return row;
        }

        function check(on) {
            var c = icon('checkmarkImage', '', 14, on ? 1 : 0);
            return c;
        }

        // Copy Style
        section('Copy Style', 'copyImage');
        COPY_MODES.forEach(function (mode) {
            item([check(settings.copy === mode.id), icon(mode.icon, '', 16, mode.dim ? 0.4 : 1), mode.label], function () {
                settings.copy = mode.id;
                saveSettings(ctx.key, settings);
                closeMenu();
                ctx.refresh();
            }, mode.tip);
        });

        // Paste Style
        section('Paste Style', 'pasteImage');
        PASTE_MODES.forEach(function (mode) {
            item([check(settings.paste === mode.id), icon(mode.icon, '', 16), mode.label], function () {
                settings.paste = mode.id;
                saveSettings(ctx.key, settings);
                closeMenu();
                ctx.refresh();
            }, mode.tip);
        });

        // Sources: each with its value for this cell; the one in effect is checked; drag the handle to reorder
        section('Sources', 'layersImage');
        var sourcesBox = document.createElement('div');
        menu.appendChild(sourcesBox);

        function fillSources() {
            sourcesBox.innerHTML = '';
            settings.sources.forEach(function (source, index) {
                var def = SOURCE_LIST[source];
                var value = ctx.sourceValue(source);
                var inEffect = source === ctx.scope; // the source the value in effect comes from (a value set on the cell is none of these)

                var handle = icon('swapImage', 'Drag to reorder', 14, 0.75);
                handle.style.cursor = 'ns-resize';
                var valueText = document.createElement('span');
                valueText.style.cssText = 'margin-left:auto;opacity:0.7;font-family:monospace;font-size:11px;max-width:90px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
                valueText.textContent = value === undefined || value === null ? '—' : String(value);
                var label = document.createElement('span');
                label.textContent = def.label;
                label.style.fontWeight = value !== undefined && value !== null ? 'bold' : 'normal';

                var parts = [check(inEffect), handle, icon(def.icon, '', 16), label, valueText];
                if (source === 'binding') parts.push(icon('arrowRightImage', '', 14));

                var row = item(parts, function (e, rowElement) {
                    if (source === 'binding') {
                        showBindingMenu(rowElement, ctx);
                        return;
                    }

                    if (value === undefined || value === null) return;
                    ctx.apply(String(value)); // the cell takes this source's value
                    closeMenu();
                }, source === 'binding' ? def.tip : def.tip + (value !== undefined && value !== null ? '. Click to set the cell to ' + value : ''));
                sourcesBox.appendChild(row);

                // reorder by dragging the handle (mouse capture on the document: the menu is a plain popup)
                handle.addEventListener('mousedown', function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    var from = index;
                    row.style.opacity = '0.6';

                    function move(ev) {
                        var rows = Array.prototype.slice.call(sourcesBox.children);
                        for (var i = 0; i < rows.length; i++) {
                            var box = rows[i].getBoundingClientRect();
                            if (ev.clientY >= box.top && ev.clientY <= box.bottom && i !== from) {
                                var moved = settings.sources.splice(from, 1)[0];
                                settings.sources.splice(i, 0, moved);
                                from = i;
                                saveSettings(ctx.key, settings);
                                fillSources();
                                sourcesBox.children[from].style.opacity = '0.6';
                                break;
                            }
                        }
                    }

                    function up() {
                        document.removeEventListener('mousemove', move, true);
                        document.removeEventListener('mouseup', up, true);
                        if (sourcesBox.children[from]) sourcesBox.children[from].style.opacity = '';
                    }

                    document.addEventListener('mousemove', move, true);
                    document.addEventListener('mouseup', up, true);
                });
            });
        }

        fillSources();

        // the token's own actions
        var footer = document.createElement('div');
        footer.style.cssText = 'border-top:1px solid rgba(128,128,128,0.25);margin-top:4px;padding-top:2px;';
        menu.appendChild(footer);
        var useDefault = item([icon('refreshImage', '', 16), 'Use Default'], function () {
            var v = ctx.firstDefault();
            ctx.apply(v === undefined ? null : String(v));
            closeMenu();
        }, 'Set the cell to the first default in the order of Sources above');
        footer.appendChild(useDefault);
        if (ctx.scope === 'local') {
            var clear = item([icon('crossImage', '', 16), 'Remove from Cell'], function () {
                ctx.apply(null);
                closeMenu();
            }, 'Take the value off the cell: what draw.io resolves next applies');
            footer.appendChild(clear);
        }

        document.body.appendChild(menu);
        var at = anchor.getBoundingClientRect();
        var w = menu.offsetWidth, h = menu.offsetHeight;
        var left = Math.max(4, Math.min(at.left - w - 6, window.innerWidth - w - 4));
        if (left < 4 || at.left - w - 6 < 4) left = Math.min(at.right + 6, window.innerWidth - w - 4);
        var top = Math.max(4, Math.min(at.top - 8, window.innerHeight - h - 4));
        menu.style.left = left + 'px';
        menu.style.top = top + 'px';

        menu._outside = function (e) { if (!menu.contains(e.target) && !(openBinding && openBinding.contains(e.target))) closeMenu(); };
        menu._escape = function (e) { if (e.key === 'Escape') closeMenu(); };
        document.addEventListener('mousedown', menu._outside, true);
        document.addEventListener('keydown', menu._escape, true);
        openMenu = menu;
    }

    var openBinding = null;

    /** Data Binding ▶: the cell's silo-style.<key> template, to edit or remove. */
    function showBindingMenu(row, ctx) {
        if (openBinding) openBinding.remove();
        var colors = menuColors(row);
        var sub = document.createElement('div');
        sub.setAttribute('role', 'menu');
        sub.style.cssText = 'position:fixed;z-index:100001;min-width:220px;padding:4px 0;border-radius:6px;font:12px Helvetica,Arial,sans-serif;' +
            'box-shadow:0 6px 20px rgba(0,0,0,0.25);border:1px solid rgba(128,128,128,0.45);background:' + colors.bg + ';color:' + colors.fg + ';';

        function subItem(iconName, text, click, enabled) {
            var r = document.createElement('div');
            r.setAttribute('role', 'menuitem');
            r.style.cssText = 'display:flex;align-items:center;gap:7px;padding:4px 10px;' + (enabled === false ? 'opacity:0.45;' : 'cursor:pointer;');
            r.appendChild(icon(iconName, '', 16));
            r.appendChild(document.createTextNode(text));
            if (enabled !== false) {
                r.addEventListener('mouseenter', function () { r.style.background = 'rgba(122,79,214,0.16)'; });
                r.addEventListener('mouseleave', function () { r.style.background = ''; });
                r.addEventListener('click', function (e) { e.stopPropagation(); click(); });
            }
            sub.appendChild(r);
        }

        var template = ctx.sourceValue('binding');
        var current = document.createElement('div');
        current.style.cssText = 'padding:4px 10px 6px;opacity:0.75;font-family:monospace;font-size:11px;word-break:break-all;';
        current.textContent = template ? 'silo-style.' + ctx.key + ' = ' + template : 'Not bound';
        sub.appendChild(current);
        subItem('editImage', template ? 'Edit Binding…' : 'Bind to Data…', function () { closeMenu(); ctx.editBinding(template || ''); });
        subItem('trashImage', 'Remove Binding', function () { closeMenu(); ctx.setBinding(null); }, !!template);

        document.body.appendChild(sub);
        var at = row.getBoundingClientRect();
        var left = at.right + 2;
        if (left + sub.offsetWidth > window.innerWidth - 4) left = Math.max(4, at.left - sub.offsetWidth - 2);
        sub.style.left = left + 'px';
        sub.style.top = Math.min(at.top, window.innerHeight - sub.offsetHeight - 4) + 'px';
        openBinding = sub;

        var dispose = function () { if (openBinding === sub) { sub.remove(); openBinding = null; } };
        // the submenu goes with the menu
        var watcher = setInterval(function () { if (!openMenu) { clearInterval(watcher); dispose(); } }, 150);
    }

    // ---- the panel ---------------------------------------------------------------------------------------------------

    function buildPropertiesPanel(panel, sstate) {
        var ui = panel.editorUi;
        var graph = ui.editor.graph;
        var cells = sstate.cells || [];
        var first = cells[0];
        var props = sstate.customProperties || {};
        var div = panel.createPanel();
        div.style.paddingTop = '6px';
        div.style.paddingBottom = '6px';

        function refresh() { ui.format.refresh(); }

        // the rows: draw.io's typed properties, plus (when showing everything) the cell's other own keys
        var rows = [];
        for (var key in props) {
            var prop = props[key];
            if (!prop || prop.type === 'separator' || prop.primary) continue;
            var visible = typeof prop.isVisible !== 'function' || guard('propertiesPanel.isVisible', function () { return prop.isVisible(sstate, panel); }) !== false;
            if (!visible && !propsView.all) continue;
            rows.push({ key: key, prop: prop, dim: !visible });
        }

        if (propsView.all && first) {
            var own = parseStyle(graph.getModel().getStyle(first)).own;
            for (var k in own)
                if (!props[k]) rows.push({ key: k, prop: { name: k, dispName: k, type: 'string' }, dim: false });
        }

        rows.forEach(function (row) {
            var p = row.prop;
            var current = sstate.style[row.key];
            if (current === undefined) current = sourceValue(graph, first, row.key, p, sstate, panel, 'factory');
            row.value = current;
            row.scope = scopeOf(graph, first, row.key);
            var category = categoryOf(row.key);
            row.group = propsView.group === 'source' ? row.scope : category[0];
            row.label = p.dispName || row.key;
            row.isLabel = category[0] === 'Label';
        });

        function apply(key, value) {
            graph.getModel().beginUpdate();
            try {
                graph.setCellStyles(key, value, graph.getSelectionCells());
            } finally {
                graph.getModel().endUpdate();
            }
        }

        // header: title, then the toolbar (one icon per action)
        var header = document.createElement('div');
        header.style.cssText = 'display:flex;align-items:center;flex-wrap:wrap;gap:2px 0;margin-bottom:4px;';
        var title = document.createElement('div');
        title.style.cssText = 'flex:1 1 auto;font-weight:bold;font-size:13px;cursor:pointer;user-select:none;display:flex;align-items:center;gap:4px;';
        title.setAttribute('role', 'button');
        title.setAttribute('aria-expanded', propsView.open ? 'true' : 'false');
        title.appendChild(icon(propsView.open ? 'expandMoreImage' : 'expandLessImage', '', 14));
        title.appendChild(icon('sparklesImage', '', 16));
        title.appendChild(document.createTextNode('Silo Properties'));
        title.addEventListener('click', function () { propsView.open = !propsView.open; saveView(); refresh(); });
        header.appendChild(title);
        if (propsView.open) {
            var tools = document.createElement('div');
            tools.style.cssText = 'display:flex;align-items:center;flex-wrap:wrap;';
            tools.appendChild(toolButton(propsView.all ? 'visibleImage' : 'hiddenImage',
                propsView.all ? 'Showing every token, also ones that do not apply now (click to hide those)' : 'Show every token, also ones that do not apply now',
                propsView.all, function () { propsView.all = !propsView.all; saveView(); refresh(); }));
            tools.appendChild(toolButton('unorderedListImage', 'Group by category', propsView.group === 'category',
                function () { propsView.group = 'category'; saveView(); refresh(); }));
            tools.appendChild(toolButton('layoutImage', 'Group by definition scope (where each value comes from)', propsView.group === 'source',
                function () { propsView.group = 'source'; saveView(); refresh(); }));
            tools.appendChild(toolButton('layersImage', 'Show the per-token layering buttons (Copy Style / Paste Style settings and sources)', propsView.layering,
                function () { propsView.layering = !propsView.layering; saveView(); refresh(); }));
            tools.appendChild(toolButton('thinShapesImage', 'Show what each token applies to (the shape or its label)', propsView.target,
                function () { propsView.target = !propsView.target; saveView(); refresh(); }));
            tools.appendChild(toolButton('codeImage', 'Show the style token names', propsView.names,
                function () { propsView.names = !propsView.names; saveView(); refresh(); }));
            tools.appendChild(toolButton('copyImage', 'Silo Copy Style: copy the tokens as each one\'s Copy Style setting says', false,
                function () { siloCopyStyle(graph, rows); refresh(); }));
            tools.appendChild(toolButton('pasteImage', styleClipboard ? 'Silo Paste Style: paste ' + Object.keys(styleClipboard.values).length + ' copied tokens as each one\'s Paste Style setting says' : 'Silo Paste Style (copy a style first)',
                false, function () { siloPasteStyle(graph, rows, sstate, panel); }));
            header.appendChild(tools);
        }
        div.appendChild(header);
        if (!propsView.open || !first) return div;

        // search
        var searchRow = document.createElement('div');
        searchRow.style.cssText = 'display:flex;align-items:center;gap:4px;margin:2px 0 6px;';
        searchRow.appendChild(icon('magnifyImage', 'Search', 16));
        var search = document.createElement('input');
        search.type = 'search';
        search.placeholder = 'Search properties';
        search.setAttribute('aria-label', 'Search properties');
        search.value = propsView.filter;
        search.style.cssText = 'position:static;flex:1;min-width:0;box-sizing:border-box;margin:0;font-size:11px;text-align:left;height:22px;padding:0 4px;';
        searchRow.appendChild(search);
        div.appendChild(searchRow);

        var groups = {}, order = [];
        rows.forEach(function (row) {
            if (!groups[row.group]) { groups[row.group] = []; order.push(row.group); }
            groups[row.group].push(row);
        });

        if (propsView.group === 'source')
            order.sort(function (a, b) { return SCOPES[a].order - SCOPES[b].order; });
        else
            order.sort(function (a, b) {
                return CATEGORY_RULES.findIndex(function (r) { return r[0] === a; }) - CATEGORY_RULES.findIndex(function (r) { return r[0] === b; });
            });

        var rowHeight = propsView.names ? 32 : 24;
        var list = document.createElement('div');
        order.forEach(function (name) {
            var groupIcon = propsView.group === 'source' ? SCOPES[name].icon : CATEGORY_RULES.find(function (r) { return r[0] === name; })[1];
            var groupTitle = propsView.group === 'source' ? SCOPES[name].heading : name;
            var collapsed = !!propsView.collapsed[propsView.group + ':' + name];

            var section = document.createElement('div');
            section.className = 'silo-prop-section';
            section.style.cssText = 'margin-top:8px;';

            // the heading: a band with the group's icon, its name and how many tokens it has; a click folds it
            var heading = document.createElement('div');
            heading.setAttribute('role', 'button');
            heading.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
            heading.style.cssText = 'display:flex;align-items:center;gap:6px;padding:4px 6px;margin:0 -4px 3px;border-radius:4px;cursor:pointer;' +
                'font-weight:bold;font-size:12px;background:rgba(122,79,214,0.13);border-left:3px solid #7a4fd6;user-select:none;';
            heading.appendChild(icon(collapsed ? 'expandLessImage' : 'expandMoreImage', '', 12));
            heading.appendChild(icon(groupIcon, '', 16));
            heading.appendChild(document.createTextNode(groupTitle));
            var count = document.createElement('span');
            count.textContent = String(groups[name].length);
            count.style.cssText = 'margin-left:auto;font-weight:normal;font-size:10px;opacity:0.7;';
            heading.appendChild(count);
            heading.addEventListener('click', function () {
                propsView.collapsed[propsView.group + ':' + name] = !collapsed;
                saveView();
                refresh();
            });
            section.appendChild(heading);
            if (collapsed) {
                list.appendChild(section);
                return;
            }

            groups[name].forEach(function (row) {
                var settings = settingsOf(row.key);
                var line = document.createElement('div');
                line.className = 'silo-prop-row';
                line.setAttribute('data-search', (row.label + ' ' + row.key + ' ' + groupTitle).toLowerCase());
                line.style.cssText = 'display:flex;align-items:center;gap:4px;min-height:' + rowHeight + 'px;margin:' + (propsView.names ? 3 : 1) + 'px 0;' +
                    (row.dim ? 'opacity:0.55;' : '');

                var ctx = {
                    key: row.key, label: row.label, scope: row.scope, refresh: refresh,
                    apply: function (v) { apply(row.key, v); },
                    sourceValue: function (s) { return sourceValue(graph, first, row.key, row.prop, sstate, panel, s); },
                    firstDefault: function () { return firstDefault(graph, first, row.key, row.prop, sstate, panel); },
                    setBinding: function (template) {
                        graph.getModel().beginUpdate();
                        try {
                            graph.getSelectionCells().forEach(function (c) { graph.setAttributeForCell(c, 'silo-style.' + row.key, template); });
                        } finally {
                            graph.getModel().endUpdate();
                        }
                    },
                    editBinding: function (template) {
                        var set = this.setBinding;
                        var done = function (value) { if (value !== null && value !== undefined) set(value === '' ? null : value); };
                        if (window.FilenameDialog) {
                            var dlg = new window.FilenameDialog(ui, template, 'Bind', done, 'Template for ' + row.key + ' (e.g. {push:cpu})');
                            ui.showDialog(dlg.container, 360, 90, true, true);
                            if (dlg.init) dlg.init();
                        } else done(prompt('Template for ' + row.key + ' (e.g. {push:cpu})', template));
                    }
                };

                // the layering button: how Copy/Paste Style treat the token; opens its Advanced Property Menu
                if (propsView.layering) {
                    var copyMode = COPY_MODES.find(function (m) { return m.id === settings.copy; });
                    var pasteMode = PASTE_MODES.find(function (m) { return m.id === settings.paste; });
                    var layer = toolButton(copyMode.icon, 'Copy: ' + copyMode.label + ' · Paste: ' + pasteMode.label + ' · value from: ' +
                        SCOPES[row.scope].heading.toLowerCase() + '. Click for the Advanced Property Menu', false,
                        function (e) { showTokenMenu(e.currentTarget, ctx); }, 22);
                    layer.firstChild.style.opacity = copyMode.dim ? '0.4' : '1';
                    layer.style.margin = '0';
                    line.appendChild(layer);
                }

                line.appendChild(icon(tokenIcon(row.key), '', 16));

                var label = document.createElement('div');
                label.style.cssText = 'flex:1 1 auto;min-width:0;overflow:hidden;line-height:1.2;white-space:normal;overflow-wrap:break-word;';
                label.title = row.label + '  (' + row.key + ', ' + SCOPES[row.scope].heading.toLowerCase() + ')' + (row.dim ? ' -- does not apply now' : '');
                label.appendChild(document.createTextNode(row.label));
                if (propsView.names) {
                    // the token on a line of its own under the name, so neither is cut short
                    var code = document.createElement('code');
                    code.textContent = row.key;
                    code.style.cssText = 'display:block;font-size:10px;opacity:0.75;white-space:normal;word-break:break-all;margin-top:1px;';
                    label.appendChild(code);
                }
                line.appendChild(label);

                if (propsView.target)
                    line.appendChild(icon(row.isLabel ? 'textFormatImage' : 'thinRectangleImage', row.isLabel ? 'Applies to the label' : 'Applies to the shape', 14, 0.8));

                var holder = document.createElement('div');
                // a checkbox needs only its own width; other editors get a fixed share of the row
                holder.style.cssText = (row.prop.type === 'bool' ? 'flex:0 0 auto;padding:0 4px;' : 'flex:0 0 34%;') + 'min-width:0;overflow:hidden;';
                holder.appendChild(valueEditor(row.prop, row.value, function (v) { apply(row.key, v); }));
                line.appendChild(holder);

                var reset = toolButton('crossImage', 'Remove this value from the cell', false, function () { apply(row.key, null); }, 18);
                reset.style.visibility = row.scope === 'local' ? 'visible' : 'hidden';
                reset.style.margin = '0';
                line.appendChild(reset);

                section.appendChild(line);
            });

            list.appendChild(section);
        });

        if (rows.length === 0) {
            var none = document.createElement('div');
            none.textContent = 'No typed properties for this selection.';
            none.style.opacity = '0.7';
            list.appendChild(none);
        }

        div.appendChild(list);

        function filter() {
            propsView.filter = search.value;
            var q = search.value.trim().toLowerCase();
            Array.prototype.forEach.call(list.querySelectorAll('.silo-prop-section'), function (section) {
                var shown = 0, all = section.querySelectorAll('.silo-prop-row');
                Array.prototype.forEach.call(all, function (r) {
                    var match = !q || r.getAttribute('data-search').indexOf(q) >= 0;
                    r.style.display = match ? 'flex' : 'none';
                    if (match) shown++;
                });
                section.style.display = shown > 0 || all.length === 0 && !q ? '' : 'none';
            });
        }

        search.addEventListener('input', filter);
        filter();
        return div;
    }

    function installPropertiesPanel(ui) {
        if (options.propertiesPanel === false) return;

        var StyleFormatPanel = window.StyleFormatPanel;
        if (!StyleFormatPanel || !StyleFormatPanel.prototype.init) throw new Error('StyleFormatPanel.prototype.init not found');

        var init = StyleFormatPanel.prototype.init;
        StyleFormatPanel.prototype.init = function () {
            init.apply(this, arguments);

            var panel = this;
            guard('propertiesPanel', function () {
                closeMenu();
                var sstate = panel.editorUi.getSelectionState();
                if (!sstate || !sstate.cells || sstate.cells.length === 0) return;

                var ours = buildPropertiesPanel(panel, sstate);

                // above draw.io's own grid when there is one, else at the end of the Style tab
                var grid = panel.container.querySelector('table.geProperties');
                var gridPanel = grid;
                while (gridPanel && gridPanel.parentNode !== panel.container) gridPanel = gridPanel.parentNode;
                if (gridPanel) panel.container.insertBefore(ours, gridPanel);
                else panel.container.appendChild(ours);
            });
        };
    }

    // ---- operations the host can call ----------------------------------------------------------------------------

    var operations = {
        ping: function () {
            return { pong: true };
        },

        setMenu: function (msg) {
            state.menu = msg.items || [];
            return { count: state.menu.length };
        },

        setContextItems: function (msg) {
            state.contextItems = msg.items || [];
            return { count: state.contextItems.length };
        },

        setPalettes: function (msg) {
            state.palettes = msg.palettes || [];
            applyPalettes();
            return { count: state.palettes.length };
        },

        addStencil: function (msg) {
            var xml = new DOMParser().parseFromString(msg.xml, 'text/xml').documentElement;
            window.mxStencilRegistry.addStencil(msg.name, new window.mxStencil(xml));
            return { name: msg.name };
        },

        customProperties: function (msg) {
            var shape = window.mxCellRenderer.defaultShapes[msg.shape];
            if (!shape) throw new Error('Unknown shape: ' + msg.shape);
            shape.prototype.customProperties = msg.properties;
            return { shape: msg.shape, count: (msg.properties || []).length };
        },

        selectCell: function (msg) {
            var cell = findCell(msg.id);
            if (!cell) throw new Error('No cell with id ' + msg.id);
            state.graph.setSelectionCell(cell);
            state.graph.scrollCellToVisible(cell, true);
            return { id: msg.id };
        },

        scrollToCell: function (msg) {
            var cell = findCell(msg.id);
            if (!cell) throw new Error('No cell with id ' + msg.id);
            state.graph.scrollCellToVisible(cell, true);
            return { id: msg.id };
        },

        setCellAttribute: function (msg) {
            var cell = findCell(msg.id);
            if (!cell) throw new Error('No cell with id ' + msg.id);
            state.graph.setAttributeForCell(cell, msg.key, msg.value === null || msg.value === undefined ? null : String(msg.value));
            return { id: msg.id, key: msg.key };
        },

        getSelection: function () {
            return { cells: state.graph.getSelectionCells().map(cellInfo) };
        },

        /** Every cell carrying silo-* binding attributes: [{ id, attributes: { 'silo-label': ..., ... } }]. */
        bindingCells: function () {
            var model = state.graph.getModel();
            var cells = [];
            for (var id in model.cells) {
                var cell = model.cells[id];
                if (!hasSiloData(cell)) continue;
                var attributes = {};
                for (var i = 0; i < cell.value.attributes.length; i++) {
                    var a = cell.value.attributes[i];
                    if (/^silo[-_]/i.test(a.nodeName)) attributes[a.nodeName] = a.nodeValue;
                }
                cells.push({ id: cell.id, attributes: attributes });
            }
            return { cells: cells };
        },

        /**
         * Puts evaluated binding values into cells IN PLACE: [{ id, label?, tooltip?, style?: { key: value } }]. The cells are
         * changed directly and redrawn, outside the undoable model, so the selection, the undo history and any open
         * in-place editor are left alone and no autosave is triggered. A cell being edited is skipped.
         */
        applyBindings: function (msg) {
            var graph = state.graph;
            var model = graph.getModel();
            var editing = graph.isEditing() ? graph.cellEditor.getEditingCell() : null;
            var changed = 0;

            (msg.cells || []).forEach(function (c) {
                var cell = model.getCell(c.id);
                if (!cell || cell === editing || !cell.value || !cell.value.setAttribute) return;
                var dirty = false;

                if (c.label !== undefined && c.label !== null && cell.value.getAttribute('label') !== c.label) {
                    cell.value.setAttribute('label', c.label);
                    dirty = true;
                }

                if (c.tooltip !== undefined && c.tooltip !== null && cell.value.getAttribute('tooltip') !== c.tooltip) {
                    cell.value.setAttribute('tooltip', c.tooltip);
                    dirty = true;
                }

                if (c.style) {
                    var style = cell.style || '';
                    for (var key in c.style) style = window.mxUtils.setStyle(style, key, c.style[key]);
                    if (style !== (cell.style || '')) {
                        cell.style = style;
                        dirty = true;
                    }
                }

                if (dirty) {
                    graph.refresh(cell);
                    changed++;
                }
            });

            return { changed: changed };
        },

        /**
         * Adds, updates and removes cells IN PLACE, for content sources that grow while they are shown
         * (DrawioView.PluginApplyEditsAsync): { remove: [id], cells: [{ id, edge?, label?, tooltip?, link?, style?,
         * styleEntries?: { key: value|null }, x?, y?, width?, height?, source?, target?, parent?, labelAt?, attributes?: { name: value|null } }] }.
         * The entries of cells run IN ORDER; an entry { id, remove: true } removes that cell there (the older remove array runs
         * first, before every entry). An entry with add: true creates the cell when it does not exist yet, with an object value
         * (so it can carry attributes); an entry without it only changes an existing cell (style replaces the whole style,
         * styleEntries change single entries) and is skipped when the cell is gone. Removing a vertex removes its connectors too. It is one model update, and the undo entry it makes is taken off again so the person's undo history
         * keeps only their own edits (VERIFY on a draw.io bump: ui.editor.undoManager is an mxUndoManager with history /
         * indexOfNextAdd). A cell being edited is left alone.
         */
        upsertCells: function (msg) {
            var graph = state.graph;
            var model = graph.getModel();
            var parent = graph.getDefaultParent();
            var editing = graph.isEditing() ? graph.cellEditor.getEditingCell() : null;
            var undo = state.ui.editor ? state.ui.editor.undoManager : null;
            var lastEdit = undo && undo.history && undo.history.length ? undo.history[undo.history.length - 1] : null;
            var result = { added: 0, updated: 0, removed: 0, skipped: [], missingTerminals: [] };

            // Through the model, not graph.removeCells: that one silently keeps cells styled deletable=0, which is every
            // generated cell of a locked layer. The cell goes with what it holds and every connector at either.
            function removeCell(id) {
                var cell = model.getCell(id);
                if (!cell || cell === editing) return;

                var doomed = [cell];
                var stack = [cell];
                while (stack.length) {
                    var c = stack.pop();
                    for (var i = 0; i < model.getChildCount(c); i++) stack.push(model.getChildAt(c, i));
                    var edges = model.getEdges(c) || [];
                    for (var j = 0; j < edges.length; j++) if (doomed.indexOf(edges[j]) < 0) doomed.push(edges[j]);
                }

                doomed.forEach(function (d) { if (model.contains(d)) model.remove(d); });
                result.removed++;
            }

            function newValue(label) {
                var value = window.mxUtils.createXmlDocument().createElement('object');
                value.setAttribute('label', label === null || label === undefined ? '' : String(label));
                return value;
            }

            function writeValue(value, c) {
                if (c.label !== undefined && c.label !== null) value.setAttribute('label', c.label);
                if (c.tooltip !== undefined && c.tooltip !== null) value.setAttribute('tooltip', c.tooltip);
                if (c.link !== undefined && c.link !== null) value.setAttribute('link', c.link);
                if (c.attributes) {
                    for (var name in c.attributes) {
                        var v = c.attributes[name];
                        if (v === null || v === undefined) value.removeAttribute(name);
                        else value.setAttribute(name, String(v));
                    }
                }
            }

            function connect(cell, c) {
                var source = c.source ? model.getCell(c.source) : null;
                var target = c.target ? model.getCell(c.target) : null;
                if (source && model.getTerminal(cell, true) !== source) model.setTerminal(cell, source, true);
                if (target && model.getTerminal(cell, false) !== target) model.setTerminal(cell, target, false);
                if ((c.source && !source) || (c.target && !target)) result.missingTerminals.push(c.id);
            }

            state.applying = true; // the change below is Silo's, not the person's (see installGeneratedWatch)
            model.beginUpdate();
            try {
                (msg.remove || []).forEach(removeCell);

                (msg.cells || []).forEach(function (c) {
                    if (c.remove) {
                        removeCell(c.id);
                        return;
                    }

                    var cell = model.getCell(c.id);
                    if (cell && cell === editing) return;

                    if (!cell) {
                        // an update of a cell that is gone (removed earlier) must not bring it back as a default box
                        if (!c.add) {
                            result.skipped.push(c.id);
                            return;
                        }

                        var value = newValue(c.label);
                        writeValue(value, c);

                        var geometry;
                        if (c.edge) {
                            geometry = new window.mxGeometry();
                            geometry.relative = true;
                            if (typeof c.labelAt === 'number') geometry.x = c.labelAt;
                        } else {
                            geometry = new window.mxGeometry(c.x || 0, c.y || 0, c.width || 120, c.height || 60);
                        }

                        cell = new window.mxCell(value, geometry, c.style || '');
                        cell.setId(String(c.id));
                        if (c.edge) cell.setEdge(true);
                        else cell.setVertex(true);

                        model.add(c.parent ? model.getCell(c.parent) || parent : parent, cell);
                        if (c.edge) connect(cell, c);
                        result.added++;
                        return;
                    }

                    var touchesValue = (c.label !== undefined && c.label !== null) || (c.tooltip !== undefined && c.tooltip !== null) ||
                        (c.link !== undefined && c.link !== null) || !!c.attributes;
                    if (touchesValue) {
                        var current = cell.value;
                        var next = current && current.cloneNode && current.setAttribute ? current.cloneNode(true) : newValue(current);
                        // an add is the whole cell: bindings it no longer has go (an update only changes what it names)
                        if (c.add && c.attributes && next.attributes) {
                            for (var a = next.attributes.length - 1; a >= 0; a--) {
                                var attr = next.attributes[a].nodeName;
                                if (/^silo[-_]/i.test(attr) && !(attr in c.attributes)) next.removeAttribute(attr);
                            }
                        }
                        writeValue(next, c);
                        model.setValue(cell, next);
                    }

                    if (typeof c.style === 'string' && c.style !== (cell.style || '')) model.setStyle(cell, c.style);

                    if (c.styleEntries) {
                        var style = cell.style || '';
                        for (var key in c.styleEntries) {
                            var entry = c.styleEntries[key];
                            style = window.mxUtils.setStyle(style, key, entry === undefined ? null : entry);
                        }
                        if (style !== (cell.style || '')) model.setStyle(cell, style);
                    }

                    if (model.isEdge(cell)) {
                        connect(cell, c);
                    } else if (c.x != null || c.y != null || c.width != null || c.height != null) {
                        var g = cell.geometry ? cell.geometry.clone() : new window.mxGeometry();
                        if (c.x != null) g.x = c.x;
                        if (c.y != null) g.y = c.y;
                        if (c.width != null) g.width = c.width;
                        if (c.height != null) g.height = c.height;
                        model.setGeometry(cell, g);
                    }

                    result.updated++;
                });
            } finally {
                try { model.endUpdate(); } finally { state.applying = false; }
            }

            guard('upsertCells.undo', function () {
                if (undo && undo.history && undo.history.length && undo.history[undo.history.length - 1] !== lastEdit) {
                    undo.history.pop();
                    undo.indexOfNextAdd = undo.history.length;
                }
            });

            return result;
        }
    };

    // ---- a Format panel that can be resized --------------------------------------------------------------------------
    // draw.io gives the Format panel (the right-hand sidebar) one width and no splitter. A thin handle on its left edge
    // lets the width change; it is kept in localStorage. Two layouts exist:
    //   grid layout (current draw.io): the panel is a CSS grid column and its width comes from the stylesheet (240px);
    //     EditorUi.refresh clears any inline width and ui.formatWidth is only an on/off flag (0 = hidden). The plugin puts the
    //     inline width back after every refresh.
    //   absolute layout (older draw.io): EditorUi.refresh lays the panel out from ui.formatWidth.
    // The handle is fixed-positioned on the page, not a child of the panel: the Format panel empties its container when it
    // refreshes. draw.io internals used (VERIFY on a version change): ui.formatContainer, ui.formatWidth, ui.refresh,
    // ui.format.refresh, ui.editor.graph.sizeDidChange.

    var FORMAT_WIDTH_KEY = 'silo.formatWidth';
    var FORMAT_WIDTH_MIN = 200;

    function installFormatResizer(ui) {
        var container = ui.formatContainer;
        if (!container || !container.parentNode || typeof ui.refresh !== 'function') throw new Error('ui.formatContainer or ui.refresh not found');

        var defaultWidth = 240;
        var width = 0;                     // 0 = the panel's own width
        var isGrid = window.getComputedStyle(container.parentNode).display === 'grid';

        function clamp(w) {
            var max = Math.max(FORMAT_WIDTH_MIN, Math.floor(window.innerWidth * 0.6));
            return Math.max(FORMAT_WIDTH_MIN, Math.min(max, Math.round(w)));
        }

        function isOn() { return ui.format != null && (typeof ui.formatWidth !== 'number' || ui.formatWidth > 0); }

        // puts the wanted width on the panel (draw.io's refresh has just reset it)
        function apply() {
            if (!width || !isOn()) return false;
            if (isGrid) {
                var text = width + 'px';
                if (container.style.width === text) return false;
                container.style.width = text;
                return true;
            }

            if (ui.formatWidth === width) return false;
            ui.formatWidth = width;
            return true;
        }

        var handle = document.createElement('div');
        handle.className = 'silo-format-resizer';
        handle.title = 'Drag to resize the panel; double-click for the usual width';
        handle.style.cssText = 'position:fixed;width:7px;cursor:ew-resize;z-index:2147483000;background:transparent;touch-action:none;display:none;';
        document.body.appendChild(handle);

        function place() {
            var box = container.getBoundingClientRect();
            var shown = container.style.display !== 'none' && box.width > 20 && box.height > 0;
            handle.style.display = shown ? 'block' : 'none';
            if (!shown) return;
            handle.style.left = (box.left - 3) + 'px';
            handle.style.top = box.top + 'px';
            handle.style.height = box.height + 'px';
        }

        function sizeChanged() {
            var graph = ui.editor && ui.editor.graph;
            if (graph && typeof graph.sizeDidChange === 'function') graph.sizeDidChange();
        }

        var refresh = ui.refresh;
        var inRefresh = false;
        ui.refresh = function () {
            var result = refresh.apply(this, arguments);
            if (!inRefresh && apply()) {
                inRefresh = true;
                try { if (!isGrid) refresh.apply(this, arguments); else sizeChanged(); } finally { inRefresh = false; }
            }
            place();
            return result;
        };

        function setWidth(w, rebuild) {
            width = w ? clamp(w) : 0;
            if (!width && isGrid) container.style.width = '';
            if (!width && !isGrid) ui.formatWidth = defaultWidth;
            ui.refresh(true);
            place();
            if (rebuild && ui.format && typeof ui.format.refresh === 'function') ui.format.refresh();
        }

        if (typeof ResizeObserver === 'function') new ResizeObserver(place).observe(container);
        window.addEventListener('resize', function () { place(); });

        var dragging = false;
        var frame = 0;
        var wanted = 0;

        function shade(on) { handle.style.background = on ? 'rgba(128,128,128,0.45)' : 'transparent'; }

        handle.addEventListener('mouseenter', function () { shade(true); });
        handle.addEventListener('mouseleave', function () { if (!dragging) shade(false); });
        handle.addEventListener('pointerdown', function (e) {
            if (e.button !== 0) return;
            dragging = true;
            shade(true);
            try { handle.setPointerCapture(e.pointerId); } catch (x) { /* the drag still works while the pointer stays over the handle */ }
            e.preventDefault();
        });
        handle.addEventListener('pointermove', function (e) {
            if (!dragging) return;
            // the panel runs to the right edge of the page: its width is what is between the pointer and that edge
            wanted = container.getBoundingClientRect().right - e.clientX;
            if (!frame) frame = requestAnimationFrame(function () { frame = 0; setWidth(wanted, false); });
        });
        function finish(e) {
            if (!dragging) return;
            dragging = false;
            shade(false);
            try { handle.releasePointerCapture(e.pointerId); } catch (x) { }
            if (frame) { cancelAnimationFrame(frame); frame = 0; setWidth(wanted, false); }
            writeStored(FORMAT_WIDTH_KEY, width);
            setWidth(width, true);
        }
        handle.addEventListener('pointerup', finish);
        handle.addEventListener('pointercancel', finish);
        handle.addEventListener('dblclick', function () {
            writeStored(FORMAT_WIDTH_KEY, null);
            setWidth(0, true);
        });

        var kept = readStored(FORMAT_WIDTH_KEY, null);
        if (typeof kept === 'number' && kept > 0) setWidth(kept, true);
        else place();
    }

    function receive(msg) {
        var answer = { requestId: msg.requestId, ok: true };
        try {
            var op = operations[msg.op];
            if (!op) throw new Error('Unknown plugin operation: ' + msg.op);
            if (!state.ui && msg.op !== 'ping') throw new Error('The editor is not ready yet.');
            answer.result = op(msg);
        } catch (e) {
            answer.ok = false;
            answer.error = e && e.message ? e.message : String(e);
        }

        post('silo.result', answer);
    }

    window.SiloPlugin = {
        version: 1,
        options: options,
        receive: receive,
        operations: operations,
        /** Lets other code running in the editor (e.g. a second plugin) reach the host. */
        post: post
    };

    // ---- installation --------------------------------------------------------------------------------------------

    // ---- the person's edits of generated cells -------------------------------------------------------------------
    // Generated cells (siloGenerated="1") are the person's to move and delete. Every model change that is not Silo's own
    // (upsertCells sets state.applying; diagram loads are root changes) is checked: generated cells removed, put back (undo) or
    // moved/resized/re-routed are reported as silo.generatedEdited { removed, restored, moved }, so the host stops putting them
    // back or moving them. Folding a container (+/-) is not a move.

    function isGenerated(cell) {
        return !!(cell && cell.value && cell.value.getAttribute && cell.value.getAttribute('siloGenerated') === '1');
    }

    function installGeneratedWatch(graph) {
        var model = graph.getModel();
        model.addListener(window.mxEvent.CHANGE, function (sender, evt) {
            if (state.applying) return;
            guard('generatedWatch', function () {
                var edit = evt.getProperty('edit');
                var changes = (edit && edit.changes) || evt.getProperty('changes') || [];
                var removed = {}, restored = {}, moved = {};
                var folding = changes.some(function (c) { return window.mxCollapseChange && c instanceof window.mxCollapseChange; });
                if (changes.some(function (c) { return window.mxRootChange && c instanceof window.mxRootChange; })) return;

                function each(cell, fn) {
                    var stack = [cell];
                    while (stack.length) {
                        var c = stack.pop();
                        fn(c);
                        for (var i = 0; i < model.getChildCount(c); i++) stack.push(model.getChildAt(c, i));
                    }
                }

                changes.forEach(function (c) {
                    if (window.mxChildChange && c instanceof window.mxChildChange) {
                        if (c.parent == null && c.previous != null) {
                            each(c.child, function (x) { if (isGenerated(x)) { removed[x.id] = true; delete restored[x.id]; } });
                        } else if (c.parent != null && c.previous == null) {
                            each(c.child, function (x) { if (isGenerated(x)) { restored[x.id] = true; delete removed[x.id]; } });
                        }
                    } else if (!folding && ((window.mxGeometryChange && c instanceof window.mxGeometryChange) ||
                                            (window.mxTerminalChange && c instanceof window.mxTerminalChange))) {
                        if (isGenerated(c.cell)) moved[c.cell.id] = true;
                    }
                });

                var report = { removed: Object.keys(removed), restored: Object.keys(restored), moved: Object.keys(moved) };
                if (report.removed.length || report.restored.length || report.moved.length) post('silo.generatedEdited', report);
            });
        });
    }

    function install(ui) {
        state.ui = ui;
        state.graph = ui.editor.graph;
        var features = {};

        features.menu = guard('menu', function () { installMenu(ui); return true; }) === true;
        features.contextMenu = guard('contextMenu', function () { installContextMenu(ui); return true; }) === true;
        features.selection = guard('selection', function () { installSelectionEvents(state.graph); return true; }) === true;
        features.clicks = guard('clicks', function () { installClickEvents(state.graph); return true; }) === true;
        features.generatedWatch = guard('generatedWatch', function () { installGeneratedWatch(state.graph); return true; }) === true;
        features.propertiesPanel = guard('propertiesPanel', function () { installPropertiesPanel(ui); return true; }) === true;
        features.formatResizer = options.resizableFormat === false ? false : guard('formatResizer', function () { installFormatResizer(ui); return true; }) === true;

        post('silo.ready', {
            version: window.EditorUi && window.EditorUi.VERSION ? window.EditorUi.VERSION : null,
            features: features
        });
        log('installed');
    }

    // Wait for draw.io to define Draw.loadPlugin (it does so while the App starts; it is only defined when plugins are
    // not disabled with plugins=0), then register. Give up after a minute and report it.
    var started = Date.now();
    var timer = setInterval(function () {
        if (window.Draw && typeof window.Draw.loadPlugin === 'function') {
            clearInterval(timer);
            window.Draw.loadPlugin(function (ui) {
                guard('install', function () { install(ui); });
            });
        } else if (Date.now() - started > 60000) {
            clearInterval(timer);
            post('silo.error', { section: 'install', message: 'Draw.loadPlugin never appeared (plugins disabled, or a different draw.io version).' });
        }
    }, 25);
})();
