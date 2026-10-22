using System.Collections.Specialized;
using Serilog;

namespace Silo.ViewModel;

/// <summary>
///     Holds the nodes that connections, tools and documents contribute to the Explorer, as <see cref="SiloNode" />s,
///     and is the <see cref="INavManager" /> handed to connections through their <see cref="Silo.Connectors.ConnectContext" />.
///     Each Silo has one (<c>OpenSilo.Nodes</c>: its connections and documents) and the app has one (<c>App.Nodes</c>:
///     tools, which belong to no Silo). The Explorer shows the roots of both and redraws when <see cref="Changed" />.
/// </summary>
/// <remarks>
///     <para>
///         A <see cref="INodeProvider" /> is asked for its node by <see cref="Add" />; the host remembers whose node it is,
///         so <see cref="Remove" /> can take it out again when the connection is removed, the tool unregistered or the
///         document closed. Nodes added directly with <see cref="AddNode" /> (what <c>ConnectContext.CreateNode</c> does)
///         belong to no provider and stay until <see cref="RemoveNode" />.
///     </para>
///     <para>
///         Reference equality is used throughout: <see cref="SiloNode" /> is a record, so <c>==</c> and <c>Contains</c>
///         would compare values, and two distinct nodes with the same text must stay distinct.
///     </para>
/// </remarks>
public sealed class NodeHost : INavManager
{
    private readonly object                      _gate      = new();
    private readonly List<SiloNode>              _roots     = [];
    private readonly List<(object Owner, SiloNode Node)> _owned = [];

    /// <summary>The top-level nodes, in the order they were added.</summary>
    public IReadOnlyList<SiloNode> Roots
    {
        get
        {
            lock (_gate) return _roots.ToList();
        }
    }

    /// <summary>Raised (on any thread) when a node was added or removed, or a node's children changed.</summary>
    public event EventHandler? Changed;

    /// <inheritdoc />
    public void AddNode(SiloNode siloNode, SiloNode? parent)
    {
        siloNode.Required();

        if (parent is null)
        {
            lock (_gate)
            {
                if (_roots.Any(r => ReferenceEquals(r, siloNode))) return;
                _roots.Add(siloNode);
            }
        }
        else
        {
            parent.Children.Add(siloNode);
        }

        Watch(siloNode);
        RaiseChanged();
    }

    /// <summary>
    ///     Asks <paramref name="provider" /> for its node and puts it at root level. A provider that has nothing to
    ///     offer (<c>GetNode</c> returns null), fails, or was already added, adds nothing.
    /// </summary>
    /// <returns>The node added, or null.</returns>
    public SiloNode? Add(INodeProvider provider)
    {
        provider.Required();

        lock (_gate)
        {
            if (_owned.Any(o => ReferenceEquals(o.Owner, provider))) return null;
        }

        SiloNode? node;
        try
        {
            node = provider.GetNode();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Provider} could not provide a navigation node", provider.GetType().Name);
            return null;
        }

        if (node is null) return null;

        lock (_gate)
        {
            _owned.Add((provider, node));
        }

        // a connection may already have put its node in the tree through ConnectContext.CreateNode
        if (!Contains(node)) AddNode(node, null);
        return node;
    }

    /// <summary>Takes out the node <paramref name="provider" /> gave (and the node's descendants with it).</summary>
    public bool Remove(INodeProvider provider)
    {
        provider.Required();

        SiloNode? node = null;
        lock (_gate)
        {
            int i = _owned.FindIndex(o => ReferenceEquals(o.Owner, provider));
            if (i >= 0)
            {
                node = _owned[i].Node;
                _owned.RemoveAt(i);
            }
        }

        return node is not null && RemoveNode(node);
    }

    /// <summary>Takes a node out of the tree, wherever it is (root level or below another node).</summary>
    public bool RemoveNode(SiloNode? node)
    {
        if (node is null) return false;

        bool removed;
        lock (_gate)
        {
            removed = _roots.RemoveAll(r => ReferenceEquals(r, node)) > 0;
        }

        if (!removed)
            foreach (var root in Roots)
                if (RemoveBelow(root, node))
                {
                    removed = true;
                    break;
                }

        if (removed)
        {
            Unwatch(node);
            RaiseChanged();
        }

        return removed;
    }

    /// <summary>True when <paramref name="provider" /> has a node in this host (so it need not be shown a second time).</summary>
    public bool Provides(INodeProvider provider)
    {
        lock (_gate)
        {
            return _owned.Any(o => ReferenceEquals(o.Owner, provider));
        }
    }

    /// <summary>True when the node is in the tree at any depth.</summary>
    public bool Contains(SiloNode? node)
    {
        if (node is null) return false;

        return Roots.Any(r => Contains(r, node));
    }

    private static bool Contains(SiloNode within, SiloNode node)
    {
        if (ReferenceEquals(within, node)) return true;

        foreach (var child in within.Children.ToList())
            if (Contains(child, node))
                return true;

        return false;
    }

    private static bool RemoveBelow(SiloNode within, SiloNode node)
    {
        for (int i = 0; i < within.Children.Count; i++)
        {
            if (ReferenceEquals(within.Children[i], node))
            {
                within.Children.RemoveAt(i);
                return true;
            }

            if (RemoveBelow(within.Children[i], node)) return true;
        }

        return false;
    }

    // children added to / removed from a node after it joined the tree are as much a change as the node itself
    private void Watch(SiloNode node)
    {
        node.Children.CollectionChanged -= OnChildrenChanged;
        node.Children.CollectionChanged += OnChildrenChanged;
        node.ChildrenLoadedChanged      -= OnChildrenLoaded;
        node.ChildrenLoadedChanged      += OnChildrenLoaded;
        foreach (var child in node.Children.ToList()) Watch(child);
    }

    private void Unwatch(SiloNode node)
    {
        node.Children.CollectionChanged -= OnChildrenChanged;
        node.ChildrenLoadedChanged      -= OnChildrenLoaded;
        foreach (var child in node.Children.ToList()) Unwatch(child);
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (var added in e.NewItems.OfType<SiloNode>())
                Watch(added);

        RaiseChanged();
    }

    // a lazy node that turned out to have no children changes what the tree shows (its "Loading..." entry goes) without adding anything
    private void OnChildrenLoaded(object? sender, EventArgs e)
    {
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
