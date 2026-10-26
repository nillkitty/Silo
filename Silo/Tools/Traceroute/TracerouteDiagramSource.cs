using System.Globalization;
using System.Linq;
using Silo.Drawio.Live;
using Silo.Drawio.Mx;

namespace Silo.Tools.Traceroute;

/// <summary>
///     The generated layer of the Visual Traceroute pane, laid out in rows:
///     <list type="bullet">
///         <item>this computer (name and address, yellow) on the left of the first row, the first hop to its right;</item>
///         <item>then one row per hop, each box holding the host name (when there is one) over the address in bold, or <c>*</c>;</item>
///         <item>consecutive silent hops share one row of small <c>*</c> boxes (one collapsible box with <see cref="CollapseSilent" />);</item>
///         <item>the destination (yellow) at the end of the last row.</item>
///     </list>
///     Connectors leave a box on its right and enter the next on its left (looping round between rows) and carry the round-trip
///     time of the hop they leave; a dashed connector bridges each run of silent hops, from the hop before it to the one after.
/// </summary>
/// <remarks>
///     <para>
///         Structure and values travel separately. Whenever the trace changes, the whole layer is laid out again and only the
///         difference is reported (<see cref="DiagramDiff" />, <see cref="IIncrementalDiagramSource.EditsReady" />): new boxes,
///         moved or resized ones, connectors that changed. Forming or breaking a collapsible run, or toggling
///         <see cref="CollapseSilent" />, rebuilds the layer (<see cref="IDiagramContentSource.Changed" />). Text and colours are
///         values under the <c>trace</c> scheme (<see cref="Values" />) the cells bind to, re-evaluated in place.
///     </para>
///     <para>Call <see cref="Begin" /> and <see cref="Apply" /> on the UI thread.</para>
/// </remarks>
public sealed class TracerouteDiagramSource : IIncrementalDiagramSource
{
    /// <summary>The binding scheme of the values: <c>{trace:h3.addr}</c>.</summary>
    public const string Scheme = "trace";

    public const string LocalId = "trace-local";

    /// <summary>The value of <c>siloKind</c> on hop cells.</summary>
    public const string HopKind = "trace-hop";

    /// <summary>The value of <c>siloKind</c> on the box of this computer.</summary>
    public const string LocalKind = "trace-local";

    /// <summary>The attribute that carries a hop cell's number.</summary>
    public const string HopAttribute = "siloHop";

    /// <summary>The font of the boxes and connectors (a CSS font list: the first one installed is used).</summary>
    public const string FontFamily = "Comic Mono,Cascadia Mono,Consolas,monospace";

    private const double FontSize = 14;
    private const double CharW    = 8.4; // a monospaced character at FontSize (0.6 em)
    private const double PadX     = 14;
    private const double OneLineH = 36;
    private const double TwoLineH = 58;
    private const double MinW     = 160;
    private const double SmallW   = 84;
    private const double Left     = 40;
    private const double Top      = 40;
    private const double RowGap   = 44;
    private const double ItemGap  = 36;
    private const double EndGap   = 84;
    private const double FoldPad  = 8;
    private const double FoldHead = 24;
    private const double Jetty    = 24; // how far a connector runs out of a box before it turns

    private static readonly string Font = $"fontSize={FontSize.ToString(CultureInfo.InvariantCulture)};fontFamily={FontFamily};";

    private static readonly string BoxStyle =
        "rounded=1;arcSize=14;html=1;whiteSpace=nowrap;align=left;verticalAlign=middle;spacingLeft=10;spacingRight=10;" +
        "fillColor=none;strokeWidth=2;" + Font;

    private static readonly string GapStyle =
        "rounded=1;arcSize=14;html=1;whiteSpace=nowrap;align=center;verticalAlign=top;spacingTop=8;container=1;collapsible=1;" +
        "recursiveResize=0;fillColor=none;strokeWidth=2;dashed=1;" + Font;

    private static readonly string EdgeStyle =
        $"edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;jettySize={Jetty};exitX=1;exitY=0.5;exitDx=0;exitDy=0;entryX=0;entryY=0.5;entryDx=0;entryDy=0;" +
        "startArrow=oval;startFill=1;startSize=5;endArrow=classic;endFill=1;strokeWidth=2;fontStyle=1;fontSize=13;" +
        $"labelBackgroundColor=default;fontFamily={FontFamily};";

    private readonly SortedDictionary<int, TraceHop> _hops = [];

