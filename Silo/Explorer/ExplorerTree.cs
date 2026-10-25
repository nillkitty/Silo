using Serilog;
using Silo.Bindings;
using Silo.DbModel.Worlds;
using Silo.Design;
using Silo.Drawio;
using Silo.Model;
using Silo.Providers.Definitions;
using Silo.Tabular;
using Silo.Typing;
using Silo.Views;
using AuthoredTable = Silo.DbModel.Global.AuthoredTable;
using DataBinding = Silo.DbModel.Global.DataBinding;
using Diagram = Silo.DbModel.Global.Diagram;
using SyntaxKind = Silo.DbModel.Meta.SyntaxKind;

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

    /// <summary>
    ///     The keys of the document tabs the "Enums" and "Syntaxes" lists share between the Explorer and the navigation
    ///     flyout.
    /// </summary>
    public const string EnumsDocKey = "view:Enums";

    public const string SyntaxesDocKey = "view:Syntaxes";

    public static IReadOnlyList<ExplorerNode> Build(OpenSilo silo)
    {
        // whatever connections, tools and documents contribute through INodeProvider / ConnectContext
        var provided = ProvidedNodes.For(silo).ToList();

        // the Silo's own node (what it keeps: saved characters, strings, shortcuts, ...) comes first, so it is the first thing seen
        var own   = provided.Where(n => n.Kind == "Silo").ToList();
        var roots = new List<ExplorerNode>(own) { BuildLocal(silo) };
        if (BuildDiagrams(silo) is { } diagrams) roots.Add(diagrams);
        if (BuildDataBindings(silo) is { } bindings) roots.Add(bindings);
        if (BuildTypes(silo) is { } types) roots.Add(types);
        roots.AddRange(BuildConnections(silo));

        roots.AddRange(provided.Except(own));
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
                         Details = () =>
                                   [
                                       ("Contains",
                                        "Tables typed or pasted into this Silo"),
                                       ("World", "Local")
                                   ],
                         Actions = () =>
                                   [
                                       new
                                           ExplorerAction("New _rich text table",
                                                          () =>
                                                              TableEditors
                                                                 .CreateAndOpen(silo,
                                                                                TableTemplate
                                                                                   .RichText)),
                                       new
                                           ExplorerAction("New _plain table",
                                                          () =>
                                                              TableEditors
                                                                 .CreateAndOpen(silo,
                                                                                TableTemplate
                                                                                   .PlainText)),
                                       new
                                           ExplorerAction("New _typed table",
                                                          () =>
                                                              TableEditors
                                                                 .CreateAndOpen(silo,
                                                                                TableTemplate
                                                                                   .Typed)),
                                       new
                                           ExplorerAction("New table from _clipboard...",
                                                          () =>
                                                              TablePaste
                                                                 .AsNewTable(silo))
                                   ]
                     };

        if (silo.Data is ITableStore store)
            foreach (var table in store.ListTables())
                tables.Children.Add(BuildTable(silo, table));

        var local = new ExplorerNode("local", "Local", "World", "Database5")
                    {
                        Realm           = Realm.World, WorldKey = LocalWorld.Key,
                        ExpandByDefault = true,
                        Details = () =>
                                  [
                                      ("Key", LocalWorld.Key),
                                      ("Provider", LocalWorld.Provider),
                                      ("Authority",
                                       "Yes: the Silo holds the true copy; nothing is synchronised from elsewhere"),
                                      ("Holds",
                                       "Data created or pasted into this Silo")
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
                   Realm  = Realm.World, WorldKey = LocalWorld.Key,
                   DocKey = TableEditors.DocKey(silo, id),
                   Open =
                       () =>
                           TableEditors.BuildDocument(silo, id, false),
                   Closed  = TableEditors.Release,
                   Stamp   = table.Modified.Ticks.ToString(),
                   Details = () => TableDetails(silo, id),
                   Actions = () =>
                             [
                                 new ExplorerAction("_Open",
                                                    () =>
                                                        TableEditors
                                                           .OpenExisting(silo,
                                                                         id)),
                                 new
                                     ExplorerAction("Open in a new _window",
                                                    () =>
                                                        TableEditors
                                                           .OpenInWindow(silo,
                                                                         id)),
                                 new ExplorerAction("_Duplicate",
                                                    () =>
                                                        TableEditors
                                                           .Duplicate(silo,
                                                                      id)),
                                 new ExplorerAction("D_elete...",
                                                    () =>
                                                        TableEditors
                                                           .Delete(silo,
                                                                   id))
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
            Log.Warning(ex, "Listing the diagrams for the Explorer failed");
            list = [];
        }

        var folder = new ExplorerNode("diagrams", "Diagrams", "Folder", "FlowDiagram")
                     {
                         ExpandByDefault = true,
                         Details = () =>
                                   [
                                       ("Contains",
                                        "Diagrams drawn in this Silo (draw.io)"),
                                       ("Count",
                                        list.Count.ToString())
                                   ],
                         Actions = () =>
                                   [
                                       new
                                           ExplorerAction("_New diagram...",
                                                          () =>
                                                              DiagramActions
                                                                 .New(silo))
                                   ]
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
                   Realm = Realm.Global,
                   Stamp = diagram.Modified.Ticks.ToString(),
                   Activate =
                       () => DiagramActions.Open(silo, id),
                   Details = () => DiagramDetails(store, id),
                   Actions = () =>
                             [
                                 new
                                     ExplorerAction("_Open",
                                                    () =>
                                                        DiagramActions
                                                           .Open(silo,
                                                                 id)),
                                 new
                                     ExplorerAction("_Rename...",
                                                    () =>
                                                        DiagramActions
                                                           .Rename(silo,
                                                                   id)),
                                 new
                                     ExplorerAction("D_elete...",
                                                    () =>
                                                        DiagramActions
                                                           .Delete(silo,
                                                                   id))
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

    // ---- data bindings -----------------------------------------------------------------------------

    /// <summary>
    ///     The live values the Silo samples (performance counters and the like), shown in diagrams as <c>{bind:Name}</c>; null
    ///     when the Silo
    ///     cannot hold them.
    /// </summary>
    private static ExplorerNode? BuildDataBindings(OpenSilo silo)
    {
        if (silo.Data is not IDataBindingStore store) return null;

        IReadOnlyList<DataBinding> list;
        try
        {
            list = store.ListDataBindings();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing the data bindings for the Explorer failed");
            list = [];
        }

        var folder = new ExplorerNode("bindings", "Data bindings", "Folder", "DataStructure")
                     {
                         Details = () =>
                                   [
                                       ("Contains",
                                        "Live values sampled while the Silo is open"),
                                       ("Use",
                                        "Put {bind:Name} in a diagram cell's silo-label (Edit Data)"),
                                       ("Count",
                                        list.Count
                                            .ToString())
                                   ],
                         Actions = () =>
                                   [
                                       new
                                           ExplorerAction("_New data binding...",
                                                          () =>
                                                              DataBindingActions
                                                                 .New(silo))
                                   ]
                     };

        foreach (var binding in list)
            folder.Children.Add(BuildDataBinding(silo, store, binding));

        return folder;
    }

    private static ExplorerNode BuildDataBinding(OpenSilo silo, IDataBindingStore store, DataBinding binding)
    {
        int    id   = binding.Id;
        string name = binding.Name;
        return new ExplorerNode($"bindings/{id}", name, "DataBinding", "Data1")
               {
                   Realm    = Realm.Global,
                   Stamp    = binding.Modified.Ticks.ToString(),
                   Activate = () => DataBindingActions.Edit(silo, id),
                   Details  = () => DataBindingDetails(store, id),
                   Actions = () =>
                             [
                                 new ExplorerAction("_Edit...",
                                                    () =>
                                                        DataBindingActions
                                                           .Edit(silo,
                                                                 id)),
                                 new ExplorerAction("_Copy token",
                                                    () =>
                                                        DataBindingActions
                                                           .CopyToken(name)),
                                 new ExplorerAction("D_elete...",
                                                    () =>
                                                        DataBindingActions
                                                           .Delete(silo,
                                                                   id))
                             ]
               };
    }

    private static IEnumerable<(string, string)> DataBindingDetails(IDataBindingStore store, int id)
    {
        var b = store.GetDataBinding(id);
        if (b is null)
        {
            yield return ("Data binding", "No longer exists");
            yield break;
        }

        yield return ("Name", b.Name);
        yield return ("Token", "{bind:" + b.Name + "}");
        yield return ("Source", b.Source);
        yield return ("Every", b.Interval.TotalSeconds.ToString("0.###") + " s");
        yield return ("Sampling", b.Enabled ? "While the Silo is open" : "Off");
        if (DataBindingService.Current.TryGetSample(b.Name, out var sample))
        {
            yield return (sample.Error is null ? "Value" : "Error", sample.Error ?? sample.Value?.ToString() ?? "(nothing)");
            yield return ("Read at", sample.Time.ToLocalTime().ToString("T"));
        }

        if (b.Description is { Length: > 0 } d) yield return ("Description", d);
    }

    // ---- enums and syntaxes ------------------------------------------------------------------------

    /// <summary>
    ///     The types defined in the Silo itself: its enums (<c>enum:Name</c>) and its syntaxes (<c>syntax:Name</c>). Like the
    ///     diagrams they belong
    ///     to the Silo rather than to a world; null when the Silo can hold neither.
    /// </summary>
    private static ExplorerNode? BuildTypes(OpenSilo silo)
    {
        var folders = new List<ExplorerNode>();
        if (silo.Data is IEnumStore enums) folders.Add(BuildEnums(silo, enums));
        if (silo.Data is ISyntaxStore syntaxes) folders.Add(BuildSyntaxes(silo, syntaxes));
        if (folders.Count == 0) return null;

        var types = new ExplorerNode("types", "Types", "Folder", "DataModel")
                    {
                        ExpandByDefault = true,
                        Details = () =>
                                  [
                                      ("Contains",
                                       "Enums and syntaxes defined in this Silo"),
                                      ("Referred to as",
                                       "enum:Name and syntax:Name")
                                  ]
                    };
        foreach (var folder in folders) types.Children.Add(folder);
        return types;
    }

    private static ExplorerNode BuildEnums(OpenSilo silo, IEnumStore store)
    {
        IReadOnlyList<EnumRecord> list;
        try
        {
            list = store.GetEnums();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing the enums for the Explorer failed");
            list = [];
        }

        var folder = new ExplorerNode("types/enums", "Enums", "Folder", "Checklist")
                     {
                         ExpandByDefault = true,
                         DocKey          = EnumsDocKey,
                         Open            = () => new EnumsView(),
                         Details = () =>
                                   [
                                       ("Contains",
                                        "Enums defined in this Silo: named sets of known values"),
                                       ("Count",
                                        list.Count.ToString()),
                                       ("Referred to as", "enum:Name")
                                   ],
                         Actions = () =>
                                   [
                                       new
                                           ExplorerAction("_New enum...",
                                                          () =>
                                                              DefinitionActions
                                                                 .NewEnum(silo))
                                   ]
                     };

        foreach (var e in list) folder.Children.Add(BuildEnum(silo, e));
        return folder;
    }

    private static ExplorerNode BuildEnum(OpenSilo silo, EnumRecord e)
    {
        int id = e.Id;
        var node = new ExplorerNode($"types/enums/{id}", e.Name, "Enum", "Checklist")
                   {
                       Realm = Realm.Meta,
                       Stamp = DefinitionText.Stamp(e),
                       Activate =
                           () => DefinitionActions.EditEnum(silo, id),
                       Details = () => EnumDetails(e),
                       Actions = () =>
                                 [
                                     new ExplorerAction("_Edit...",
                                                        () =>
                                                            DefinitionActions
                                                               .EditEnum(silo,
                                                                         id)),
                                     new
                                         ExplorerAction("_Add value...",
                                                        () =>
                                                            DefinitionActions
                                                               .AddValue(silo,
                                                                         id)),
                                     new
                                         ExplorerAction("D_elete...",
                                                        () =>
                                                            DefinitionActions
                                                               .DeleteEnum(silo,
                                                                           id))
                                 ]
                   };

        foreach (var v in e.Values) node.Children.Add(BuildEnumValue(silo, e, v));
        return node;
    }

    private static ExplorerNode BuildEnumValue(OpenSilo silo, EnumRecord e, EnumValueRecord v)
    {
        int enumId = e.Id, valueId = v.Id;
        return new ExplorerNode($"types/enums/{enumId}/{valueId}", DefinitionText.Label(v), "Enum value", "Record")
               {
                   Realm = Realm.Meta,
                   Stamp =
                       v.Name  + "|" +
                       v.Value + "|" +
                       v.Description,
                   Activate =
                       () =>
                           DefinitionActions
                              .EditValue(silo,
                                         enumId,
                                         valueId),
                   Details = () =>
                             [
                                 ("Label",
                                  v.Name),
                                 ("Value",
                                  v.Value ??
                                  string
                                     .Empty),
                                 ("Enum",
                                  e.Name),
                                 ("Type",
                                  e.Type
                                   .ToString()),
                                 ("Note",
                                  v.Description ??
                                  string
                                     .Empty)
                             ],
                   Actions = () =>
                             [
                                 new
                                     ExplorerAction("_Edit...",
                                                    () =>
                                                        DefinitionActions
                                                           .EditValue(silo,
                                                                      enumId,
                                                                      valueId)),
                                 new
                                     ExplorerAction("D_elete...",
                                                    () =>
                                                        DefinitionActions
                                                           .DeleteValue(silo,
                                                                        enumId,
                                                                        valueId))
                             ]
               };
    }

    private static IEnumerable<(string, string)> EnumDetails(EnumRecord e)
    {
        yield return ("Name", e.Name);
        yield return ("Referred to as", "enum:" + e.Name);
        yield return ("Values are", DefinitionText.Describe(e.Type));
        if (DefinitionText.Describe(e.Flags) is { Length: > 0 } flags) yield return ("Flags", flags);
        if (!string.IsNullOrEmpty(e.Description)) yield return ("Description", e.Description);
        yield return ("Known values", e.Values.Count.ToString());
        foreach (var v in e.Values) yield return ("  " + v.Name, v.Value ?? string.Empty);
    }

    private static ExplorerNode BuildSyntaxes(OpenSilo silo, ISyntaxStore store)
    {
        IReadOnlyList<SyntaxRecord> list;
        try
        {
            list = store.GetSyntaxes();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing the syntaxes for the Explorer failed");
            list = [];
        }

        var folder = new ExplorerNode("types/syntaxes", "Syntaxes", "Folder", "Code")
                     {
                         ExpandByDefault = false,
                         DocKey          = SyntaxesDocKey,
                         Open            = () => new SyntaxesView(),
                         Details = () =>
                                   [
                                       ("Contains",
                                        "Syntaxes defined in this Silo: rules for checking a string and turning it into an object"),
                                       ("Count",
                                        list.Count.ToString()),
                                       ("Referred to as",
                                        "syntax:Name")
                                   ],
                         Actions = () =>
                                   [
                                       new
                                           ExplorerAction("_New syntax...",
                                                          () =>
                                                              DefinitionActions
                                                                 .NewSyntax(silo)),
                                       new
                                           ExplorerAction("_Restore predefined syntaxes",
                                                          () =>
                                                              DefinitionActions
                                                                 .RestoreDefaults(silo))
                                   ]
                     };

        foreach (var s in list) folder.Children.Add(BuildSyntax(silo, s));
        return folder;
    }

    private static ExplorerNode BuildSyntax(OpenSilo silo, SyntaxRecord s)
    {
        int id = s.Id;
        return new ExplorerNode($"types/syntaxes/{id}", DefinitionText.NameWithAlias(s), "Syntax", "Code")
               {
                   Realm = Realm.Meta,
                   Stamp =
                       DefinitionText.Stamp(s),
                   Activate =
                       () => DefinitionActions
                          .EditSyntax(silo, id),
                   Details =
                       () => SyntaxDetails(s),
                   Actions = () =>
                             [
                                 new
                                     ExplorerAction("_Edit...",
                                                    () =>
                                                        DefinitionActions
                                                           .EditSyntax(silo,
                                                                       id)),
                                 new
                                     ExplorerAction("D_elete...",
                                                    () =>
                                                        DefinitionActions
                                                           .DeleteSyntax(silo,
                                                                         id))
                             ]
               };
    }

    private static IEnumerable<(string, string)> SyntaxDetails(SyntaxRecord s)
    {
        yield return ("Name", s.Name);
        if (!string.IsNullOrEmpty(s.Alias)) yield return ("Alias", s.Alias);
        yield return ("Referred to as", "syntax:" + s.Name + (string.IsNullOrEmpty(s.Alias) ? string.Empty : " or syntax:" + s.Alias));
        yield return ("Built on", DefinitionText.Source(s));
        if (s.Kind == SyntaxKind.Pattern && s.Pattern is not null) yield return ("Pattern", s.Pattern);
        if (s.Kind == SyntaxKind.Parser  && SyntaxParsers.Find(s.Parser) is { } parser) yield return ("Becomes", parser.ValueType.Name);
        if (!string.IsNullOrEmpty(s.Description)) yield return ("Description", s.Description);
        if (!string.IsNullOrEmpty(s.Example)) yield return ("Example", s.Example);
        if (s.IsSeeded) yield return ("Origin", "Predefined (came with Silo)");
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
            if (c.Node is { } own           && silo.Nodes.Contains(own)) continue;

            string  name = c.Connector.DisplayName;
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
                                           ("State", c.ConnectionState switch
                                                     {
                                                         true => "Connected", false => "Disconnected", _ => "Connectionless"
                                                     }),
                                           ("Types and objects", "Will be listed here as the connector reports them")
                                       ]
                         };
        }
    }
}