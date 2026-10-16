using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using Silo.Connectors.Virtual;

namespace Silo.Tests.Connectors;

public class RuntimeNodeTests
{
    private static readonly Assembly SiloAssembly = typeof(RuntimeConnector).Assembly;

    [Test]
    public async Task The_app_domain_lists_the_loaded_assemblies_by_name()
    {
        var node = new AppDomainNode(AppDomain.CurrentDomain);

        await node.LoadChildrenAsync();

        var names = node.Children.OfType<AssemblyNode>().Select(a => a.Assembly.GetName().Name!).ToList();
        Assert.That(names, Does.Contain(SiloAssembly.GetName().Name));
        Assert.That(names, Is.EqualTo(names.Order(StringComparer.OrdinalIgnoreCase).ToList()));
        Assert.That(node.Children, Has.All.InstanceOf<AssemblyNode>());
    }

    [Test]
    public async Task An_assembly_lists_its_types_then_its_resources()
    {
        var node = new AssemblyNode(SiloAssembly);

        await node.LoadChildrenAsync();

        var types = node.Children.OfType<TypeNode>().ToList();
        Assert.That(types.Select(t => t.Type), Does.Contain(typeof(RuntimeConnector)));
        Assert.That(types.Select(t => t.Text), Is.EqualTo(types.Select(t => t.Text).Order(StringComparer.Ordinal).ToList()));

        int firstResource = node.Children.ToList().FindIndex(c => c is AssemblyResourceNode);
        Assert.That(firstResource, Is.GreaterThan(0), "the Silo plugin script is embedded");
        Assert.That(node.Children.Skip(firstResource), Has.All.InstanceOf<AssemblyResourceNode>());
    }

    [Test]
    public async Task A_resource_node_is_called_by_the_name_of_the_resource()
    {
        var node = new AssemblyNode(SiloAssembly);

        await node.LoadChildrenAsync();

        var resource = node.Children.OfType<AssemblyResourceNode>().SingleOrDefault(r => r.Text == "silo-plugin.js");
        Assert.That(resource, Is.Not.Null);
        Assert.That(resource!.TypeName, Is.EqualTo("Manifest Resource"));
        Assert.That(resource.Info,      Is.Not.Null);
    }

    [Test]
    public async Task A_dynamic_assembly_has_types_and_no_resources_and_does_not_fail()
    {
        var emitted = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Silo.Tests.Dynamic"), AssemblyBuilderAccess.Run);
        emitted.DefineDynamicModule("m").DefineType("Thing").CreateType();

        var node = new AssemblyNode(emitted);
        await node.LoadChildrenAsync();

        Assert.That(node.LoadError, Is.Null);
        Assert.That(node.Children.OfType<AssemblyResourceNode>(), Is.Empty);
        Assert.That(AssemblyNode.ResourceNames(emitted), Is.Empty);
    }

    [Test]
    public void Each_kind_of_node_has_its_own_icon_and_words()
    {
        var domain   = new AppDomainNode(AppDomain.CurrentDomain);
        var assembly = new AssemblyNode(SiloAssembly);
        var type     = new TypeNode(typeof(RuntimeConnector));

        Assert.That(new[] { domain.Icon, assembly.Icon, type.Icon }.Select(i => i!.OriginalString),
                    Is.EqualTo(new[] { "Modules", "Puzzle", "Cube" }));
        Assert.That(domain.TypeName,   Is.EqualTo("App Domain"));
        Assert.That(assembly.TypeName, Is.EqualTo("Assembly"));
        Assert.That(type.TypeName,     Is.EqualTo("Type"));
        Assert.That(type.Text,         Is.EqualTo(typeof(RuntimeConnector).FullName));
    }

    [Test]
    public void Every_icon_the_runtime_nodes_use_is_drawn_in_the_icon_dictionary()
    {
        string? file = FindNavIcons();
        if (file is null) Assert.Ignore("Silo/Resources/NavIcons.xaml is not above the test output folder.");

        var drawn = Regex.Matches(File.ReadAllText(file!), "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.That(drawn, Does.Contain("Modules"));
        Assert.That(drawn, Does.Contain("Puzzle"));
        Assert.That(drawn, Does.Contain("Cube"));
        Assert.That(drawn, Does.Contain("Document"));
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