    private bool         _collapseSilent;
    private string?      _destination;
    private DiagramModel _published = new();
    private TraceResult? _result;
    private TraceTarget? _target;

    /// <summary>The values the cells bind to.</summary>
    public static DiagramValues Values { get; } = DiagramValues.For(Scheme);

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public event Action<IReadOnlyList<DiagramEdit>>? EditsReady;

    /// <summary>
    ///     Whether a run of two or more consecutive hops that never answered is one collapsible box (draw.io's +/- expands it to
    ///     the row of <c>*</c> boxes) instead of the row itself. Changing it rebuilds the layer.
    /// </summary>
    public bool CollapseSilent
    {
        get => _collapseSilent;
        set
        {
            if (_collapseSilent == value) return;
            _collapseSilent = value;
            PublishHops();
            Changed?.Invoke();
        }
    }

    /// <summary>What the person asked to trace to.</summary>
    public string? Destination => _destination;

    /// <summary>What is being traced, once the destination resolved.</summary>
    public TraceTarget? Target => _target;

    /// <summary>The end of the trace, once it is over.</summary>
    public TraceResult? Result => _result;

    /// <summary>The hops so far, in order.</summary>
    public IReadOnlyCollection<TraceHop> Hops => _hops.Values.ToList();

    /// <summary>Where the trace stands, in a sentence.</summary>
    public string Status => TraceText.Status(_destination, _target, _hops.Values, _result);

    /// <summary>The hop numbered <paramref name="number" />, if there is one.</summary>
    public TraceHop? HopAt(int number)
    {
        return _hops.TryGetValue(number, out var hop) ? hop : null;
    }

    /// <summary>The cell id of a hop.</summary>
    public static string HopId(int number)
    {
        return "trace-hop-" + Num(number);
    }

    /// <summary>The cell id of the connector leading to a hop.</summary>
    public static string EdgeId(int number)
    {
        return "trace-edge-" + Num(number);
    }

    /// <summary>The cell id of the dashed connector that bridges the silent hops after hop <paramref name="from" />.</summary>
    public static string BridgeId(int from)
    {
        return "trace-skip-" + Num(from);
    }

    /// <summary>The cell id of the collapsible box of a run of silent hops, by the run's first hop.</summary>
    public static string GapId(int first)
    {
        return "trace-gap-" + Num(first);
    }

    // ---- feeding ---------------------------------------------------------------------------------

    /// <summary>Starts over for a new trace to <paramref name="destination" /> (the diagram is rebuilt).</summary>
    public void Begin(string destination)
    {
        _destination = destination.Trim();
        _target      = null;
        _result      = null;
        _hops.Clear();

        Values.Clear();
        PublishRun();
        PublishLocal();
        _published = Model();
        Changed?.Invoke();
    }

    /// <summary>Takes one update of the engine into the diagram.</summary>
    public void Apply(TraceUpdate update)
    {
        switch (update)
        {
            case TraceUpdate.Started s:
                _target = s.Target;
                PublishLocal();
                break;

            case TraceUpdate.HopChanged { Hop: var hop }:
                _hops[hop.Number] = hop;
                break;

            case TraceUpdate.HopRemoved r:
                if (_hops.Remove(r.Number)) Values.RemovePrefix(Prefix(r.Number));
                break;

            case TraceUpdate.Finished f:
                _result = f.Result;
                break;
        }

        // values first (a hop's colour depends on the hops after it), then the structure, which is evaluated against them
        PublishHops();
        PublishRun();
        Publish();
    }

    /// <summary>Lays the layer out again and reports what changed (or asks for a rebuild).</summary>
    private void Publish()
    {
        var next  = Model();
        var edits = DiagramDiff.Between(_published, next);
        _published = next;

        if (edits is null) Changed?.Invoke();
        else if (edits.Count > 0) EditsReady?.Invoke(edits);
    }

    /// <inheritdoc />
    public DiagramModel Build()
    {
        return _published = Model();
    }

    // ---- layout ----------------------------------------------------------------------------------

    /// <summary>One thing in a row: a hop, or a run of two or more silent hops.</summary>
    private sealed record Item(int First, int Last)
    {
        public bool IsRun => Last > First;

        public bool Holds(int n)
        {
            return First <= n && n <= Last;
        }
    }

    private sealed class Placed(Item item, int row, double x, double width, double height)
    {
        public Item   Item   { get; } = item;
        public int    Row    { get; } = row;
        public double X      { get; } = x;
        public double Width  { get; } = width;
        public double Height { get; } = height;
        public double Y      { get; set; }
    }

