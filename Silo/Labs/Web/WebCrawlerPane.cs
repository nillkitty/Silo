using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Silo.Drawio;
using Silo.Drawio.Host;
using Silo.Drawio.Live;
using Silo.Drawio.Mx;
using Silo.Drawio.Options;
using DiagramRecord = Silo.DbModel.Global.Diagram;

namespace Silo.Labs;

/// <summary>
///     The Link Crawler: type an address and how many levels to follow, and get back every address the pages link to, how many links point
///     at it and what they most often say. The pages are read as text (see <see cref="Web.LinkCrawler" />), never rendered.
/// </summary>
internal sealed class CrawlerInspector : WebInspector
{
    private readonly ObservableCollection<Web.CrawledLink> _rows = [];

    private Button?                  _go;
    private CancellationTokenSource? _running;
    private TextBox?                 _address;
    private ComboBox?                _levels;
    private CheckBox?                _sameHost;
    private CheckBox?                _includeSkipped;
    private TextBlock?               _status;

    // the live map of the crawl: its data is kept whether or not the Map tab was ever opened (the browser is only made when it is)
    private readonly Web.CrawlDiagramSource _map = new();
    private TabControl?                     _tabs;
    private TabItem?                        _mapTab;
    private DrawioView?                     _mapView;
    private Button?                         _saveMap;

    // the column the grid is sorted by (a binding path) and which way, so a second click on its header turns it round
    private string?                  _sortPath;
    private ListSortDirection        _sortDirection;
    private readonly Dictionary<GridViewColumn, string> _titles = [];

    public override LabDock Dock => LabDock.Bottom;

    public override string IconKey => "Link";

    public override string Id => "crawler";

    public override string Title => "Link Crawler";

    public override bool VisibleByDefault => false;

    internal override void OnNavigated(Uri? uri)
    {
        // start from the page being studied, unless an address was typed
        if (_address is { } box && box.Text.Length == 0 && uri is { Scheme: "http" or "https" }) box.Text = uri.AbsoluteUri;
    }

