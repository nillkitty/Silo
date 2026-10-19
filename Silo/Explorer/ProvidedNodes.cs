using System.Windows.Media;
using Silo.Model;
using Silo.Ui.Documents;
using Silo.ViewModel;

using Silo.Design;
namespace Silo.Explorer;

/// <summary>
///     Turns the <see cref="SiloNode" />s that connections, tools and documents contribute (see
///     <see cref="INodeProvider" />
///     and <see cref="NodeHost" />) into <see cref="ExplorerNode" />s, so they show up as roots of the Explorer tree.
/// </summary>
public static class ProvidedNodes
{
    /// <summary>
    ///     The Explorer roots for everything contributed to the app (tools) and to <paramref name="silo" /> (connections,
    ///     documents).
    /// </summary>
    public static IEnumerable<ExplorerNode> For(OpenSilo silo)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (host, scope) in Hosts(silo))
        {
            var roots = host.Roots;
            for (int i = 0; i < roots.Count; i++)
                yield return Adapt(roots[i], $"node/{scope}", i, seen);
        }
    }

    private static IEnumerable<(NodeHost Host, string Scope)> Hosts(OpenSilo silo)
    {
        if (App.Instance?.Nodes is { } app) yield return (app, "app");
        yield return (silo.Nodes, "silo");
    }

    private static ExplorerNode Adapt(SiloNode source, string parentKey, int index, HashSet<string> seen)
    {
        string key  = KeyOf(source, parentKey, index, seen);
        string kind = KindOf(source.DataContext);

        // children that are read when the node is first expanded: until then it shows a "Loading..." entry, which makes it expandable
        bool lazy = source.ChildLoader is not null && !source.ChildrenLoaded;

        ExplorerNode? self = null;
        var node = new ExplorerNode(key, HeaderOf(source), kind, IconKeyOf(source, kind))
                   {
                       DocKey =
                           source.DataContext is INodeViewProvider
                               ? "provided:" + key
                               : null,
                       Open =
                           source.DataContext is INodeViewProvider
                               provider
                               ? () => provider.BuildView() ??
                                       NodeViews.Build(self!)
                               : null,
                       ExpandByDefault = source.IsExpanded,
                       Stamp = source.Children.Count +
                               (lazy ? "?" : string.Empty),
                       Details = () => DetailsOf(source, kind),
                       Actions = () => ActionsOf(source),
                       Commands =
                           source.DataContext as ICommandProvider,
                       Model    = source.DataContext,
                       Realm    = (source.DataContext as IRealmed)?.Realm ?? Realm.None,
                       WorldKey = (source.DataContext as IRealmed)?.WorldKey,
                       Activate = ActivateOf(source),
                       LoadChildren =
                           lazy
                               ? () => source.LoadChildrenAsync()
                               : null
                   };

        self = node;

        var children = source.Children.ToList();
        for (int i = 0; i < children.Count; i++)
            node.Children.Add(Adapt(children[i], key, i, seen));

        if (lazy && children.Count == 0)
            node.Children.Add(new ExplorerNode(key + "/loading", "Loading...", "Placeholder"));

        return node;
    }

    /// <summary>
    ///     Stable across rebuilds (so expansion and selection survive): the node's URI if it has one, else its place in
    ///     the tree.
    /// </summary>
    private static string KeyOf(SiloNode source, string parentKey, int index, HashSet<string> seen)
    {
        Uri?   uri = source.Uri;
        string key = uri is null ? $"{parentKey}/{index}" : $"{parentKey}/{uri.OriginalString}";
        if (!seen.Add(key))
        {
            key = $"{parentKey}/{index}";
            seen.Add(key);
        }

        return key;
    }

    private static string HeaderOf(SiloNode source)
    {
        if (!string.IsNullOrWhiteSpace(source.Text)) return source.Text;

        return source.DataContext switch
               {
                   IConnection c => c.DisplayText is { Length: > 0 } name ? name : c.Connector.DisplayName,
                   ITool t => t.Header,
                   IDocumentContent { DocumentTitle: { Length: > 0 } title } => title,
                   var other => other?.ToString() is { Length: > 0 } text ? text : other?.GetType().Name ?? "Node"
               };
    }

    private static string KindOf(object? context)
    {
        return context switch
               {
                   IConnection          => "Connection",
                   INodeDetails details => details.TypeName,
                   ITool                => "Tool",
                   IDocumentContent     => "Document",
                   UIElement            => "Document",
                   null                 => "Node",
                   _                    => context.GetType().Name
               };
    }

    // a node's Icon is a URI; here a resource key (e.g. "Plug") written as a relative URI is understood, anything else falls back to the kind's icon
    private static string IconKeyOf(SiloNode source, string kind)
    {
        string? given = source.Icon is { IsAbsoluteUri: false } icon ? icon.OriginalString : null;
        if (!string.IsNullOrEmpty(given) && Application.Current?.TryFindResource(given) is Geometry) return given;

        return kind switch
               {
                   "Connection" => "Plug",
                   "Tool"       => "Settings",
                   _            => "Document"
               };
    }

    private static IEnumerable<(string, string)> DetailsOf(SiloNode source, string kind)
    {
        yield return ("Name", HeaderOf(source));
        yield return ("Kind", kind);

        if (source.DataContext is INodeDetails described)
        {
            foreach (var row in described.GetDetails()) yield return row;
            yield break;
        }

        if (source.DataContext is IConnection c)
        {
            yield return ("Connector", c.Connector.DisplayName);
            yield return ("State", c.ConnectionState switch { true => "Connected", false => "Disconnected", _ => "Connectionless" });
        }
        else if (source.DataContext is ITool t)
        {
            yield return ("Open", t.IsChecked ? "Yes" : "No");
        }

        if ((Uri?)source.Uri is { } uri) yield return ("URI", uri.OriginalString);
        if (source.Children.Count > 0) yield return ("Contains", source.Children.Count + " item(s)");
    }

    private static Action? ActivateOf(SiloNode source)
    {
        return source.DataContext switch
               {
                   ITool t => () => t.Toggle(),
                   _       => null
               };
    }

    private static IEnumerable<ExplorerAction> ActionsOf(SiloNode source)
    {
        switch (source.DataContext)
        {
            case ITool t:
                yield return new ExplorerAction(t.IsChecked ? "_Close" : "_Open", () => t.Toggle());
                break;
            case IConnection c:
                yield return new ExplorerAction("_Open window", () =>
                                                                {
                                                                    if (c.BuildWindow() is { } window) window.Show();
                                                                });
                break;
        }
    }
}