    private static bool IsSilent(TraceHop hop)
    {
        return hop.State == TraceHopState.NoReply && !hop.IsProbing;
    }

    private List<Item> Items()
    {
        var  items = new List<Item>();
        int? first = null, last = null;

        void Flush()
        {
            if (first is null) return;
            if (last > first)
                items.Add(new Item(first.Value, last!.Value));
            else
                items.Add(new Item(first.Value, first.Value));
            first = last = null;
        }

        foreach (var hop in _hops.Values)
        {
            if (IsSilent(hop))
            {
                if (first is not null && last == hop.Number - 1)
                {
                    last = hop.Number;
                }
                else
                {
                    Flush();
                    first = last = hop.Number;
                }

                continue;
            }

            Flush();
            items.Add(new Item(hop.Number, hop.Number));
        }

        Flush();
        return items;
    }

    private (double Width, double Height) LocalSize()
    {
        string name = _target?.LocalName ?? "…";
        string ip   = _target?.LocalAddress?.ToString() ?? "";
        return (TextWidth(Math.Max(name.Length, ip.Length)), TwoLineH);
    }

    private static (double Width, double Height) HopSize(TraceHop hop)
    {
        string name = hop.Name ?? "";
        int    len  = Math.Max(name.Length, TraceText.BoxAddress(hop).Length);
        return (TextWidth(len), name.Length > 0 ? TwoLineH : OneLineH);
    }

    private static double TextWidth(int chars)
    {
        return Math.Max(MinW, Math.Ceiling(chars * CharW + 2 * PadX + 8));
    }

    private double RunWidth(Item run)
    {
        int    count = run.Last - run.First + 1;
        double row   = count * SmallW + (count - 1) * ItemGap;
        return _collapseSilent ? row + 2 * FoldPad : row; // the expanded box, so nothing is placed under it
    }

    private DiagramModel Model()
    {
        var model = new DiagramModel();
        var (localW, localH) = LocalSize();
        double x0 = Left + localW + EndGap;

        // rows: the first item joins this computer's row; the destination joins the row before it
        var items  = Items();
        var placed = new List<Placed>();
        int rows   = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var  item  = items[i];
            bool isEnd = !item.IsRun && i == items.Count - 1 && _hops[item.First].IsDestination;
            var (w, h) = item.IsRun ? (RunWidth(item), OneLineH) : HopSize(_hops[item.First]);

            if (i > 0 && isEnd)
            {
                var last = placed[^1];
                placed.Add(new Placed(item, last.Row, last.X + last.Width + EndGap, w, h));
            }
            else
            {
                placed.Add(new Placed(item, rows++, x0, w, h));
            }
        }

        // vertical: each row as tall as its tallest box, boxes centred on it
        double top    = Top;
        double localY = Top;
        for (int r = 0; r < Math.Max(1, rows); r++)
        {
            var    inRow  = placed.Where(p => p.Row == r).ToList();
            double height = inRow.Count > 0 ? inRow.Max(p => p.Height) : 0;
            if (r == 0) height = Math.Max(height, localH);
            double middle = top + height / 2;

            if (r == 0)
            {
                localY = middle - localH / 2;
                model.Nodes.Add(LocalNode(localY, localW, localH));
            }

            foreach (var p in inRow) p.Y = middle - p.Height / 2;

            top += height + RowGap;
        }

        Placed PlaceOf(int n) => placed.First(p => p.Item.Holds(n));

        // where a hop's box is drawn on the page (a collapsed run: its box)
        (double X, double Y, double W, double H) BoxOf(int? n)
        {
            if (n is not { } hop) return (Left, localY, localW, localH);
            var p = PlaceOf(hop);
            if (!p.Item.IsRun) return (p.X, p.Y, p.Width, p.Height);
            if (_collapseSilent) return (p.X, p.Y, SmallW, OneLineH);
            return (p.X + (hop - p.Item.First) * (SmallW + ItemGap), p.Y, SmallW, OneLineH);
        }

        // nodes: plain boxes, then each run (a collapsible box before the boxes it holds)
        foreach (var p in placed.Where(p => !p.Item.IsRun)) model.Nodes.Add(HopNode(_hops[p.Item.First], p.X, p.Y, p.Width, p.Height, null));
        foreach (var p in placed.Where(p => p.Item.IsRun)) AddRun(model, p);

