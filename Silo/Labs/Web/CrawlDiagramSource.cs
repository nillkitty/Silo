using System.Globalization;
using System.Net;
using Silo.Drawio.Live;
using Silo.Drawio.Mx;

namespace Silo.Labs.Web;

/// <summary>
///     The map of a crawl, drawn as it happens: the pages read, one column per level (the start page on the left), each joined to the page it
///     was first found from; and, in a last column, every other host the pages link to -- one box per host with the number of links, grey and
///     dashed when the host is on the skip list. A page's box says where it stands: queued (grey, dashed), being read (blue), read (green),
///     failed or not HTML (red), or never read (yellow: the crawl was stopped or hit its page limit).
/// </summary>
/// <remarks>
///     <para>
///         It follows <see cref="IIncrementalDiagramSource" /> the way the traceroute does: structure (boxes, connectors, positions) is built again
///         on each event and only the difference is reported; text and colours are values under the <c>crawl</c> scheme (<see cref="Values" />)
///         the cells bind to, so they change in place. Nothing is ever moved: a box keeps the row it was given when it appeared.
///     </para>
///     <para>
///         The map shows what the crawl <i>found</i>: a same-host link beyond the last level, or past the page limit, is counted on its page
///         ("+12 not read") rather than drawn. Call <see cref="Begin" />, <see cref="Apply" /> and <see cref="Finish" /> on the UI thread.
///     </para>
/// </remarks>
public sealed class CrawlDiagramSource : IIncrementalDiagramSource
{
    /// <summary>The binding scheme of the values: <c>{crawl:p3.label}</c>.</summary>
    public const string Scheme = "crawl";

    public const string PageKind = "crawl-page", HostKind = "crawl-host";

    private const double BoxW = 200, BoxH = 48, ColGap = 80, RowGap = 14, Left = 40, Top = 40;
    private const int    MaxLabel = 34;

    public static DiagramValues Values { get; } = DiagramValues.For(Scheme);

    public enum PageState
    {
        Queued,
        Reading,
        Read,
        Failed,
        NotRead
    }

    private sealed class Page(string id, Uri url, int depth, string? parentId, int row)
    {
        public string    Id       { get; } = id;
        public Uri       Url      { get; } = url;
        public int       Depth    { get; } = depth;
        public string?   ParentId { get; } = parentId;
        public int       Row      { get; } = row;
        public PageState State    { get; set; } = PageState.Queued;
        public int       Links    { get; set; }
        public int       Unread   { get; set; }
    }

    private sealed class Host(string id, string name, bool skipped, int row)
    {
        public string                 Id      { get; } = id;
        public string                 Name    { get; } = name;
        public bool                   Skipped { get; } = skipped;
        public int                    Row     { get; } = row;
        public int                    Links   { get; set; }
        public Dictionary<string, int> ByPage { get; } = [];
    }

    private readonly Dictionary<string, Page> _pages = new(StringComparer.Ordinal);
    private readonly List<Page>               _pageOrder = [];
    private readonly Dictionary<string, Host> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Host>               _hostOrder = [];
    private readonly Dictionary<int, int>     _nextRow = [];

    private CrawlOptions? _options;
    private DiagramModel  _published = new();
    private bool          _finished;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public event Action<IReadOnlyList<DiagramEdit>>? EditsReady;

    public int PageCount => _pageOrder.Count;

    public int HostCount => _hostOrder.Count;

    public IReadOnlyList<(Uri Url, PageState State)> Pages => _pageOrder.Select(p => (p.Url, p.State)).ToList();

    /// <summary>The map's cell id of a page (by its address), or null when it is not on the map.</summary>
    public string? IdOf(Uri url)
    {
        return _pages.TryGetValue(url.AbsoluteUri, out var p) ? p.Id : null;
    }

