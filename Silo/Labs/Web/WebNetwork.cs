using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace Silo.Labs;

// ---- Network --------------------------------------------------------------------------------

/// <summary>One request of the page, filled in as the answer comes.</summary>
public sealed class NetworkEntry : INotifyPropertyChanged
{
    private string _flags  = string.Empty;
    private string _size   = string.Empty;
    private string _status = "...";
    private string _time   = string.Empty;

    public NetworkEntry(string requestId, string method, string url, string type)
    {
        RequestId = requestId;
        Method    = method;
        Url       = url;
        Type      = type;
        Started   = Stopwatch.GetTimestamp();

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Host = uri.Scheme is "http" or "https" ? uri.Host : uri.Scheme + ":";
            Path = uri.Scheme is "http" or "https" ? uri.PathAndQuery : url.Length <= 80 ? url : url[..80] + "...";
        }
        else
        {
            Host = string.Empty;
            Path = url;
        }
    }

    // ---- shown in the list

    public string Error { get; set; } = string.Empty;

    /// <summary>What the Lab itself did to the request: blocked, replaced, headers rewritten.</summary>
    public string Flags
    {
        get => _flags;
        set => Set(ref _flags, value);
    }

    public string Host   { get; }
    public string Method { get; }
    public string Mime   { get; set; } = string.Empty;
    public string Path   { get; }

    public string RequestId { get; }

    public string Size
    {
        get => _size;
        set => Set(ref _size, value);
    }

    public long Started { get; }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string Time
    {
        get => _time;
        set => Set(ref _time, value);
    }

    public string Type { get; }
    public string Url  { get; }

    // ---- kept for the details, the exports and the timeline

    public int    StatusCode  { get; set; }
    public string StatusText  { get; set; } = string.Empty;
    public string Protocol    { get; set; } = string.Empty;
    public string RemoteAddress { get; set; } = string.Empty;
    public bool   FromCache   { get; set; }
    public bool   Finished    { get; set; }
    public bool   Failed      { get; set; }

    /// <summary>When the request was made, in seconds of the browser's clock (what the timeline is drawn from).</summary>
    public double Timestamp { get; set; }

    /// <summary>When the request was made, as seconds since 1970.</summary>
    public double WallTime { get; set; }

    /// <summary>When it finished, in seconds of the browser's clock; NaN until then.</summary>
    public double EndTimestamp { get; set; } = double.NaN;

    /// <summary>When the answer began, in seconds of the browser's clock; NaN until then.</summary>
    public double ResponseTimestamp { get; set; } = double.NaN;

    public double EncodedBytes { get; set; }
    public double DecodedBytes { get; set; }

    public JObject? Timing { get; set; }

    public List<KeyValuePair<string, string>> RequestHeaders  { get; set; } = [];
    public List<KeyValuePair<string, string>> ResponseHeaders { get; set; } = [];

    public string? PostData    { get; set; }
    public bool    HasPostData { get; set; }
    public string  Initiator   { get; set; } = string.Empty;

    public string? Body       { get; set; }
    public bool    BodyIsBinary { get; set; }
    public string? BodyError  { get; set; }

    public bool IsBlocked  => Flags.Contains("blocked", StringComparison.Ordinal);
    public bool IsReplaced => Flags.Contains("replaced", StringComparison.Ordinal);

    public bool IsFailed => Failed || StatusCode >= 400;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Turns requests into the forms people copy them as: cURL, fetch, HAR.</summary>
public static class WebExports
{
    private static readonly HashSet<string> SkippedHeaders = new(StringComparer.OrdinalIgnoreCase) { "content-length" };

    private static IEnumerable<KeyValuePair<string, string>> Sendable(NetworkEntry e)
    {
        return e.RequestHeaders.Where(h => !h.Key.StartsWith(':') && !SkippedHeaders.Contains(h.Key));
    }

    /// <summary>The request as a cURL command: for a bash shell, or (<paramref name="cmd" />) for the Windows command line.</summary>
    public static string Curl(NetworkEntry e, bool cmd)
    {
        string Quote(string s) => cmd ? "\"" + s.Replace("\"", "\\\"") + "\"" : "'" + s.Replace("'", "'\\''") + "'";

        var text = new StringBuilder("curl ").Append(Quote(e.Url));
        bool body = !string.IsNullOrEmpty(e.PostData);
        if (!e.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !(body && e.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)))
            text.Append(" -X ").Append(e.Method);

