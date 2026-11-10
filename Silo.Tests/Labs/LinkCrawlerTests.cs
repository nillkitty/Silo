using System.Net;
using System.Net.Http;
using Silo.Drawio.Mx;
using Silo.Labs.Web;

namespace Silo.Tests.Labs;

public class LinkCrawlerTests
{
    private sealed class Site(Dictionary<string, string> pages) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            lock (Requested) Requested.Add(request.RequestUri!.AbsoluteUri);
            if (!pages.TryGetValue(request.RequestUri!.AbsoluteUri, out var html))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") });
        }
    }

    [Test]
    public void Links_are_made_absolute_without_fragments_and_scripts_are_skipped()
    {
        const string html = """
                            <base href="https://x.com/docs/">
                            <a href="a.html#top">  First <b>link</b> </a>
                            <a href='/root'>Root &amp; more</a>
                            <a href="#only-a-fragment">skip</a>
                            <a href="javascript:void(0)">skip</a>
                            <a href="mailto:me@x.com">skip</a>
                            <!-- <a href="/commented">skip</a> -->
                            <script>var s = '<a href="/in-script">skip</a>';</script>
                            <a href="https://other.org/p?q=1"><img src="i.png"></a>
                            """;

        var links = LinkCrawler.ExtractLinks(html, new Uri("https://x.com/"));

        Assert.That(links.Select(l => (l.Url.AbsoluteUri, l.Text)), Is.EqualTo(new[]
                                                                               {
                                                                                   ("https://x.com/docs/a.html", "First link"),
                                                                                   ("https://x.com/root", "Root & more"),
                                                                                   ("https://other.org/p?q=1", "")
                                                                               }));
    }

    [Test]
    public async Task Levels_decide_how_far_the_crawl_goes_and_each_page_is_read_once()
    {
        var site = new Site(new Dictionary<string, string>
                            {
                                ["https://x.com/"]  = "<a href='/a'>Alpha</a><a href='/b'>Bee</a><a href='/a'>Alpha</a><a href='/a'>First</a>",
                                ["https://x.com/a"] = "<a href='/'>Home</a><a href='/c'>Sea</a>",
                                ["https://x.com/b"] = "<a href='/c'>Sea</a><a href='https://other.org/'>Elsewhere</a>",
                                ["https://x.com/c"] = "<a href='/d'>Deep</a>"
                            });
        var crawler = new LinkCrawler(new HttpClient(site));

        var one = await crawler.CrawlAsync(new CrawlOptions(new Uri("https://x.com/"), 1));
        Assert.That(one.Select(l => (l.LinkUrl, l.InstanceCount, l.MostCommonLinkText)),
                    Is.EqualTo(new[] { ("https://x.com/a", 3, "Alpha"), ("https://x.com/b", 1, "Bee") }));
        Assert.That(site.Requested, Is.EqualTo(new[] { "https://x.com/" }));

        var two = await crawler.CrawlAsync(new CrawlOptions(new Uri("https://x.com/"), 2));
        Assert.That(two.Select(l => (l.LinkUrl, l.InstanceCount)),
                    Is.EquivalentTo(new[]
                                    {
                                        ("https://x.com/a", 3), ("https://x.com/b", 1), ("https://x.com/", 1), ("https://x.com/c", 2),
                                        ("https://other.org/", 1)
                                    }));
        Assert.That(two.Single(l => l.LinkUrl == "https://x.com/c").MostCommonLinkText, Is.EqualTo("Sea"));
    }

    // ---- the skip list ---------------------------------------------------------------------------------------

    [TestCase("facebook.com",            true)]
    [TestCase("www.facebook.com",        true)]
    [TestCase("m.facebook.com",          true)]
    [TestCase("FACEBOOK.COM.",           true)]
    [TestCase("notfacebook.com",         false)]
    [TestCase("facebook.com.evil.test",  false)]
    [TestCase("docs.google.com",         true)]
    [TestCase("www.google-analytics.com", true)]
    [TestCase("d111.cloudfront.net",     true)]
    [TestCase("notion.so",               true)]
    [TestCase("acme.atlassian.net",      true)]
    [TestCase("dl.dropboxusercontent.com", true)]
    [TestCase("ftp.example.org",         true)]
    [TestCase("cdn.example.org",         true)]
    [TestCase("assets.mycdn.example.org", true)]
    [TestCase("example.org",             false)]
    [TestCase("docs.example.org",        false)]
    public void The_default_skip_list_covers_what_a_crawl_does_not_care_about(string host, bool skipped)
    {
        Assert.That(CrawlSkipList.Defaults.Matches(host), Is.EqualTo(skipped));
    }

    [Test]
    public void A_skip_list_is_text_with_comments_domains_and_patterns()
    {
        var list = CrawlSkipList.Parse("""
                                       # a comment
                                       Example.COM   # trailing comment
                                       https://tracker.test/some/path?q=1
                                       *.wild.test
                                       files.*

                                       example.com
                                       """);

        Assert.That(list.Entries, Is.EqualTo(new[] { "example.com", "tracker.test", "wild.test", "files.*" }));
        Assert.That(list.Matches("a.b.example.com"), Is.True);
        Assert.That(list.Matches("tracker.test"),    Is.True);
        Assert.That(list.Matches("files.corp.test"), Is.True);
        Assert.That(list.Matches("profiles.test"),   Is.False);
    }

    [Test]
    public void The_defaults_hold_the_kinds_of_domain_the_list_is_for()
    {
        var entries = CrawlSkipList.Defaults.Entries;

        Assert.That(entries, Does.Contain("google-analytics.com"));
        Assert.That(entries, Does.Contain("facebook.com"));
        Assert.That(entries, Does.Contain("docs.google.com"));
        Assert.That(entries, Does.Contain("notion.so"));
        Assert.That(entries, Does.Contain("atlassian.net"));
        Assert.That(entries, Does.Contain("dropbox.com"));
        Assert.That(entries, Does.Contain("cloudfront.net"));
        Assert.That(entries, Does.Contain("ftp.*"));
    }

    [Test]
    public async Task Skipped_domains_are_listed_and_counted_but_not_read_unless_included()
    {
        var site = new Site(new Dictionary<string, string>
                            {
                                ["https://x.com/"]        = "<a href='https://www.facebook.com/x'>FB</a><a href='https://www.facebook.com/x'>FB</a><a href='https://blog.other.org/'>Blog</a><a href='/a'>A</a>",
                                ["https://blog.other.org/"] = "<a href='/deeper'>Deeper</a>",
                                ["https://www.facebook.com/x"] = "<a href='/inside'>Inside</a>",
                                ["https://x.com/a"]       = "<a href='https://analytics.google.com/'>GA</a>"
                            });
        var crawler = new LinkCrawler(new HttpClient(site));
        var skip    = CrawlSkipList.Parse("facebook.com\nanalytics.google.com");

        var found = await crawler.CrawlAsync(new CrawlOptions(new Uri("https://x.com/"), 3, false, Skip: skip));

        var facebook = found.Single(l => l.LinkUrl == "https://www.facebook.com/x");
        Assert.That((facebook.InstanceCount, facebook.Skipped), Is.EqualTo((2, true)));
        Assert.That(found.Single(l => l.LinkUrl == "https://analytics.google.com/").Skipped, Is.True);
        Assert.That(found.Single(l => l.LinkUrl == "https://blog.other.org/").Skipped,       Is.False);
        Assert.That(found.Any(l => l.LinkUrl.Contains("/inside")),                           Is.False);
        Assert.That(site.Requested, Does.Not.Contain("https://www.facebook.com/x"));
        Assert.That(site.Requested, Does.Not.Contain("https://analytics.google.com/"));
        Assert.That(site.Requested, Does.Contain("https://blog.other.org/"));

        var all = await crawler.CrawlAsync(new CrawlOptions(new Uri("https://x.com/"), 3, false));
        Assert.That(all.Any(l => l.Skipped),                          Is.False);
        Assert.That(site.Requested, Does.Contain("https://www.facebook.com/x"));
        Assert.That(all.Any(l => l.LinkUrl == "https://www.facebook.com/inside"), Is.True);
    }

    [Test]
    public async Task The_root_is_read_even_when_its_own_host_is_on_the_list()
    {
        var site = new Site(new Dictionary<string, string> { ["https://docs.google.com/"] = "<a href='/more'>More</a>", ["https://docs.google.com/more"] = "<a href='/end'>End</a>" });
        var crawler = new LinkCrawler(new HttpClient(site));

        var found = await crawler.CrawlAsync(new CrawlOptions(new Uri("https://docs.google.com/"), 2, Skip: CrawlSkipList.Defaults));

        Assert.That(found.Select(l => l.LinkUrl), Is.EquivalentTo(new[] { "https://docs.google.com/more", "https://docs.google.com/end" }));
        Assert.That(found.Any(l => l.Skipped),    Is.False);
    }

    [Test]
    public async Task Other_hosts_are_listed_but_read_only_when_asked()
    {
        var site = new Site(new Dictionary<string, string>
                            {
                                ["https://x.com/"]    = "<a href='https://other.org/'>Out</a>",
                                ["https://other.org/"] = "<a href='/deeper'>Deeper</a>"
                            });
        var crawler = new LinkCrawler(new HttpClient(site));

        var same = await crawler.CrawlAsync(new CrawlOptions(new Uri("https://x.com/"), 3));
        Assert.That(same.Select(l => l.LinkUrl), Is.EqualTo(new[] { "https://other.org/" }));

        var any = await crawler.CrawlAsync(new CrawlOptions(new Uri("https://x.com/"), 3, false));
        Assert.That(any.Select(l => l.LinkUrl), Is.EquivalentTo(new[] { "https://other.org/", "https://other.org/deeper" }));
    }
}

