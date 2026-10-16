using System.Linq;
using Microsoft.Data.SqlClient;
using Silo.Connectors.Sql;
using Silo.Contracts.ConnectorServices;

namespace Silo.Tests.Connectors;

public class SqlSettingsTests
{
    private static readonly Uri Server = new("sqlserver://db.example.com:1444/Sales");

    private static SqlConnectionStringBuilder Parse(SqlAttempt attempt) => new(attempt.ConnectionString);

    // ---- the URI -------------------------------------------------------------------------------

    [Test]
    public void The_server_is_the_host_with_the_port_when_there_is_one()
    {
        Assert.That(SqlSettings.DataSource(new Uri("sqlserver://db.example.com/Sales")),      Is.EqualTo("db.example.com"));
        Assert.That(SqlSettings.DataSource(new Uri("sqlserver://db.example.com:1444/Sales")), Is.EqualTo("db.example.com,1444"));
    }

    [Test]
    public void A_named_instance_is_host_backslash_instance_and_needs_no_port()
    {
        Assert.That(SqlSettings.DataSource(new Uri("sqlserver://pc/?instance=SQLEXPRESS")),      Is.EqualTo("pc\\SQLEXPRESS"));
        Assert.That(SqlSettings.DataSource(new Uri("sqlserver://pc:1444/Sales?instance=INST1")), Is.EqualTo("pc\\INST1"));
    }

    [Test]
    public void The_database_is_the_first_path_segment()
    {
        Assert.That(SqlSettings.Database(Server),                                         Is.EqualTo("Sales"));
        Assert.That(SqlSettings.Database(new Uri("sqlserver://h/My%20DB/x")),              Is.EqualTo("My DB"));
        Assert.That(SqlSettings.Database(new Uri("sqlserver://h")),                        Is.Null);
        Assert.That(SqlSettings.Database(new Uri("sqlserver://h/")),                       Is.Null);
    }

    [Test]
    public void The_options_in_the_query_reach_the_connection_string()
    {
        var uri = new Uri("sqlserver://h/Sales?encrypt=false&trust=true&timeout=7&readonly=true&app=Reports");

        var builder = SqlSettings.Common(uri);

        Assert.That(builder.InitialCatalog,         Is.EqualTo("Sales"));
        Assert.That(builder.TrustServerCertificate, Is.True);
        Assert.That(builder.ConnectTimeout,         Is.EqualTo(7));
        Assert.That(builder.ApplicationIntent,      Is.EqualTo(ApplicationIntent.ReadOnly));
        Assert.That(builder.ApplicationName,        Is.EqualTo("Reports"));
        Assert.That(builder.Encrypt == SqlConnectionEncryptOption.Optional, Is.True);
    }

    [Test]
    public void Without_options_the_application_is_called_Silo_and_the_driver_defaults_stand()
    {
        var builder = SqlSettings.Common(new Uri("sqlserver://h/Sales"));

        Assert.That(builder.ApplicationName,   Is.EqualTo("Silo"));
        Assert.That(builder.ApplicationIntent, Is.EqualTo(ApplicationIntent.ReadWrite));
        Assert.That(builder.ConnectTimeout,    Is.EqualTo(new SqlConnectionStringBuilder().ConnectTimeout));
    }

    [Test]
    public void An_option_that_makes_no_sense_is_ignored()
    {
        var builder = SqlSettings.Common(new Uri("sqlserver://h/Sales?timeout=soon&trust=maybe"));

        Assert.That(builder.TrustServerCertificate, Is.EqualTo(new SqlConnectionStringBuilder().TrustServerCertificate));
        Assert.That(builder.ConnectTimeout,         Is.EqualTo(new SqlConnectionStringBuilder().ConnectTimeout));
    }

    [Test]
    public void A_user_and_password_in_the_uri_are_found_and_unescaped()
    {
        var found = SqlSettings.CredentialFromUri(new Uri("sqlserver://app%40corp:p%40ss%3Aword@h/Sales"));

        Assert.That(found, Is.EqualTo(("app@corp", (string?)"p@ss:word")));
        Assert.That(SqlSettings.CredentialFromUri(new Uri("sqlserver://sa@h")), Is.EqualTo(("sa", (string?)null)));
        Assert.That(SqlSettings.CredentialFromUri(Server),                       Is.Null);
    }

    // ---- who signs in, in what order ------------------------------------------------------------

    private static IEnumerable<SqlAuthMethod> Order(bool credential, ConnectorAuthFlags auth, WinAuthFlags win) => SqlSettings.AuthOrder(credential, auth, win);

    [Test]
    public void Without_a_credential_Windows_is_used_if_the_flags_allow_it()
    {
        Assert.That(Order(false, ConnectorAuthFlags.None, WinAuthFlags.WinAuthWhenNoCreds), Is.EqualTo(new[] { SqlAuthMethod.Windows }));
    }

