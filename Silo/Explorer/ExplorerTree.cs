using System.Linq;
using AuthoredTable = Silo.DbModel.Global.AuthoredTable;
using Diagram = Silo.DbModel.Global.Diagram;
using Silo.Drawio;
using Silo.DbModel.Worlds;
using Silo.Model;
using Silo.Tabular;
using Silo.Views;

namespace Silo.Explorer;

/// <summary>
///     Builds the Explorer tree for a Silo. The Silo is the single home for everything a person keeps: its top-level nodes
///     are the worlds the data comes from -- the built-in Local world (data authored here, the authoritative copy) and one
///     node per connection -- so the tree is a map of where the Silo's data lives.
/// </summary>
public static class ExplorerTree
{
    /// <summary>The key of the document tab the "Tables" list shares between the Explorer and the navigation flyout.</summary>
    public const string TablesDocKey = "view:Tables";

    public static IReadOnlyList<ExplorerNode> Build(OpenSilo silo)
    {
        var roots = new List<ExplorerNode> { BuildLocal(silo) };
        if (BuildDiagrams(silo) is { } diagrams) roots.Add(diagrams);
        roots.AddRange(BuildConnections(silo));

        // whatever connections, tools and documents contribute through INodeProvider / ConnectContext
        roots.AddRange(ProvidedNodes.For(silo));
        return roots;
    }

    // ---- the local world ---------------------------------------------------------------------------

    private static ExplorerNode BuildLocal(OpenSilo silo)
    {
        var tables = new ExplorerNode("local/tables", "Tables", "Folder", "DataStructure")
                     {
                         ExpandByDefault = true,
                         DocKey          = TablesDocKey,
                         Open            = () => new TablesView(),
                         Details         = () => [("Contains", "Tables typed or pasted into this Silo"), ("World", "Local")],
                         Actions = () =>
                                   [
                                       new ExplorerAction("New _rich text table", () => TableEditors.CreateAndOpen(silo, TableTemplate.RichText)),
                                       new ExplorerAction("New _plain table", () => TableEditors.CreateAndOpen(silo, TableTemplate.PlainText)),
                                       new ExplorerAction("New _typed table", () => TableEditors.CreateAndOpen(silo, TableTemplate.Typed)),
                                       new ExplorerAction("New table from _clipboard...", () => TablePaste.AsNewTable(silo))
                                   ]
                     };

        if (silo.Data is ITableStore store)
            foreach (var table in store.ListTables())
                tables.Children.Add(BuildTable(silo, table));

        var local = new ExplorerNode("local", "Local", "World", "Database5")
                    {
                        ExpandByDefault = true,
                        Details = () =>
                                  [
                                      ("Key", LocalWorld.Key),
                                      ("Provider", LocalWorld.Provider),
                                      ("Authority", "Yes: the Silo holds the true copy; nothing is synchronised from elsewhere"),
                                      ("Holds", "Data created or pasted into this Silo")
                                  ]
                    };
        local.Children.Add(tables);
        return local;
    }

    private static ExplorerNode BuildTable(OpenSilo silo, AuthoredTable table)
    {
        int id = table.Id;
        return new ExplorerNode($"local/tables/{id}", table.Name, "Table", "Data1")
               {
                   DocKey  = TableEditors.DocKey(silo, id),
                   Open    = () => TableEditors.BuildDocument(silo, id, false),
                   Closed  = TableEditors.Release,
                   Stamp   = table.Modified.Ticks.ToString(),
                   Details = () => TableDetails(silo, id),
                   Actions = () =>
                             [
                                 new ExplorerAction("_Open", () => TableEditors.OpenExisting(silo, id)),
                                 new ExplorerAction("Open in a new _window", () => TableEditors.OpenInWindow(silo, id)),
                                 new ExplorerAction("_Duplicate", () => TableEditors.Duplicate(silo, id)),
                                 new ExplorerAction("D_elete...", () => TableEditors.Delete(silo, id))
                             ]
               };
    }

    private static IEnumerable<(string, string)> TableDetails(OpenSilo silo, int id)
    {
        var table = (silo.Data as ITableStore)?.GetTable(id);
        if (table is null)
        {
            yield return ("Table", "No longer exists");
            yield break;
        }

        var sheet = table.ReadSheet();
        yield return ("Name", table.Name);
        yield return ("World", "Local (authoritative)");
        yield return ("Rows", sheet.Rows.Count.ToString());
        yield return ("Columns", sheet.Columns.Count.ToString());
        foreach (var c in sheet.Columns)
            yield return ("  " + c.Name, c.Type.ToString());
        yield return ("Created", table.Created.ToLocalTime().ToString("g"));
        yield return ("Modified", table.Modified.ToLocalTime().ToString("g"));
    }

