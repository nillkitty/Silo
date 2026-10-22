using Silo.Connectors;
using Silo.Connectors.Web;
using Silo.Platform;

namespace Silo.Tests.Connectors;

public class ShellCommandTests
{
    [Test]
    public void A_quoted_program_is_split_from_its_arguments()
    {
        var split = ShellCommand.Split("\"C:\\Program Files\\Browser\\browser.exe\" --single-argument %1");

        Assert.That(split, Is.Not.Null);
        Assert.That(split!.Value.Program, Is.EqualTo("C:\\Program Files\\Browser\\browser.exe"));
        Assert.That(split.Value.Arguments, Is.EqualTo("--single-argument %1"));
    }

    [Test]
    public void An_unquoted_program_with_spaces_ends_at_its_exe()
    {
        var split = ShellCommand.Split("C:\\Program Files\\Browser\\browser.exe -url %1");

        Assert.That(split!.Value.Program, Is.EqualTo("C:\\Program Files\\Browser\\browser.exe"));
        Assert.That(split.Value.Arguments, Is.EqualTo("-url %1"));
    }

    [Test]
    public void An_unquoted_program_without_exe_ends_at_the_first_space()
    {
        var split = ShellCommand.Split("browser %1");

        Assert.That(split!.Value.Program, Is.EqualTo("browser"));
        Assert.That(split.Value.Arguments, Is.EqualTo("%1"));
    }

    [Test]
    public void An_exe_inside_a_folder_name_is_not_the_end_of_the_program()
    {
        var split = ShellCommand.Split("C:\\apps\\my.exe.tools\\run.exe /x");

        Assert.That(split!.Value.Program, Is.EqualTo("C:\\apps\\my.exe.tools\\run.exe"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\"\"")]
    public void A_command_that_names_nothing_has_no_program(string? command)
    {
        Assert.That(ShellCommand.Split(command), Is.Null);
        Assert.That(ShellCommand.ProgramOf(command), Is.Null);
    }

    [Test]
    public void Programs_are_the_same_without_regard_to_case_or_quotes()
    {
        Assert.That(ShellCommand.SameProgram("\"C:\\A\\b.exe\"", "c:\\a\\B.EXE"), Is.True);
        Assert.That(ShellCommand.SameProgram("C:\\A\\b.exe", "C:\\A\\c.exe"), Is.False);
        Assert.That(ShellCommand.SameProgram(null, null), Is.False);
        Assert.That(ShellCommand.SameProgram("", "C:\\A\\b.exe"), Is.False);
    }

    [Test]
    public void The_same_file_name_elsewhere_is_the_same_program_name()
    {
        Assert.That(ShellCommand.SameProgramName("C:\\A\\silo.exe", "D:\\bin\\Debug\\SILO.exe"), Is.True);
        Assert.That(ShellCommand.SameProgramName("C:\\A\\silo.exe", "C:\\A\\other.exe"), Is.False);
    }

    [TestCase("%1", "https://example.com/a", "https://example.com/a")]
    [TestCase("--new-window %1", "https://example.com/a", "--new-window https://example.com/a")]
    [TestCase("\"%L\" --x", "https://example.com/a", "\"https://example.com/a\" --x")]
    [TestCase("", "https://example.com/a", "\"https://example.com/a\"")]
    [TestCase("--flag", "https://example.com/a", "--flag \"https://example.com/a\"")]
    public void The_address_goes_where_the_registration_puts_it(string arguments, string url, string expected)
    {
        Assert.That(ShellCommand.Substitute(arguments, new Uri(url)), Is.EqualTo(expected));
    }

    [Test]
    public void Without_arguments_the_address_is_quoted_on_its_own()
    {
        Assert.That(ShellCommand.Substitute(null, new Uri("https://example.com/")), Is.EqualTo("\"https://example.com/\""));
    }
}

public class SiloRouteTests
{
    [TestCase("silo:main", "main", "")]
    [TestCase("silo:///main", "main", "")]
    [TestCase("silo:lab/web", "lab", "web")]
    [TestCase("silo:///lab/web", "lab", "web")]
    [TestCase("silo-uri:tool/unicode", "tool", "unicode")]
    [TestCase("SILO:Lab/Web", "lab", "Web")]
    public void A_silo_address_names_a_place(string address, string kind, string id)
    {
        Assert.That(SiloRoute.TryParse(new Uri(address), out var route), Is.True);
        Assert.That(route.Kind, Is.EqualTo(kind));
        Assert.That(route.Id, Is.EqualTo(id));
    }

