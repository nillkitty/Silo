using System.IO;
using System.Text.RegularExpressions;
using Silo.Connectors.Ldap;

namespace Silo.Tests.Connectors;

public class LdapModelTests
{
    // ---- groups --------------------------------------------------------------------------------

    [TestCase(-2147483646, LdapModel.GroupScope.Global, true)]       // global security
    [TestCase(-2147483644, LdapModel.GroupScope.DomainLocal, true)]  // domain local security
    [TestCase(-2147483640, LdapModel.GroupScope.Universal, true)]    // universal security
    [TestCase(2, LdapModel.GroupScope.Global, false)]                // global distribution
    [TestCase(4, LdapModel.GroupScope.DomainLocal, false)]
    [TestCase(8, LdapModel.GroupScope.Universal, false)]
    [TestCase(0, LdapModel.GroupScope.Unknown, false)]
    public void A_group_type_says_the_scope_and_whether_it_is_a_security_group(int raw, LdapModel.GroupScope scope, bool security)
    {
        var info = LdapModel.FromGroupType(raw);

        Assert.That(info.Scope,             Is.EqualTo(scope));
        Assert.That(info.IsSecurityEnabled, Is.EqualTo(security));
    }

    [Test]
    public void A_built_in_group_is_built_in_and_not_just_domain_local()
    {
        // Administrators, Users...: 0x80000005 is built-in, domain local and security enabled at once
        var info = LdapModel.FromGroupType(-2147483643);

        Assert.That(info.Scope,             Is.EqualTo(LdapModel.GroupScope.BuiltinLocal));
        Assert.That(info.IsSecurityEnabled, Is.True);
        Assert.That(info.ToString(),        Is.EqualTo("BuiltinLocal Security"));
    }

    // ---- names ---------------------------------------------------------------------------------

    [Test]
    public void The_category_is_the_first_name_in_the_category_s_distinguished_name()
    {
        string category = "CN=Person,CN=Schema,CN=Configuration,DC=contoso,DC=com";

        Assert.That(LdapModel.FirstRdn(category),                          Is.EqualTo("CN=Person"));
        Assert.That(LdapModel.RdnValue(LdapModel.FirstRdn(category)),      Is.EqualTo("Person"));
        Assert.That(LdapModel.RdnValue(LdapModel.FirstRdn(null)),          Is.Empty);
        Assert.That(LdapModel.FirstRdn("CN=Smith\\, John,OU=Sales,DC=x"), Is.EqualTo("CN=Smith\\, John"), "an escaped comma stays in the name");
        Assert.That(LdapModel.RdnValue("no-type"),                         Is.EqualTo("no-type"));
    }

    [TestCase("user", "Person", "user")]
    [TestCase("computer", "Computer", "computer")]
    [TestCase("organizationalUnit", "Organizational-Unit", "organizationalunit")]
    [TestCase(null, "Domain-DNS", "domaindns")]
    [TestCase("", "Organizational-Unit", "organizationalunit")]
    [TestCase(null, null, "object")]
    public void A_kind_is_one_lower_case_word(string? schemaClass, string? category, string kind)
    {
        Assert.That(LdapModel.KindOf(schemaClass, category), Is.EqualTo(kind));
    }

    [TestCase("computer", "Computer Account")]
    [TestCase("user", "User Account")]
    [TestCase("person", "Person")]
    [TestCase("contact", "Contact")]
    [TestCase("organizationalunit", "OU")]
    [TestCase("domaindns", "Domain")]
    [TestCase("rootdse", "Root DSE")]
    [TestCase("container", "Container")]
    [TestCase("subnet", "Subnet")]
    [TestCase("site", "Site")]
    public void Each_kind_has_its_words(string kind, string words)
    {
        Assert.That(LdapModel.TypeNameOf(kind, 0), Is.EqualTo(words));
    }

    [Test]
    public void A_group_says_what_kind_of_group_it_is()
    {
        Assert.That(LdapModel.TypeNameOf("group", -2147483646), Is.EqualTo("Global Security Group"));
        Assert.That(LdapModel.TypeNameOf("group", 8),           Is.EqualTo("Universal Distribution Group"));
    }

    [Test]
    public void A_kind_nobody_knows_is_called_by_its_fallback_or_just_an_object()
    {
        Assert.That(LdapModel.TypeNameOf("msds-managedserviceaccount", 0, "ms-DS-Managed-Service-Account"), Is.EqualTo("ms-DS-Managed-Service-Account"));
        Assert.That(LdapModel.TypeNameOf("thing", 0),                                                       Is.EqualTo("Object"));
    }

    // ---- icons ---------------------------------------------------------------------------------

    private static readonly string[] Kinds =
        ["computer", "user", "person", "organizationalperson", "inetorgperson", "contact", "group", "organizationalunit", "domaindns", "domain",
         "rootdse", "container", "builtindomain", "foreignsecurityprincipal", "subnet", "site", "object"];

    [Test]
    public void The_kinds_people_meet_have_an_icon_of_their_own()
    {
        foreach (string kind in Kinds.Where(k => k != "object"))
            Assert.That(LdapModel.IconKeyOf(kind), Is.Not.EqualTo("Record"), kind);

        Assert.That(LdapModel.IconKeyOf("object"), Is.EqualTo("Record"));
    }

    [Test]
    public void Every_icon_the_entries_use_is_drawn_in_the_icon_dictionary()
    {
        string? file = FindNavIcons();
        if (file is null) Assert.Ignore("Silo/Resources/NavIcons.xaml is not above the test output folder.");

        var drawn = Regex.Matches(File.ReadAllText(file!), "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        foreach (string kind in Kinds)
            Assert.That(drawn, Does.Contain(LdapModel.IconKeyOf(kind)), kind);
    }

    private static string? FindNavIcons()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "Silo", "Resources", "NavIcons.xaml");
            if (File.Exists(path)) return path;
        }

        return null;
    }
}