    /// <summary>Where the crawl stands, in a sentence.</summary>
    public string Status
    {
        get
        {
            int read    = _pageOrder.Count(p => p.State is PageState.Read or PageState.Failed);
            int failed  = _pageOrder.Count(p => p.State == PageState.Failed);
            int queued  = _pageOrder.Count(p => p.State is PageState.Queued or PageState.Reading);
            int skipped = _hostOrder.Count(h => h.Skipped);
            var text    = $"{read:N0} page{(read == 1 ? "" : "s")} read" + (failed > 0 ? $" ({failed:N0} failed)" : "") + (queued > 0 && !_finished ? $", {queued:N0} to go" : "") +
                          $"; {_hostOrder.Count:N0} other host{(_hostOrder.Count == 1 ? "" : "s")}" + (skipped > 0 ? $" ({skipped:N0} skipped)" : "");
            return text;
        }
    }

    // ---- feeding ---------------------------------------------------------------------------------

    /// <summary>Starts a map for a new crawl (it is rebuilt, with the start page queued).</summary>
    public void Begin(CrawlOptions options)
    {
        _options  = options;
        _finished = false;
        _pages.Clear();
        _pageOrder.Clear();
        _hosts.Clear();
        _hostOrder.Clear();
        _nextRow.Clear();
        Values.Clear();

        AddPage(new UriBuilder(options.Root) { Fragment = string.Empty }.Uri, 1, null);
        PublishValues();
        _published = Model();
        Changed?.Invoke();
    }

    /// <summary>Takes one event of the crawl into the map.</summary>
    public void Apply(CrawlEvent e)
    {
        if (_options is null) return;

        switch (e)
        {
            case CrawlEvent.Reading r:
                if (_pages.TryGetValue(r.Url.AbsoluteUri, out var reading)) reading.State = PageState.Reading;
                break;

            case CrawlEvent.Read read:
                if (!_pages.TryGetValue(read.Url.AbsoluteUri, out var page)) break;

                page.State = read.Ok ? PageState.Read : PageState.Failed;
                page.Links = read.Links.Count;
                foreach (var (url, _) in read.Links) Found(page, url);
                break;
        }

        Publish();
    }

    /// <summary>The crawl is over (or was stopped): what was queued and never read is marked so.</summary>
    public void Finish()
    {
        _finished = true;
        foreach (var p in _pageOrder.Where(p => p.State is PageState.Queued or PageState.Reading)) p.State = PageState.NotRead;
        Publish();
    }

    // ---- what a link does to the map -------------------------------------------------------------

    private void Found(Page from, Uri url)
    {
        var  options = _options!;
        bool ownHost = string.Equals(url.Host, options.Root.Host, StringComparison.OrdinalIgnoreCase);
        bool skipped = !ownHost && options.Skip?.Matches(url.Host) == true;
        string key   = url.AbsoluteUri;

        // the crawl reads it: a box for the page, joined to the one it was first found from
        bool wouldRead = from.Depth < options.Levels && (!options.SameHostOnly || ownHost) && !skipped;
        if (wouldRead && (_pages.ContainsKey(key) || _pages.Count < options.MaxPages))
        {
            if (!_pages.ContainsKey(key)) AddPage(url, from.Depth + 1, from.Id);
            return;
        }

        if (_pages.ContainsKey(key)) return; // a link back to a page already on the map

        // it will not be read: another host is one box with a count; one of the site's own is only counted on its page
        if (!ownHost && (skipped || options.SameHostOnly))
        {
            if (!_hosts.TryGetValue(url.Host, out var host))
            {
                host = new Host("h" + _hostOrder.Count.ToString(CultureInfo.InvariantCulture), url.Host, skipped, NextRow(options.Levels));
                _hosts[url.Host] = host;
                _hostOrder.Add(host);
            }

            host.Links++;
            host.ByPage[from.Id] = host.ByPage.GetValueOrDefault(from.Id) + 1;
        }
        else
        {
            from.Unread++;
        }
    }