    // ---- diagrams ----------------------------------------------------------------------------------

    /// <summary>
    ///     The diagrams stored in the Silo (draw.io sketches, notes, models). They belong to the Silo itself rather than to a
    ///     world, so they get a node of their own; null when the Silo cannot hold diagrams.
    /// </summary>
    private static ExplorerNode? BuildDiagrams(OpenSilo silo)
    {
        if (silo.Data is not IDiagramStore store) return null;

        IReadOnlyList<Diagram> list;
        try
        {
            list = store.ListDiagrams();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Listing the diagrams for the Explorer failed");
            list = [];
        }

        var folder = new ExplorerNode("diagrams", "Diagrams", "Folder", "FlowDiagram")
                     {
                         ExpandByDefault = true,
                         Details         = () => [("Contains", "Diagrams drawn in this Silo (draw.io)"), ("Count", list.Count.ToString())],
                         Actions         = () => [new ExplorerAction("_New diagram...", () => DiagramActions.New(silo))]
                     };

        foreach (var diagram in list.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            folder.Children.Add(BuildDiagram(silo, store, diagram));

        return folder;
    }

    private static ExplorerNode BuildDiagram(OpenSilo silo, IDiagramStore store, Diagram diagram)
    {
        int id = diagram.Id;
        return new ExplorerNode($"diagrams/{id}", diagram.Name, "Diagram", "FlowDiagram")
               {
                   Stamp    = diagram.Modified.Ticks.ToString(),
                   Activate = () => DiagramActions.Open(silo, id),
                   Details  = () => DiagramDetails(store, id),
                   Actions = () =>
                             [
                                 new ExplorerAction("_Open", () => DiagramActions.Open(silo, id)),
                                 new ExplorerAction("_Rename...", () => DiagramActions.Rename(silo, id)),
                                 new ExplorerAction("D_elete...", () => DiagramActions.Delete(silo, id))
                             ]
               };
    }

    private static IEnumerable<(string, string)> DiagramDetails(IDiagramStore store, int id)
    {
        var diagram = store.GetDiagram(id);
        if (diagram is null)
        {
            yield return ("Diagram", "No longer exists");
            yield break;
        }

        yield return ("Name", diagram.Name);
        yield return ("Kind", diagram.Kind);
        if (diagram.Profile.Length > 0) yield return ("Profile", diagram.Profile);
        yield return ("Opens in", "The Diagram Canvas (double-click)");
        yield return ("Created", diagram.Created.ToLocalTime().ToString("g"));
        yield return ("Modified", diagram.Modified.ToLocalTime().ToString("g"));
    }

    // ---- connections -------------------------------------------------------------------------------

    private static IEnumerable<ExplorerNode> BuildConnections(OpenSilo silo)
    {
        int i = 0;
        foreach (var connection in silo.Connections.ToList())
        {
            var c = connection;

            // a connection that put its own node in the tree (INodeProvider, or ConnectContext.CreateNode) is shown by that node
            if (c is INodeProvider provider && silo.Nodes.Provides(provider)) continue;
            if (c.Node is { } own && silo.Nodes.Contains(own)) continue;

            string name = c.Connector.DisplayName;
            string? host = null;
            try
            {
                host = c.GetHostname();
            }
            catch
            {
                /* a connection that cannot say where it points is shown by its connector only */
            }

            string shown = c.DisplayText is { Length: > 0 } given ? given : host ?? string.Empty;

            yield return new ExplorerNode($"conn/{i++}", string.IsNullOrWhiteSpace(shown) ? name : $"{name}: {shown}", "Connection", "Plug")
                         {
                             Details = () =>
                                       [
                                           ("Connector", name),
                                           ("Destination", string.IsNullOrWhiteSpace(shown) ? "(none)" : shown),
                                           ("State", c.ConnectionState switch { true => "Connected", false => "Disconnected", _ => "Connectionless" }),
                                           ("Types and objects", "Will be listed here as the connector reports them")
                                       ]
                         };
        }
    }
}
