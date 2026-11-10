using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Silo.Labs.Web;

/// <summary>
///     The domains the Link Crawler does not read: analytics and advertising, social media, document and SaaS apps, file sharing, CDNs and
///     FTP hosts -- places a crawl of a site has no reason to follow, and that make it long. Links to them are still found, counted and listed
///     (flagged as skipped); they are just not fetched. The list is plain text, one entry a line, kept in <c>%APPDATA%\Silo</c> once it is
///     edited, and starts as <see cref="DefaultText" />.
/// </summary>
/// <remarks>
///     An entry is a domain (<c>facebook.com</c>: that host and every host below it, so <c>m.facebook.com</c> too but not
///     <c>notfacebook.com</c>), or a pattern with <c>*</c> and <c>?</c> (<c>ftp.*</c>: any host whose name starts <c>ftp.</c>). Lines starting
///     with <c>#</c> are comments; blank lines are ignored.
/// </remarks>
public sealed class CrawlSkipList
{
    public const string FileName = "crawler-skip-domains.txt";

    private readonly List<string> _domains  = [];
    private readonly List<Regex>  _patterns = [];

    /// <summary>The entries (domains and patterns), as normalised.</summary>
    public IReadOnlyList<string> Entries { get; private set; } = [];

    public static CrawlSkipList Defaults => Parse(DefaultText);

    /// <summary>Reads the list from its text. Never fails: a line that is not understood is left out.</summary>
    public static CrawlSkipList Parse(string text)
    {
        var list    = new CrawlSkipList();
        var entries = new List<string>();
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = Normalize(raw);
            if (entry is null || entries.Contains(entry)) continue;

            entries.Add(entry);
            if (entry.Contains('*') || entry.Contains('?'))
                list._patterns.Add(new Regex("^" + Regex.Escape(entry).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            else
                list._domains.Add(entry);
        }

        list.Entries = entries;
        return list;
    }

    /// <summary>One line as an entry: lower case, without a scheme, path, port, <c>www.</c> or leading <c>*.</c>/<c>.</c>; null for a comment or a blank.</summary>
    internal static string? Normalize(string line)
    {
        var s = line.Trim();
        if (s.Length == 0 || s.StartsWith('#')) return null;

        var hash = s.IndexOf(" #", StringComparison.Ordinal);
        if (hash > 0) s = s[..hash].Trim();

        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];

        var end = s.IndexOfAny(['/', '?', '#']);
        if (end >= 0 && !s.StartsWith('*')) s = s[..end];

        var port = s.LastIndexOf(':');
        if (port > 0 && s[(port + 1)..].All(char.IsDigit)) s = s[..port];

        s = s.ToLowerInvariant().Trim('.');
        while (s.StartsWith("*.", StringComparison.Ordinal) && !s[2..].Contains('*')) s = s[2..];
        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];