    protected override UIElement BuildView()
    {
        var root = new DockPanel();

        var bar = new WrapPanel { Margin = new Thickness(6, 4, 6, 4) };
        bar.Children.Add(new TextBlock { Text = "Start at", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }.Muted());
        _address = new TextBox { Width = 320, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Where the crawl begins" };
        _address.KeyDown += (_, e) =>
                            {
                                if (e.Key == Key.Enter) Start();
                            };
        bar.Children.Add(_address);

        bar.Children.Add(new TextBlock { Text = "Levels", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }.Muted());
        _levels = new ComboBox
                  {
                      ItemsSource   = Enumerable.Range(1, 10).ToList(), SelectedItem = 2, Width = 48, Margin = new Thickness(0, 0, 8, 0),
                      ToolTip       = "How many levels of pages to read: 1 is the start page alone, 2 also the pages it links to, and so on"
                  };
        bar.Children.Add(_levels);

        _sameHost = new CheckBox
                    {
                        Content = "Same host only", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                        ToolTip = "Read only pages on the start address's host (links to other hosts are still listed)"
                    };
        bar.Children.Add(_sameHost);

        _includeSkipped = new CheckBox
                          {
                              Content = "Include skipped domains", IsChecked = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
                              ToolTip = "Also read the domains on the skip list (analytics, social media, SaaS apps, file sharing, CDNs, FTP...). Off: links to them are still listed and counted, but not read"
                          };
        bar.Children.Add(_includeSkipped);
        bar.Children.Add(WebUi.Tool("Skip list...", EditSkipList, "Edit the domains the crawl does not read", "Settings"));

        _go = new Button { Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 4, 0), ToolTip = "Read the pages and list the addresses they link to (again to stop)" };
        _go.Click += (_, _) => Start();
        SetRunning(false);
        bar.Children.Add(_go);
        bar.Children.Add(WebUi.Tool("Copy", Copy, "Copy the table as tab-separated text", "TbCopy"));
        _saveMap = WebUi.Tool("Save map as diagram", SaveMap, "Keep the map of this crawl as a diagram in the Silo (as it stands now, to edit like any other diagram)", "FlowDiagram");
        _saveMap.IsEnabled = false;
        bar.Children.Add(_saveMap);
        DockPanel.SetDock(bar, System.Windows.Controls.Dock.Top);
        root.Children.Add(bar);

        _status = new TextBlock { Margin = new Thickness(8, 2, 8, 4), TextTrimming = TextTrimming.CharacterEllipsis, Text = "Reads pages as text; nothing is rendered." }.Muted();
        DockPanel.SetDock(_status, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(_status);

        var grid = new GridView();
        grid.Columns.Add(Column("LinkUrl",            nameof(Web.CrawledLink.LinkUrl),            420));
        grid.Columns.Add(Column("InstanceCount",      nameof(Web.CrawledLink.InstanceCount),      100));
        grid.Columns.Add(Column("MostCommonLinkText", nameof(Web.CrawledLink.MostCommonLinkText), 260));
        grid.Columns.Add(Column("Crawl",              nameof(Web.CrawledLink.Crawl),               80));
        foreach (var c in grid.Columns) _titles[c] = (string)c.Header;

        var list = new ListView { ItemsSource = _rows, View = grid, BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Extended };
        list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
        list.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        list.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        list.MouseDoubleClick += (_, _) =>
                                 {
                                     if (list.SelectedItem is Web.CrawledLink link && Uri.TryCreate(link.LinkUrl, UriKind.Absolute, out var uri)) Session.OpenAddress(uri);
                                 };

        // the links as a table, and the crawl as a map drawn while it runs
        _tabs = new TabControl { BorderThickness = new Thickness(0) };
        _tabs.Items.Add(new TabItem { Header = "Links", Content = list });
        _mapTab = new TabItem { Header = "Map" };
        _tabs.Items.Add(_mapTab);
        _tabs.SelectionChanged += (_, _) => EnsureMapView();
        root.Children.Add(_tabs);
        return root;
    }

    /// <summary>The map's browser is made the first time the Map tab is shown (it is the expensive part), and then fed by the source that has been following the crawl all along.</summary>
    private void EnsureMapView()
    {
        if (_mapTab is null || !ReferenceEquals(_tabs?.SelectedItem, _mapTab) || _mapView is not null) return;

        // what a crawl draws is the person's to move and delete
        var profile = DrawioProfiles.Instance.Get(DrawioProfiles.CrawlMap);
        if (profile.Options.LockGeneratedCells)
        {
            profile.Options.LockGeneratedCells = false;
            profile.NotifyChanged();
        }

        _mapView        = new DrawioView(DrawioProfiles.CrawlMap) { Source = _map, Title = "Link Crawl Map" };
        _mapTab.Content = _mapView;
    }

    /// <summary>The Crawl button: its icon and text, or Stop while a crawl runs.</summary>
    private void SetRunning(bool running)
    {
        if (_go is null) return;

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (LabUi.Icon(running ? "CrawlStop" : "Crawl", 14) is { } glyph)
        {
            glyph.Margin = new Thickness(0, 0, 5, 0);
            content.Children.Add(glyph);
        }

        content.Children.Add(new TextBlock { Text = running ? "Stop" : "Crawl", VerticalAlignment = VerticalAlignment.Center });
        _go.Content = content;
    }

    /// <summary>Keeps the map of the crawl as a diagram in the active Silo: what the map shows now (moved boxes included when it is open), made ordinary cells.</summary>
    private async void SaveMap()
    {
        if (App.Instance?.ActiveSilo is not { IsWritable: true } silo || silo.Data is not IDiagramStore store)
        {
            Say("Open a Silo that can be written to, to keep the map in it.");
            return;
        }

        if (_map.PageCount == 0)
        {
            Say("Crawl something first.");
            return;
        }

        try
        {
            string? xml = null;
            if (_mapView is not null)
            {
                var current = DrawioDocument.Parse(await _mapView.GetXmlAsync());
                if (current.Cells.Any())
                {
                    DiagramBindings.Freeze(current);
                    xml = current.ToXml();
                }
            }

            xml ??= _map.ToDiagramXml();
            string what  = Uri.TryCreate(_address?.Text, UriKind.Absolute, out var root) ? root.Host : "site";
            var    saved = store.SaveDiagram(new DiagramRecord { Name = $"Link crawl of {what} {DateTime.Now:yyyy-MM-dd HH.mm}", Kind = "canvas", Profile = DrawioProfiles.Canvas, Xml = xml });
            DiagramEvents.RaiseChanged();
            Say($"Saved \"{saved.Name}\" to the Silo's diagrams.");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Saving the crawl map failed");
            Say("Saving the map failed: " + ex.Message);
        }
    }

    private static GridViewColumn Column(string header, string path, double width)
    {
        return new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new Binding(path) };
    }

