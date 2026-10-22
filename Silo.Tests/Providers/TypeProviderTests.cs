using System.Data;
using System.Dynamic;
using Microsoft.Win32;
using Silo.Connectors.System;
using Silo.Providers;
using Silo.Providers.Data.Shape;
using Silo.Providers.Shell;

namespace Silo.Tests.Providers;

public class DynamicTypeProviderTests
{
    private static readonly Uri Anywhere = new("clr-namespace:System", UriKind.Absolute);

    private readonly DynamicTypeProvider _provider = new();

    [TestCase("dynamic")]
    [TestCase("Dynamic")]
    public void The_fully_dynamic_type_is_found_by_its_name(string name)
    {
        Assert.That(_provider.Lookup(new TypeLookup(Anywhere, "dyn", name)), Is.SameAs(DynamicType.Dynamic));
    }

    [TestCase("dto")]
    [TestCase("DynamicDto")]
    public void The_data_only_dynamic_type_is_found_by_its_name(string name)
    {
        Assert.That(_provider.Lookup(new TypeLookup(Anywhere, "dyn", name)), Is.SameAs(DynamicType.Dto));
    }

    [Test]
    public void Another_prefix_or_another_name_finds_nothing()
    {
        Assert.That(_provider.Lookup(new TypeLookup(Anywhere, "clr", "dynamic")), Is.Null);
        Assert.That(_provider.Lookup(new TypeLookup(Anywhere, "dyn", "string")),  Is.Null);
        Assert.That(DynamicType.Parse(null),                                      Is.Null);
    }

    [Test]
    public void The_runtime_types_that_bind_at_run_time_have_dynamic_types()
    {
        Assert.That(_provider.Lookup(typeof(DynamicObject)), Is.SameAs(DynamicType.Dynamic));
        Assert.That(_provider.Lookup(typeof(ExpandoObject)), Is.SameAs(DynamicType.Dto));
        Assert.That(_provider.Lookup(typeof(string)),        Is.Null);
    }

    [Test]
    public void A_dynamic_type_says_what_it_is()
    {
        IType type = DynamicType.Dynamic;

        Assert.That(type.Kind,   Is.EqualTo(DataKind.Dynamic));
        Assert.That(type.Shape,  Is.EqualTo(DataShape.Dynamic));
        Assert.That(type.Prefix, Is.EqualTo("dyn"));
    }

    [Test]
    public void The_two_dynamic_types_have_addresses_of_their_own()
    {
        Assert.That(DynamicType.Dynamic.Uri.ToString(), Does.EndWith("dynamic"));
        Assert.That(DynamicType.Dto.Uri.ToString(),     Does.EndWith("dto"));
    }
}

public class RuntimeTypeTests
{
    private static readonly Uri Anywhere = new("clr-namespace:System", UriKind.Absolute);

    [TestCase(typeof(int),                     DataShape.Scalar)]
    [TestCase(typeof(int?),                    DataShape.Scalar)]
    [TestCase(typeof(string),                  DataShape.Scalar)]
    [TestCase(typeof(DateTime),                DataShape.Scalar)]
    [TestCase(typeof(DayOfWeek),               DataShape.Scalar)]
    [TestCase(typeof(KeyValuePair<int, int>),  DataShape.Columnar)]
    [TestCase(typeof((int, string)),           DataShape.Columnar)]
    [TestCase(typeof(int[]),                   DataShape.Linear)]
    [TestCase(typeof(List<int>),               DataShape.Linear)]
    [TestCase(typeof(int[,]),                  DataShape.Tabular)]
    [TestCase(typeof(DataTable),               DataShape.Tabular)]
    [TestCase(typeof(DataSet),                 DataShape.Multitabular)]
    [TestCase(typeof(Dictionary<string, int>), DataShape.Container)]
    [TestCase(typeof(ExpandoObject),           DataShape.Dynamic)]
    [TestCase(typeof(object),                  DataShape.Object)]
    public void A_runtime_type_has_the_shape_of_the_data_it_holds(Type type, DataShape shape)
    {
        Assert.That(RuntimeType.GetShape(type), Is.EqualTo(shape));
    }

    [Test]
    public void The_kind_says_how_the_type_is_typed()
    {
        Assert.That(RuntimeType.GetKind(typeof(string)),        Is.EqualTo(DataKind.Nominal));
        Assert.That(RuntimeType.GetKind(typeof(List<>)),        Is.EqualTo(DataKind.Open));
        Assert.That(RuntimeType.GetKind(typeof(ExpandoObject)), Is.EqualTo(DataKind.Dynamic));
        Assert.That(RuntimeType.GetKind(typeof(ISyntax)),       Is.EqualTo(DataKind.Syntax));
    }