    [Test]
    public void A_credential_is_used_first_and_Windows_only_if_asked_for_when_it_fails()
    {
        Assert.That(Order(true, ConnectorAuthFlags.None, WinAuthFlags.WinAuthWhenNoCreds),
                    Is.EqualTo(new[] { SqlAuthMethod.Credential }));
        Assert.That(Order(true, ConnectorAuthFlags.None, WinAuthFlags.WinAuthIfCredsFail),
                    Is.EqualTo(new[] { SqlAuthMethod.Credential, SqlAuthMethod.Windows }));
    }

    [Test]
    public void Windows_first_comes_before_the_credential()
    {
        Assert.That(Order(true, ConnectorAuthFlags.None, WinAuthFlags.WinAuthThenCreds),
                    Is.EqualTo(new[] { SqlAuthMethod.Windows, SqlAuthMethod.Credential }));
        Assert.That(Order(false, ConnectorAuthFlags.None, WinAuthFlags.WinAuthThenCreds),
                    Is.EqualTo(new[] { SqlAuthMethod.Windows }), "no credential to fall back to");
    }

    [Test]
    public void A_credential_alone_is_enough_when_Windows_is_not_allowed()
    {
        Assert.That(Order(true, ConnectorAuthFlags.None, WinAuthFlags.None), Is.EqualTo(new[] { SqlAuthMethod.Credential }));
    }

    [Test]
    public void With_no_credential_and_no_Windows_there_is_no_way_to_sign_in()
    {
        Assert.Throws<InvalidOperationException>(() => SqlSettings.AuthOrder(false, ConnectorAuthFlags.None, WinAuthFlags.None));
    }

    [Test]
    public void A_required_credential_that_is_missing_is_refused_even_if_Windows_would_do()
    {
        Assert.Throws<InvalidOperationException>(() => SqlSettings.AuthOrder(false, ConnectorAuthFlags.RequireCredential, SqlSettings.DefaultWinAuth));
        Assert.DoesNotThrow(() => SqlSettings.AuthOrder(true, ConnectorAuthFlags.RequireCredential, WinAuthFlags.None));
    }

    [Test]
    public void The_default_windows_flags_are_for_a_connector_that_says_nothing()
    {
        Assert.That(Order(false, ConnectorAuthFlags.None, SqlSettings.DefaultWinAuth), Is.EqualTo(new[] { SqlAuthMethod.Windows }));
        Assert.That(Order(true,  ConnectorAuthFlags.None, SqlSettings.DefaultWinAuth), Is.EqualTo(new[] { SqlAuthMethod.Credential, SqlAuthMethod.Windows }));
    }

    // ---- the attempts --------------------------------------------------------------------------

    [Test]
    public void A_windows_attempt_uses_integrated_security_and_no_user()
    {
        var attempts = SqlSettings.Attempts(Server, null, null, ConnectorAuthFlags.None, SqlSettings.DefaultWinAuth);

        var one = Parse(attempts.Single());
        Assert.That(one.IntegratedSecurity, Is.True);
        Assert.That(one.UserID,             Is.Empty);
        Assert.That(one.DataSource,         Is.EqualTo("db.example.com,1444"));
        Assert.That(one.InitialCatalog,     Is.EqualTo("Sales"));
    }

    [Test]
    public void A_credential_attempt_carries_the_user_and_password()
    {
        var attempts = SqlSettings.Attempts(Server, "sa", "s3cret", ConnectorAuthFlags.None, SqlSettings.DefaultWinAuth);

        Assert.That(attempts.Select(a => a.Method), Is.EqualTo(new[] { SqlAuthMethod.Credential, SqlAuthMethod.Windows }));
        var first = Parse(attempts[0]);
        Assert.That(first.IntegratedSecurity, Is.False);
        Assert.That(first.UserID,             Is.EqualTo("sa"));
        Assert.That(first.Password,           Is.EqualTo("s3cret"));
        Assert.That(Parse(attempts[1]).Password, Is.Empty, "the Windows attempt carries no password");
    }

    [Test]
    public void The_credential_in_the_uri_is_used_when_none_is_given_and_a_given_one_wins()
    {
        var uri = new Uri("sqlserver://uriuser:uripass@h/Sales");

        var fromUri = SqlSettings.Attempts(uri, null, null, ConnectorAuthFlags.None, WinAuthFlags.None).Single();
        var given   = SqlSettings.Attempts(uri, "given", "pw", ConnectorAuthFlags.None, WinAuthFlags.None).Single();

        Assert.That(Parse(fromUri).UserID, Is.EqualTo("uriuser"));
        Assert.That(Parse(given).UserID,   Is.EqualTo("given"));
    }

    [Test]
    public void An_attempt_never_shows_its_password_when_written_out()
    {
        var attempt = SqlSettings.Attempts(Server, "sa", "s3cret", ConnectorAuthFlags.None, WinAuthFlags.None).Single();

        Assert.That(attempt.ToString(),    Does.Not.Contain("s3cret"));
        Assert.That(attempt.Description,   Is.EqualTo("SQL login 'sa'"));
        Assert.That($"{attempt}",          Does.Not.Contain("s3cret"));
    }

    // ---- which errors mean "try another way" ----------------------------------------------------