    private Page AddPage(Uri url, int depth, string? parentId)
    {
        var page = new Page("p" + _pageOrder.Count.ToString(CultureInfo.InvariantCulture), url, depth, parentId, NextRow(depth - 1));
        _pages[url.AbsoluteUri] = page;
        _pageOrder.Add(page);
        return page;
    }

    private int NextRow(int column)
    {
        int row = _nextRow.GetValueOrDefault(column);
        _nextRow[column] = row + 1;
        return row;
    }

    // ---- publishing ------------------------------------------------------------------------------

    /// <summary>Sets the values, lays the map out again and reports what changed (or asks for a rebuild).</summary>
    private void Publish()
    {
        PublishValues();

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

    /// <summary>
    ///     The map as an ordinary diagram (draw.io XML): every box and connector as it stands now, the live values written into them, and nothing
    ///     locked, so it can be kept in the Silo and edited like any other diagram.
    /// </summary>
    public string ToDiagramXml()
    {
        PublishValues();

        var doc = DrawioDocument.CreateEmpty();
        doc.AddModel(Model(), generated: false, locked: false);
        DiagramBindings.Freeze(doc);
        return doc.ToXml();
    }

    private static string Enc(string text) => WebUtility.HtmlEncode(text);

    private static string Short(Uri url, bool root)
    {
        string text = root ? url.Host : url.PathAndQuery == "/" ? url.Host + "/" : url.PathAndQuery;
        return text.Length <= MaxLabel ? text : text[..(MaxLabel - 1)] + "…";
    }

    private static (string Fill, string Stroke, string Width, string Dashed) Look(PageState state)
    {
        return state switch
               {
                   PageState.Queued  => ("#f5f5f5", "#999999", "1", "1"),
                   PageState.Reading => ("#dae8fc", "#6c8ebf", "3", "0"),
                   PageState.Read    => ("#d5e8d4", "#82b366", "1", "0"),
                   PageState.Failed  => ("#f8cecc", "#b85450", "1", "0"),
                   _                 => ("#fff2cc", "#d6b656", "1", "1")
               };
    }

    private void PublishValues()
    {
        var values = new Dictionary<string, object?>();
        foreach (var p in _pageOrder)
        {
            var (fill, stroke, width, dashed) = Look(p.State);
            bool root = p.Depth == 1 && p.ParentId is null;
            string state = p.State switch
                           {
                               PageState.Queued  => "queued",
                               PageState.Reading => "reading…",
                               PageState.Read    => $"{p.Links:N0} link{(p.Links == 1 ? "" : "s")}" + (p.Unread > 0 ? $", +{p.Unread:N0} not read" : ""),
                               PageState.Failed  => "could not be read",
                               _                 => "not read"
                           };
            values[p.Id + ".label"]  = $"<b>{Enc(Short(p.Url, root))}</b><br>{Enc(state)}";
            values[p.Id + ".tip"]    = $"{p.Url.AbsoluteUri}\nlevel {p.Depth} · {state}";
            values[p.Id + ".fill"]   = fill;
            values[p.Id + ".stroke"] = stroke;
            values[p.Id + ".width"]  = width;
            values[p.Id + ".dashed"] = dashed;
        }

        foreach (var h in _hostOrder)
        {
            values[h.Id + ".label"] = $"<b>{Enc(h.Name)}</b><br>{h.Links:N0} link{(h.Links == 1 ? "" : "s")}" + (h.Skipped ? " · skipped" : "");
            values[h.Id + ".tip"]   = h.Skipped ? $"{h.Name} is on the skip list: linked to, never read" : $"{h.Name}: linked to, not read";
            foreach (var (pageId, n) in h.ByPage) values[EdgeKey(pageId, h.Id) + ".n"] = n.ToString("N0", CultureInfo.InvariantCulture);
        }

        Values.SetMany(values);
    }

    private static string EdgeKey(string pageId, string hostId) => "e_" + pageId + "_" + hostId;

    // ---- layout ----------------------------------------------------------------------------------

    private static double X(int column) => Left + column * (BoxW + ColGap);

    private static double Y(int row) => Top + row * (BoxH + RowGap);

    private const string PageStyle = "rounded=1;arcSize=12;html=1;whiteSpace=wrap;align=left;verticalAlign=middle;spacingLeft=8;spacingRight=8;fontSize=12;";

    private const string HostStyle = "rounded=1;arcSize=30;html=1;whiteSpace=wrap;align=left;verticalAlign=middle;spacingLeft=8;spacingRight=8;fontSize=12;fillColor=#ffffff;strokeColor=#666666;";

    private const string SkippedHostStyle = "rounded=1;arcSize=30;html=1;whiteSpace=wrap;align=left;verticalAlign=middle;spacingLeft=8;spacingRight=8;fontSize=12;fillColor=#f0f0f0;strokeColor=#aaaaaa;fontColor=#666666;dashed=1;";

    private const string TreeEdgeStyle = "edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;endArrow=classic;endFill=1;strokeColor=#888888;";

    private const string HostEdgeStyle = "edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;endArrow=classic;endFill=1;strokeColor=#bbbbbb;dashed=1;fontSize=10;labelBackgroundColor=default;";

    private DiagramModel Model()
    {
        var model = new DiagramModel();
        if (_options is null) return model;

        int hostColumn = _options.Levels;
        foreach (var p in _pageOrder)
        {
            string id = p.Id;
            model.Nodes.Add(new DiagramNode(id, "", X(p.Depth - 1), Y(p.Row), BoxW, BoxH, PageStyle,
                                            new Dictionary<string, string>
                                            {
                                                ["siloKind"]                                    = PageKind,
                                                ["url"]                                         = p.Url.AbsoluteUri,
                                                [DiagramBindings.LabelAttribute]                = $"{{crawl:{id}.label}}",
                                                [DiagramBindings.TooltipAttribute]              = $"{{crawl:{id}.tip}}",
                                                [DiagramBindings.StylePrefix + "fillColor"]     = $"{{crawl:{id}.fill}}",
                                                [DiagramBindings.StylePrefix + "strokeColor"]   = $"{{crawl:{id}.stroke}}",
                                                [DiagramBindings.StylePrefix + "strokeWidth"]   = $"{{crawl:{id}.width}}",
                                                [DiagramBindings.StylePrefix + "dashed"]        = $"{{crawl:{id}.dashed}}"
                                            }, p.Url.AbsoluteUri));
        }

        foreach (var h in _hostOrder)
        {
            model.Nodes.Add(new DiagramNode(h.Id, "", X(hostColumn), Y(h.Row), BoxW, BoxH, h.Skipped ? SkippedHostStyle : HostStyle,
                                            new Dictionary<string, string>
                                            {
                                                ["siloKind"]                       = HostKind,
                                                ["host"]                           = h.Name,
                                                [DiagramBindings.LabelAttribute]   = $"{{crawl:{h.Id}.label}}",
                                                [DiagramBindings.TooltipAttribute] = $"{{crawl:{h.Id}.tip}}"
                                            }));
        }

        foreach (var p in _pageOrder.Where(p => p.ParentId is not null))
            model.Edges.Add(new DiagramEdge("e_" + p.Id, p.ParentId!, p.Id, "", TreeEdgeStyle));

        foreach (var h in _hostOrder)
        foreach (var pageId in h.ByPage.Keys)
            model.Edges.Add(new DiagramEdge(EdgeKey(pageId, h.Id), pageId, h.Id, "", HostEdgeStyle,
                                            new Dictionary<string, string> { [DiagramBindings.LabelAttribute] = $"{{crawl:{EdgeKey(pageId, h.Id)}.n}}" }));

        return model;
    }
}
