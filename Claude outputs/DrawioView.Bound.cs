using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Threading;
using Silo.Drawio.Extensions;
using Silo.Drawio.Live;
using Silo.Drawio.Mx;
using Silo.Providers.Diagramming;

namespace Silo.Drawio.Host;

/// <summary>
///     Data-bound nodes (Status, Table, List, Tree; see <see cref="DiagramNodeBinder" />): cells whose <c>silo-node</c>
///     attribute names a kind and whose <c>silo-uri</c> names a source. The pane reads the nodes from the editor
///     (<c>boundCells</c>), resolves each source (<see cref="BoundData" />), has the kind's binder work out the changes, and
///     applies them in place (<c>upsertCells</c>) -- no reload. What the nodes make is ordinary diagram content (it is saved
///     with the diagram, so a saved or shared diagram shows the last values), marked <c>silo-bound-of</c>.
/// </summary>
/// <remarks>
///     Nodes are read when a diagram is loaded (unless set to Manual), on Refresh (Silo menu, or Refresh data on a node's
///     context menu), when a tree item is expanded, and -- for Live nodes -- when the source reports a change or its poll
///     interval passes. The person's deletions and moves of the cells a node made are respected like those of generated
///     cells (<see cref="DismissedCells" />, <see cref="MovedCells" />).
/// </remarks>
public sealed partial class DrawioView
{
    private static readonly TimeSpan BoundDelay = TimeSpan.FromMilliseconds(400);

    private readonly Dictionary<string, IBindingScope> _boundScopes  = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BoundWatch>    _boundWatches = new(StringComparer.Ordinal);
    private readonly HashSet<string>                   _boundOwners  = new(StringComparer.Ordinal);
    private readonly HashSet<string>                   _boundExpand  = new(StringComparer.Ordinal);

    private bool                     _boundAll;
    private bool                     _boundOnLoad;
    private bool                     _boundLoadPending = true;
    private bool                     _boundRunning;
    private CancellationTokenSource? _boundCancel;
    private DispatcherTimer?         _boundTimer;
    private List<DiagramNodeBinder>? _binders;

    /// <summary>
    ///     Raised on the UI thread after data-bound nodes changed the diagram. Their values are saved with the diagram, so an
    ///     owner that persists only on <see cref="DocumentChanged" /> (the person's edits) may want to persist here too.
    /// </summary>
    public event EventHandler? BoundDataChanged;

    /// <summary>The scope a cell inside a data-bound node reads its scoped tokens from, if the node has been read.</summary>
    public IBindingScope? BoundScopeOf(string cellId)
    {
        return _boundScopes.GetValueOrDefault(cellId);
    }

    /// <summary>Reads every data-bound node again (or only <paramref name="ownerId" />), whatever its refresh setting.</summary>
    public void RefreshBoundNodes(string? ownerId = null)
    {
        ScheduleBound(ownerId, false);
    }

    // ---- palettes ------------------------------------------------------------------------------

    /// <summary>The binders: the registered palette providers that are node binders.</summary>
    private List<DiagramNodeBinder> Binders => _binders ??= PaletteProviders().OfType<DiagramNodeBinder>().ToList();

    private static List<IPaletteProvider> PaletteProviders()
    {
        try
        {
            return App.ResolveAll<IPaletteProvider>();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not resolve the diagram palette providers");
            return [];
        }
    }

    /// <summary>What the plugin's sidebar shows: <see cref="PluginPalettes" /> and every <see cref="IPaletteProvider" />'s, merged by id.</summary>
    private List<DrawioPluginPalette> CollectPalettes()
    {
        var all = new List<DrawioPluginPalette>(PluginPalettes);
        foreach (var provider in PaletteProviders())
            try
            {
                all.AddRange(provider.GetPalettes());
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Palette provider {provider} failed", provider.GetType().Name);
            }

        return DrawioPluginPalette.Merge(all);
    }

    // ---- triggers ------------------------------------------------------------------------------

    /// <summary>A diagram was loaded (by the owner or at start, not by a live reload): read its nodes once the plugin is there.</summary>
    private void OnBoundDiagramLoaded()
    {
        if (!_boundLoadPending || !IsPluginReady) return;

        _boundLoadPending = false;
        StopBoundWatches();
        _boundScopes.Clear();
        ScheduleBound(null, true);
    }

    /// <summary><c>silo.boundExpand</c>: the person expanded a vertex of a tree node. Field <c>owner</c>.</summary>
    private void OnBoundExpand(DrawioMessage m)
    {
        if (m.GetString("owner") is { Length: > 0 } owner) ScheduleBound(owner, false);
    }