    [Test]
    public void A_refused_login_is_an_authentication_failure_and_an_unreachable_server_is_not()
    {
        Assert.That(SqlSettings.IsAuthFailure(18456), Is.True,  "Login failed");
        Assert.That(SqlSettings.IsAuthFailure(18452), Is.True,  "untrusted domain");
        Assert.That(SqlSettings.IsAuthFailure(18487), Is.True,  "password expired");
        Assert.That(SqlSettings.IsAuthFailure(53),    Is.False, "network path not found");
        Assert.That(SqlSettings.IsAuthFailure(4060),  Is.False, "cannot open the database");
        Assert.That(SqlSettings.IsAuthFailure(-2),    Is.False, "timeout");
    }
}

public class SqlNamesTests
{
    [Test]
    public void Names_are_quoted_and_a_closing_bracket_is_doubled()
    {
        Assert.That(SqlNames.Quote("Orders"),     Is.EqualTo("[Orders]"));
        Assert.That(SqlNames.Quote("odd]name"),   Is.EqualTo("[odd]]name]"));
        Assert.That(SqlNames.Quote("x]; DROP--"), Is.EqualTo("[x]]; DROP--]"));
        Assert.That(SqlNames.FullName("Sales", "dbo", "Orders"), Is.EqualTo("[Sales].[dbo].[Orders]"));
    }

    [Test]
    public void The_uri_shown_never_carries_the_user_and_password()
    {
        var safe = SqlNames.SafeUri(new Uri("sqlserver://sa:s3cret@db.example.com:1444/Sales"));

        Assert.That(safe.UserInfo,         Is.Empty);
        Assert.That(safe.ToString(),       Does.Not.Contain("s3cret"));
        Assert.That(safe.Host,             Is.EqualTo("db.example.com"));
        Assert.That(safe.Port,             Is.EqualTo(1444));
    }

    [Test]
    public void A_uri_without_a_user_is_left_alone()
    {
        var uri = new Uri("sqlserver://h/Sales");

        Assert.That(SqlNames.SafeUri(uri), Is.SameAs(uri));
    }

    [Test]
    public void Items_have_uris_below_the_server_and_without_the_password()
    {
        var server = new Uri("sqlserver://sa:s3cret@db.example.com:1444/Sales");

        Assert.That(SqlNames.ItemUri(server).AbsoluteUri,                                  Is.EqualTo("sqlserver://db.example.com:1444/"));
        Assert.That(SqlNames.ItemUri(server, "Sales").AbsoluteUri,                         Is.EqualTo("sqlserver://db.example.com:1444/Sales"));
        Assert.That(SqlNames.ItemUri(server, "Sales", "dbo", "Orders", "Id").AbsoluteUri,  Is.EqualTo("sqlserver://db.example.com:1444/Sales/dbo/Orders/Id"));
        Assert.That(SqlNames.ItemUri(server, "Sales", "~tables").AbsoluteUri,              Does.EndWith("/Sales/~tables"));
    }

    [Test]
    public void Names_with_odd_characters_make_distinct_uris()
    {
        var server = new Uri("sqlserver://h");

        var a = SqlNames.ItemUri(server, "My DB", "dbo", "a/b");
        var b = SqlNames.ItemUri(server, "My DB", "dbo", "a", "b");

        Assert.That(a, Is.Not.EqualTo(b));
        Assert.That(a.AbsoluteUri, Does.Contain("My%20DB"));
        Assert.That(a.AbsoluteUri, Does.Contain("a%2Fb"));
    }

    [TestCase("int",            4,   10, 0, "int")]
    [TestCase("varchar",        50,  0,  0, "varchar(50)")]
    [TestCase("varchar",        -1,  0,  0, "varchar(max)")]
    [TestCase("nvarchar",       100, 0,  0, "nvarchar(50)")]
    [TestCase("nvarchar",       -1,  0,  0, "nvarchar(max)")]
    [TestCase("nchar",          20,  0,  0, "nchar(10)")]
    [TestCase("varbinary",      16,  0,  0, "varbinary(16)")]
    [TestCase("decimal",        9,   18, 2, "decimal(18,2)")]
    [TestCase("numeric",        5,   10, 0, "numeric(10,0)")]
    [TestCase("datetime2",      8,   27, 7, "datetime2")]
    [TestCase("datetime2",      6,   23, 3, "datetime2(3)")]
    [TestCase("time",           5,   16, 0, "time(0)")]
    [TestCase("uniqueidentifier", 16, 0, 0, "uniqueidentifier")]
    public void A_columns_type_is_written_as_in_its_definition(string type, int maxLength, int precision, int scale, string expected)
    {
        Assert.That(SqlNames.TypeText(type, maxLength, precision, scale), Is.EqualTo(expected));
    }

    [Test]
    public void Icons_are_the_ones_in_the_icon_set_and_an_unreachable_item_is_shown_missing()
    {
        Assert.That(SqlItem.IconKeyOf(SqlItemKind.Server,   true),  Is.EqualTo("Server"));
        Assert.That(SqlItem.IconKeyOf(SqlItemKind.Database, true),  Is.EqualTo("Database5"));
        Assert.That(SqlItem.IconKeyOf(SqlItemKind.Database, false), Is.EqualTo("FsFileMissing"));
    }
}
