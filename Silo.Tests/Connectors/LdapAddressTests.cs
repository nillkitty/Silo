using System.Text.RegularExpressions;
using Silo.Connectors.Ldap;
using Silo.Contracts.ConnectorServices;
using Silo.Contracts.Core;
using Silo.Model;
using Silo.Shell;
using Silo.ViewModel;

namespace Silo.Tests.Connectors;

public class LdapAddressTests
{
    // ---- URI -> ADSI path ----------------------------------------------------------------------

    [TestCase("adsi://", "LDAP://")]
    [TestCase("adsi://dc1.contoso.com", "LDAP://dc1.contoso.com")]
    [TestCase("adsi://dc1.contoso.com/CN=Users,DC=contoso,DC=com", "LDAP://dc1.contoso.com/CN=Users,DC=contoso,DC=com")]
    [TestCase("ldap://dc1/dc=x,dc=y", "LDAP://dc1/dc=x,dc=y")]
    [TestCase("ldap://dc1:389/dc=x", "LDAP://dc1/dc=x")]
    [TestCase("ldap://dc1:3268/dc=x", "LDAP://dc1:3268/dc=x")]
    [TestCase("ldaps://dc1/dc=x", "LDAP://dc1:636/dc=x")]
    [TestCase("ldaps://dc1:3269", "LDAP://dc1:3269")]
    [TestCase("adsi:///DC=contoso,DC=com", "LDAP://DC=contoso,DC=com")]
    [TestCase("adsi://dc1/RootDSE", "LDAP://dc1/RootDSE")]
    public void A_uri_is_written_as_the_ADSI_path_it_stands_for(string uri, string path)
    {
        Assert.That(LdapModel.ToAdsPath(new Uri(uri)), Is.EqualTo(path));
    }

    [TestCase("adsi://dc1.contoso.com/CN=Users,DC=contoso,DC=com", "ldap://dc1.contoso.com/CN=Users,DC=contoso,DC=com")]
    [TestCase("adsi://dc1.contoso.com", "ldap://dc1.contoso.com/")]
    [TestCase("adsi://dc1:3268/DC=contoso", "ldap://dc1:3268/DC=contoso")]
    [TestCase("ADSI://DC1/dc=x", "ldap://dc1/dc=x")]
    [TestCase("adsi://bob:secret@dc1/dc=x?cn", "ldap://dc1/dc=x")]
    public void An_adsi_address_is_the_same_address_under_the_ldap_scheme(string adsi, string ldap)
    {
        Assert.That(LdapModel.AdsiToLdap(new Uri(adsi)).AbsoluteUri, Is.EqualTo(ldap));
    }

    [Test]
    public void Only_an_adsi_address_can_be_converted()
    {
        Assert.Throws<NotSupportedException>(() => LdapModel.AdsiToLdap(new Uri("ldaps://dc1:3269/dc=x")));
    }

    [Test]
    public void The_distinguished_name_is_unescaped_and_the_user_info_and_query_are_dropped()
    {
        Assert.That(LdapModel.ToAdsPath(new Uri("ldap://dc1/CN=Smith%5C,%20John,DC=x")), Is.EqualTo("LDAP://dc1/CN=Smith\\, John,DC=x"));
        Assert.That(LdapModel.ToAdsPath(new Uri("ldap://bob:secret@dc1/dc=x")),        Is.EqualTo("LDAP://dc1/dc=x"));
        Assert.That(LdapModel.ToAdsPath(new Uri("ldap://dc1/dc=x?cn?sub?(objectClass=*)")), Is.EqualTo("LDAP://dc1/dc=x"));
    }

    [Test]
    public void Only_ldap_ldaps_and_adsi_are_LDAP_addresses()
    {
        Assert.Throws<ArgumentException>(() => LdapModel.ToAdsPath(new Uri("http://dc1/")));
        Assert.That(LdapModel.IsSecure(new Uri("ldaps://dc1")), Is.True);
        Assert.That(LdapModel.IsSecure(new Uri("ldap://dc1")),  Is.False);
    }

