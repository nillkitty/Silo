using System.Globalization;
using System.Linq;
using System.Threading;
using Silo.Drawio.Extensions;
using Silo.Drawio.Mx;
using Telefrag.DI;

namespace Silo.Providers.Diagramming;

/// <summary>
///     The Tree node: grows a tree of vertices from the node, one per item of the source's tree, joined by connectors and
///     laid out as a tree (draw.io's tree folding gives every vertex its +/-). Items are copies of the node itself (its
///     style and bindings, with the item in scope); <c>silo-item-label</c>, <c>silo-item-tooltip</c>,
///     <c>silo-item-style.*</c> and <c>silo-item-attr.*</c> on the node override what the copies get.
/// </summary>
/// <remarks>
///     A level is read when it is above <see cref="TreeNode.ExpandDepth" />, when its vertex is expanded, or when it
///     already has children; otherwise an item that may have children gets a hidden "…" child and starts collapsed, so
///     expanding it (or double-clicking the "…") reads it.
/// </remarks>
[Singleton(typeof(IPaletteProvider))]
public sealed class TreeNodeBinder : DiagramNodeBinder
{
    /// <summary>The most vertices one tree node makes; deeper items wait to be expanded.</summary>
    public const int VertexBudget = 2000;

    private const string ItemPrefix = "silo-item-";

    public override string Kind         => "tree";
    public override Type   SettingsType => typeof(TreeNode);

    protected override DrawioPaletteEntry CreatePaletteEntry()
    {
        const string style = "rounded=1;whiteSpace=wrap;html=1;treeFolding=1;fillColor=#fff2cc;strokeColor=#d6b656;";
        string xml = PaletteTemplates.Model(
            $"<object id=\"2\" label=\"Tree\" silo-label=\"{{.}}\"{DefaultAttributesXml()}>" +
            $"<mxCell style=\"{style}\" vertex=\"1\" parent=\"1\"><mxGeometry width=\"120\" height=\"40\" as=\"geometry\"/></mxCell></object>");

        return new DrawioPaletteEntry(style, 120, 40, "Tree", "Tree node (grows from a URI)", xml);
    }