        // connectors along the route, each carrying the time of the hop it leaves
        int? previous = null;
        foreach (int n in _hops.Keys)
        {
            var  at      = PlaceOf(n);
            bool bridged = previous is { } pv && Bridge(pv) is not null; // that hop's time goes on its bridge instead
            string? label = previous is { } pn && !bridged ? $"{{trace:{Prefix(pn)}ms}}" : null;

            string? parent  = at.Item.IsRun && _collapseSilent && n > at.Item.First ? GapId(at.Item.First) : null;
            bool    sameRow = previous is { } pr ? PlaceOf(pr).Row == at.Row : at.Row == 0;
            string  source  = previous is { } ps ? HopId(ps) : LocalId;

            model.Edges.Add(Edge(EdgeId(n), source, HopId(n), label, EdgeStyle, n, parent, sameRow ? 0 : LoopLabelAt(BoxOf(previous), BoxOf(n))));
            previous = n;
        }

        // dashed bridges over runs of silent hops: into the far hop's right side, or its left when it sits at the end of the run's row
        foreach (int n in _hops.Keys)
        {
            if (Bridge(n) is not { } to) continue;

            bool   besideRun = PlaceOf(to).Row == PlaceOf(to - 1).Row;
            // its own, wider channel beside the solid connector's
            string style = (besideRun ? EdgeStyle.Replace("entryY=0.5;", "entryY=0.3;") : EdgeStyle.Replace("entryX=0;", "entryX=1;")) +
                           $"dashed=1;sourceJettySize={2 * Jetty};" + (besideRun ? "" : $"targetJettySize={2 * Jetty};");
            model.Edges.Add(Edge(BridgeId(n), HopId(n), HopId(to), $"{{trace:{Prefix(n)}ms}}", style, n, null, 0));
        }