    [TestCase("https://example.com/lab/web")]
    [TestCase("silo:lab")]
    [TestCase("silo:main/extra")]
    [TestCase("silo:unknown/thing")]
    [TestCase("silo:")]
    public void Anything_else_is_not_a_place(string address)
    {
        Uri.TryCreate(address, UriKind.Absolute, out var uri);

        Assert.That(SiloRoute.TryParse(uri, out _), Is.False);
    }

    [Test]
    public void No_address_is_not_a_place()
    {
        Assert.That(SiloRoute.TryParse(null, out _), Is.False);
    }
}

public class SystemUrlHandlerTests
{
    private const string Edge    = "\"C:\\Program Files\\Edge\\msedge.exe\" --single-argument %1";
    private const string Firefox = "\"C:\\Program Files\\Firefox\\firefox.exe\" -osint -url \"%1\"";
    private const string Mail    = "\"C:\\Program Files\\Mail\\mail.exe\" %1";
    private const string SiloCmd = "\"C:\\Tools\\Silo\\silo.exe\" \"%1\"";

    private sealed class FakeUrlHandler(params UrlHandler[] handlers) : SystemUrlHandler
    {
        public int Loads { get; private set; }

        protected override IReadOnlyList<UrlHandler> LoadHandlers()
        {
            Loads++;
            return handlers;
        }
    }

    private static FakeUrlHandler Make()
    {
        return new FakeUrlHandler(new UrlHandler("https", Edge, true), new UrlHandler("http", Edge, true), new UrlHandler("ftp", Firefox, true),
                                  new UrlHandler("mailto", Mail, false), new UrlHandler("silo", SiloCmd, false));
    }

    [Test]
    public void The_scheme_of_an_address_finds_its_handler()
    {
        var info = Make().GetInfo(new Uri("https://example.com/"));

        Assert.That(info, Is.Not.Null);
        Assert.That(info!.Scheme, Is.EqualTo("https"));
        Assert.That(info.HandlerCommand, Is.EqualTo(Edge));
        Assert.That(info.IsWebUrl, Is.True);
        Assert.That(info.IsOurs, Is.False);
        Assert.That(info.CanonicalUri, Is.EqualTo(new Uri("https://example.com/")));
    }

    [Test]
    public void A_scheme_nothing_handles_has_no_info()
    {
        Assert.That(Make().GetInfo(new Uri("gopher://example.com/")), Is.Null);
    }

    [Test]
    public void A_handler_that_runs_silo_is_ours()
    {
        var info = Make().GetInfo(new Uri("silo:lab/web"));

        Assert.That(info!.IsOurs, Is.True);
        Assert.That(info.IsWebUrl, Is.False);
    }

    [Test]
    public void A_program_is_asked_about_by_its_path_and_a_web_scheme_wins()
    {
        var info = Make().GetInfo("C:\\Program Files\\Edge\\msedge.exe");

        Assert.That(info!.Scheme, Is.EqualTo("https"));
        Assert.That(info.CanonicalUri, Is.Null);
    }

    [Test]
    public void The_schemes_of_a_program_are_listed_in_order()
    {
        Assert.That(Make().GetProtocolsFor("c:\\program files\\edge\\MSEDGE.exe"), Is.EqualTo(new[] { "http", "https" }));
    }

    [Test]
    public void Another_copy_of_a_program_is_found_by_its_file_name()
    {
        Assert.That(Make().GetProtocolsFor("D:\\build\\Debug\\silo.exe"), Is.EqualTo(new[] { "silo" }));
    }

    [Test]
    public void A_program_with_no_scheme_has_none()
    {
        var urls = Make();

        Assert.That(urls.GetProtocolsFor("C:\\nothing\\here.exe"), Is.Empty);
        Assert.That(urls.GetInfo("C:\\nothing\\here.exe"), Is.Null);
        Assert.That(urls.GetProtocolsFor(" "), Is.Null);
    }

    [Test]
    public void What_the_shell_has_is_read_once_until_refreshed()
    {
        var urls = Make();

        urls.GetInfo(new Uri("https://a/"));
        urls.GetInfo(new Uri("http://a/"));
        Assert.That(urls.Loads, Is.EqualTo(1));

        urls.Refresh();
        urls.GetInfo(new Uri("http://a/"));
        Assert.That(urls.Loads, Is.EqualTo(2));
    }
}

public class RegisteredBrowserProviderTests
{
    [Test]
    public void A_client_becomes_a_browser_with_what_its_registration_says()
    {
        var browser = RegisteredBrowserProvider.FromClient("HKEY_LOCAL_MACHINE", "Contoso", "Contoso Browser", "\"C:\\Contoso\\contoso.exe\" --url %1", ["http", "https"]);

        Assert.That(browser, Is.Not.Null);
        Assert.That(browser!.Name, Is.EqualTo("Contoso Browser"));
        Assert.That(browser.ExePath, Is.EqualTo("C:\\Contoso\\contoso.exe"));
        Assert.That(browser.DefaultFor, Is.EqualTo(new[] { "http", "https" }));
        Assert.That(browser.GetManagementUri(), Is.Not.Null);
    }

