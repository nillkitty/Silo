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
    public void Another_computer_is_refused_not_ignored()
    {
        Assert.Throws<NotSupportedException>(() => Parse("wmi://server42/Win32_Service"));
    }

    [Test]
    public void Another_scheme_is_refused()
    {
        Assert.Throws<NotSupportedException>(() => Parse("http://localhost/Win32_Service"));
    }

    [TestCase("wmi:///Win32_OperatingSystem/@#Caption", Description = "a fragment is not used")]
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