        return model;
    }

    /// <summary>
    ///     Where along a connector that loops from one row to the next its label goes: halfway down its first vertical run, just
    ///     after it leaves the box (as a fraction of the route draw.io draws: out, down, back left, down, in).
    /// </summary>
    private static double LoopLabelAt((double X, double Y, double W, double H) from, (double X, double Y, double W, double H) to)
    {
        double sx = from.X + from.W, sy = from.Y + from.H / 2;
        double tx = to.X, ty = to.Y + to.H / 2;
        double mid = (from.Y + from.H + to.Y) / 2;

        double length = Jetty + Math.Abs(mid - sy) + Math.Abs(sx + Jetty - (tx - Jetty)) + Math.Abs(ty - mid) + Jetty;
        double at     = Jetty + Math.Abs(mid - sy) / 2;
        return length <= 0 ? 0 : Math.Clamp(Math.Round(2 * at / length - 1, 3), -0.95, 0.95);
    }

    /// <summary>The hop a dashed bridge from <paramref name="from" /> leads to: the next hop that is not silent, past one or more silent ones.</summary>
    private int? Bridge(int from)
    {
        if (!_hops.TryGetValue(from, out var a) || IsSilent(a) || a.State == TraceHopState.Pending) return null;
        if (!_hops.TryGetValue(from + 1, out var next) || !IsSilent(next)) return null;

        int n = from + 1;
        while (_hops.TryGetValue(n, out var h) && IsSilent(h)) n++;
        return _hops.ContainsKey(n) ? n : null;
    }

    private void AddRun(DiagramModel model, Placed p)
    {
        var run   = p.Item;
        int count = run.Last - run.First + 1;

        if (!_collapseSilent)
        {
            for (int k = 0; k < count; k++)
                model.Nodes.Add(HopNode(_hops[run.First + k], p.X + k * (SmallW + ItemGap), p.Y, SmallW, OneLineH, null));
            return;
        }

        model.Nodes.Add(new DiagramNode(GapId(run.First), $"<b>* ×{count}</b>", p.X, p.Y, SmallW, OneLineH, GapStyle,
                                        new Dictionary<string, string>
                                        {
                                            ["siloKind"]                                  = "trace-gap",
                                            ["siloHops"]                                  = $"{run.First}-{run.Last}",
                                            [DiagramBindings.TooltipAttribute]            = $"Hops {run.First}–{run.Last} did not answer (+ to expand)",
                                            [DiagramBindings.StylePrefix + "strokeColor"] = $"{{trace:g{Num(run.First)}.stroke}}"
                                        },
                                        Fold: new DiagramFold(true, p.X, p.Y, p.Width, FoldHead + OneLineH + FoldPad)));

        for (int k = 0; k < count; k++)
            model.Nodes.Add(HopNode(_hops[run.First + k], FoldPad + k * (SmallW + ItemGap), FoldHead, SmallW, OneLineH, GapId(run.First)));
    }

    private static DiagramNode LocalNode(double y, double width, double height)
    {
        return new DiagramNode(LocalId, "This computer", Left, y, width, height,
                               BoxStyle + $"strokeColor={TracePalette.EndStroke};",
                               new Dictionary<string, string>
                               {
                                   ["siloKind"]                       = LocalKind,
                                   [DiagramBindings.LabelAttribute]   = "<b>{trace:local.name}</b><br><b>{trace:local.ip}</b>",
                                   [DiagramBindings.TooltipAttribute] = "This computer · {trace:local.name} · {trace:local.ip}"
                               });
    }

    private static DiagramNode HopNode(TraceHop hop, double x, double y, double width, double height, string? parent)
    {
        int    n = hop.Number;
        string p = Prefix(n);

        return new DiagramNode(HopId(n), $"Hop {Num(n)}", x, y, width, height, BoxStyle,
                               new Dictionary<string, string>
                               {
                                   ["siloKind"]                                  = HopKind,
                                   [HopAttribute]                                = Num(n),
                                   [DiagramBindings.LabelAttribute]              = $"<div>{{trace:{p}dns}}</div><b>{{trace:{p}addr}}</b>",
                                   [DiagramBindings.TooltipAttribute]            = $"{{trace:{p}tip}}",
                                   [DiagramBindings.StylePrefix + "strokeColor"] = $"{{trace:{p}stroke}}",
                                   [DiagramBindings.StylePrefix + "dashed"]      = $"{{trace:{p}dashed}}"
                               },
                               Parent: parent);
    }

    private static DiagramEdge Edge(string id, string source, string target, string? label, string style, int hop, string? parent, double labelAt)
    {
        var data = new Dictionary<string, string> { ["siloKind"] = "trace-edge", [HopAttribute] = Num(hop) };
        if (label is not null) data[DiagramBindings.LabelAttribute] = label;
        return new DiagramEdge(id, source, target, "", style, data, parent, labelAt);
    }

    // ---- values ----------------------------------------------------------------------------------

    private static string Num(int n)
    {
        return n.ToString(CultureInfo.InvariantCulture);
    }

    private static string Prefix(int n)
    {
        return "h" + Num(n) + ".";
    }

    private void PublishRun()
    {
        Values.SetMany(new Dictionary<string, object?>
                       {
                           ["run.title"]   = TraceText.Title(_destination, _target),
                           ["run.status"]  = Status,
                           ["run.hops"]    = _hops.Count,
                           ["run.outcome"] = _result?.Outcome.ToString() ?? (_target is null ? "resolving" : "probing")
                       });
    }

    private void PublishLocal()
    {
        Values.SetMany(new Dictionary<string, object?>
                       {
                           ["local.name"] = _target?.LocalName ?? "…",
                           ["local.ip"]   = _target?.LocalAddress?.ToString() ?? ""
                       });
    }

    private void PublishHops()
    {
        foreach (var hop in _hops.Values.ToList()) PublishHop(hop);

        // a run's box is red when its last hop is where the path fails, gray when the path goes on
        foreach (var run in Items().Where(i => i.IsRun))
        {
            var role = TracePalette.RoleOf(_hops[run.Last], _hops.Values, _result is not null);
            Values.Set("g" + Num(run.First) + ".stroke", TracePalette.Stroke(role));
        }
    }

    private void PublishHop(TraceHop hop)
    {
        string p        = Prefix(hop.Number);
        var    role     = TracePalette.RoleOf(hop, _hops.Values, _result is not null);
        var    previous = _hops.Where(h => h.Key < hop.Number).Select(h => h.Value).LastOrDefault();

        Values.SetMany(new Dictionary<string, object?>
                       {
                           [p + "addr"]   = TraceText.BoxAddress(hop),
                           [p + "dns"]    = hop.Name ?? "",
                           [p + "name"]   = TraceText.Name(hop),
                           [p + "ms"]     = TraceText.Latency(hop),
                           [p + "rtt"]    = TraceText.Probes(hop),
                           [p + "loss"]   = TraceText.Loss(hop),
                           [p + "tip"]    = TraceText.Tooltip(hop),
                           [p + "delta"]  = TraceText.Delta(previous, hop),
                           [p + "role"]   = role.ToString(),
                           [p + "stroke"] = TracePalette.Stroke(role),
                           [p + "dashed"] = TracePalette.Dashed(hop),
                           [p + "best"]   = hop.Best,
                           [p + "state"]  = hop.State.ToString()
                       });
    }
}
