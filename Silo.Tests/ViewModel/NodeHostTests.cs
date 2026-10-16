using Silo.Contracts.Core;
using Silo.ViewModel;

namespace Silo.Tests.ViewModel;

public class NodeHostTests
{
    private sealed class Provider(SiloNode? node, bool throws = false) : INodeProvider
    {
        public int Calls { get; private set; }

        public SiloNode? GetNode()
        {
            Calls++;
            if (throws) throw new InvalidOperationException("no node today");
            return node;
        }
    }

    private static SiloNode Node(string text) => new(text, text, null, null, 1);

    [Test]
    public void A_node_added_without_a_parent_is_a_root_and_raises_Changed()
    {
        var host    = new NodeHost();
        int changed = 0;
        host.Changed += (_, _) => changed++;
        var node = Node("a");

        host.AddNode(node, null);

        Assert.That(host.Roots, Has.Count.EqualTo(1));
        Assert.That(host.Roots[0], Is.SameAs(node));
        Assert.That(changed, Is.GreaterThan(0));
    }

    [Test]
    public void A_node_added_with_a_parent_becomes_its_child()
    {
        var host   = new NodeHost();
        var parent = Node("parent");
        var child  = Node("child");
        host.AddNode(parent, null);

        host.AddNode(child, parent);

        Assert.That(parent.Children, Has.Count.EqualTo(1));
        Assert.That(host.Roots, Has.Count.EqualTo(1));
        Assert.That(host.Contains(child), Is.True);
    }

    [Test]
    public void A_provider_is_asked_once_and_its_node_added_at_root_level()
    {
        var host     = new NodeHost();
        var provider = new Provider(Node("tool"));

        var first  = host.Add(provider);
        var second = host.Add(provider);

        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Null);
        Assert.That(provider.Calls, Is.EqualTo(1));
        Assert.That(host.Roots, Has.Count.EqualTo(1));
        Assert.That(host.Provides(provider), Is.True);
    }

    [Test]
    public void A_provider_with_nothing_to_offer_adds_nothing()
    {
        var host = new NodeHost();

        Assert.That(host.Add(new Provider(null)), Is.Null);
        Assert.That(host.Roots, Is.Empty);
    }

    [Test]
    public void A_provider_that_throws_adds_nothing_and_does_not_throw()
    {
        var host = new NodeHost();

        Assert.That(host.Add(new Provider(null, throws: true)), Is.Null);
        Assert.That(host.Roots, Is.Empty);
    }

    [Test]
    public void Removing_a_provider_takes_its_node_out()
    {
        var host     = new NodeHost();
        var provider = new Provider(Node("doc"));
        host.Add(provider);

        Assert.That(host.Remove(provider), Is.True);

        Assert.That(host.Roots, Is.Empty);
        Assert.That(host.Provides(provider), Is.False);
        Assert.That(host.Remove(provider), Is.False);
    }

    [Test]
    public void A_node_the_provider_already_put_in_the_tree_is_not_added_twice()
    {
        var host = new NodeHost();
        var node = Node("connection");
        host.AddNode(node, null);

        host.Add(new Provider(node));

        Assert.That(host.Roots, Has.Count.EqualTo(1));
    }

    [Test]
    public void RemoveNode_finds_a_node_below_a_root()
    {
        var host   = new NodeHost();
        var parent = Node("parent");
        var child  = Node("child");
        host.AddNode(parent, null);
        host.AddNode(child, parent);

        Assert.That(host.RemoveNode(child), Is.True);

        Assert.That(parent.Children, Is.Empty);
        Assert.That(host.Contains(child), Is.False);
    }

    [Test]
    public void Children_added_after_the_node_joined_raise_Changed()
    {
        var host = new NodeHost();
        var root = Node("root");
        host.AddNode(root, null);
        int changed = 0;
        host.Changed += (_, _) => changed++;

        var child = Node("child");
        root.Children.Add(child);
        child.Children.Add(Node("grandchild"));

        Assert.That(changed, Is.EqualTo(2));
    }

    [Test]
    public void Two_nodes_with_the_same_text_stay_distinct()
    {
        var host = new NodeHost();
        var a    = new SiloNode("same", "same", null, null, 1);
        var b    = new SiloNode("same", "same", null, null, 1);
        host.AddNode(a, null);
        host.AddNode(b, null);

        Assert.That(host.Roots, Has.Count.EqualTo(2));
        Assert.That(host.RemoveNode(b), Is.True);
        Assert.That(host.Roots, Has.Count.EqualTo(1));
        Assert.That(host.Roots[0], Is.SameAs(a));
    }
}