    public override async Task<BindResult> Bind(BindContext context)
    {
        var result   = new BindResult();
        var snapshot = context.Snapshot;
        var owner    = snapshot.Owner;
        var settings = (TreeNode)context.Settings;

        BindOwner(context, result);
        if (context.Error is not null || context.Value is null) return result;

        if (DiagramSources.AsTree(context.Value) is not { } source)
        {
            result.Changes.Add(new CellChange { Id = owner.Id, Attributes = new() { [DiagramNodeSchema.ErrorAttribute] = $"'{ObjectPath.Display(context.Value)}' cannot be shown as a tree." } });
            return result;
        }

        // the vertices the node already made, by item key
        var existing = snapshot.Made.Where(c => !c.Edge && c.Attribute(DiagramNodeSchema.MoreAttribute) is null && c.Attribute(DiagramNodeSchema.KeyAttribute) is not null)
                               .GroupBy(c => c.Attribute(DiagramNodeSchema.KeyAttribute)!)
                               .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var withChildren = new HashSet<string>(existing.Keys.Select(ParentKey), StringComparer.Ordinal);

        var grower = new Grower(settings, context, existing, withChildren);
        var root   = new Shape("", owner.Id, null, ShapeKind.Root) { Collapsed = owner.Collapsed };
        await grower.GrowAsync(root, 0, source.GetRootsAsync, null, owner);

        // layout
        var size = owner.Bounds ?? new DiagramBounds(0, 0, 120, 40);
        MarkHidden(root, false);
        Measure(root, settings, size);
        Place(root, settings, size, 0, 0);
        double dx = size.X - root.X, dy = size.Y - root.Y;

        // owner: keep tree folding on so it has its +/-
        if (StyleValue(owner.Style, "treeFolding") != "1")
            result.Changes.Add(new CellChange { Id = owner.Id, StyleEntries = new() { ["treeFolding"] = "1" } });
        if (grower.ForceOpen.Contains("")) result.Changes.Add(new CellChange { Id = owner.Id, Collapsed = false });

        var itemStyle  = WithStyle(owner.Style, "treeFolding", "1");
        var itemAttrs  = ItemAttributes(owner);
        var bindings   = itemAttrs.Where(a => a.Value is not null && IsBinding(a.Key)).Select(a => (a.Key, a.Value!)).ToList();
        var edgeStyle  = settings.EdgeStyle is { Length: > 0 } es ? es : DefaultEdgeStyle(settings.Direction);

        foreach (var (parent, shape) in Walk(root))
        {
            if (parent is null) continue; // the owner

            var bounds = new DiagramBounds(shape.X + dx, shape.Y + dy, shape.Width, shape.Height);
            bool known = existing.TryGetValue(shape.Key, out var cell) && shape.Kind == ShapeKind.Item;

            CellChange change;
            if (shape.Kind == ShapeKind.Item)
            {
                var attributes = new Dictionary<string, string?>(itemAttrs, StringComparer.Ordinal)
                {
                    [DiagramNodeSchema.BoundOfAttribute] = owner.Id,
                    [DiagramNodeSchema.KeyAttribute]     = shape.Key
                };

                change = known
                             ? new CellChange { Id = shape.Id, Bounds = bounds, Attributes = attributes }
                             : new CellChange
                             {
                                 Id = shape.Id, Add = true, Parent = owner.Parent, Style = itemStyle, Label = shape.Item?.Text ?? "", Attributes = attributes,
                                 Bounds = bounds, Collapsed = shape.Collapsed, Visible = !shape.Hidden
                             };

                var scope = new BindingScope(shape.Item, context.Value);
                Fill(change, bindings, scope, shape.Item);
                if (grower.ForceOpen.Contains(shape.Key)) change.Collapsed = false;
                result.Scopes[shape.Id] = scope;
            }
            else
            {
                // "… N more" and the "…" a lazy item waits behind: fixed text, no bindings
                change = new CellChange
                {
                    Id = shape.Id, Add = true, Parent = owner.Parent, Label = shape.Text, Bounds = bounds, Visible = exists(shape.Id) ? null : !shape.Hidden,
                    Style = WithStyle(WithStyle(WithStyle(itemStyle, "fontStyle", "2"), "treeFolding", null), "dashed", "1"),
                    Attributes = new()
                    {
                        [DiagramNodeSchema.BoundOfAttribute] = owner.Id,
                        [DiagramNodeSchema.KeyAttribute]     = parent.Key,
                        [DiagramNodeSchema.MoreAttribute]    = shape.Kind == ShapeKind.Lazy ? "lazy" : "1"
                    }
                };
            }

            result.Changes.Add(change);
            result.Keep.Add(shape.Id);

            string edgeId = MadeId(owner.Id, "edge", shape.Id);
            result.Changes.Add(new CellChange
            {
                Id = edgeId, Add = true, Edge = true, Parent = owner.Parent, Source = parent.Id, Target = shape.Id, Style = edgeStyle,
                Visible = snapshot.Find(edgeId) is null ? !shape.Hidden : null,
                Attributes = new() { [DiagramNodeSchema.BoundOfAttribute] = owner.Id, [DiagramNodeSchema.KeyAttribute] = shape.Key }
            });
            result.Keep.Add(edgeId);
        }

        result.RemoveStale(snapshot);
        return result;

        bool exists(string id)
        {
            return snapshot.Find(id) is not null;
        }
    }

    // ---- growing -----------------------------------------------------------------------------

    private enum ShapeKind
    {
        Root,
        Item,
        More,
        Lazy
    }

    /// <summary>One vertex of the tree being built.</summary>
    private sealed class Shape(string key, string id, ITreeItem? item, ShapeKind kind)
    {
        public string     Key  { get; } = key;
        public string     Id   { get; } = id;
        public ITreeItem? Item { get; } = item;
        public ShapeKind  Kind { get; } = kind;