    /// <summary>Double-click on a tree item's "…": read that item.</summary>
    private bool TryExpandPlaceholder(DrawioMessage m)
    {
        if (m.Get("attributes") is not { ValueKind: JsonValueKind.Object } a) return false;
        if (!a.TryGetProperty(DiagramNodeSchema.MoreAttribute, out var more) || more.GetString() != "lazy") return false;
        if (!a.TryGetProperty(DiagramNodeSchema.BoundOfAttribute, out var owner) || owner.GetString() is not { } ownerId) return false;

        if (a.TryGetProperty(DiagramNodeSchema.KeyAttribute, out var key) && key.GetString() is { } k) _boundExpand.Add(k);
        ScheduleBound(ownerId, false);
        return true;
    }

    /// <summary>The node a cell belongs to: itself when it is one, else the node that made it.</summary>
    private static string? OwnerOf(DrawioMessage m)
    {
        if (m.Get("attributes") is not { ValueKind: JsonValueKind.Object } a) return null;
        if (a.TryGetProperty(DiagramNodeSchema.KindAttribute, out _)) return m.GetString("cellId");
        return a.TryGetProperty(DiagramNodeSchema.BoundOfAttribute, out var owner) ? owner.GetString() : null;
    }

    private void ScheduleBound(string? ownerId, bool onLoad)
    {
        if (_disposed) return;

        if (ownerId is null) _boundAll = true;
        else _boundOwners.Add(ownerId);
        _boundOnLoad |= onLoad;

        if (_boundTimer is null)
        {
            _boundTimer      =  new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = BoundDelay };
            _boundTimer.Tick += async (_, _) =>
                                {
                                    _boundTimer.Stop();
                                    await RunBoundAsync();
                                };
        }

