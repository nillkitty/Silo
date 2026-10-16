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
}