        public string      Text     { get; init; } = "";
        public List<Shape> Children { get; } = [];
        public bool        Collapsed { get; set; }
        public bool        Hidden    { get; set; }

        public double Width  { get; set; }
        public double Height { get; set; }
        public double Span   { get; set; }
        public double X      { get; set; }
        public double Y      { get; set; }
    }

    private sealed class Grower(TreeNode settings, BindContext context, Dictionary<string, BoundCell> existing, HashSet<string> withChildren)
    {
        private int _count;

        /// <summary>Keys of vertices that must be opened (the person double-clicked their "…").</summary>
        public HashSet<string> ForceOpen { get; } = new(StringComparer.Ordinal);

        public async Task GrowAsync(Shape shape, int depth, Func<CancellationToken, ValueTask<IReadOnlyList<ITreeItem>>> read, bool? hasChildren, BoundCell? cell)
        {
            context.Cancel.ThrowIfCancellationRequested();
            if (depth >= settings.MaxDepth || hasChildren == false) return;

            bool forced = context.Expand.Contains(shape.Key);
            bool load = forced ||
                        depth < settings.ExpandDepth ||
                        (cell is not null && (!cell.Collapsed || withChildren.Contains(shape.Key)));

            if (!load || _count >= VertexBudget)
            {
                // read later: a hidden "…" waits under the collapsed vertex
                shape.Collapsed = cell?.Collapsed ?? true;
                shape.Children.Add(new Shape(shape.Key, MadeId(context.Snapshot.Owner.Id, "lazy", shape.Key), null, ShapeKind.Lazy) { Text = "…" });
                return;
            }

            if (forced)
            {
                ForceOpen.Add(shape.Key);
                shape.Collapsed = false;
            }
            else if (cell is null)
            {
                shape.Collapsed = false;
            }

            IReadOnlyList<ITreeItem> items;
            try
            {
                items = await read(context.Cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                shape.Children.Add(new Shape(shape.Key, MadeId(context.Snapshot.Owner.Id, "more", shape.Key), null, ShapeKind.More) { Text = "⚠ " + ex.Message });
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.Take(Math.Max(0, settings.MaxChildren)))
            {
                string local = Escape(item.Key);
                for (int n = 2; !seen.Add(local); n++) local = Escape(item.Key) + "#" + n.ToString(CultureInfo.InvariantCulture);

                string key   = shape.Key.Length == 0 ? local : shape.Key + "/" + local;
                var    child = new Shape(key, MadeId(context.Snapshot.Owner.Id, "item", key), item, ShapeKind.Item);
                shape.Children.Add(child);
                _count++;

                existing.TryGetValue(key, out var childCell);
                await GrowAsync(child, depth + 1, item.GetChildrenAsync, item.HasChildren, childCell);
            }

            if (items.Count > settings.MaxChildren)
                shape.Children.Add(new Shape(shape.Key, MadeId(context.Snapshot.Owner.Id, "more", shape.Key), null, ShapeKind.More)
                {
                    Text = $"… {(items.Count - settings.MaxChildren).ToString("N0", CultureInfo.CurrentCulture)} more"
                });
        }
    }

    // ---- layout ------------------------------------------------------------------------------

    private static void MarkHidden(Shape shape, bool hidden)
    {
        shape.Hidden = hidden;
        foreach (var c in shape.Children) MarkHidden(c, hidden || shape.Collapsed);
    }

    /// <summary>Sizes every vertex and works out how much room (across the levels) each subtree needs. Hidden subtrees take none.</summary>
    private static void Measure(Shape shape, TreeNode settings, DiagramBounds size)
    {
        (shape.Width, shape.Height) = shape.Kind switch
        {
            ShapeKind.Lazy => (Math.Min(40, size.Width), Math.Min(24, size.Height)),
            _              => (size.Width, size.Height)
        };

        bool   down     = settings.Direction == TreeDirection.Down;
        double own      = down ? shape.Width : shape.Height;
        double children = 0;
        int    shown    = 0;
        foreach (var c in shape.Children)
        {
            Measure(c, settings, size);
            if (c.Hidden) continue;
            children += c.Span;
            shown++;
        }

        if (shown > 1) children += (shown - 1) * settings.SiblingGap;
        shape.Span = Math.Max(own, children);
    }