public class CrawlDiagramSourceTests
{
    private static readonly Uri Root = new("https://x.com/");

    private static object? Value(string key)
    {
        return CrawlDiagramSource.Values.TryGet(key, out var v) ? v : null;
    }

    private static CrawlEvent.Read Read(Uri page, int depth, bool ok, params string[] links)
    {
        return new CrawlEvent.Read(page, depth, ok, links.Select(l => (new Uri(l), "text")).ToList());
    }

    [Test]
    public void The_map_starts_with_the_queued_start_page()
    {
        var map = new CrawlDiagramSource();
        map.Begin(new CrawlOptions(Root, 2));

        var model = map.Build();
        Assert.That(model.Nodes.Select(n => n.Id), Is.EqualTo(new[] { "p0" }));
        Assert.That(Value("p0.fill"),  Is.EqualTo("#f5f5f5"));
        Assert.That(Value("p0.label"), Is.EqualTo("<b>x.com</b><br>queued"));
    }

    [Test]
    public void Pages_hosts_and_connectors_appear_as_pages_are_read_and_are_reported_as_edits()
    {
        var map   = new CrawlDiagramSource();
        var edits = new List<DiagramEdit>();
        map.EditsReady += e => edits.AddRange(e);
        map.Begin(new CrawlOptions(Root, 2, true, Skip: CrawlSkipList.Parse("facebook.com")));

        map.Apply(new CrawlEvent.Reading(Root, 1));
        Assert.That(Value("p0.fill"), Is.EqualTo("#dae8fc"));

        map.Apply(Read(Root, 1, true, "https://x.com/a", "https://www.facebook.com/p", "https://www.facebook.com/q", "https://other.org/", "https://x.com/"));

        Assert.That(Value("p0.fill"),  Is.EqualTo("#d5e8d4"));
        Assert.That(Value("p0.label"), Is.EqualTo("<b>x.com</b><br>5 links"));
        Assert.That(Value("p1.fill"),  Is.EqualTo("#f5f5f5"), "the page that will be read is queued");
        Assert.That(Value("h0.label"), Is.EqualTo("<b>www.facebook.com</b><br>2 links \u00b7 skipped"));
        Assert.That(Value("h1.label"), Is.EqualTo("<b>other.org</b><br>1 link"));
        Assert.That(Value("e_p0_h0.n"), Is.EqualTo("2"));

        var added = edits.OfType<DiagramEdit.AddNode>().Select(a => a.Node.Id).ToList();
        Assert.That(added, Is.EquivalentTo(new[] { "p1", "h0", "h1" }));
        Assert.That(edits.OfType<DiagramEdit.AddEdge>().Select(a => a.Edge.Id), Is.EquivalentTo(new[] { "e_p1", "e_p0_h0", "e_p0_h1" }));

        // the skipped host is drawn apart from the other
        var model = map.Build();
        Assert.That(model.Nodes.Single(n => n.Id == "h0").Style, Does.Contain("dashed=1"));
        Assert.That(model.Nodes.Single(n => n.Id == "h1").Style, Does.Not.Contain("dashed=1"));
    }