        foreach (var (name, value) in Sendable(e)) text.Append(" -H ").Append(Quote($"{name}: {value}"));
        if (body) text.Append(" --data-raw ").Append(Quote(e.PostData!));
        text.Append(" --compressed");
        return text.ToString();
    }

    /// <summary>The request as a JavaScript <c>fetch</c> call.</summary>
    public static string Fetch(NetworkEntry e)
    {
        var options = new JObject { ["method"] = e.Method };
        var headers = new JObject();
        foreach (var (name, value) in Sendable(e)) headers[name] = value;
        if (headers.Count > 0) options["headers"] = headers;
        if (!string.IsNullOrEmpty(e.PostData)) options["body"] = e.PostData;
        return $"fetch({JsonConvert.ToString(e.Url)}, {options.ToString(Formatting.Indented)});";
    }

    /// <summary>The headers as lines of text ("Name: value").</summary>
    public static string HeaderLines(IEnumerable<KeyValuePair<string, string>> headers)
    {
        return string.Join(Environment.NewLine, headers.Select(h => $"{h.Key}: {h.Value}"));
    }

    /// <summary>The requests as an HTTP Archive (HAR 1.2): what browsers' developer tools save, and other tools open.</summary>
    public static string Har(IReadOnlyList<NetworkEntry> entries, string pageUrl)
    {
        JArray Pairs(IEnumerable<KeyValuePair<string, string>> pairs) => new(pairs.Select(p => new JObject { ["name"] = p.Key, ["value"] = p.Value }));

        var items = new JArray();
        foreach (var e in entries)
        {
            double wait    = 0, receive = 0, dns = -1, connect = -1, ssl = -1, send = 0;
            var    timing  = e.Timing;
            if (timing is not null)
            {
                double Part(string start, string end) => (double?)timing[start] is { } a && (double?)timing[end] is { } b && a >= 0 && b >= a ? b - a : -1;
                dns     = Part("dnsStart", "dnsEnd");
                connect = Part("connectStart", "connectEnd");
                ssl     = Part("sslStart", "sslEnd");
                send    = Math.Max(0, Part("sendStart", "sendEnd"));
                wait    = Math.Max(0, ((double?)timing["receiveHeadersEnd"] ?? 0) - ((double?)timing["sendEnd"] ?? 0));
            }

            double total = !double.IsNaN(e.EndTimestamp) ? (e.EndTimestamp - e.Timestamp) * 1000 : 0;
            if (!double.IsNaN(e.EndTimestamp) && !double.IsNaN(e.ResponseTimestamp)) receive = Math.Max(0, (e.EndTimestamp - e.ResponseTimestamp) * 1000);

            var request = new JObject
                          {
                              ["method"]      = e.Method,
                              ["url"]         = e.Url,
                              ["httpVersion"] = string.IsNullOrEmpty(e.Protocol) ? "HTTP/1.1" : e.Protocol,
                              ["cookies"]     = new JArray(),
                              ["headers"]     = Pairs(e.RequestHeaders),
                              ["queryString"] = Query(e.Url),
                              ["headersSize"] = -1,
                              ["bodySize"]    = e.PostData?.Length ?? 0
                          };
            if (!string.IsNullOrEmpty(e.PostData))
                request["postData"] = new JObject { ["mimeType"] = e.RequestHeaders.FirstOrDefault(h => h.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty, ["text"] = e.PostData };

            var content = new JObject { ["size"] = (long)e.DecodedBytes, ["mimeType"] = e.Mime };
            if (e.Body is not null && !e.BodyIsBinary) content["text"] = e.Body;
            else if (e.Body is not null) { content["text"] = e.Body; content["encoding"] = "base64"; }

            var response = new JObject
                           {
                               ["status"]      = e.StatusCode,
                               ["statusText"]  = e.StatusText,
                               ["httpVersion"] = string.IsNullOrEmpty(e.Protocol) ? "HTTP/1.1" : e.Protocol,
                               ["cookies"]     = new JArray(),
                               ["headers"]     = Pairs(e.ResponseHeaders),
                               ["content"]     = content,
                               ["redirectURL"] = e.ResponseHeaders.FirstOrDefault(h => h.Key.Equals("location", StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty,
                               ["headersSize"] = -1,
                               ["bodySize"]    = (long)e.EncodedBytes
                           };

            items.Add(new JObject
                      {
                          ["startedDateTime"] = DateTimeOffset.FromUnixTimeMilliseconds((long)(e.WallTime * 1000)).ToString("o"),
                          ["time"]            = total,
                          ["request"]         = request,
                          ["response"]        = response,
                          ["cache"]           = new JObject(),
                          ["timings"]         = new JObject { ["blocked"] = -1, ["dns"] = dns, ["connect"] = connect, ["ssl"] = ssl, ["send"] = send, ["wait"] = wait, ["receive"] = receive },
                          ["serverIPAddress"] = e.RemoteAddress,
                          ["_resourceType"]   = e.Type
                      });
        }

        var log = new JObject
                  {
                      ["version"] = "1.2",
                      ["creator"] = new JObject { ["name"] = "Silo Web Lab", ["version"] = "1.0" },
                      ["pages"]   = new JArray { new JObject { ["startedDateTime"] = DateTimeOffset.UtcNow.ToString("o"), ["id"] = "page_1", ["title"] = pageUrl, ["pageTimings"] = new JObject() } },
                      ["entries"] = items
                  };
        foreach (var item in items.OfType<JObject>()) item["pageref"] = "page_1";
        return new JObject { ["log"] = log }.ToString(Formatting.Indented);
    }

    private static JArray Query(string url)
    {
        var result = new JArray();
        int q = url.IndexOf('?');
        if (q < 0) return result;

        string query = url[(q + 1)..];
        int hash = query.IndexOf('#');
        if (hash >= 0) query = query[..hash];
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            result.Add(new JObject { ["name"] = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]), ["value"] = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) });
        }

        return result;
    }

    /// <summary>Whether a response of this type is text worth reading as text.</summary>
    public static bool IsText(string mime)
    {
        return mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mime.Contains("json", StringComparison.OrdinalIgnoreCase) || mime.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
               mime.Contains("javascript", StringComparison.OrdinalIgnoreCase) || mime.Contains("ecmascript", StringComparison.OrdinalIgnoreCase) || mime.Contains("svg", StringComparison.OrdinalIgnoreCase) ||
               mime.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) || mime.Contains("graphql", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
///     Every request the page makes (the "Sources" of a page): what was asked, the status, type, size and time of the answer, and whether
///     the Lab blocked, replaced or rewrote it. Select one to read its headers, what was sent, the answer and the timing; filter the list;
///     keep it across navigations; copy a request as cURL or fetch, or save them all as a HAR.
/// </summary>
internal sealed class NetworkInspector : WebInspector
{
    private const int Limit = 3000;

    private static readonly string[] TypeFilters = ["All", "Document", "XHR/Fetch", "Script", "Stylesheet", "Image", "Media", "Font", "WebSocket", "Other"];

    private readonly Dictionary<string, NetworkEntry>   _byId    = new(StringComparer.Ordinal);
    private readonly ObservableCollection<NetworkEntry> _entries = [];

    private TextBox?     _filter;
    private string       _filterText = string.Empty;
    private string       _type = "All";
    private ComboBox?    _typeBox;
    private CheckBox?    _keep;
    private bool         _keepEntries;
    private TextBlock?   _summary;
    private TabControl?  _tabs;
    private TextBox?     _overview;
    private TextBox?     _headers;
    private TextBox?     _payload;
    private TextBox?     _response;
    private TextBox?     _timing;
    private ListView?    _list;
    private string?      _sortProperty;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private int          _bodyTicket;

    public NetworkInspector(WebLabSession session) : base(session)
    {
    }

    public override LabDock Dock => LabDock.Right;

    public override string IconKey => "Network";

    public override string Id => "network";

    public override string Title => "Network";

    /// <summary>The requests (newest last), for the timeline and the exports.</summary>
    public IReadOnlyList<NetworkEntry> Entries => _entries.ToList();

    /// <summary>The filter as a preset: the text, the type, and whether to keep the requests across navigations.</summary>
    public LabPresetKind Preset => new("network-filter", "Network filter", () => new Dictionary<string, string> { ["text"] = _filterText, ["type"] = _type, ["keep"] = _keepEntries.ToString() },
                                       settings => ApplyFilter(settings.GetValueOrDefault("text") ?? string.Empty, settings.GetValueOrDefault("type") ?? "All", bool.TryParse(settings.GetValueOrDefault("keep"), out bool keep) && keep),
                                       "Network");

    private void ApplyFilter(string text, string type, bool keep)
    {
        _filterText  = text;
        _type        = TypeFilters.Contains(type) ? type : "All";
        _keepEntries = keep;
        _terms       = ParseTerms(text);
        if (_filter is not null) _filter.Text = text;
        if (_typeBox is not null) _typeBox.SelectedItem = _type;
        if (_keep is not null) _keep.IsChecked = keep;
        Refilter();
    }

    protected override UIElement BuildView()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 80 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 60 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // the buttons, then the filter
        var top = new StackPanel();
        var bar = new WrapPanel { Margin = new Thickness(6, 4, 6, 2) };
        bar.Children.Add(WebUi.Tool("Clear", Clear, "Forget the requests so far"));

        _keep = new CheckBox
                {
                    Content = "Keep", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0), IsChecked = _keepEntries,
                    ToolTip = "Keep the requests when the page goes somewhere else"
                };
        _keep.Click += (_, _) => _keepEntries = _keep.IsChecked == true;
        bar.Children.Add(_keep);

        var noCache = new CheckBox
                      {
                          Content = "No cache", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                          ToolTip = "Ask the network for everything, ignoring the browser's cache"
                      };
        noCache.Click += (_, _) => RunSafe(async () =>
                                           {
                                               if (Dev is not null) await Dev.CallAsync("Network.setCacheDisabled", new { cacheDisabled = noCache.IsChecked == true });
                                           });
        bar.Children.Add(noCache);

        var export = WebUi.Tool("Export", () => { }, "Copy or save the requests");
        export.Click += (_, _) => ShowExportMenu(export);
        bar.Children.Add(export);
        top.Children.Add(bar);

        var filterBar = new DockPanel { Margin = new Thickness(6, 2, 6, 4) };
        _typeBox = new ComboBox { ItemsSource = TypeFilters, SelectedItem = _type, Width = 96, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Show only this kind of request" };
        _typeBox.SelectionChanged += (_, _) =>
                                     {
                                         _type = _typeBox.SelectedItem as string ?? "All";
                                         Refilter();
                                     };
        DockPanel.SetDock(_typeBox, System.Windows.Controls.Dock.Right);
        filterBar.Children.Add(_typeBox);
        _filter = new TextBox
                  {
                      Text    = _filterText,
                      ToolTip = "Show only requests that match. Plain text matches the address; /pattern/ is a regular expression; -word hides; method:POST status:404 (or 4xx) type:script host:github.com mime:json is:failed is:blocked is:replaced is:cached"
                  };
        _filter.TextChanged += (_, _) =>
                               {
                                   _filterText = _filter.Text;
                                   _terms      = ParseTerms(_filter.Text);
                                   Refilter();
                               };
        filterBar.Children.Add(_filter);
        top.Children.Add(filterBar);
        Grid.SetRow(top, 0);
        root.Children.Add(top);

        var grid = new GridView();
        grid.Columns.Add(Column("Status", nameof(NetworkEntry.Status), 52));
        grid.Columns.Add(Column("Method", nameof(NetworkEntry.Method), 56));
        grid.Columns.Add(Column("Type",   nameof(NetworkEntry.Type),   70));
        grid.Columns.Add(Column("Host",   nameof(NetworkEntry.Host),   140));
        grid.Columns.Add(Column("Path",   nameof(NetworkEntry.Path),   240));
        grid.Columns.Add(Column("Size",   nameof(NetworkEntry.Size),   64));
        grid.Columns.Add(Column("Time",   nameof(NetworkEntry.Time),   60));
        grid.Columns.Add(Column("Lab",    nameof(NetworkEntry.Flags),  90));

        _list = new ListView { ItemsSource = _entries, View = grid, BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Extended };
        _list.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        _list.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        CollectionViewSource.GetDefaultView(_entries).Filter =  o => o is NetworkEntry entry && Matches(entry);
        _list.SelectionChanged                               += (_, _) => ShowDetails(_list.SelectedItem as NetworkEntry);
        _list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
        _list.MouseDoubleClick += (_, _) =>
                                  {
                                      if (_list.SelectedItem is NetworkEntry entry) CopyText(entry.Url, "the address");
                                  };
        _list.KeyDown += (_, e) =>
                         {
                             if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _list.SelectedItem is NetworkEntry entry) CopyText(entry.Url, "the address");
                         };
        _list.ContextMenu = RequestMenu();
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);

        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 2);
        root.Children.Add(splitter);

        _tabs = new TabControl { BorderThickness = new Thickness(0, 1, 0, 0) };
        _overview = AddTab("Overview", false);
        _headers  = AddTab("Headers",  false);
        _payload  = AddTab("Payload",  false);
        _response = AddTab("Response", false);
        _timing   = AddTab("Timing",   false);
        _tabs.SelectionChanged += (_, e) =>
                                  {
                                      if (e.OriginalSource == _tabs && _tabs.SelectedIndex == 3) LoadBody(_list.SelectedItem as NetworkEntry);
                                  };
        Grid.SetRow(_tabs, 3);
        root.Children.Add(_tabs);

        _summary = WebUi.Muted(new TextBlock { Margin = new Thickness(8, 3, 8, 3), FontSize = 11 });
        Grid.SetRow(_summary, 4);
        root.Children.Add(_summary);
        UpdateSummary();
        return root;
    }

    private TextBox AddTab(string header, bool wrap)
    {
        var box = WebUi.Output();
        box.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _tabs!.Items.Add(new TabItem { Header = header, Content = box });
        return box;
    }

    private static GridViewColumn Column(string header, string property, double width)
    {
        return new GridViewColumn { Header = new GridViewColumnHeader { Content = header, Tag = property, ToolTip = "Click to sort" }, Width = width, DisplayMemberBinding = new Binding(property) };
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string property }) return;

        _sortDirection = _sortProperty == property && _sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        _sortProperty  = property;
        var view = CollectionViewSource.GetDefaultView(_entries);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(property, _sortDirection));
    }

    // ---- the filter -------------------------------------------------------------------------

    private List<(bool Not, string Key, string Value)> _terms = [];

    private static List<(bool Not, string Key, string Value)> ParseTerms(string text)
    {
        var terms = new List<(bool, string, string)>();
        foreach (string word in WebTextFilter.Words(text))
        {
            bool   not  = word.Length > 1 && word[0] == '-';
            string body = not ? word[1..] : word;
            int    colon = body.IndexOf(':');
            if (colon > 0 && body[..colon].ToLowerInvariant() is "method" or "status" or "type" or "host" or "mime" or "is" or "flag")
                terms.Add((not, body[..colon].ToLowerInvariant(), body[(colon + 1)..]));
            else
                terms.Add((not, "text", body));
        }

        return terms;
    }

    private bool Matches(NetworkEntry entry)
    {
        if (_type != "All" && !TypeMatches(entry)) return false;

        foreach (var (not, key, value) in _terms)
        {
            bool hit = key switch
                       {
                           "method" => entry.Method.Equals(value, StringComparison.OrdinalIgnoreCase),
                           "status" => StatusMatches(entry, value),
                           "type"   => entry.Type.Contains(value, StringComparison.OrdinalIgnoreCase),
                           "host"   => entry.Host.Contains(value, StringComparison.OrdinalIgnoreCase),
                           "mime"   => entry.Mime.Contains(value, StringComparison.OrdinalIgnoreCase),
                           "flag"   => entry.Flags.Contains(value, StringComparison.OrdinalIgnoreCase),
                           "is"     => Is(entry, value),
                           _        => new WebTextFilter(value).Matches(entry.Url)
                       };
            if (hit == not) return false;
        }

        return true;
    }

    private static bool StatusMatches(NetworkEntry entry, string value)
    {
        if (value.Length == 3 && value.EndsWith("xx", StringComparison.OrdinalIgnoreCase)) return entry.Status.StartsWith(value[0]);

        return entry.Status.StartsWith(value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Is(NetworkEntry entry, string what)
    {
        return what.ToLowerInvariant() switch
               {
                   "failed"   => entry.IsFailed,
                   "blocked"  => entry.IsBlocked,
                   "replaced" => entry.IsReplaced,
                   "cached"   => entry.FromCache,
                   "pending"  => !entry.Finished && !entry.Failed,
                   _          => false
               };
    }

    private bool TypeMatches(NetworkEntry entry)
    {
        return _type switch
               {
                   "Document"   => entry.Type == "Document",
                   "XHR/Fetch"  => entry.Type is "XHR" or "Fetch",
                   "Script"     => entry.Type == "Script",
                   "Stylesheet" => entry.Type == "Stylesheet",
                   "Image"      => entry.Type == "Image",
                   "Media"      => entry.Type == "Media",
                   "Font"       => entry.Type == "Font",
                   "WebSocket"  => entry.Type == "WebSocket",
                   "Other"      => entry.Type is not ("Document" or "XHR" or "Fetch" or "Script" or "Stylesheet" or "Image" or "Media" or "Font" or "WebSocket"),
                   _            => true
               };
    }

    private void Refilter()
    {
        CollectionViewSource.GetDefaultView(_entries).Refresh();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_summary is null) return;

        var shown = _entries.Where(Matches).ToList();
        double bytes = shown.Sum(e => e.EncodedBytes);
        var last = shown.Where(e => !double.IsNaN(e.EndTimestamp)).Select(e => e.EndTimestamp).DefaultIfEmpty(double.NaN).Max();
        var first = shown.Select(e => e.Timestamp).DefaultIfEmpty(double.NaN).Min();
        string span = !double.IsNaN(last) && !double.IsNaN(first) ? $"  ·  finished in {(last - first):0.##} s" : string.Empty;
        _summary.Text = shown.Count == _entries.Count
                            ? $"{shown.Count} requests  ·  {WebUi.FormatSize(bytes)} transferred{span}"
                            : $"{shown.Count} of {_entries.Count} requests  ·  {WebUi.FormatSize(bytes)} transferred{span}";
    }

    // ---- details ----------------------------------------------------------------------------

    private void ShowDetails(NetworkEntry? entry)
    {
        if (_overview is null) return;

        _bodyTicket++;
        if (entry is null)
        {
            _overview.Text = _headers!.Text = _payload!.Text = _response!.Text = _timing!.Text = string.Empty;
            return;
        }

        _overview.Text = string.Join(Environment.NewLine, new[]
                                                          {
                                                              $"{entry.Method} {entry.Url}",
                                                              $"Status: {entry.Status} {entry.StatusText}".TrimEnd(),
                                                              $"Type: {entry.Type}   Mime: {entry.Mime}",
                                                              $"Size: {entry.Size}   Time: {entry.Time}{(entry.FromCache ? "   (from cache)" : string.Empty)}",
                                                              entry.Protocol.Length > 0 ? $"Protocol: {entry.Protocol}   Remote: {entry.RemoteAddress}" : string.Empty,
                                                              entry.Initiator.Length > 0 ? $"Started by: {entry.Initiator}" : string.Empty,
                                                              $"Lab: {(entry.Flags.Length > 0 ? entry.Flags : "-")}",
                                                              entry.Error.Length > 0 ? "Error: " + entry.Error : string.Empty
                                                          }.Where(l => l.Length > 0));

        var headers = new StringBuilder();
        headers.AppendLine("Request headers").AppendLine(entry.RequestHeaders.Count == 0 ? "(none)" : WebExports.HeaderLines(entry.RequestHeaders)).AppendLine();
        headers.AppendLine("Response headers").Append(entry.ResponseHeaders.Count == 0 ? "(none yet)" : WebExports.HeaderLines(entry.ResponseHeaders));
        _headers!.Text = headers.ToString();

        _payload!.Text = entry.PostData ?? (entry.HasPostData ? "(the request has a body that could not be read)" : "(no body was sent)");
        _timing!.Text  = TimingText(entry);

        _response!.Text = entry.Body ?? (entry.BodyError ?? "(select this tab to read the answer)");
        if (_tabs!.SelectedIndex == 3) LoadBody(entry);

        if (entry.HasPostData && entry.PostData is null) RunSafe(async () => await LoadPostDataAsync(entry));
    }

    private static string TimingText(NetworkEntry e)
    {
        var lines = new List<string> { $"Started {DateTimeOffset.FromUnixTimeMilliseconds((long)(e.WallTime * 1000)).LocalDateTime:HH:mm:ss.fff}" };
        if (e.Timing is { } t)
        {
            double Part(string start, string end) => (double?)t[start] is { } a && (double?)t[end] is { } b && a >= 0 && b >= a ? b - a : -1;
            void Add(string name, double ms)
            {
                if (ms >= 0) lines.Add($"{name,-22}{ms,9:0.##} ms");
            }

            Add("DNS lookup", Part("dnsStart", "dnsEnd"));
            Add("Connecting", Part("connectStart", "connectEnd"));
            Add("TLS", Part("sslStart", "sslEnd"));
            Add("Sending", Part("sendStart", "sendEnd"));
            Add("Waiting (first byte)", Math.Max(0, ((double?)t["receiveHeadersEnd"] ?? 0) - ((double?)t["sendEnd"] ?? 0)));
        }

        if (!double.IsNaN(e.EndTimestamp) && !double.IsNaN(e.ResponseTimestamp)) lines.Add($"{"Downloading",-22}{(e.EndTimestamp - e.ResponseTimestamp) * 1000,9:0.##} ms");
        if (!double.IsNaN(e.EndTimestamp)) lines.Add($"{"Total",-22}{(e.EndTimestamp - e.Timestamp) * 1000,9:0.##} ms");
        else if (!e.Failed) lines.Add("Not finished yet");
        return string.Join(Environment.NewLine, lines);
    }

    private async Task LoadPostDataAsync(NetworkEntry entry)
    {
        if (Dev is null) return;

        var response = await Dev.TryCallAsync("Network.getRequestPostData", new { requestId = entry.RequestId });
        if (response?["postData"] is { } post)
        {
            entry.PostData = post.ToString();
            if (_list?.SelectedItem == entry && _payload is not null) _payload.Dispatcher.Invoke(() => _payload.Text = entry.PostData);
        }
    }

    private void LoadBody(NetworkEntry? entry)
    {
        if (entry is null || _response is null) return;

        if (entry.Body is not null || entry.BodyError is not null)
        {
            _response.Text = BodyText(entry);
            return;
        }

        if (!entry.Finished)
        {
            _response.Text = entry.Failed ? "(the request failed: there is no answer)" : "(still loading)";
            return;
        }

        int ticket = ++_bodyTicket;
        _response.Text = "Reading the answer...";
        RunSafe(async () =>
                {
                    await ReadBodyAsync(entry);
                    if (ticket == _bodyTicket && _list?.SelectedItem == entry) _response.Dispatcher.Invoke(() => _response.Text = BodyText(entry));
                });
    }

    private async Task ReadBodyAsync(NetworkEntry entry)
    {
        if (entry.Body is not null || Dev is null) return;

        var response = await Dev.TryCallAsync("Network.getResponseBody", new { requestId = entry.RequestId });
        if (response is null)
        {
            entry.BodyError = "(the browser no longer has this answer, or it has none)";
            return;
        }

        string body = (string?)response["body"] ?? string.Empty;
        entry.BodyIsBinary = (bool?)response["base64Encoded"] == true;
        if (entry.BodyIsBinary && WebExports.IsText(entry.Mime))
        {
            try
            {
                body = Encoding.UTF8.GetString(Convert.FromBase64String(body));
                entry.BodyIsBinary = false;
            }
            catch (FormatException)
            {
                // stays as base64
            }
        }

        // text as text; anything else stays base64 (what a HAR file wants too)
        entry.Body = body;
    }

    /// <summary>What the Response tab shows for the entry: the text, or a note and the start of the base64 for an answer that is not text.</summary>
    private static string BodyText(NetworkEntry entry)
    {
        if (entry.Body is null) return entry.BodyError ?? string.Empty;
        if (!entry.BodyIsBinary) return entry.Body;

        return $"(binary, {entry.DecodedBytes:0} bytes; base64 follows)\n{(entry.Body.Length > 4000 ? entry.Body[..4000] + "..." : entry.Body)}";
    }

    // ---- copy and export --------------------------------------------------------------------

    private ContextMenu RequestMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
                       {
                           menu.Items.Clear();
                           BuildRequestMenu(menu);
                       };
        return menu;
    }

    private void BuildRequestMenu(ContextMenu menu)
    {
        var selected = _list?.SelectedItems.OfType<NetworkEntry>().ToList() ?? [];
        if (selected.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(select a request)", IsEnabled = false });
            return;
        }

        var one = selected[0];
        void Item(ItemsControl parent, string header, string? icon, Action act)
        {
            var item = new MenuItem { Header = header, Icon = icon is null ? null : LabUi.Icon(icon) };
            item.Click += (_, _) => act();
            parent.Items.Add(item);
        }

        var copy = new MenuItem { Header = "Copy", Icon = LabUi.Icon("TbCopy") };
        Item(copy, "Address",                    null, () => CopyText(string.Join(Environment.NewLine, selected.Select(e => e.Url)), "the address"));
        Item(copy, "As cURL (bash)",             null, () => CopyText(string.Join(Environment.NewLine, selected.Select(e => WebExports.Curl(e, false))), "cURL"));
        Item(copy, "As cURL (cmd)",              null, () => CopyText(string.Join(Environment.NewLine, selected.Select(e => WebExports.Curl(e, true))), "cURL"));
        Item(copy, "As fetch",                   null, () => CopyText(string.Join(Environment.NewLine, selected.Select(WebExports.Fetch)), "fetch"));
        copy.Items.Add(new Separator());
        Item(copy, "Request headers",            null, () => CopyText(WebExports.HeaderLines(one.RequestHeaders), "the request headers"));
        Item(copy, "Response headers",           null, () => CopyText(WebExports.HeaderLines(one.ResponseHeaders), "the response headers"));
        Item(copy, "Response",                   null, () => RunSafe(async () =>
                                                                       {
                                                                           await ReadBodyAsync(one);
                                                                           if (one.Body is not null) _list!.Dispatcher.Invoke(() => CopyText(one.Body, "the response"));
                                                                       }));
        menu.Items.Add(copy);
        Item(menu, "Open in Browser", "Launch", () =>
                                                {
                                                    try
                                                    {
                                                        Process.Start(new ProcessStartInfo(one.Url) { UseShellExecute = true });
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        Session.Say("Could not open the browser: " + ex.Message);
                                                    }
                                                });
        menu.Items.Add(new Separator());
        Item(menu, "Block This Host", "Stop", () => Session.BlockHost(one.Host));
        menu.Items.Add(new Separator());
        Item(menu, "Save Shown as HAR...", "TbSave", () => RunSafe(() => SaveHarAsync(false)));
    }

    private void ShowExportMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        void Item(string header, string icon, Action act)
        {
            var item = new MenuItem { Header = header, Icon = LabUi.Icon(icon) };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }

        Item("Save Shown as HAR...", "TbSave", () => RunSafe(() => SaveHarAsync(false)));
        Item("Save All as HAR...", "TbSave", () => RunSafe(() => SaveHarAsync(true)));
        Item("Save Shown as HAR with Responses...", "TbSave", () => RunSafe(() => SaveHarAsync(false, true)));
        menu.Items.Add(new Separator());
        Item("Copy Shown Addresses", "TbCopy", () => CopyText(string.Join(Environment.NewLine, _entries.Where(Matches).Select(e => e.Url)), "the addresses"));
        Item("Copy Shown as cURL", "TbCopy", () => CopyText(string.Join(Environment.NewLine, _entries.Where(Matches).Select(e => WebExports.Curl(e, false))), "cURL"));
        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void CopyText(string text, string what)
    {
        if (text.Length == 0) return;

        LabUi.Copy(text);
        Session.Say($"Copied {what}");
    }

    /// <summary>Saves the requests as a HAR file; <paramref name="all" /> takes the ones the filter hides too; <paramref name="bodies" /> reads the answers of the text ones first.</summary>
    public async Task SaveHarAsync(bool all, bool bodies = false)
    {
        var items = (all ? _entries.ToList() : _entries.Where(Matches).ToList());
        if (items.Count == 0)
        {
            Session.Say("There are no requests to save.");
            return;
        }

        string? path = null;
        await Application.Current.Dispatcher.InvokeAsync(() =>
                                                         {
                                                             var dialog = new SaveFileDialog { Title = "Save the requests", Filter = "HTTP Archive (*.har)|*.har|JSON (*.json)|*.json", FileName = "requests.har" };
                                                             if (dialog.ShowDialog() == true) path = dialog.FileName;
                                                         });
        if (path is null) return;

        if (bodies)
        {
            Session.Say("Reading the answers...");
            foreach (var entry in items.Where(e => e.Finished && e.Body is null && e.DecodedBytes <= 512 * 1024 && WebExports.IsText(e.Mime)).Take(500)) await ReadBodyAsync(entry);
        }

        try
        {
            File.WriteAllText(path, WebExports.Har(items, Session.CurrentValue?.AbsoluteUri ?? string.Empty));
            Session.Say($"Saved {items.Count} request(s) to {path}");
        }
        catch (Exception ex)
        {
            Session.Say("Could not save the requests: " + ex.Message);
        }
    }

    /// <summary>Forgets the requests.</summary>
    public void Clear()
    {
        _entries.Clear();
        _byId.Clear();
        ShowDetails(null);
        UpdateSummary();
    }

    // ---- the page ---------------------------------------------------------------------------

    private static List<KeyValuePair<string, string>> HeaderList(JToken? headers)
    {
        return (headers as JObject)?.Properties().Select(p => new KeyValuePair<string, string>(p.Name, p.Value.ToString())).ToList() ?? [];
    }

    protected override void OnAttached()
    {
        Dev!.On("Network.requestWillBeSent", e =>
                                             {
                                                 string id = (string?)e["requestId"] ?? string.Empty;
                                                 if (_byId.TryGetValue(id, out var previous) && e["redirectResponse"] is JObject redirect)
                                                 {
                                                     previous.Status     = ((int?)redirect["status"])?.ToString() ?? "redirect";
                                                     previous.StatusCode = (int?)redirect["status"] ?? 0;
                                                     previous.Finished   = true;
                                                     previous.EndTimestamp = (double?)e["timestamp"] ?? double.NaN;
                                                 }

                                                 var request = e["request"];
                                                 var entry = new NetworkEntry(id, (string?)request?["method"] ?? "GET", (string?)request?["url"] ?? string.Empty, (string?)e["type"] ?? "Other")
                                                             {
                                                                 Timestamp      = (double?)e["timestamp"] ?? 0,
                                                                 WallTime       = (double?)e["wallTime"] ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                                                                 RequestHeaders = HeaderList(request?["headers"]),
                                                                 PostData       = (string?)request?["postData"],
                                                                 HasPostData    = (bool?)request?["hasPostData"] == true || request?["postData"] is not null,
                                                                 Initiator      = Initiator(e["initiator"])
                                                             };
                                                 entry.Flags = Session.FlagsFor(entry.Url);
                                                 _byId[id]   = entry;
                                                 _entries.Add(entry);
                                                 while (_entries.Count > Limit)
                                                 {
                                                     _byId.Remove(_entries[0].RequestId);
                                                     _entries.RemoveAt(0);
                                                 }

                                                 UpdateSummary();
                                             });
        Dev.On("Network.requestWillBeSentExtraInfo", e =>
                                                     {
                                                         // the headers as they went on the wire (cookies and all) are fuller than the ones the page set
                                                         if (_byId.TryGetValue((string?)e["requestId"] ?? string.Empty, out var entry) && HeaderList(e["headers"]) is { Count: > 0 } wire) entry.RequestHeaders = wire;
                                                     });
        Dev.On("Network.responseReceived", e =>
                                           {
                                               if (!_byId.TryGetValue((string?)e["requestId"] ?? string.Empty, out var entry)) return;

                                               var response = e["response"];
                                               entry.StatusCode        = (int?)response?["status"] ?? 0;
                                               entry.Status            = entry.StatusCode == 0 ? "?" : entry.StatusCode.ToString();
                                               entry.StatusText        = (string?)response?["statusText"] ?? string.Empty;
                                               entry.Mime              = (string?)response?["mimeType"] ?? string.Empty;
                                               entry.Protocol          = (string?)response?["protocol"] ?? string.Empty;
                                               entry.RemoteAddress     = response?["remoteIPAddress"] is { } ip ? $"{ip}:{(int?)response?["remotePort"]}" : string.Empty;
                                               entry.FromCache         = (bool?)response?["fromDiskCache"] == true || (bool?)response?["fromServiceWorker"] == true;
                                               entry.ResponseHeaders   = HeaderList(response?["headers"]);
                                               entry.Timing            = response?["timing"] as JObject;
                                               entry.ResponseTimestamp = (double?)e["timestamp"] ?? double.NaN;
                                               if ((string?)e["type"] is { Length: > 0 } type && entry.Type != type) { /* the type of the request stays what it was asked as */ }
                                           });
        Dev.On("Network.responseReceivedExtraInfo", e =>
                                                    {
                                                        if (_byId.TryGetValue((string?)e["requestId"] ?? string.Empty, out var entry) && HeaderList(e["headers"]) is { Count: > 0 } wire) entry.ResponseHeaders = wire;
                                                    });
        Dev.On("Network.dataReceived", e =>
                                       {
                                           if (_byId.TryGetValue((string?)e["requestId"] ?? string.Empty, out var entry)) entry.DecodedBytes += (double?)e["dataLength"] ?? 0;
                                       });
        Dev.On("Network.loadingFinished", e =>
                                          {
                                              if (!_byId.TryGetValue((string?)e["requestId"] ?? string.Empty, out var entry)) return;

                                              entry.Finished     = true;
                                              entry.EncodedBytes = (double?)e["encodedDataLength"] ?? 0;
                                              entry.EndTimestamp = (double?)e["timestamp"] ?? double.NaN;
                                              entry.Size         = WebUi.FormatSize(entry.EncodedBytes);
                                              entry.Time         = $"{Stopwatch.GetElapsedTime(entry.Started).TotalMilliseconds:0} ms";
                                              UpdateSummary();
                                          });
        Dev.On("Network.loadingFailed", e =>
                                        {
                                            if (!_byId.TryGetValue((string?)e["requestId"] ?? string.Empty, out var entry)) return;

                                            entry.Failed       = true;
                                            entry.Error        = (string?)e["errorText"] ?? string.Empty;
                                            entry.Status       = (bool?)e["canceled"] == true ? "canceled" : "failed";
                                            entry.EndTimestamp = (double?)e["timestamp"] ?? double.NaN;
                                            entry.Time         = $"{Stopwatch.GetElapsedTime(entry.Started).TotalMilliseconds:0} ms";
                                            if (entry.Flags.Length == 0 && (string?)e["blockedReason"] is { Length: > 0 } reason) entry.Flags = reason;

                                            // a request that failed is a problem to look at (one the person cancelled, or the Lab blocked on purpose, is not)
                                            if ((bool?)e["canceled"] != true && !entry.IsBlocked)
                                                Session.ReportProblem(LabProblemSeverity.Error, "Network", $"{entry.Method} {entry.Url}: {entry.Error}", entry.Host.Length > 0 ? entry.Host : null, () => Session.ShowPane("network"));
                                            UpdateSummary();
                                        });

        // the Lab hears of a request it blocked, replaced or rewrote, possibly after the protocol told of the request
        Session.FlagChanged += (url, flags) =>
                               {
                                   var entry = _entries.LastOrDefault(en => en.Url == url);
                                   if (entry is not null) entry.Flags = flags;
                               };

        RunSafe(() => Dev.EnableAsync("Network"));
    }

    private static string Initiator(JToken? initiator)
    {
        if (initiator is null) return string.Empty;

        string type = (string?)initiator["type"] ?? string.Empty;
        var    frame = initiator["stack"]?["callFrames"]?.FirstOrDefault();
        if (frame is not null) return $"{type}: {(string?)frame["functionName"]} {(string?)frame["url"]}:{((int?)frame["lineNumber"] ?? 0) + 1}".Trim();

        return (string?)initiator["url"] is { Length: > 0 } url ? $"{type}: {url}" : type;
    }

    internal override void OnNavigating(Uri uri)
    {
        if (!_keepEntries) Clear();
    }
}