    /// <summary>Clicking a column header sorts the rows by it; clicking it again turns the order round. An arrow in the header shows which.</summary>
    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { DisplayMemberBinding: Binding { Path.Path: { } path } } column }) return;

        _sortDirection = _sortPath == path && _sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        _sortPath      = path;

        var view = CollectionViewSource.GetDefaultView(_rows);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(path, _sortDirection));
        if (path != nameof(Web.CrawledLink.LinkUrl))
            view.SortDescriptions.Add(new SortDescription(nameof(Web.CrawledLink.LinkUrl), ListSortDirection.Ascending)); // equal rows keep a steady order

        foreach (var (c, title) in _titles) c.Header = c == column ? title + (_sortDirection == ListSortDirection.Ascending ? " ▲" : " ▼") : title;
    }

    /// <summary>Edits the skip list as text: one domain or pattern a line, # for comments. Saving it unchanged from the defaults goes back to following the defaults.</summary>
    private void EditSkipList()
    {
        var window = new Window
                     {
                         Title = "Link Crawler: domains to skip", Width = 560, Height = 640, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                         Owner = Window.GetWindow(_address)
                     };
        var box = new TextBox
                  {
                      Text = Web.CrawlSkipList.LoadText(), AcceptsReturn = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                      VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 12, 12, 8)
                  };
        var note = new TextBlock
                   {
                       Text = "One domain a line: it covers every host below it (facebook.com also skips m.facebook.com). * and ? patterns work (ftp.*). Lines starting # are comments. " +
                              "Links to these domains are still listed and counted, just not read.",
                       TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 12, 12, 0)
                   }.Muted();

        var save   = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), MinWidth = 80 };
        var reset  = new Button { Content = "Reset to defaults", Padding = new Thickness(14, 4, 14, 4), ToolTip = "Put back the list Silo starts with" };
        reset.Click += (_, _) => box.Text = Web.CrawlSkipList.DefaultText;
        save.Click += (_, _) =>
                      {
                          try
                          {
                              Web.CrawlSkipList.SaveText(box.Text);
                              Say($"The skip list has {Web.CrawlSkipList.Parse(box.Text).Entries.Count:N0} entries");
                              window.DialogResult = true;
                          }
                          catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                          {
                              Say("The skip list could not be saved: " + ex.Message);
                          }
                      };

        var row = new DockPanel { Margin = new Thickness(12, 0, 12, 12), LastChildFill = false };
        DockPanel.SetDock(reset, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(cancel, System.Windows.Controls.Dock.Right);
        DockPanel.SetDock(save, System.Windows.Controls.Dock.Right);
        row.Children.Add(reset);
        row.Children.Add(cancel);
        row.Children.Add(save);

        var root = new DockPanel();
        DockPanel.SetDock(note, System.Windows.Controls.Dock.Top);
        DockPanel.SetDock(row, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(note);
        root.Children.Add(row);
        root.Children.Add(box);
        window.Content = root;
        window.ShowDialog();
    }

    private async void Start()
    {
        if (_running is not null)
        {
            _running.Cancel();
            return;
        }

        if (!Session.TryParseAddress(_address?.Text ?? string.Empty, out var root) || root is not { Scheme: "http" or "https" })
        {
            Say("Type the http or https address to start at.");
            return;
        }

        _address!.Text = root.AbsoluteUri;
        int levels = _levels?.SelectedItem as int? ?? 2;
        // the skip list is read each time, so an edit applies to the next crawl; "include skipped domains" leaves it out for this one
        var skip    = _includeSkipped?.IsChecked == true ? null : Web.CrawlSkipList.Load();
        var options = new Web.CrawlOptions(root, levels, _sameHost?.IsChecked == true, Skip: skip);

        _running = new CancellationTokenSource();
        var cancel = _running.Token;
        var task   = Silo.Tasks.TaskCenter.Begin($"Crawling {root.Host}", "Link Crawler", canCancel: true);
        var running = _running;
        task.CancelRequested += () => running.Cancel(); // Cancel in the Tasks pane stops it like Stop does
        _rows.Clear();
        SetRunning(true);
        _map.Begin(options);
        _saveMap!.IsEnabled = true;
        if (_mapTab is not null && _tabs is not null) _tabs.SelectedItem = _mapTab; // watch it happen
        var events = new Progress<Web.CrawlEvent>(_map.Apply);
        int pages = 0;
        var progress = new Progress<string>(url =>
                                            {
                                                Say($"Reading {++pages}: {url}");
                                                task.Report($"{pages:N0} page{(pages == 1 ? string.Empty : "s")} read", pages);
                                            });

        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = System.Net.DecompressionMethods.All })
                             {
                                 Timeout = TimeSpan.FromSeconds(20)
                             };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Session.CrawlUserAgent());
            http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.5");

            var found = await new Web.LinkCrawler(http).CrawlAsync(options, progress, cancel, events);
            foreach (var link in found) _rows.Add(link);
            var skippedLinks = found.Where(l => l.Skipped).ToList();
            var summary      = $"{found.Count:N0} address{(found.Count == 1 ? string.Empty : "es")} linked from {pages:N0} page{(pages == 1 ? string.Empty : "s")}";
            if (skippedLinks.Count > 0)
            {
                int links   = skippedLinks.Sum(l => l.InstanceCount);
                int domains = skippedLinks.Select(l => new Uri(l.LinkUrl).Host).Distinct().Count();
                summary += $"; {links:N0} link{(links == 1 ? string.Empty : "s")} to {domains:N0} skipped domain{(domains == 1 ? string.Empty : "s")} not read (tick Include skipped domains to read them)";
            }

            Say(summary);
            task.Succeed($"{pages:N0} page{(pages == 1 ? string.Empty : "s")} read", found.Count);
        }
        catch (OperationCanceledException)
        {
            Say($"Stopped after {pages:N0} page{(pages == 1 ? string.Empty : "s")}");
            task.MarkCancelled($"Stopped after {pages:N0} page{(pages == 1 ? string.Empty : "s")}");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "The link crawl failed");
            Say("The crawl failed: " + ex.Message);
            task.Fail(ex);
        }
        finally
        {
            _running?.Dispose();
            _running = null;
            SetRunning(false);
            _map.Finish();
        }
    }

    private void Copy()
    {
        if (_rows.Count == 0) return;

        var text = new StringBuilder("LinkUrl\tInstanceCount\tMostCommonLinkText\tCrawl\n");
        foreach (var r in _rows) text.Append(r.LinkUrl).Append('\t').Append(r.InstanceCount).Append('\t').Append(r.MostCommonLinkText).Append('\t').Append(r.Crawl).Append('\n');
        Clipboard.SetText(text.ToString());
        Say($"Copied {_rows.Count:N0} rows");
    }

    private void Say(string text)
    {
        if (_status is not null) _status.Text = text;
    }

    public CrawlerInspector(WebLabSession session) : base(session)
    {
    }
}
