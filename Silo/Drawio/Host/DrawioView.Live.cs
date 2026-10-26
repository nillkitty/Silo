using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Silo.Drawio.Live;
using Silo.Drawio.Mx;
using Silo.Drawio.Options;

namespace Silo.Drawio.Host;

/// <summary>
///     In-place edits of the generated layer: what an <see cref="IIncrementalDiagramSource" /> reports is queued, batched and
///     handed to the Silo plugin (<c>upsertCells</c>), so the diagram grows while it is shown -- no reload, the zoom, the
///     selection and the person's undo history stay as they are. When the plugin is not there, or an edit fails, the pane
///     falls back to the ordinary path: rebuild from <see cref="IDiagramContentSource.Build" /> and reload.
/// </summary>
public sealed partial class DrawioView
{
    private static readonly TimeSpan LiveEditDelay = TimeSpan.FromMilliseconds(60);

    private readonly List<DiagramEdit> _pendingEdits = [];

    private static readonly TimeSpan LoadWaitLimit = TimeSpan.FromSeconds(5);

    private DateTime?        _awaitingLoadSinceUtc; // a "load" was sent and the editor has not answered yet
    private DispatcherTimer? _editTimer;
    private bool             _flushingEdits;

    // generated cells the person deleted or moved (reported by the plugin): not put back, not moved again, until forgotten
    private readonly HashSet<string> _dismissed = [];
    private readonly HashSet<string> _moved     = [];

    /// <summary>
    ///     Forgets which generated cells the person deleted or moved, so the source's layout applies to all of them again. Call it
    ///     when the generated layer starts over (a new trace) -- ids are reused.
    /// </summary>
    public void ForgetGeneratedEdits()
    {
        _dismissed.Clear();
        _moved.Clear();
    }

    /// <summary>Generated cells the person deleted; the pane does not add them again.</summary>
    public IReadOnlyCollection<string> DismissedCells => _dismissed;

    /// <summary>Generated cells the person moved, resized or re-routed; the pane leaves their geometry alone.</summary>
    public IReadOnlyCollection<string> MovedCells => _moved;

    /// <summary><c>silo.generatedEdited</c>: the person removed, put back (undo) or moved generated cells.</summary>
    private void OnGeneratedEdited(DrawioMessage m)
    {
        foreach (string id in Ids(m, "removed")) _dismissed.Add(id);
        foreach (string id in Ids(m, "restored")) _dismissed.Remove(id);
        foreach (string id in Ids(m, "moved")) _moved.Add(id);
    }

    private static IEnumerable<string> Ids(DrawioMessage m, string field)
    {
        if (m.Get(field) is not { ValueKind: System.Text.Json.JsonValueKind.Array } array) yield break;
        foreach (var e in array.EnumerateArray())
            if (e.GetString() is { } id)
                yield return id;
    }

    /// <summary>Before a rebuild: the geometry of the generated cells the person moved, as they are now.</summary>
    private Dictionary<string, System.Xml.Linq.XElement> KeepMovedGeometry(DrawioDocument doc)
    {
        var kept = new Dictionary<string, System.Xml.Linq.XElement>();
        foreach (string id in _moved)
            if (doc.GeometryOf(id) is { } g)
                kept[id] = g;
        return kept;
    }

    /// <summary>After a rebuild: takes out the cells the person deleted and puts the moved ones back where the person left them.</summary>
    private void RestorePersonEdits(DrawioDocument doc, Dictionary<string, System.Xml.Linq.XElement> moved)
    {
        foreach (string id in _dismissed) doc.Remove(id);
        foreach (var (id, geometry) in moved) doc.SetGeometry(id, geometry);
    }

    /// <summary>
    ///     Leaves out what would undo the person's own edits: anything about a cell they deleted (or a connector to one), and new
    ///     bounds for a cell they moved. A connector they re-routed is not removed and added again but updated, keeping its route.
    /// </summary>
    private List<DiagramEdit> RespectPersonEdits(IEnumerable<DiagramEdit> edits)
    {
        var kept = new List<DiagramEdit>();
        foreach (var edit in edits)
            switch (edit)
            {
                case DiagramEdit.AddEdge a when _dismissed.Contains(a.Edge.Id) || _dismissed.Contains(a.Edge.SourceId) || _dismissed.Contains(a.Edge.TargetId):
                    break;

                case DiagramEdit.Remove r when _moved.Contains(r.Id) && edits.OfType<DiagramEdit.AddEdge>().Any(a => a.Edge.Id == r.Id):
                    break; // the add that follows updates it in place

                case DiagramEdit.Update u when _moved.Contains(u.Id) && u.Bounds is not null:
                    var rest = u with { Bounds = null };
                    if (rest.Label is not null || rest.Style is not null || rest.Attributes is not null) kept.Add(rest);
                    break;

                case var e when DiagramEdit.IdOf(e) is { } id && _dismissed.Contains(id):
                    break;

                default:
                    kept.Add(edit);
                    break;
            }

        return kept;
    }

