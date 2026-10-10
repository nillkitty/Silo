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

        // Add the menu bar entry once; its contents are rebuilt from state.menu every time it opens.
        ui.menubar.addMenu('Silo', ui.menus.get('silo').funct);
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
        }
    };

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

    function install(ui) {
        state.ui = ui;
        state.graph = ui.editor.graph;
        var features = {};

        features.menu = guard('menu', function () { installMenu(ui); return true; }) === true;
        features.contextMenu = guard('contextMenu', function () { installContextMenu(ui); return true; }) === true;
        features.selection = guard('selection', function () { installSelectionEvents(state.graph); return true; }) === true;
        features.clicks = guard('clicks', function () { installClickEvents(state.graph); return true; }) === true;

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