    [Test]
    public void Columns_are_levels_rows_are_stable_and_the_hosts_have_the_last_column()
    {
        var map = new CrawlDiagramSource();
        map.Begin(new CrawlOptions(Root, 3));
        map.Apply(Read(Root, 1, true, "https://x.com/a", "https://x.com/b", "https://elsewhere.org/"));
        var before = map.Build().Nodes.ToDictionary(n => n.Id);

        map.Apply(Read(new Uri("https://x.com/a"), 2, true, "https://x.com/c", "https://x.com/d"));
        var after = map.Build().Nodes.ToDictionary(n => n.Id);

        Assert.That(after["p0"].X, Is.LessThan(after["p1"].X));
        Assert.That(after["p1"].X, Is.EqualTo(after["p2"].X));
        Assert.That(after["p3"].X, Is.GreaterThan(after["p1"].X), "the next level is the next column");
        Assert.That(after["h0"].X, Is.GreaterThan(after["p3"].X), "other hosts are after the deepest level");
        foreach (var id in before.Keys) Assert.That((after[id].X, after[id].Y), Is.EqualTo((before[id].X, before[id].Y)), id + " did not move");
    }

    [Test]
    public void Links_that_are_not_drawn_are_counted_on_their_page_and_a_failed_page_is_red()
    {
        var map = new CrawlDiagramSource();
        map.Begin(new CrawlOptions(Root, 2));
        map.Apply(Read(Root, 1, true, "https://x.com/a", "https://x.com/gone"));
        map.Apply(Read(new Uri("https://x.com/a"), 2, true, "https://x.com/deeper", "https://x.com/deepest"));
        map.Apply(Read(new Uri("https://x.com/gone"), 2, false));

        Assert.That(Value("p1.label"), Is.EqualTo("<b>/a</b><br>2 links, +2 not read"));
        Assert.That(Value("p2.fill"),  Is.EqualTo("#f8cecc"));
        Assert.That(Value("p2.label"), Is.EqualTo("<b>/gone</b><br>could not be read"));
        Assert.That(map.PageCount,     Is.EqualTo(3));
    }