    [TestCase(null)]
    [TestCase("")]
    public void A_client_with_no_command_is_not_a_browser(string? command)
    {
        Assert.That(RegisteredBrowserProvider.FromClient("HKEY_CURRENT_USER", "Nothing", "Nothing", command, null), Is.Null);
    }
}

public class BrowserManagerTests
{
    private sealed class Fake(string name, string exe, params string[] schemes) : IBrowser
    {
        public List<Uri> Launched { get; } = [];

        public string[] DefaultFor => schemes;
        public string   ExePath    => exe;
        public string   Name       => name;
        public string   Publisher  => "Test";
        public Version? Version    => null;

        public Uri? GetManagementUri()
        {
            return null;
        }

        public void Launch(Uri destination)
        {
            Launched.Add(destination);
        }
    }

    private sealed class Provider(params IBrowser[] browsers) : IBrowserProvider
    {
        public string DisplayName => "Test";

        public IEnumerable<IBrowser> GetBrowsers()
        {
            return browsers;
        }
    }

    private sealed class ThrowingProvider : IBrowserProvider
    {
        public string DisplayName => "Broken";

        public IEnumerable<IBrowser> GetBrowsers()
        {
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class FakeUrls(params (string Scheme, string Command)[] handlers) : IUrlInfo
    {
        public UrlInfo? GetInfo(Uri uri)
        {
            foreach (var (scheme, command) in handlers)
                if (scheme.Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase))
                    return new UrlInfo(scheme, uri, command, scheme is "http" or "https", false);

            return null;
        }

        public UrlInfo? GetInfo(string exePath)
        {
            return null;
        }

        public string[]? GetProtocolsFor(string exePath)
        {
            return null;
        }
    }

    private sealed class TestManager(IUrlInfo? urls, IEnumerable<IBrowser> registered, IEnumerable<IBrowserProvider> providers) : BrowserManager
    {
        protected override IUrlInfo? Urls => urls;

        protected override IEnumerable<IBrowser> ResolveContracts()
        {
            return registered;
        }

        protected override IEnumerable<IBrowserProvider> ResolveProviders()
        {
            return providers;
        }
    }

    private const string ChromeExe  = "C:\\Chrome\\chrome.exe";
    private const string FirefoxExe = "C:\\Firefox\\firefox.exe";

    private static TestManager Manager(IUrlInfo? urls = null, IBrowser[]? registered = null, params IBrowserProvider[] providers)
    {
        return new TestManager(urls, registered ?? [], providers);
    }

    [Test]
    public void Silos_own_browsers_come_first_then_the_rest()
    {
        var chrome  = new Fake("Chrome", ChromeExe);
        var firefox = new Fake("Firefox", FirefoxExe);

        var all = Manager(null, [chrome], new Provider(firefox)).GetBrowsers().ToList();

        Assert.That(all.Take(InternalBrowsers.Listed.Count), Is.EqualTo(InternalBrowsers.Listed));
        Assert.That(all.Skip(InternalBrowsers.Listed.Count), Is.EqualTo(new IBrowser[] { chrome, firefox }));
    }

    [Test]
    public void A_browser_found_twice_is_listed_once_and_the_registered_one_wins()
    {
        var registered = new Fake("Registered", ChromeExe);
        var found      = new Fake("Found", ChromeExe.ToLowerInvariant());

        var all = Manager(null, [registered], new Provider(found)).GetBrowsers().Skip(InternalBrowsers.Listed.Count).ToList();

        Assert.That(all, Is.EqualTo(new IBrowser[] { registered }));
    }

    [Test]
    public void A_provider_that_throws_does_not_hide_the_others()
    {
        var chrome = new Fake("Chrome", ChromeExe);

        var all = Manager(null, null, new ThrowingProvider(), new Provider(chrome)).GetBrowsers().Skip(InternalBrowsers.Listed.Count).ToList();

        Assert.That(all, Is.EqualTo(new IBrowser[] { chrome }));
    }

