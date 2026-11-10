using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Silo.Labs.Web;

/// <summary>One address the crawl found links to: how many links point at it, and the text most of them are shown with.</summary>
/// <param name="Skipped">The address is on the skip list, so it was counted but not read.</param>
public sealed record CrawledLink(string LinkUrl, int InstanceCount, string MostCommonLinkText, bool Skipped = false)
{
    /// <summary>"skipped" for an address on the skip list, else empty: the grid's column for it.</summary>
    public string Crawl => Skipped ? "skipped" : string.Empty;
}

/// <summary>What <see cref="LinkCrawler" /> is asked to do.</summary>
/// <param name="Root">Where to begin.</param>
/// <param name="Levels">How many levels of pages to read: 1 is the root page alone, 2 also the pages it links to, and so on.</param>
/// <param name="SameHostOnly">Read only pages on the root's host (links to other hosts are still listed).</param>
/// <param name="MaxPages">The most pages read, whatever the levels, so a wide site cannot run away.</param>
/// <param name="Skip">
///     Domains that are listed and counted but never read (see <see cref="CrawlSkipList" />); null reads everything the other options allow. The root's
///     own host is always read, even when it is on the list.
/// </param>
public sealed record CrawlOptions(Uri Root, int Levels = 2, bool SameHostOnly = true, int MaxPages = 500, CrawlSkipList? Skip = null);

/// <summary>What happens during a crawl, as it happens (see <see cref="LinkCrawler.CrawlAsync" />): what a live map of the crawl is drawn from.</summary>
public abstract record CrawlEvent
{
    /// <summary>A page is about to be fetched. <paramref name="Depth" /> 1 is the start page.</summary>
    public sealed record Reading(Uri Url, int Depth) : CrawlEvent;

    /// <summary>A page was fetched (or could not be: <paramref name="Ok" /> is false for an error, a non-HTML page, a failed request) and these are its links.</summary>
    public sealed record Read(Uri Url, int Depth, bool Ok, IReadOnlyList<(Uri Url, string Text)> Links) : CrawlEvent;
}

/// <summary>
///     Finds the hyperlinks of a site by reading its pages as text: each page is fetched and its <c>&lt;a href&gt;</c> tags read, nothing is
///     rendered and no script runs (so links a script adds are not seen).
/// </summary>
public sealed class LinkCrawler(HttpClient http)
{
    private const int Parallel = 6;