    [TestCase("LDAP://", "", "")]
    [TestCase("LDAP://dc1", "dc1", "")]
    [TestCase("LDAP://dc1:3268", "dc1:3268", "")]
    [TestCase("LDAP://dc1/CN=Users,DC=x", "dc1", "CN=Users,DC=x")]
    [TestCase("LDAP://DC=x,DC=y", "", "DC=x,DC=y")]
    [TestCase("LDAP://RootDSE", "", "RootDSE")]
    [TestCase("LDAP://dc1/RootDSE", "dc1", "RootDSE")]
    [TestCase("LDAP://CN=a\\/b,DC=x", "", "CN=a\\/b,DC=x")]
    public void An_ADSI_path_splits_into_the_server_and_the_distinguished_name(string path, string server, string dn)
    {
        var (s, d) = LdapModel.SplitAdsPath(path);

        Assert.That(s, Is.EqualTo(server));
        Assert.That(d, Is.EqualTo(dn));
    }

    [Test]
    public void The_connector_s_default_address_is_the_current_domain()
    {
        var connector = new LdapConnector();

        Assert.That(LdapModel.ToAdsPath(connector.DefaultUri), Is.EqualTo("LDAP://"));
    }

    // ---- credentials ---------------------------------------------------------------------------

    [TestCase("bob", "CONTOSO", "CONTOSO\\bob")]
    [TestCase("bob", null, "bob")]
    [TestCase("bob", "", "bob")]
    [TestCase("bob@contoso.com", "CONTOSO", "bob@contoso.com")]
    [TestCase("CORP\\bob", "CONTOSO", "CORP\\bob")]
    [TestCase(null, "CONTOSO", null)]
    [TestCase("  ", "CONTOSO", null)]
    public void The_user_name_to_sign_in_as_carries_the_domain_when_it_was_kept_apart(string? user, string? domain, string? expected)
    {
        Assert.That(LdapCredentialSink.Combine(user, domain), Is.EqualTo(expected));
    }

    [Test]
    public void A_saved_credential_hands_its_user_name_and_password_to_the_sink()
    {
        var credential = new Credential { Name = "Contoso admin", Realm = AuthRealm.Domain, Username = "bob", Domain = "CONTOSO", Password = "hunter2" };
        var sink       = new LdapCredentialSink();

        credential.Configure(sink);

        Assert.That(sink.UserName, Is.EqualTo("CONTOSO\\bob"));
        Assert.That(sink.Password, Is.EqualTo("hunter2"));
    }

    [Test]
    public void A_user_name_and_password_credential_signs_in_as_the_name_it_was_given()
    {
        var sink = new LdapCredentialSink();

        new Credential { Name = "n", Username = "bob", Domain = "CONTOSO", Password = "hunter2" }.Configure(sink);

        Assert.That(sink.UserName, Is.EqualTo("bob"), "simple authentication: the domain is not part of the name");
        Assert.That(sink.Password, Is.EqualTo("hunter2"));
    }

    [TestCase(AuthRealm.DefaultCredential)]
    [TestCase(AuthRealm.DefaultNetwork)]
    [TestCase(AuthRealm.Anonymous)]
    public void The_realms_that_name_nobody_leave_the_sink_signing_in_as_the_current_user(AuthRealm realm)
    {
        var sink = new LdapCredentialSink();
        sink.SetBasicCredential("old", "old");

        new Credential { Name = "n", Realm = realm, Username = "bob", Password = "hunter2" }.Configure(sink);

        Assert.That(sink.UserName, Is.Null);
        Assert.That(sink.Password, Is.Null);
    }

    // ---- the connector ---------------------------------------------------------------------------

    [TestCase("adsi", UriSchemeScope.Private)]
    [TestCase("ldap", UriSchemeScope.WellKnown)]
    [TestCase("LDAPS", UriSchemeScope.WellKnown)]
    [TestCase("http", UriSchemeScope.Unknown)]
    public void The_connector_takes_the_ldap_schemes_and_no_other(string scheme, UriSchemeScope expected)
    {
        Assert.That(new LdapConnector().HandlesScheme(scheme), Is.EqualTo(expected));
    }

    [Test]
    public void The_connector_checks_an_address_the_way_it_will_read_it()
    {
        var connector = new LdapConnector();

        Assert.That(connector.Validate(new Uri("adsi://dc1/DC=contoso,DC=com")), Is.Null);
        Assert.That(connector.Validate(new Uri("adsi://")), Is.Null, "no host: the current domain");
        Assert.That(connector.Validate(new Uri("http://dc1/")), Is.Not.Null);
    }

    [Test]
    public void The_connector_asks_for_no_credential_by_default()
    {
        Assert.That(new LdapConnector().AuthFlags, Is.EqualTo(ConnectorAuthFlags.None));
    }

    // ---- making a connection ---------------------------------------------------------------------