        if (!_boundTimer.IsEnabled) _boundTimer.Start();
    }

    private async Task RunBoundAsync()
    {
        if (_disposed || !IsPluginReady) return;

        // one read at a time; what is asked for meanwhile runs after it
        if (_boundRunning)
        {
            _boundTimer?.Start();
            return;
        }

        bool all    = _boundAll;
        bool onLoad = _boundOnLoad;
        var  only   = _boundOwners.ToList();
        var  expand = _boundExpand.ToHashSet(StringComparer.Ordinal);
        _boundAll = false;
        _boundOnLoad = false;
        _boundOwners.Clear();
        _boundExpand.Clear();

        _boundRunning = true;
        _boundCancel  = new CancellationTokenSource();
        try
        {
            await ReadBoundNodesAsync(all ? null : only, onLoad, expand, _boundCancel.Token);
        }
        catch (OperationCanceledException)
        {
            // the pane is going away
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Reading data-bound diagram nodes failed");
        }
        finally
        {
            _boundRunning = false;
            _boundCancel.Dispose();
            _boundCancel = null;
        }
    }

    // ---- reading and applying -----------------------------------------------------------------

    private async Task ReadBoundNodesAsync(IReadOnlyCollection<string>? only, bool onLoad, IReadOnlySet<string> expand, CancellationToken cancel)
    {
        var args = new JsonObject();
        if (only is not null) args["owners"] = new JsonArray(only.Select(o => (JsonNode?)o).ToArray());

        var answer = await SendPluginAsync("boundCells", args, 5000);
        if (answer?.GetBool("ok") != true || answer.Get("result") is not { } result) return;

        var snapshots = Snapshots(result);
        if (only is null)
            // nodes that are gone stop being watched
            foreach (string gone in _boundWatches.Keys.Where(k => snapshots.All(s => s.Owner.Id != k)).ToList())
                StopWatch(gone);

        var changes = new List<CellChange>();
        foreach (var snapshot in snapshots)
        {
            cancel.ThrowIfCancellationRequested();

            var owner = snapshot.Owner;
            if (Binders.FirstOrDefault(b => string.Equals(b.Kind, owner.Attribute(DiagramNodeSchema.KindAttribute), StringComparison.OrdinalIgnoreCase)) is not { } binder)
                continue;

            var settings = DiagramNodeSchema.Read(binder.SettingsType, owner.Attributes);
            if (settings.Uri is null || (onLoad && settings.Refresh == NodeRefresh.Manual)) continue;

            object?    value = null;
            Exception? error = null;
            try
            {
                value = await BoundData.ResolveAsync(settings.Uri, settings.Path, cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex;
            }

            var context = new BindContext { Snapshot = snapshot, Settings = settings, Value = value, Error = error, Expand = expand, Cancel = cancel };
            BindResult bound;
            try
            {
                // binders read their sources (directories, files, providers): off the UI thread
                bound = await Task.Run(() => binder.Bind(context), cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warning(ex, "The {kind} node {id} could not be bound", binder.Kind, owner.Id);
                continue;
            }

            foreach (var (id, scope) in bound.Scopes) _boundScopes[id] = scope;
            changes.AddRange(bound.Changes);

            Watch(owner.Id, settings, value);
        }

        if (changes.Count > 0 && await ApplyBoundChangesAsync(changes)) BoundDataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The plugin's answer to <c>boundCells</c>, one snapshot per node.</summary>
    private static List<BoundSnapshot> Snapshots(JsonElement result)
    {
        var list = new List<BoundSnapshot>();
        if (!result.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) return list;

        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("owner", out var o) || !node.TryGetProperty("cells", out var cs) || cs.ValueKind != JsonValueKind.Array) continue;
            list.Add(new BoundSnapshot(CellOf(o), cs.EnumerateArray().Select(CellOf).ToList()));
        }

        return list;
    }

    private static BoundCell CellOf(JsonElement e)
    {
        string? Str(string name)
        {
            return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        bool Flag(string name, bool fallback)
        {
            return e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.True : fallback;
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (e.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object)
            foreach (var p in a.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Name != "label")
                    attributes[p.Name] = p.Value.GetString() ?? "";

        DiagramBounds? bounds = null;
        if (e.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.Object)
            bounds = new DiagramBounds(Num(g, "x"), Num(g, "y"), Num(g, "width"), Num(g, "height"));

        return new BoundCell
        {
            Id         = Str("id") ?? "",
            Parent     = Str("parent"),
            Edge       = Flag("edge", false),
            Source     = Str("source"),
            Target     = Str("target"),
            Style      = Str("style") ?? "",
            Label      = Str("label"),
            Attributes = attributes,
            Bounds     = bounds,
            Collapsed  = Flag("collapsed", false),
            Visible    = Flag("visible", true),
            Order      = e.TryGetProperty("order", out var order) && order.TryGetInt32(out int n) ? n : 0
        };

        static double Num(JsonElement g, string name)
        {
            return g.TryGetProperty(name, out var v) && v.TryGetDouble(out double d) ? d : 0;
        }
    }

    /// <summary>Applies binder changes through the plugin, leaving out what would undo the person's own deletions and moves.</summary>
    private async Task<bool> ApplyBoundChangesAsync(List<CellChange> changes)
    {
        var cells = new JsonArray();
        foreach (var c in changes)
        {
            // a cell the person deleted stays deleted (as does a connector to it); one they moved keeps its place
            if (_dismissed.Contains(c.Id) || (c.Edge && ((c.Source is { } s && _dismissed.Contains(s)) || (c.Target is { } t && _dismissed.Contains(t)))))
                continue;

            if (c.Remove)
            {
                cells.Add(new JsonObject { ["id"] = c.Id, ["remove"] = true });
                continue;
            }

            var cell = new JsonObject { ["id"] = c.Id };
            if (c.Add) cell["add"]   = true;
            if (c.Edge) cell["edge"] = true;
            if (c.Parent is not null) cell["parent"] = c.Parent;
            if (c.Source is not null) cell["source"] = c.Source;
            if (c.Target is not null) cell["target"] = c.Target;
            if (c.Index is { } index) cell["index"]   = index;
            if (c.Label is not null) cell["label"]     = c.Label;
            if (c.Tooltip is not null) cell["tooltip"] = c.Tooltip;
            if (c.Link is not null) cell["link"]       = c.Link;
            if (c.Style is not null) cell["style"]     = c.Style;
            if (c.Collapsed is { } collapsed) cell["collapsed"] = collapsed;
            if (c.Visible is { } visible) cell["visible"]       = visible;

            if (c.StyleEntries is { Count: > 0 })
            {
                var entries = new JsonObject();
                foreach (var (k, v) in c.StyleEntries) entries[k] = v;
                cell["styleEntries"] = entries;
            }

            if (c.Attributes is { Count: > 0 })
            {
                var attributes = new JsonObject();
                foreach (var (k, v) in c.Attributes) attributes[k] = v;
                cell["attributes"] = attributes;
            }

            if (c.Bounds is { } b && !c.Edge && !_moved.Contains(c.Id))
            {
                cell["x"]      = b.X;
                cell["y"]      = b.Y;
                cell["width"]  = b.Width;
                cell["height"] = b.Height;
            }

            cells.Add(cell);
        }

        if (cells.Count == 0) return false;

        // the change goes through draw.io's model, so the editor reports an autosave: that is not the person editing
        _ignoreChangesUntilUtc = DateTime.UtcNow.AddMilliseconds(1500);

        var answer = await SendPluginAsync("upsertCells", new JsonObject { ["cells"] = cells }, 8000);
        if (answer?.GetBool("ok") == true) return true;

        _log.Debug("Applying data-bound node changes failed: {error}", answer?.GetString("error") ?? "no answer");
        return false;
    }

    // ---- live nodes --------------------------------------------------------------------------

    /// <summary>A live node's watch on its source; replaced when the node's settings change.</summary>
    private sealed class BoundWatch(string signature, IDisposable subscription) : IDisposable
    {
        public string Signature { get; } = signature;

        public void Dispose()
        {
            subscription.Dispose();
        }
    }

    private void Watch(string ownerId, DynamicNode settings, object? value)
    {
        string signature = $"{settings.Refresh}|{settings.Uri}|{settings.Path}|{settings.PollSeconds}";
        if (_boundWatches.TryGetValue(ownerId, out var current) && current.Signature == signature) return;

        StopWatch(ownerId);
        if (settings.Refresh != NodeRefresh.Live || _disposed) return;

        void Changed()
        {
            if (!_disposed) Dispatcher.BeginInvoke(new Action(() => ScheduleBound(ownerId, false)));
        }

        _boundWatches[ownerId] = new BoundWatch(signature, Subscribe(settings, value, Changed));
    }

    /// <summary>
    ///     Watches a live node's source in the best way it offers: <see cref="IObservableDataSource" />,
    ///     <see cref="INotifySourceChanged" />, the file system for <c>file:</c> sources, <see cref="DiagramData" /> for live
    ///     value schemes, and otherwise a poll.
    /// </summary>
    private IDisposable Subscribe(DynamicNode settings, object? value, Action changed)
    {
        var uri = settings.Uri!;

        if (value is IObservableDataSource observable)
        {
            var cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
                         {
                             try
                             {
                                 await foreach (var _ in observable.WatchAsync(null, cts.Token)) changed();
                             }
                             catch (OperationCanceledException)
                             {
                             }
                             catch (Exception ex)
                             {
                                 _log.Debug(ex, "Watching {uri} stopped", uri);
                             }
                         });
            return new Disposer(() =>
                                {
                                    cts.Cancel();
                                    cts.Dispose();
                                });
        }

        if (value is INotifySourceChanged notifying)
        {
            notifying.Changed += changed;
            return new Disposer(() => notifying.Changed -= changed);
        }

        if (uri.IsAbsoluteUri && DiagramData.IsRegistered(uri.Scheme))
        {
            DiagramData.Changed += changed;
            return new Disposer(() => DiagramData.Changed -= changed);
        }

        if (uri.IsAbsoluteUri && uri.IsFile)
            try
            {
                string path    = uri.LocalPath;
                bool   isDir   = Directory.Exists(path);
                var    watcher = new FileSystemWatcher(isDir ? path : Path.GetDirectoryName(path)!, isDir ? "*" : Path.GetFileName(path))
                {
                    NotifyFilter          = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = isDir,
                    EnableRaisingEvents   = true
                };
                FileSystemEventHandler onChange = (_, _) => changed();
                watcher.Changed += onChange;
                watcher.Created += onChange;
                watcher.Deleted += onChange;
                watcher.Renamed += (_, _) => changed();
                return watcher;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Cannot watch {uri}; polling instead", uri);
            }

        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(Math.Max(1, settings.PollSeconds)) };
        timer.Tick += (_, _) => changed();
        timer.Start();
        return new Disposer(timer.Stop);
    }

    private void StopWatch(string ownerId)
    {
        if (_boundWatches.Remove(ownerId, out var watch)) watch.Dispose();
    }

    private void StopBoundWatches()
    {
        foreach (var w in _boundWatches.Values) w.Dispose();
        _boundWatches.Clear();
    }

    /// <summary>Stops everything the bound nodes run (called from <see cref="Dispose" />).</summary>
    private void DisposeBound()
    {
        _boundTimer?.Stop();
        _boundCancel?.Cancel();
        StopBoundWatches();
    }

    private sealed class Disposer(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
