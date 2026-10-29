using System.Collections.Concurrent;
using Serilog;
using Silo.Connectors;
using Silo.Design;
using Silo.Ui.Panes;
using Silo.ViewModel;

namespace Silo.Explorer;

/// <summary>
///     Re-resolves the URI of each node the Explorer loads (the way the node would be found again from its URI: some
///     connector must handle the scheme and accept the rest, and the place must still be there) and, when that fails,
///     puts a <see cref="ProblemVisibility.Dev" /> warning in the Problems pane. Checks run on the thread pool, only at the
///     Developer design level, and once per URI until the node goes (or is found to resolve).
/// </summary>
public static class NodeUriCheck
{
    /// <summary>Owns the warnings in <see cref="ProblemList" />.</summary>
    private static readonly object Owner = new();

    /// <summary>The warning for each node key whose URI did not resolve.</summary>
    private static readonly ConcurrentDictionary<string, Problem> Failures = new(StringComparer.Ordinal);

    /// <summary>The URI last checked for each node key.</summary>
    private static readonly ConcurrentDictionary<string, string> Checked = new(StringComparer.Ordinal);

    /// <summary>Why the node's URI cannot be resolved; null when it can (or the node has none).</summary>
    public static string? Unresolvable(Uri? uri)
    {
        if (uri is null) return null;

        try
        {
            var connectors = ConnectorCatalog.Connectors();
            if (connectors.Count == 0) return null; // the container is not up yet: nothing to say

            var check = UriRules.CheckUri(uri, ConnectorCatalog.UriConnectors(connectors), UriRules.AnyScope);
            if (check.IsValid || check.Connector is null && ConnectionRestore.FindConnector(uri, connectors) is not null) return null;

            return check.Message;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Queues the check of a node just loaded into the tree (does nothing below the Developer design level).</summary>
    public static void Queue(string key, SiloNode node)
    {
        if (node.Uri is not { } uri || !AppDesignLevel.Instance.IsAtLeast(DesignLevel.Developer)) return;

        string text = uri.OriginalString;
        if (Checked.TryGetValue(key, out var before) && before == text) return;

        Checked[key] = text;
        string title = string.IsNullOrWhiteSpace(node.Text) ? text : node.Text;

        _ = Task.Run(() =>
                     {
                         string? why = Unresolvable(uri);
                         if (why is null)
                         {
                             if (Failures.TryRemove(key, out _)) Publish();
                             return;
                         }

                         Log.Debug("Node {Key} loaded with a URI that does not resolve: {Uri} ({Why})", key, text, why);
                         Failures[key] = new Problem(ProblemSeverity.Warning,
                                                     $"\"{title}\" was loaded with a URI that cannot be resolved ({text}): {why}",
                                                     "Explorer", null, null, "Node URI", null, ProblemVisibility.Dev);
                         Publish();
                     });
    }

    private static void Publish()
    {
        var problems = Failures.Values.OrderBy(p => p.Message, StringComparer.CurrentCulture).ToList();
        if (Application.Current?.Dispatcher is not { } dispatcher) return;

        dispatcher.BeginInvoke(new Action(() => ProblemList.Publish(Owner, problems)));
    }

    /// <summary>Forgets the results (the tree was rebuilt from scratch, so every node is checked again).</summary>
    public static void Reset()
    {
        Checked.Clear();
        if (Failures.IsEmpty) return;

        Failures.Clear();
        Publish();
    }
}