    [Test]
    public void Making_a_connection_asks_nothing_of_the_directory()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("ADSI is Windows only.");

        var connection = LdapModel.Create(new LdapConnector(), new ConnectionRequest(new Uri("adsi://dc1.contoso.com/DC=contoso,DC=com")));
        try
        {
            Assert.That(connection.State, Is.InstanceOf<LdapState.Disconnected>());
            Assert.That(connection.ConnectionState, Is.False);
            Assert.That(connection.Name, Is.EqualTo("dc1.contoso.com"));
            Assert.That(connection.Uri, Is.EqualTo(new Uri("adsi://dc1.contoso.com/DC=contoso,DC=com")));
            Assert.That(connection.GetHostname(), Is.EqualTo("dc1.contoso.com"));
            Assert.That(connection.CurrentOperation, Is.Null);

            // the details of a connection that has not read its directory say so without reading it
            Assert.That(connection.GetDetails().Select(r => r.Name), Does.Contain("State"));
        }
        finally
        {
            connection.Entry.Dispose();
        }
    }

    // ---- which entries can be opened -------------------------------------------------------------

    [TestCase("organizationalunit", true)]
    [TestCase("container", true)]
    [TestCase("domaindns", true)]
    [TestCase("builtindomain", true)]
    [TestCase("user", false)]
    [TestCase("computer", false)]
    [TestCase("group", false)]
    [TestCase("object", false)]
    public void Containers_are_read_when_expanded_and_the_rest_are_leaves(string kind, bool expandable)
    {
        Assert.That(LdapModel.HasChildrenOfKind(kind), Is.EqualTo(expandable));
    }

    [Test]
    public void A_sink_can_ask_to_be_configured_from_a_credential()
    {
        var sink = new LdapCredentialSink();

        sink.ConfigureFrom(new Credential { Name = "n", Username = "u", Password = "p" });

        Assert.That(sink.UserName, Is.EqualTo("u"));
    }

    [Test]
    public void A_credential_is_called_by_its_name_and_a_sink_that_takes_no_password_is_left_alone()
    {
        ICredential credential = new Credential { Name = "Contoso admin", Username = "bob", Password = "hunter2" };

        Assert.That(credential.DisplayName, Is.EqualTo("Contoso admin"));
        Assert.DoesNotThrow(() => credential.Configure(new NoPasswordSink()));
    }

    private sealed class NoPasswordSink : ICredentialSink
    {
        public void ConfigureFrom(ICredential credential)
        {
        }
    }

    // ---- states and their icons ----------------------------------------------------------------

    private static IEnumerable<LdapState> States()
    {
        yield return new LdapState.Disconnected();
        yield return new LdapState.Connecting(new Uri("ldap://dc1:389"));
        yield return new LdapState.Busy(new LdapState.Connected(), "reading", CancellationToken.None);
        yield return new LdapState.Connected();
        yield return new LdapState.Failed(new InvalidOperationException("no route"));
        yield return new LdapState.Aborting(new LdapState.Busy(new LdapState.Connected(), "reading", CancellationToken.None));
    }

    [Test]
    public void Each_state_has_an_icon_of_its_own()
    {
        var keys = States().Select(s => s.IconKey).ToList();

        Assert.That(keys, Is.EqualTo(new[] { "StateDisconnected", "StateConnecting", "StateBusy", "StateConnected", "StateFailed", "StateBusy" }));
        Assert.That(IconAttribute.KeyOf(typeof(LdapState)), Is.Null, "the base state is not one of them");
    }

    [Test]
    public void Every_state_icon_is_drawn_in_the_icon_dictionary()
    {
        string? file = FindNavIcons();
        if (file is null) Assert.Ignore("Silo/Resources/NavIcons.xaml is not above the test output folder.");

        var drawn = Regex.Matches(File.ReadAllText(file!), "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var state in States())
            Assert.That(drawn, Does.Contain(state.IconKey), state.GetType().Name);
    }

    [Test]
    public void A_state_says_what_it_is_in_words()
    {
        Assert.That(new LdapState.Connecting(new Uri("ldap://dc1:389")).DisplayName, Is.EqualTo("Connecting to dc1:389"));
        Assert.That(new LdapState.Failed(new InvalidOperationException("no route")).DisplayName, Is.EqualTo("Failed: no route"));
        Assert.That(new LdapState.Connected().DisplayName, Is.EqualTo("Connected"));
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