    private static readonly Regex AnchorTag = new(@"<a\b(?<attrs>[^>]*)>(?<text>.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HrefAttribute = new(@"\bhref\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BaseTag = new(@"<base\b[^>]*?\bhref\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Comment = new(@"<!--.*?-->|<(script|style)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AnyTag = new(@"<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The links of a page: the address (made absolute, without its <c>#fragment</c>) and the text shown for it. Only http and https links.</summary>
    public static IReadOnlyList<(Uri Url, string Text)> ExtractLinks(string html, Uri pageUri)
    {
        html = Comment.Replace(html, string.Empty);

        var baseUri = pageUri;
        if (BaseTag.Match(html) is { Success: true } b && Uri.TryCreate(pageUri, WebUtility.HtmlDecode(b.Groups["v"].Value.Trim()), out var declared)) baseUri = declared;

        var found = new List<(Uri, string)>();
        foreach (Match m in AnchorTag.Matches(html))
        {
            var href = HrefAttribute.Match(m.Groups["attrs"].Value);
            if (!href.Success) continue;

            string raw = WebUtility.HtmlDecode(href.Groups["v"].Value).Trim();
            if (raw.Length == 0 || raw.StartsWith('#')) continue;
            if (!Uri.TryCreate(baseUri, raw, out var url) || url.Scheme is not ("http" or "https")) continue;

            var clean = new UriBuilder(url) { Fragment = string.Empty }.Uri;
            string text = Spaces.Replace(WebUtility.HtmlDecode(AnyTag.Replace(m.Groups["text"].Value, " ")), " ").Trim();
            found.Add((clean, text));
        }

        return found;
    }

    /// <summary>
    ///     Reads the root page, then the pages it links to, and so on for <see cref="CrawlOptions.Levels" /> levels, each address once.
    ///     Returns every address linked from a page that was read, with how often and under what text; most-linked first.
    /// </summary>
    /// <param name="progress">Told the address of each page as it is read.</param>
    /// <param name="events">Told, as each page is started and finished, what it was and what it links to (the live map of the crawl is drawn from these).</param>
    public async Task<IReadOnlyList<CrawledLink>> CrawlAsync(CrawlOptions options, IProgress<string>? progress = null, CancellationToken cancel = default,
                                                              IProgress<CrawlEvent>? events = null)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var texts  = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var order  = new Dictionary<string, int>(StringComparer.Ordinal);
        var read   = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal); // listed and counted, never read

        var level = new List<Uri> { Strip(options.Root) };
        read.Add(level[0].AbsoluteUri);

        for (int depth = 1; depth <= options.Levels && level.Count > 0; depth++)
        {
            var pages = level; // every address in it was counted against MaxPages when it was added
            var next  = new List<Uri>();

            var results = new List<(Uri Page, IReadOnlyList<(Uri Url, string Text)> Links)>();
            using (var gate = new SemaphoreSlim(Parallel))
            {
                var tasks = pages.Select(async page =>
                                         {
                                             await gate.WaitAsync(cancel);
                                             try
                                             {
                                                 progress?.Report(page.AbsoluteUri);
                                                 events?.Report(new CrawlEvent.Reading(page, depth));
                                                 var (ok, links) = await FetchLinksAsync(page, cancel);
                                                 events?.Report(new CrawlEvent.Read(page, depth, ok, links));
                                                 return (Page: page, Links: links);
                                             }
                                             finally
                                             {
                                                 gate.Release();
                                             }
                                         }).ToList();
                results.AddRange(await Task.WhenAll(tasks));
            }

            foreach (var (page, links) in results)
            foreach (var (url, text) in links)
            {
                string key = url.AbsoluteUri;
                counts[key] = counts.GetValueOrDefault(key) + 1;
                order.TryAdd(key, order.Count);
                if (!texts.TryGetValue(key, out var seen)) texts[key] = seen = new Dictionary<string, int>(StringComparer.Ordinal);
                seen[text] = seen.GetValueOrDefault(text) + 1;

                bool ownHost = string.Equals(url.Host, options.Root.Host, StringComparison.OrdinalIgnoreCase);
                if (!ownHost && options.Skip?.Matches(url.Host) == true) skipped.Add(key);

                bool follow = (!options.SameHostOnly || ownHost) && !skipped.Contains(key);
                if (follow && read.Count < options.MaxPages && read.Add(key)) next.Add(url);
            }

            level = next;
        }

        return counts.OrderByDescending(c => c.Value)
                     .ThenBy(c => order[c.Key])
                     .Select(c => new CrawledLink(c.Key, c.Value, MostCommon(texts[c.Key]), skipped.Contains(c.Key)))
                     .ToList();
    }

    /// <summary>The commonest text, preferring one that is not empty (an image link has none); the first seen wins a tie.</summary>
    private static string MostCommon(Dictionary<string, int> texts)
    {
        var named = texts.Where(t => t.Key.Length > 0).ToList();
        return (named.Count > 0 ? named : texts.ToList()).MaxBy(t => t.Value).Key;
    }

    private static Uri Strip(Uri uri) => new UriBuilder(uri) { Fragment = string.Empty }.Uri;

    /// <summary>The links of one page; a page that cannot be read or is not HTML has none (and is not <c>Ok</c>).</summary>
    private async Task<(bool Ok, IReadOnlyList<(Uri Url, string Text)> Links)> FetchLinksAsync(Uri page, CancellationToken cancel)
    {
        try
        {
            using var response = await http.GetAsync(page, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode) return (false, []);

            string? type = response.Content.Headers.ContentType?.MediaType;
            if (type is not null && !type.Contains("html", StringComparison.OrdinalIgnoreCase)) return (false, []);

            // follow redirects to where the page is, so its relative links resolve against the right place
            var at = response.RequestMessage?.RequestUri ?? page;
            return (true, ExtractLinks(await response.Content.ReadAsStringAsync(cancel), at));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException && !cancel.IsCancellationRequested)
        {
            return (false, []);
        }
    }
}
