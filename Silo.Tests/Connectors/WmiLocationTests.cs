using Silo.Connectors.System;

namespace Silo.Tests.Connectors;

public class WmiLocationTests
{
    private static WmiLocation Parse(string uri) => WmiLocation.FromUri(new Uri(uri));

    [Test]
    public void No_path_is_the_root_of_the_default_namespace()
    {
        Assert.That(Parse("wmi:///"), Is.EqualTo(new WmiRoot(@"root\cimv2")));
    }

    [Test]
    public void A_class_alone_is_a_class_in_the_default_namespace()
    {
        Assert.That(Parse("wmi:///Win32_Service"), Is.EqualTo(new WmiClass(@"root\cimv2", "Win32_Service")));
    }

    [Test]
    public void An_at_sign_names_the_only_instance_of_the_class()
    {
        var path = (WmiPath)Parse("wmi:///Win32_ComputerSystem/@");

        Assert.That(path.Namespace, Is.EqualTo(@"root\cimv2"));
        Assert.That(path.Hops, Is.EqualTo(new[] { new WmiHop("Win32_ComputerSystem", "@") }));
    }

    [Test]
    public void Namespace_segments_come_before_the_class_and_get_their_root()
    {
        var path = (WmiPath)Parse("wmi:///Citrix/Client/ICA/ICA_Client/@");

        Assert.That(path.Namespace, Is.EqualTo(@"root\Citrix\Client\ICA"));
        Assert.That(path.Hops.Single().ClassName, Is.EqualTo("ICA_Client"));
    }

    [TestCase("wmi:///root/cimv2/Win32_Service", @"root\cimv2")]
    [TestCase("wmi:///ROOT/SecurityCenter2/AntiVirusProduct", @"ROOT\SecurityCenter2")]
    public void A_namespace_that_starts_with_root_is_left_as_written(string uri, string expected)
    {
        Assert.That(((WmiClass)Parse(uri)).Namespace, Is.EqualTo(expected));
    }

    [Test]
    public void A_path_ending_in_a_slash_is_a_namespace_with_no_class()
    {
        Assert.That(Parse("wmi:///Citrix/Client/"), Is.EqualTo(new WmiRoot(@"root\Citrix\Client")));
        Assert.That(Parse("wmi:///root/"), Is.EqualTo(new WmiRoot("root")));
    }

