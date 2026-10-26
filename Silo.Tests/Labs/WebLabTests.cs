using Newtonsoft.Json.Linq;
using Silo.Labs;
using Silo.Labs.Web;

namespace Silo.Tests.Labs;

public class WebLabTests
{
    private static NetworkEntry Post()
    {
        var entry = new NetworkEntry("1", "POST", "https://github.com/api/items?page=2&q=a+b", "XHR")
                    {
                        StatusCode      = 201,
                        StatusText      = "Created",
                        Mime            = "application/json",
                        PostData        = "{\"name\":\"it's\"}",
                        WallTime        = 1_700_000_000,
                        Timestamp       = 10,
                        ResponseTimestamp = 10.2,
                        EndTimestamp    = 10.5,
                        Finished        = true
                    };
        entry.RequestHeaders.Add(new KeyValuePair<string, string>("Content-Type", "application/json"));
        entry.RequestHeaders.Add(new KeyValuePair<string, string>("Content-Length", "15"));
        entry.RequestHeaders.Add(new KeyValuePair<string, string>(":authority", "github.com"));
        return entry;
    }

    [TestCase("", "anything", true)]
    [TestCase("error", "An Error happened", true)]
    [TestCase("error", "all fine", false)]
    [TestCase("-debug", "a debug line", false)]
    [TestCase("-debug", "an info line", true)]
    [TestCase("/^\\d+ items$/", "12 items", true)]
    [TestCase("/^\\d+ items$/", "twelve items", false)]
    [TestCase("a b", "xa yb", true)]
    [TestCase("a b", "xa y", false)]
    public void Filter_text_means_plain_words_regexes_and_exclusions(string filter, string text, bool expected)
    {
        Assert.That(new WebTextFilter(filter).Matches(text), Is.EqualTo(expected));
    }

    [Test]
    public void Words_split_at_spaces_except_inside_a_regex()
    {
        Assert.That(WebTextFilter.Words("a  b"), Is.EqualTo(new[] { "a", "b" }));
        Assert.That(WebTextFilter.Words("status:404 /^a b+$/ -/x y/ tail"), Is.EqualTo(new[] { "status:404", "/^a b+$/", "-/x y/", "tail" }));
        Assert.That(WebTextFilter.Words("/open and more"), Is.EqualTo(new[] { "/open", "and", "more" }));
        Assert.That(WebTextFilter.Words(null), Is.Empty);
    }

    [Test]
    public void A_filter_that_is_not_a_valid_regex_is_read_as_text()
    {
        var filter = new WebTextFilter("/(unclosed/");
        Assert.That(filter.Matches("a /(unclosed/ b"), Is.True);
        Assert.That(filter.Matches("nothing"), Is.False);
    }

    [Test]
    public void Curl_leaves_out_what_curl_works_out_and_quotes_for_the_shell()
    {
        string bash = WebExports.Curl(Post(), false);
        Assert.That(bash, Does.StartWith("curl 'https://github.com/api/items?page=2&q=a+b'"));
        Assert.That(bash, Does.Contain("-H 'Content-Type: application/json'"));
        Assert.That(bash, Does.Not.Contain("Content-Length"));
        Assert.That(bash, Does.Not.Contain(":authority"));
        Assert.That(bash, Does.Contain("--data-raw '{\"name\":\"it'\\''s\"}'"));
        Assert.That(bash, Does.Not.Contain("-X POST"));

        string cmd = WebExports.Curl(Post(), true);
        Assert.That(cmd, Does.StartWith("curl \"https://github.com/api/items"));
        Assert.That(cmd, Does.Contain("--data-raw \"{\\\"name\\\":\\\"it's\\\"}\""));
    }

    [Test]
    public void Fetch_carries_the_method_headers_and_body()
    {
        string fetch = WebExports.Fetch(Post());
        Assert.That(fetch, Does.StartWith("fetch(\"https://github.com/api/items?page=2&q=a+b\""));
        Assert.That(fetch, Does.Contain("\"method\": \"POST\""));
        Assert.That(fetch, Does.Contain("\"Content-Type\": \"application/json\""));
    }

    [Test]
    public void Har_is_a_version_1_2_log_with_the_query_split_out()
    {
        var har = JObject.Parse(WebExports.Har([Post()], "https://github.com/nillkitty/Silo"));
        Assert.That((string?)har["log"]?["version"], Is.EqualTo("1.2"));

        var entry = har["log"]!["entries"]![0]!;
        Assert.That((string?)entry["request"]!["method"], Is.EqualTo("POST"));
        Assert.That((int?)entry["response"]!["status"], Is.EqualTo(201));
        Assert.That((double?)entry["time"], Is.EqualTo(500).Within(0.001));

        var query = entry["request"]!["queryString"]!;
        Assert.That((string?)query[0]!["name"], Is.EqualTo("page"));
        Assert.That((string?)query[1]!["value"], Is.EqualTo("a b"));
        Assert.That((string?)entry["request"]!["postData"]!["text"], Is.EqualTo("{\"name\":\"it's\"}"));
    }

    [Test]
    public void Binary_answers_are_exported_as_base64()
    {
        var entry = Post();
        entry.Body         = "AAEC";
        entry.BodyIsBinary = true;
        var har = JObject.Parse(WebExports.Har([entry], string.Empty));
        Assert.That((string?)har["log"]!["entries"]![0]!["response"]!["content"]!["encoding"], Is.EqualTo("base64"));
    }

    [TestCase("text/html; charset=utf-8", true)]
    [TestCase("application/json", true)]
    [TestCase("application/javascript", true)]
    [TestCase("image/svg+xml", true)]
    [TestCase("image/png", false)]
    [TestCase("application/octet-stream", false)]
    public void Text_answers_are_told_from_binary_ones(string mime, bool expected)
    {
        Assert.That(WebExports.IsText(mime), Is.EqualTo(expected));
    }

    [Test]
    public void The_Web_Lab_opens_on_the_Silo_repository_and_no_example_host_is_built_in()
    {
        var lab  = new WebLab();
        Assert.That(lab.Parameters[WebLabParameters.HomeUrl].Default?.ToString(), Is.EqualTo("https://github.com/nillkitty/Silo"));

        foreach (var parameter in lab.Parameters)
            Assert.That(parameter.Hint ?? string.Empty, Does.Not.Contain("example."), parameter.Name);
    }

    [TestCase("github.com", "https://github.com/")]
    [TestCase("localhost:3000", "https://localhost:3000/")]
    [TestCase("https://github.com/nillkitty/Silo", "https://github.com/nillkitty/Silo")]
    public void Addresses_are_read_as_typed_with_https_for_a_bare_host(string text, string expected)
    {
        Assert.That(new WebLab().TryParse(text, out var uri), Is.True);
        Assert.That(uri!.AbsoluteUri, Is.EqualTo(expected));
    }

    [TestCase("not an address")]
    [TestCase("")]
    [TestCase("ftp://github.com/x")]
    public void Other_text_is_not_an_address(string text)
    {
        Assert.That(new WebLab().TryParse(text, out _), Is.False);
    }
}