    /// <summary>Places a subtree with its room starting at <paramref name="across" />, its vertex at level position <paramref name="along" />.</summary>
    private static void Place(Shape shape, TreeNode settings, DiagramBounds size, double across, double along)
    {
        bool   down = settings.Direction == TreeDirection.Down;
        double own  = down ? shape.Width : shape.Height;

        double myAcross = across + (shape.Span - own) / 2;
        if (down)
        {
            shape.X = myAcross;
            shape.Y = along;
        }
        else
        {
            shape.X = along;
            shape.Y = myAcross;
        }

        var    shown = shape.Children.Where(c => !c.Hidden).ToList();
        double total = shown.Sum(c => c.Span) + Math.Max(0, shown.Count - 1) * settings.SiblingGap;
        double next  = across + (shape.Span - total) / 2;
        double level = along + (down ? shape.Height : shape.Width) + settings.LevelGap;

        foreach (var c in shape.Children)
        {
            if (c.Hidden)
            {
                // hidden vertices wait where their parent is
                PlaceHidden(c, shape.X, shape.Y);
                continue;
            }

            Place(c, settings, size, next, level);
            next += c.Span + settings.SiblingGap;
        }
    }

    private static void PlaceHidden(Shape shape, double x, double y)
    {
        shape.X = x;
        shape.Y = y;
        foreach (var c in shape.Children) PlaceHidden(c, x, y);
    }

    private static IEnumerable<(Shape? Parent, Shape Shape)> Walk(Shape root)
    {
        var stack = new Stack<(Shape?, Shape)>();
        stack.Push((null, root));
        while (stack.Count > 0)
        {
            var (parent, shape) = stack.Pop();
            yield return (parent, shape);
            for (int i = shape.Children.Count - 1; i >= 0; i--) stack.Push((shape, shape.Children[i]));
        }
    }

    // ---- what copies carry -------------------------------------------------------------------

    /// <summary>
    ///     The attributes an item vertex starts with: the node's own (its bindings, its plain attributes), minus the node
    ///     settings, with <c>silo-item-*</c> overrides put in place of the bindings they name.
    /// </summary>
    private static Dictionary<string, string?> ItemAttributes(BoundCell owner)
    {
        var settingNames = DiagramNodeSchema.Settings(typeof(TreeNode)).Select(s => s.Attribute).ToHashSet(StringComparer.Ordinal);
        var attributes   = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (k, v) in owner.Attributes)
            if (!DiagramNodeSchema.StructuralAttributes.Contains(k) && !settingNames.Contains(k) && !k.StartsWith(ItemPrefix, StringComparison.Ordinal))
                attributes[k] = v;

        foreach (var (k, v) in owner.Attributes)
            if (k.StartsWith(ItemPrefix, StringComparison.Ordinal))
                attributes["silo-" + k[ItemPrefix.Length..]] = v;

        return attributes;
    }

    private static bool IsBinding(string name)
    {
        return name is "silo-label" or "silo-tooltip" || name.StartsWith("silo-style.", StringComparison.Ordinal) || name.StartsWith("silo-attr.", StringComparison.Ordinal);
    }

    private static string DefaultEdgeStyle(TreeDirection direction)
    {
        return direction == TreeDirection.Down
                   ? "edgeStyle=elbowEdgeStyle;elbow=vertical;rounded=0;html=1;endArrow=none;"
                   : "edgeStyle=elbowEdgeStyle;elbow=horizontal;rounded=0;html=1;endArrow=none;";
    }

    private static string Escape(string key)
    {
        return key.Replace("%", "%25", StringComparison.Ordinal).Replace("/", "%2F", StringComparison.Ordinal);
    }

    private static string ParentKey(string key)
    {
        int slash = key.LastIndexOf('/');
        return slash < 0 ? "" : key[..slash];
    }
}
