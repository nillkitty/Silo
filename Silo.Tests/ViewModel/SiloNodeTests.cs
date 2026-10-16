using System.IO;
using System.Reflection;
using Silo.Contracts.Core;
using Silo.ViewModel;

namespace Silo.Tests.ViewModel;

public class SiloNodeTests
{
    private static SiloNode Node(string text, Func<CancellationToken, Task<IReadOnlyList<SiloNode>>>? loader = null) =>
        new(text, text, null, null, 0) { ChildLoader = loader };

    [Test]
    public void A_child_that_is_removed_no_longer_points_to_its_parent()
    {
        var parent = Node("parent");
        var child  = Node("child");
        parent.Children.Add(child);
        Assert.That(child.Parent, Is.SameAs(parent));

        parent.Children.Remove(child);

        Assert.That(child.Parent, Is.Null);
    }

    [Test]
    public void A_child_that_moved_to_another_parent_keeps_the_new_one()
    {
        var first  = Node("first");
        var second = Node("second");
        var child  = Node("child");
        first.Children.Add(child);
        second.Children.Add(child);

        first.Children.Remove(child);

        Assert.That(child.Parent, Is.SameAs(second));
    }

    [Test]
    public async Task The_loader_runs_once_and_its_nodes_become_children()
    {
        int calls = 0;
        var node = Node("lazy", _ =>
                                 {
                                     calls++;
                                     return Task.FromResult<IReadOnlyList<SiloNode>>([Node("a"), Node("b")]);
                                 });

        await node.LoadChildrenAsync();
        await node.LoadChildrenAsync();

        Assert.That(calls, Is.EqualTo(1));
        Assert.That(node.Children, Has.Count.EqualTo(2));
        Assert.That(node.ChildrenLoaded, Is.True);
        Assert.That(node.Children[0].Parent, Is.SameAs(node));
    }

    [Test]
    public async Task Asking_while_a_load_is_running_waits_for_it_instead_of_starting_another()
    {
        int calls = 0;
        var gate  = new TaskCompletionSource();
        var node = Node("lazy", async _ =>
                                {
                                    calls++;
                                    await gate.Task;
                                    return [Node("a")];
                                });

        var first  = node.LoadChildrenAsync();
        var second = node.LoadChildrenAsync();
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.That(calls, Is.EqualTo(1));
        Assert.That(node.Children, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task A_loader_that_fails_leaves_one_child_saying_why()
    {
        var node = Node("lazy", _ => throw new IOException("The network path was not found."));

        await node.LoadChildrenAsync();

        Assert.That(node.LoadError, Is.EqualTo("The network path was not found."));
        Assert.That(node.Children.Select(c => c.Text), Is.EqualTo(new[] { "The network path was not found." }));
        Assert.That(node.ChildrenLoaded, Is.True);
    }

    [Test]
    public async Task A_cancelled_load_can_be_tried_again()
    {
        int calls = 0;
        var node = Node("lazy", ct =>
                                {
                                    if (calls++ == 0) throw new OperationCanceledException();
                                    return Task.FromResult<IReadOnlyList<SiloNode>>([Node("a")]);
                                });

        await node.LoadChildrenAsync();
        Assert.That(node.ChildrenLoaded, Is.False);

        await node.LoadChildrenAsync();
        Assert.That(node.Children, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task A_node_without_a_loader_has_nothing_to_load()
    {
        var node = Node("plain");

        await node.LoadChildrenAsync();

        Assert.That(node.Children, Is.Empty);
    }

    // ---- DataContext: always the object given ---------------------------------------------------

    private sealed class RawProvider(object? data) : INodeProvider
    {
        public object? Data => data;

        public SiloNode? GetNode() => null;
    }

    [Test]
    public void A_provider_becomes_the_DataContext_whether_it_is_typed_as_the_provider_or_as_object()
    {
        var provider = new RawProvider("raw");
        object asObject = provider;

        var typed  = new SiloNode(provider);
        var untyped = new SiloNode(asObject);

        Assert.That(typed.DataContext, Is.SameAs(provider));
        Assert.That(untyped.DataContext, Is.SameAs(provider));
    }

    [Test]
    public void Data_is_the_providers_raw_data_or_else_the_DataContext()
    {
        var raw = new object();

        Assert.That(new SiloNode(new RawProvider(raw)).Data, Is.SameAs(raw));
        Assert.That(new SiloNode(raw).Data, Is.SameAs(raw));
    }

    [Test]
    public void No_constructor_takes_a_provider_so_one_object_cannot_be_read_two_ways()
    {
        var parameters = typeof(SiloNode).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                          .SelectMany(c => c.GetParameters());

        Assert.That(parameters.Select(p => p.ParameterType), Has.None.Matches<Type>(typeof(INodeProvider).IsAssignableFrom));
    }

    [Test]
    public void A_file_system_node_keeps_the_FileModel_as_DataContext_and_the_FileSystemInfo_as_Data()
    {
        string file = Path.Combine(Path.GetTempPath(), "silo-ctx-" + Guid.NewGuid().ToString("N") + ".txt");
        global::System.IO.File.WriteAllText(file, "x");
        try
        {
            var model = Silo.Connectors.File.FileConnection.FromPath(new Silo.Connectors.File.FileConnector(), file);
            var node  = model.GetNode()!;

            Assert.That(node.DataContext, Is.SameAs(model));
            Assert.That(node.Data, Is.InstanceOf<FileSystemInfo>());
        }
        finally
        {
            global::System.IO.File.Delete(file);
        }
    }
}