    [Test]
    public void A_browser_with_no_program_is_left_out()
    {
        var all = Manager(null, [new Fake("Nowhere", " ")]).GetBrowsers().Skip(InternalBrowsers.Listed.Count).ToList();

        Assert.That(all, Is.Empty);
    }

    [Test]
    public void What_was_gathered_is_kept_until_refreshed()
    {
        var found = new List<IBrowser> { new Fake("Chrome", ChromeExe) };
        var manager = new TestManager(null, found, []);

        Assert.That(manager.GetBrowsers().Count(), Is.EqualTo(InternalBrowsers.Listed.Count + 1));

        found.Add(new Fake("Firefox", FirefoxExe));
        Assert.That(manager.GetBrowsers().Count(), Is.EqualTo(InternalBrowsers.Listed.Count + 1));

        manager.Refresh();
        Assert.That(manager.GetBrowsers().Count(), Is.EqualTo(InternalBrowsers.Listed.Count + 2));
    }

    [Test]
    public void A_command_finds_the_browser_that_runs_it()
    {
        var chrome = new Fake("Chrome", ChromeExe);

        var found = Manager(null, [chrome]).Find("\"c:\\chrome\\CHROME.exe\" --flag %1", BrowserFallback.None);

        Assert.That(found, Is.SameAs(chrome));
    }

    [Test]
    public void A_command_that_runs_silo_is_the_internal_browser()
    {
        var found = Manager().Find("\"C:\\Tools\\Silo\\silo.exe\" \"%1\"", BrowserFallback.None);

        Assert.That(found, Is.SameAs(InternalBrowsers.Auto));
    }

    [Test]
    public void No_browser_for_the_command_follows_the_fallback()
    {
        var manager = Manager();
        const string Missing = "\"C:\\Not\\There\\missing.exe\" %1";

        Assert.Throws<BrowserNotFoundException>(() => manager.Find(Missing, BrowserFallback.None));
        Assert.That(manager.Find(Missing, BrowserFallback.Null), Is.Null);
        Assert.That(manager.Find(Missing, BrowserFallback.Default), Is.SameAs(InternalBrowsers.Auto));
        Assert.That(manager.Find(Missing, BrowserFallback.Default, new Uri("silo:main")), Is.SameAs(InternalBrowsers.InSilo));
        Assert.That(manager.Find(string.Empty, BrowserFallback.Null), Is.Null);
    }

    [Test]
    public void The_user_fallback_asks_the_shell_for_the_address()
    {
        var chrome  = new Fake("Chrome", ChromeExe);
        var manager = Manager(new FakeUrls(("https", "\"" + ChromeExe + "\" %1")), [chrome]);

        Assert.That(manager.Find("\"C:\\Not\\There\\missing.exe\" %1", BrowserFallback.UserDefault), Is.SameAs(chrome));
    }

    [Test]
    public void The_users_default_is_the_browser_the_shell_has_for_the_scheme()
    {
        var chrome  = new Fake("Chrome", ChromeExe);
        var manager = Manager(new FakeUrls(("https", "\"" + ChromeExe + "\" %1")), [chrome]);

        Assert.That(manager.GetUserDefault(new Uri("https://example.com/")), Is.SameAs(chrome));
    }

    [Test]
    public void A_scheme_nothing_is_registered_for_falls_back_to_the_internal_default()
    {
        var manager = Manager(new FakeUrls());

        Assert.That(manager.GetUserDefault(new Uri("gopher://example.com/")), Is.SameAs(InternalBrowsers.Auto));
        Assert.That(Manager(null).GetUserDefault(new Uri("gopher://example.com/")), Is.SameAs(InternalBrowsers.Auto));
    }

    [Test]
    public void Silo_addresses_go_to_navigation_by_default()
    {
        var manager = Manager();

        Assert.That(manager.GetInternalDefault(new Uri("silo:lab/web")), Is.SameAs(InternalBrowsers.InSilo));
        Assert.That(manager.GetInternalDefault(new Uri("gopher://example.com/")), Is.SameAs(InternalBrowsers.Auto));
    }

    [Test]
    public void A_program_set_up_for_a_scheme_but_not_found_is_described_from_its_command_when_it_exists()
    {
        // the program that runs the tests is a file that exists, and not one that was found by a provider
        string exe     = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var    manager = Manager();

        var found = manager.Find($"\"{exe}\" %1", BrowserFallback.None);

        // (the running program is what Silo itself is, so it is answered with Silo's browser: either way it is described by its path)
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.ExePath, Is.EqualTo(exe));
    }
}