    [Test]
    public void A_query_says_which_instance_by_one_of_its_properties()
    {
        var path = (WmiPath)Parse(@"wmi:///Win32_Volume?name=C:\");

        Assert.That(path.Hops, Is.EqualTo(new[] { new WmiHop("Win32_Volume", @"C:\", "name") }));
    }

    [Test]
    public void The_value_of_a_key_may_hold_equals_and_ampersands_and_escapes()
    {
        var path = (WmiPath)Parse("wmi:///Win32_Share?Name=a%3Db%26c");

        Assert.That(path.Hops.Single().Key, Is.EqualTo("a=b&c"));
    }

    [TestCase("wmi://localhost/Win32_Service")]
    [TestCase("WMI:///Win32_Service")]
    public void This_computer_and_any_case_of_the_scheme_are_accepted(string uri)
    {
        Assert.That(Parse(uri), Is.EqualTo(new WmiClass(@"root\cimv2", "Win32_Service")));
    }

    [Test]
    public void Another_computer_is_named_by_the_host()
    {
        var location = Parse("wmi://server42/Win32_Service");

        Assert.That(location.Host, Is.EqualTo("server42"));
        Assert.That(location,      Is.Not.EqualTo(new WmiClass(@"root\cimv2", "Win32_Service")), "the same class on another computer is another location");
        Assert.That(location,      Is.EqualTo(new WmiClass(@"root\cimv2", "Win32_Service") { Host = "server42" }));
    }

    [Test]
    public void The_fragment_lists_the_properties_to_read()
    {
        var location = Parse("wmi:///Win32_Service#Name,%20State");

        Assert.That(location.PropertiesToLoad, Is.EqualTo(new[] { "Name", "State" }));
        Assert.That(location,                  Is.Not.EqualTo(new WmiClass(@"root\cimv2", "Win32_Service")), "reading fewer properties is another location");
        Assert.That(Parse("wmi:///Win32_Service#").PropertiesToLoad, Is.Empty);
    }

    [Test]
    public void A_query_after_q_is_run_in_the_namespace_and_comes_back_as_text()
    {
        var location = WmiLocation.FromUri(new Uri("wmi:///Citrix/?q=SELECT%20*%20FROM%20ICA_Client%20WHERE%20Name%3D%27a%27"), out string? wql);

        Assert.That(location, Is.EqualTo(new WmiRoot(@"root\Citrix")));
        Assert.That(wql,      Is.EqualTo("SELECT * FROM ICA_Client WHERE Name='a'"));
        Assert.That(WmiLocation.FromUri(new Uri("wmi:///Win32_Service"), out string? none), Is.TypeOf<WmiClass>());
        Assert.That(none, Is.Null);
    }

    [TestCase("wmi:///")]
    [TestCase("wmi:///root/")]
    [TestCase("wmi:///Citrix/Client/")]
    [TestCase("wmi:///Win32_Service")]
    [TestCase("wmi:///root/Win32_Foo")]
    [TestCase("wmi:///Win32_Service#Name,State")]
    [TestCase("wmi:///Win32_ComputerSystem/@")]
    [TestCase("wmi:///Citrix/Client/ICA/ICA_Client/@")]
    [TestCase("wmi:///ROOT/SecurityCenter2/AntiVirusProduct")]
    [TestCase("wmi://server42/Citrix/Client/ICA/ICA_Client/@")]
    public void A_location_is_written_as_the_uri_it_was_read_from(string text)
    {
        var location = Parse(text);

        Assert.That(location.ToUri().OriginalString, Is.EqualTo(text));
        Assert.That(Parse(location.ToUri().OriginalString), Is.EqualTo(location));
    }

    [Test]
    public void A_key_that_holds_odd_characters_survives_being_written()
    {
        foreach (string text in new[] { @"wmi:///Win32_Volume?name=C:\", "wmi:///Win32_Share?Name=a%3Db%26c" })
        {
            var location = Parse(text);

            Assert.That(Parse(location.ToUri().OriginalString), Is.EqualTo(location), text);
        }
    }

    [Test]
    public void A_query_is_written_for_the_namespace_whatever_the_location_was()
    {
        var uri = Parse("wmi:///Win32_Service").ToUri("SELECT * FROM Win32_Service");

        var back = WmiLocation.FromUri(uri, out string? wql);
        Assert.That(back, Is.EqualTo(new WmiRoot(@"root\cimv2")));
        Assert.That(wql,  Is.EqualTo("SELECT * FROM Win32_Service"));
    }

    [Test]
    public void Each_kind_of_location_has_a_name_a_type_and_an_icon()
    {
        Assert.That(Parse("wmi:///Citrix/Client/").Label,        Is.EqualTo("Client"));
        Assert.That(Parse("wmi:///Win32_Service").Label,         Is.EqualTo("Win32_Service"));
        Assert.That(Parse(@"wmi:///Win32_Volume?name=C:\").Label, Is.EqualTo(@"Win32_Volume (name=C:\)"));
        Assert.That(Parse("wmi:///").TypeName,                   Is.EqualTo("WMI Namespace"));
        Assert.That(Parse("wmi:///Win32_Service").TypeName,      Is.EqualTo("WMI Class"));
        Assert.That(Parse("wmi:///Win32_Service/@").TypeName,    Is.EqualTo("WMI Instance"));
        Assert.That(Parse("wmi:///").Icon!.ToString(),        Is.EqualTo("Folder17"));
    }

    [Test]
    public void Another_scheme_is_refused()
    {
        Assert.Throws<NotSupportedException>(() => Parse("http://localhost/Win32_Service"));
    }

    [TestCase("wmi:///#Caption", Description = "properties without a class")]
    [TestCase("wmi:///Citrix/#Caption", Description = "properties without a class")]
    [TestCase("wmi:///Win32_Service#bad%20name", Description = "a property name has no spaces")]
    [TestCase("wmi:///Win32_Service?q=SELECT%201", Description = "a query goes with a namespace, not a class")]
    [TestCase("wmi:///Win32_Service/@?q=SELECT%201", Description = "'@' and a WQL query")]
    [TestCase("wmi:///@", Description = "no class")]
    [TestCase("wmi:///Win32_Volume?name", Description = "a key needs a value")]
    [TestCase("wmi:///Win32_Volume?=C", Description = "a key needs a property")]
    [TestCase("wmi:///Win32_Volume?name=", Description = "a key needs a value")]
    [TestCase("wmi:///Win32_Volume/@?name=C", Description = "'@' and a query")]
    [TestCase("wmi:///?name=C", Description = "a key without a class")]
    [TestCase("wmi:///Citrix/?name=C", Description = "a key without a class")]
    [TestCase("wmi:///Win32%20Volume", Description = "a class name has no spaces")]
    [TestCase("wmi:///Win32_Volume?na%20me=C", Description = "a property name has no spaces")]
    [TestCase("wmi:///bad%20ns/Win32_Service", Description = "nor a namespace")]
    public void Anything_else_is_a_format_error(string uri)
    {
        Assert.Throws<UriFormatException>(() => Parse(uri));
    }
}