    [Test]
    public void A_runtime_type_has_a_name_a_prefix_and_a_namespace()
    {
        var type = new RuntimeTypeProvider().Lookup(typeof(List<string>))!;

        Assert.That(type.Name,                                                 Is.EqualTo("List`1"));
        Assert.That(type.Prefix,                                               Is.EqualTo("clr"));
        Assert.That(RuntimeTypeProvider.GetClrNamespace(type.Namespace, true), Is.EqualTo("System.Collections.Generic"));
    }

    [TestCase(typeof(string))]
    [TestCase(typeof(List<string>))]
    [TestCase(typeof(Dictionary<string, List<int>>))]
    [TestCase(typeof(int[]))]
    [TestCase(typeof(int[,]))]
    [TestCase(typeof(List<int>[]))]
    [TestCase(typeof(List<>))]
    public void A_runtime_type_is_written_as_a_uri_and_read_back(Type type)
    {
        var uri = RuntimeType.UriFromType(type);

        Assert.That(uri.Scheme,                   Is.EqualTo("clr-namespace"));
        Assert.That(RuntimeType.TypeFromUri(uri), Is.EqualTo(type));
    }

    [Test]
    public void A_type_that_cannot_be_found_is_an_error_or_a_null()
    {
        var uri = new Uri("clr-namespace:No.Such.Type", UriKind.Absolute);

        Assert.That(RuntimeType.TryTypeFromUri(uri), Is.Null);
        Assert.Throws<TypeLoadException>(() => RuntimeType.TypeFromUri(uri));
        Assert.That(RuntimeType.TryTypeFromUri(new Uri("http://x/")), Is.Null);
    }

    [Test]
    public void A_pointer_has_no_uri()
    {
        Assert.Throws<NotSupportedException>(() => RuntimeType.UriFromType(typeof(int).MakePointerType()));
    }

    [Test]
    public void Only_a_clr_reference_is_looked_up_here_and_a_namespace_is_read_from_its_uri()
    {
        Assert.That(new RuntimeTypeProvider().Lookup(new TypeLookup(Anywhere, "dyn", "String")), Is.Null);
        Assert.That(RuntimeTypeProvider.GetClrNamespace(RuntimeTypeProvider.GetUriForClrNamespace("System.Text"), true),
                    Is.EqualTo("System.Text"));
        Assert.That(RuntimeTypeProvider.GetClrNamespace(new Uri("http://x/"), false), Is.Null);
        Assert.Throws<NotSupportedException>(() => RuntimeTypeProvider.GetClrNamespace(new Uri("http://x/"), true));
    }
}

public class ShellPathsTests
{
    [TestCase(ShellConfigurationScope.User,         RegistryHive.CurrentUser)]
    [TestCase(ShellConfigurationScope.TattooUser,   RegistryHive.CurrentUser)]
    [TestCase(ShellConfigurationScope.System,       RegistryHive.LocalMachine)]
    [TestCase(ShellConfigurationScope.TattooSystem, RegistryHive.LocalMachine)]
    public void The_scope_decides_the_hive_the_paths_are_in(ShellConfigurationScope scope, RegistryHive hive)
    {
        var paths = new ShellPaths(scope);

        Assert.That(paths.Scope,          Is.EqualTo(scope));
        Assert.That(paths.ClassesRoot,    Is.EqualTo(new RegistryPath(hive, @"Software\Classes", null)));
        Assert.That(paths.ShellRoot.Hive, Is.EqualTo(hive));
    }

    [Test]
    public void New_profiles_are_in_the_default_profile_hive_loaded_under_the_users_hive()
    {
        var paths = new ShellPaths(ShellConfigurationScope.NewProfiles);

        Assert.That(paths.ClassesRoot,
                    Is.EqualTo(new RegistryPath(RegistryHive.Users, $@"{ShellPaths.NewProfileHiveKey}\Software\Classes", null)));
    }

    [Test]
    public void A_shell_type_must_be_an_extension_or_a_scheme()
    {
        // refused before anything is opened in the registry
        Assert.Throws<NotSupportedException>(() => new ShellTypeWriter("not a type", ShellConfigurationScope.User));
    }
}