        return s.Length == 0 || s.Any(char.IsWhiteSpace) ? null : s;
    }

    /// <summary>True when <paramref name="host"/> is on the list: a listed domain or a host below one, or a host a pattern matches.</summary>
    public bool Matches(string host)
    {
        var h = host.Trim().Trim('.').ToLowerInvariant();
        if (h.StartsWith("www.", StringComparison.Ordinal)) h = h[4..];
        if (h.Length == 0) return false;

        foreach (var d in _domains)
            if (h == d || h.EndsWith("." + d, StringComparison.Ordinal))
                return true;

        foreach (var p in _patterns)
            if (p.IsMatch(h))
                return true;

        return false;
    }

    // ---- keeping the person's list ---------------------------------------------------------------------------

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", FileName);

    /// <summary>The list's text: the person's own when they edited it, else <see cref="DefaultText" />.</summary>
    public static string LoadText()
    {
        try
        {
            return File.Exists(FilePath) ? File.ReadAllText(FilePath) : DefaultText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DefaultText;
        }
    }

    public static CrawlSkipList Load()
    {
        return Parse(LoadText());
    }

    /// <summary>Keeps the person's list (their text exactly, comments and all). Text equal to the defaults removes the file, so new defaults reach them.</summary>
    public static void SaveText(string text)
    {
        if (string.Equals(text.Trim(), DefaultText.Trim(), StringComparison.Ordinal))
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, text, new UTF8Encoding(false));
    }

    // ---- what starts on it -----------------------------------------------------------------------------------

    public static string DefaultText { get; } = string.Join(Environment.NewLine,
    [
        "# Domains the Link Crawler does not read. Links to them are still listed and counted (flagged as skipped).",
        "# One domain a line (it covers every host below it), or a pattern with * and ?. Lines starting with # are comments.",
        "",
        "# --- Analytics, tag managers, tracking and advertising",
        "google-analytics.com", "analytics.google.com", "googletagmanager.com", "googletagservices.com", "googlesyndication.com", "googleadservices.com", "doubleclick.net",
        "adservice.google.com", "hotjar.com", "mixpanel.com", "segment.com", "segment.io", "cdn.segment.com", "amplitude.com", "heap.io", "heapanalytics.com", "fullstory.com",
        "clarity.ms", "matomo.cloud", "plausible.io", "newrelic.com", "nr-data.net", "datadoghq.com", "datadoghq-browser-agent.com", "sentry.io", "bugsnag.com", "rollbar.com",
        "logrocket.com", "lr-ingest.io", "quantserve.com", "quantcast.com", "scorecardresearch.com", "omtrdc.net", "demdex.net", "adobedtm.com", "2o7.net", "hs-analytics.net",
        "hs-scripts.com", "hs-banner.com", "hsadspixel.net", "hsforms.net", "optimizely.com", "crazyegg.com", "mouseflow.com", "ads-twitter.com", "adsrvr.org", "criteo.com",
        "criteo.net", "taboola.com", "outbrain.com", "pingdom.net", "statcounter.com", "chartbeat.com", "chartbeat.net", "parsely.com", "branch.io", "appsflyer.com", "adjust.com",
        "intercom.io", "intercomcdn.com", "drift.com", "driftt.com", "zdassets.com", "onetrust.com", "cookielaw.org", "cookiebot.com", "trustarc.com", "usercentrics.eu",
        "bing.com", "bat.bing.com", "yahoo.com", "adnxs.com", "rubiconproject.com", "pubmatic.com", "openx.net", "casalemedia.com", "moatads.com", "hotjar.io", "mxpnl.com",
        "",
        "# --- Social media and messaging",
        "facebook.com", "facebook.net", "fb.com", "fb.me", "fbcdn.net", "instagram.com", "twitter.com", "x.com", "t.co", "twimg.com", "linkedin.com", "licdn.com", "lnkd.in",
        "youtube.com", "youtu.be", "ytimg.com", "youtube-nocookie.com", "tiktok.com", "pinterest.com", "pin.it", "pinimg.com", "reddit.com", "redd.it", "redditmedia.com",
        "snapchat.com", "tumblr.com", "medium.com", "threads.net", "bsky.app", "mastodon.social", "whatsapp.com", "wa.me", "telegram.org", "t.me", "discord.com", "discord.gg",
        "discordapp.com", "twitch.tv", "vimeo.com", "flickr.com", "quora.com", "vk.com", "weibo.com", "wechat.com", "line.me", "signal.org", "slideshare.net", "behance.net",
        "dribbble.com", "tripadvisor.com", "yelp.com",
        "",
        "# --- Google Docs and other Google properties",
        "google.com", "docs.google.com", "drive.google.com", "sheets.google.com", "slides.google.com", "forms.google.com", "forms.gle", "calendar.google.com", "meet.google.com",
        "mail.google.com", "sites.google.com", "accounts.google.com", "goo.gl", "g.co", "googleusercontent.com", "gstatic.com", "googleapis.com", "googlevideo.com", "ggpht.com",
        "recaptcha.net", "withgoogle.com", "blogspot.com", "blogger.com", "appspot.com",
        "",
        "# --- SaaS apps: wikis, work management, office suites, CRM, support, forms, design",
        "notion.so", "notion.site", "notion.com", "atlassian.net", "atlassian.com", "confluence.com", "jira.com", "bitbucket.org", "trello.com", "asana.com", "monday.com", "clickup.com",
        "airtable.com", "coda.io", "slack.com", "slack-edge.com", "zoom.us", "zoom.com", "teams.microsoft.com", "microsoft.com", "office.com", "office365.com", "sharepoint.com",
        "onedrive.live.com", "1drv.ms", "live.com", "microsoftonline.com", "outlook.com", "salesforce.com", "force.com", "salesforceliveagent.com", "zendesk.com", "freshdesk.com",
        "freshworks.com", "servicenow.com", "workday.com", "myworkday.com", "docusign.com", "docusign.net", "hubspot.com", "hubspotusercontent.com", "mailchimp.com", "list-manage.com",
        "sendgrid.net", "sendgrid.com", "mandrillapp.com", "typeform.com", "surveymonkey.com", "calendly.com", "figma.com", "miro.com", "canva.com", "lucid.app", "lucidchart.com",
        "smartsheet.com", "basecamp.com", "monday.com", "eventbrite.com", "stripe.com", "paypal.com", "intuit.com", "quickbooks.com", "okta.com", "auth0.com", "onelogin.com",
        "shopify.com", "myshopify.com", "squarespace.com", "wix.com", "wixsite.com", "weebly.com", "wordpress.com", "godaddy.com", "gitbook.io", "readthedocs.io", "linear.app",
        "intercom.com", "pagerduty.com", "statuspage.io", "loom.com", "zapier.com", "apple.com", "icloud.com",
        "",
        "# --- File sharing and storage",
        "dropbox.com", "dropboxusercontent.com", "dropboxapi.com", "db.tt", "box.com", "box.net", "boxcloud.com", "wetransfer.com", "we.tl", "mega.nz", "mega.io", "mediafire.com",
        "sendspace.com", "4shared.com", "pcloud.com", "sync.com",
        "",
        "# --- CDNs and static asset hosts",
        "cloudfront.net", "akamai.net", "akamaihd.net", "akamaized.net", "akamaiedge.net", "edgekey.net", "edgesuite.net", "fastly.net", "fastlylb.net", "fastly.com", "cloudflare.net",
        "cdnjs.cloudflare.com", "cloudflarestorage.com", "cloudflareinsights.com", "jsdelivr.net", "unpkg.com", "bootstrapcdn.com", "code.jquery.com", "maxcdn.com",
        "stackpathcdn.com", "stackpathdns.com", "b-cdn.net", "bunnycdn.com", "azureedge.net", "azurefd.net", "cdn77.org", "cdn77.com", "imgix.net", "cloudinary.com", "kxcdn.com",
        "keycdn.com", "lldns.net", "llnwd.net", "edgecastcdn.net", "cachefly.net", "gcdn.co", "wp.com", "gravatar.com", "fontawesome.com", "typekit.net", "fonts.net",
        "cdnst.net", "sstatic.net", "polyfill.io", "staticflickr.com", "ssl-images-amazon.com", "media-amazon.com", "pstatic.net", "yimg.com",
        "",
        "# --- FTP and CDN host names, wherever they are",
        "ftp.*", "sftp.*", "ftps.*", "cdn.*", "cdn?.*", "*.cdn.*", "*cdn.*", "*-cdn.*"
    ]);
}
