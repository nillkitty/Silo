using Silo.Connectors;
using Silo.Connectors.System;

namespace Silo.Tests.Connectors;

public class UriRulesTests
{
    /// <summary>A connector stand-in that takes the given schemes with the given scope.</summary>
    private sealed class Takes(UriSchemeScope scope, params string[] schemes) : IUriConnector
    {
        public string? Problem { get; init; }

        public UriSchemeScope HandlesScheme(string scheme)
        {
            return schemes.Contains(scheme, StringComparer.OrdinalIgnoreCase) ? scope : UriSchemeScope.Unknown;
        }

        public string? Validate(Uri uri)
        {
            return Problem;
        }
    }

    [TestCase(@"C:\Data", "file")]
    [TestCase(@"\\server\share", "file")]
    [TestCase("reg:///hkey_current_user/software", "reg")]
    [TestCase("https://example.com/x", "https")]
    [TestCase("  \"C:\\Data\"  ", "file")]
    public void Parse_reads_paths_and_uris(string text, string scheme)
    {
        Assert.That(UriRules.Parse(text)?.Scheme, Is.EqualTo(scheme));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("example.com")]
    [TestCase("c:foo")]
    public void Parse_rejects_what_is_not_a_uri_or_a_full_path(string? text)
    {
        Assert.That(UriRules.Parse(text), Is.Null);
    }

    [Test]
    public void Check_accepts_a_private_scheme_and_a_well_known_one_by_default()
    {
        var connectors = new IUriConnector[] { new Takes(UriSchemeScope.Private, "reg"), new Takes(UriSchemeScope.WellKnown, "file", "https") };

        Assert.That(UriRules.Check("reg:///hkey_users", connectors).IsValid, Is.True);
        Assert.That(UriRules.Check(@"C:\Data", connectors).IsValid, Is.True);
        Assert.That(UriRules.Check("https://example.com", connectors).IsValid, Is.True);
    }

    [Test]
    public void Check_rejects_an_adhoc_scheme_unless_scopes_allow_it()
    {
        var connectors = new IUriConnector[] { new Takes(UriSchemeScope.AdHoc, "tel") };

        Assert.That(UriRules.Check("tel:+1555", connectors).IsValid, Is.False);
        Assert.That(UriRules.Check("tel:+1555", connectors, UriRules.AnyScope).IsValid, Is.True);
    }

    [Test]
    public void Check_says_why_when_no_connector_has_the_scheme()
    {
        var check = UriRules.Check("ldap://corp.example.com", [new Takes(UriSchemeScope.WellKnown, "file")]);

        Assert.That(check.IsValid, Is.False);
        Assert.That(check.Message, Does.Contain("ldap"));
        Assert.That(check.Uri, Is.Not.Null);
    }

    [Test]
    public void Check_reports_the_connectors_own_objection()
    {
        var check = UriRules.Check("reg:///nonsense", [new Takes(UriSchemeScope.Private, "reg") { Problem = "not a hive" }]);

        Assert.That(check.IsValid, Is.False);
        Assert.That(check.Message, Is.EqualTo("not a hive"));
    }

    [Test]
    public void Check_asks_for_input_when_the_text_is_empty()
    {
        Assert.That(UriRules.Check("", []).IsValid, Is.False);
        Assert.That(UriRules.Check(null, []).Message, Is.Not.Empty);
    }

    [TestCase(@"C:\Data\Reports", "Reports")]
    [TestCase(@"C:\", @"C:\")]
    [TestCase("reg:///hkey_current_user/software/vendor", "vendor")]
    [TestCase("https://example.com/", "example.com")]
    public void NameFor_names_what_the_uri_points_at(string text, string expected)
    {
        Assert.That(UriRules.NameFor(UriRules.Parse(text)!), Is.EqualTo(expected));
    }
}

public class RegistryUrisTests
{
    [TestCase(@"HKEY_CURRENT_USER\Software", "reg:///hkey_current_user/Software")]
    [TestCase(@"HKCU:\Software\Vendor Name", "reg:///hkey_current_user/Software/Vendor%20Name")]
    [TestCase(@"Computer\HKEY_LOCAL_MACHINE\SOFTWARE", "reg:///hkey_local_machine/SOFTWARE")]
    [TestCase("hklm", "reg:///hkey_local_machine")]
    [TestCase("reg:///hkey_users/s-1-5", "reg:///hkey_users/s-1-5")]
    public void FromText_builds_the_reg_uri(string text, string expected)
    {
        Assert.That(RegistryUris.FromText(text)?.AbsoluteUri, Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(@"C:\Windows")]
    [TestCase("Software")]
    public void FromText_rejects_text_without_a_hive(string? text)
    {
        Assert.That(RegistryUris.FromText(text), Is.Null);
    }
}
