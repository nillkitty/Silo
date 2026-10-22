using Telefrag.DI;

namespace Silo.Labs.Web;

/// <summary>The names of the Web Lab's parameters (what <c>set</c> in its terminal and the saved text call them).</summary>
public static class WebLabParameters
{
    public const string HomeUrl             = "homeUrl";
    public const string BlockedHosts        = "blockedHosts";
    public const string HeaderRewrite       = "headerRewrite";
    public const string SiloUriSchemes      = "siloUriSchemes";
    public const string ResourceReplacement = "resourceReplacement";
    public const string Permissions         = "permissions";
    public const string UserAgent           = "userAgent";
    public const string DisabledFeatures    = "disabledFeatures";

    /// <summary>The features that can be switched off, as <see cref="DisabledFeatures" /> names them.</summary>
    public static readonly string[] Features =
    [
        "JavaScript", "WebMessages", "DevTools", "ContextMenus", "AutoFill", "Zoom", "StatusBar",
        "ScriptDialogs", "AcceleratorKeys", "Popups", "Downloads"
    ];
}

/// <summary>
///     A place to study and experiment with how the web behaves: a page in a WebView2, with the requests it makes, its
///     DOM, its style and
///     its console beside it, and parameters that change what the page is allowed to do -- which hosts it can reach, which
///     headers it
///     sends, which resources are replaced by your own, what it may ask permission for.
/// </summary>
/// <remarks>
///     Studies a <see cref="Uri" />: <c>http</c> and <c>https</c> addresses, <c>about:</c> and <c>data:</c> pages, and
///     local web files
///     (<c>.html</c>, <c>.svg</c>, <c>.pdf</c>). Silo's own <c>silo:</c> addresses are not pages; see
///     <see cref="WebLabParameters.SiloUriSchemes" />.
/// </remarks>
[Singleton(typeof(ILab))]
public class WebLab : LabBase<Uri>
{
    private static readonly string[] LocalFileExtensions = [".html", ".htm", ".xhtml", ".svg", ".pdf"];

    public override string Description =>
        "Study how a web page behaves: its requests, DOM, style and console, and what it may reach, send and ask for.";

    public override string IconKey => "Link";

    public override string Id => "web";

    public override string Name => "Web Lab";

    public override int Order => 10;

    protected override IEnumerable<LabParameter> DefineParameters()
    {
        const string place = "Where";
        const string net   = "Network";
        const string page  = "Page";

        yield return new LabParameter(WebLabParameters.HomeUrl, "Home address", LabParameterKind.Text, "https://github.com/nillkitty/Silo",
                                      "Where Go Home and a new empty Web Lab window go.", place);

        yield return new LabParameter(WebLabParameters.BlockedHosts, "Blocked hosts", LabParameterKind.Lines, null,
                                      "Requests to these hosts fail (403) and show as blocked in Network. One per line; * matches anything: *.doubleclick.net. A line with a / matches the whole address.",
                                      net, hint: "ads.tracker.net\n*.doubleclick.net\n# a comment");

        yield return new LabParameter(WebLabParameters.HeaderRewrite, "Header rewrite", LabParameterKind.Lines, null,
                                      "Headers to set or remove on every request. Name: value sets it; -Name removes it. Start a line with @host to limit it to matching hosts.",
                                      net, hint: "X-Debug: 1\n-Referer\n@*.github.com Accept-Language: fr");

        yield return new LabParameter(WebLabParameters.ResourceReplacement, "Resource replacement", LabParameterKind.Lines, null,
                                      "Answer matching requests with something of your own. address => file path | text:content | another address (redirect). * matches anything in the address.",
                                      net,
                                      hint:
                                      "https://github.com/app.js => C:\\work\\app.js\n*/logo.png => https://github.com/other.png\n*/api/me => text:{\"name\":\"test\"}");

        yield return new LabParameter(WebLabParameters.UserAgent, "User agent", LabParameterKind.Text, string.Empty,
                                      "What the page is told the browser is. Empty is the browser's own.", net);

        yield return new LabParameter(WebLabParameters.Permissions, "Permissions", LabParameterKind.Lines, new[] { "*=ask" },
                                      "What to do when the page asks for a permission: kind=allow, deny or ask (ask puts Accept and Cancel on the toolbar). Kinds: camera, microphone, geolocation, notifications, clipboardread, autoplay, ... * is any.",
                                      page, hint: "*=ask\ngeolocation=deny\nclipboardread=allow");

        yield return new LabParameter(WebLabParameters.DisabledFeatures, "Disabled features", LabParameterKind.Flags, null,
                                      "Switch parts of the browser off to see how the page copes.", page, WebLabParameters.Features);

        yield return new LabParameter(WebLabParameters.SiloUriSchemes, "Enable Silo URI schemes", LabParameterKind.Bool, false,
                                      "Links to silo: and silo-uri: addresses are caught and opened in Silo, instead of failing in the page.",
                                      page);
    }

    public override LabSession<Uri> CreateSession()
    {
        return new WebLabSession(this);
    }

    protected override bool TryConvert(object? candidate, out Uri? value)
    {
        switch (candidate)
        {
            case Uri uri when IsOpenable(uri):
                value = uri;
                return true;
            case string text:
                return TryParse(text, out value);
            default:
                value = null;
                return false;
        }
    }

    /// <summary>
    ///     Reads what is typed in the address box: a full address, or a bare host ("github.com", "localhost:3000") that gets
    ///     <c>https://</c>.
    /// </summary>
    public override bool TryParse(string text, out Uri? value)
    {
        value = null;
        text  = text.Trim();
        if (text.Length == 0) return false;

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && IsOpenable(absolute))
        {
            value = absolute;
            return true;
        }

        if (!text.Contains("://") && !text.Contains(' ') && Uri.TryCreate("https://" + text, UriKind.Absolute, out var guessed) &&
            (guessed.Host.Contains('.') || guessed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            value = guessed;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Whether the Web Lab can show the address: a web address, an inline page, or a local file that is a web
    ///     document.
    /// </summary>
    public static bool IsOpenable(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return false;

        return uri.Scheme switch
               {
                   "http" or "https" or "about" or "data" => true,
                   "file" => LocalFileExtensions.Any(e => uri.LocalPath.EndsWith(e, StringComparison.OrdinalIgnoreCase)),
                   _ => false
               };
    }
}