    /// <summary>Raised on the UI thread after a batch of live edits was applied in place (not when it fell back to a reload).</summary>
    public event EventHandler<IReadOnlyList<DiagramEdit>>? LiveEditsApplied;

    /// <summary>
    ///     Applies <paramref name="edits" /> to the generated layer in place through the plugin. Returns false (and changes
    ///     nothing) when the plugin is not ready or refused them; the caller can then reload with <see cref="RequestRefresh" />.
    /// </summary>
    /// <remarks>
    ///     Added cells are marked as generated, get the lock styles when <see cref="DrawioOptions.LockGeneratedCells" /> is on,
    ///     and arrive with their <c>silo-*</c> bindings already evaluated -- the same result <see cref="DrawioDocument.AddModel" />
    ///     plus <see cref="DiagramBindings.Apply" /> gives on a reload.
    /// </remarks>
    public async Task<bool> PluginApplyEditsAsync(IEnumerable<DiagramEdit> edits)
    {
        if (!IsPluginReady) return false;

        // one ordered list: an add, an update and a remove of the same cell can share a batch, and must run in that order
        var cells = new JsonArray();
        foreach (var edit in RespectPersonEdits(DiagramEdit.Coalesce(edits)))
            switch (edit)
            {
                case DiagramEdit.AddNode a:
                    cells.Add(ToPluginCell(a.Node));
                    break;

                case DiagramEdit.AddEdge a:
                    cells.Add(ToPluginCell(a.Edge));
                    break;

                case DiagramEdit.Update u:
                    cells.Add(ToPluginCell(u));
                    break;

                case DiagramEdit.Remove r:
                    cells.Add(new JsonObject { ["id"] = r.Id, ["remove"] = true });
                    break;
            }

        if (cells.Count == 0) return true;

        // the change goes through draw.io's model, so the editor reports an autosave: that is not the person editing
        _ignoreChangesUntilUtc = DateTime.UtcNow.AddMilliseconds(1500);

        var answer = await SendPluginAsync("upsertCells", new JsonObject { ["cells"] = cells }, 5000);
        if (answer?.GetBool("ok") == true) return true;

        _log.Debug("In-place edit of generated cells failed: {error}", answer?.GetString("error") ?? "no answer");
        return false;
    }

    // ---- the incremental source --------------------------------------------------------------

    private void OnSourceEdits(IReadOnlyList<DiagramEdit> edits)
    {
        if (_disposed || edits.Count == 0) return;
        Dispatcher.BeginInvoke(new Action(() => QueueLiveEdits(edits)));
    }

    private void QueueLiveEdits(IReadOnlyList<DiagramEdit> edits)
    {
        if (_disposed) return;

        // nothing to patch yet (or no plugin to patch with): the next load/refresh builds the layer from the source anyway
        if (!_editorReady || !IsPluginReady)
        {
            _sourceDirty = true;
            RequestRefresh();
            return;
        }

        _pendingEdits.AddRange(edits);

        if (_editTimer is null)
        {
            _editTimer      =  new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = LiveEditDelay };
            _editTimer.Tick += async (_, _) =>
                               {
                                   _editTimer.Stop();
                                   await FlushLiveEditsAsync();
                               };
        }