    [Test]
    public void The_page_limit_keeps_the_map_small_and_finishing_marks_what_was_never_read()
    {
        var map = new CrawlDiagramSource();
        map.Begin(new CrawlOptions(Root, 2, MaxPages: 2));
        map.Apply(Read(Root, 1, true, "https://x.com/a", "https://x.com/b", "https://x.com/c"));

        Assert.That(map.PageCount, Is.EqualTo(2));
        Assert.That(Value("p0.label"), Is.EqualTo("<b>x.com</b><br>3 links, +2 not read"));

        map.Finish();
        Assert.That(Value("p1.fill"),  Is.EqualTo("#fff2cc"));
        Assert.That(Value("p1.label"), Is.EqualTo("<b>/a</b><br>not read"));
    }

    [Test]
    public void The_map_becomes_an_ordinary_diagram_with_its_live_values_written_in()
    {
        var map = new CrawlDiagramSource();
        map.Begin(new CrawlOptions(Root, 2, true, Skip: CrawlSkipList.Parse("facebook.com")));
        map.Apply(Read(Root, 1, true, "https://x.com/a", "https://www.facebook.com/p"));

        var xml = map.ToDiagramXml();

        Assert.That(xml, Does.Contain("x.com"));
        Assert.That(xml, Does.Contain("www.facebook.com"));
        Assert.That(xml, Does.Contain("fillColor=#d5e8d4"));
        Assert.That(xml, Does.Not.Contain("silo-"), "bindings are frozen into plain cells");
        Assert.That(xml, Does.Not.Contain("siloGenerated"));
    }
}