        if (!_editTimer.IsEnabled) _editTimer.Start();
    }

    private async Task FlushLiveEditsAsync()
    {
        if (_disposed || _pendingEdits.Count == 0) return;

        // an edit sent while the editor is still loading a diagram would be wiped by that load
        bool loading = _awaitingLoadSinceUtc is { } since && DateTime.UtcNow - since < LoadWaitLimit;
        if (_flushingEdits || loading)
        {
            _editTimer?.Start();
            return;
        }

        // a rebuild is already on its way and will contain these edits
        if (_sourceDirty)
        {
            _pendingEdits.Clear();
            return;
        }

        var batch = _pendingEdits.ToList();
        _pendingEdits.Clear();

        _flushingEdits = true;
        try
        {
            if (await PluginApplyEditsAsync(batch))
            {
                LiveEditsApplied?.Invoke(this, batch);
            }
            else
            {
                _sourceDirty = true;
                RequestRefresh();
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Applying live diagram edits failed");
            _sourceDirty = true;
            RequestRefresh();
        }
        finally
        {
            _flushingEdits = false;
            if (_pendingEdits.Count > 0) _editTimer?.Start();
        }
    }

    // ---- edit -> plugin cell -----------------------------------------------------------------

    private JsonObject ToPluginCell(DiagramNode n)
    {
        string style = Options.LockGeneratedCells ? DrawioDocument.Lock(n.Style, false) : n.Style;
        var cell = new JsonObject
                   {
                       ["id"]     = n.Id,
                       ["add"]    = true,
                       ["label"]  = n.Label,
                       ["x"]      = n.X,
                       ["y"]      = n.Y,
                       ["width"]  = n.Width,
                       ["height"] = n.Height
                   };
        if (n.Link is not null) cell["link"] = n.Link;
        if (n.Parent is not null) cell["parent"] = n.Parent;
        // a fold (collapsible container) is only made by a rebuild; in place, the node is added as a plain cell

        cell["style"]      = ApplyEvaluated(cell, n.Id, n.Data, style);
        cell["attributes"] = Attributes(n.Data, true);
        return cell;
    }

    private JsonObject ToPluginCell(DiagramEdge e)
    {
        string style = Options.LockGeneratedCells ? DrawioDocument.Lock(e.Style, true) : e.Style;
        var cell = new JsonObject
                   {
                       ["id"]     = e.Id,
                       ["add"]    = true,
                       ["edge"]   = true,
                       ["label"]  = e.Label ?? "",
                       ["source"] = e.SourceId,
                       ["target"] = e.TargetId
                   };
        if (e.Parent is not null) cell["parent"] = e.Parent;
        if (e.LabelAt is { } at) cell["labelAt"] = at;

        cell["style"]      = ApplyEvaluated(cell, e.Id, e.Data, style);
        cell["attributes"] = Attributes(e.Data, true);
        return cell;
    }

    private JsonObject ToPluginCell(DiagramEdit.Update u)
    {
        var cell = new JsonObject { ["id"] = u.Id };
        if (u.Label is not null) cell["label"] = u.Label;

        var entries = new JsonObject();
        if (u.Style is not null)
            foreach (var (k, v) in u.Style)
                entries[k] = v;

        if (u.Attributes is { Count: > 0 })
        {
            cell["attributes"] = Attributes(u.Attributes, false);

            // bindings that arrive with the update are evaluated now, like the ones on a new cell
            if (Options.EnableBindings && DiagramBindings.Evaluate(u.Id, Bindings(u.Attributes)) is { } evaluated)
            {
                if (evaluated["label"] is { } label) cell["label"]     = label.GetValue<string>();
                if (evaluated["tooltip"] is { } tip) cell["tooltip"]   = tip.GetValue<string>();
                if (evaluated["style"] is JsonObject style)
                    foreach (var (k, v) in style)
                        entries[k] = v?.GetValue<string>();
            }
        }

        if (entries.Count > 0) cell["styleEntries"] = entries;

        if (u.Bounds is { } b)
        {
            cell["x"]      = b.X;
            cell["y"]      = b.Y;
            cell["width"]  = b.Width;
            cell["height"] = b.Height;
        }

        return cell;
    }

    /// <summary>Evaluates the cell's bindings into its label / tooltip; returns the style with bound style entries applied.</summary>
    private string ApplyEvaluated(JsonObject cell, string id, IReadOnlyDictionary<string, string>? data, string style)
    {
        if (!Options.EnableBindings || data is null || DiagramBindings.Evaluate(id, Bindings(data)) is not { } evaluated) return style;

        if (evaluated["label"] is { } label) cell["label"]   = label.GetValue<string>();
        if (evaluated["tooltip"] is { } tip) cell["tooltip"] = tip.GetValue<string>();
        if (evaluated["style"] is JsonObject entries)
            foreach (var (k, v) in entries)
                style = DrawioDocument.WithStyleEntry(style, k, v?.GetValue<string>());

        return style;
    }

    private static IEnumerable<(string Name, string Template)> Bindings(IReadOnlyDictionary<string, string> data)
    {
        return data.Where(p => p.Key.StartsWith("silo-", StringComparison.Ordinal)).Select(p => (p.Key, p.Value));
    }

    private static JsonObject Attributes(IReadOnlyDictionary<string, string>? data, bool generated)
    {
        var attributes = new JsonObject();
        if (generated) attributes[DrawioDocument.GeneratedAttribute] = "1";
        if (data is not null)
            foreach (var (k, v) in data)
                attributes[k] = v;
        return attributes;
    }
}
