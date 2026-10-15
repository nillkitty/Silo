using System.Windows.Media;
using Silo.Views;

namespace Silo.Nav;

/// <summary>
///     One entry in the app's navigation structure: the label/icon/description shown for it,
///     the <see cref="AppView" /> it navigates to (<see langword="null" /> for a node that's
///     purely structural -- a group header like "Schema" or a not-yet-wired setting leaf), and
///     a back-link to its parent so a selected node's top-level group can be found by walking
///     up (see <c>MainWindow.xaml.cs</c>'s <c>NavTree_SelectedItemChanged</c>).
/// </summary>
/// <remarks>
///     <see cref="Icon" /> is a <see cref="Geometry" /> -- the same type <c>AppIcon.Icon</c>
///     (see <c>AppIcon.cs</c>) actually expects, since the icons themselves (<c>Resources/Paths.xaml</c>)
///     are <c>StreamGeometry</c> resources. An earlier draft of this record typed it as
///     <c>BitmapSource</c>, which would have silently failed to bind -- <c>AppIcon.Icon</c> is a
///     <c>Geometry</c>-typed dependency property, so a <c>BitmapSource</c>-typed source value
///     can never resolve to it; the binding would just fail quietly and no icon would ever
///     render.
/// </remarks>
public record AppNavItem(string Text, Geometry? Icon, string Description, AppView? View, AppNavItem? Parent = null);

/// <summary>
///     Builds the app's navigation tree -- the single source of truth for both MainWindow's
///     NavTree (the full expandable tree) and NavRail (the collapsed pane's one-icon-per-root
///     gutter). Replaces what used to be hand-written <c>TreeViewItem</c> XAML per nav entry:
///     MainWindow.xaml now has one recursive <c>HierarchicalDataTemplate</c>, and this class is
///     the only place the actual structure (what's under Schema, what's under Settings, which
///     leaf opens which view) is written down.
/// </summary>
/// <remarks>
///     Every leaf below either has a real <see cref="AppView" /> -- built from a
///     <c>Views/PlaceholderView</c> or <c>Views/EntityListView</c> stand-in where the app
///     doesn't have the real feature yet -- or, where the nav structure diagram draws no view
///     for it (most Settings leaves, a handful of Schema/Connections entries), a
///     <see langword="null" /> one; selecting it just leaves whatever content was already
///     showing rather than clearing it to something misleading. The nine or so "ListView of X"
///     boxes in that diagram (worlds, data sources, meta types/fields/indexes, connection
///     providers/destinations/credentials) all go through one reusable
///     <see cref="EntityListView" /> rather than nine near-duplicate view classes; none of them
///     are wired to a real data source yet (see <see cref="EntityListView" />'s own remarks), so
///     each is built with just its entity name for now.
/// </remarks>
public static class AppNavTree
{
    // The tree is not one fixed list any more: what sits at the top depends on which Silos are open (see
    // MainWindow.xaml.cs's SyncNav), so the pieces are built separately. Inbox, Settings and Profile do not depend on
    // a Silo and are kept apart, pinned to the bottom of the pane.

    /// <summary>The only entry at the top while no Silo is open.</summary>
    public static AppNavNode BuildRecent()
    {
        return _leaf("Recent", "History", null, "Silos opened recently",
                     _view("Recent Silos", () => new RecentSilosView()));
    }

    /// <summary>One open Silo: its own views, in the order of the navigation design.</summary>
    public static AppNavNode BuildSilo()
    {
        return _group("Silo", "DataModel", null, silo => new[]
                                                         {
                                                             _leaf("Search", "Search", silo,
                                                                   view: _view("Search", () => new PlaceholderView("Search"))),
                                                             _leaf("Universe", "Nodes", silo,
                                                                   view: _view("Universe", () => new PlaceholderView("Universe"))),

                                                             // Gets a child per world once worlds can be listed; the node's Children are observable.
                                                             _leaf("Worlds", "Layers", silo,
                                                                   view: _view("Worlds", () => new EntityListView("World"))),
                                                             _leaf("Data Sources", "Database6", silo,
                                                                   view: _view("Data Sources",
                                                                               () => new EntityListView("IDataSource"))),
                                                             _leaf("Tables", "Data1", silo, "Tables typed into this Silo",
                                                                   view: _view("Tables", () => new TablesView())),
                                                             _leaf("Visualization", "MindMap", silo,
                                                                   view: _view("Visualization",
                                                                               () => new PlaceholderView("Visualization"))),
                                                             _leaf("Analysis", "Analytics", silo,
                                                                   view: _view("Analysis", () => new PlaceholderView("Analysis"))),
                                                             _leaf("Monitoring", "Analytics1", silo,
                                                                   view: _view("Monitoring", () => new PlaceholderView("Monitoring"))),
                                                             _buildSchema(silo)
                                                         });
    }

    public static AppNavNode BuildPerformance()
    {
        return _leaf("Performance", "Power", null, view: _view("Performance", () => new PlaceholderView("Performance")));
    }

    /// <summary>Inbox, Settings and Profile: always present, shown at the bottom of the pane.</summary>
    public static IReadOnlyList<AppNavNode> BuildBottomRoots()
    {
        return new[]
               {
                   _leaf("Inbox", "Inbox", null, view: _view("Notifications & Alerts", () => new PlaceholderView("Notifications & Alerts"))),
                   BuildSettings(),
                   _leaf("Profile", "FaceRecognition", null, view: _view("Profile", () => new PlaceholderView("Profile")))
               };
    }

    /// <summary>
    ///     Schema's own second-level flyout (see the structure diagram's "Schema" panel).
    ///     Schema itself has no view -- selecting it just opens/shows this group, the same way
    ///     "Connections" and "Settings" don't navigate anywhere on their own either.
    /// </summary>
    private static AppNavNode _buildSchema(AppNavItem siloParent)
    {
        return _group("Schema", "FlowDiagram", siloParent, schema => new[]
                                                                     {
                                                                         _leaf("Internal", "Document", schema),

                                                                         // Shares its view with Connections > Connector Space below -- both arrows in the
                                                                         // structure diagram point at the same "Connector Space View" box.
                                                                         _group("World Space", "Database5", schema,
                                                                                connectorSpaceSchema => new[]
                                                                                                        {
                                                                                                            _leaf("CONTOSO",
                                                                                                                  "Server1",
                                                                                                                  connectorSpaceSchema),
                                                                                                            _leaf("TAILSPIN",
                                                                                                                  "Server1",
                                                                                                                  connectorSpaceSchema),
                                                                                                            _leaf("OneContoso",
                                                                                                                  "Server1",
                                                                                                                  connectorSpaceSchema),
                                                                                                            _leaf("OKTA",
                                                                                                                  "Server1",
                                                                                                                  connectorSpaceSchema)
                                                                                                        },
                                                                                view: _view("World Space",
                                                                                            () =>
                                                                                                new PlaceholderView("World Space"))),

                                                                         _group("Metaverse", "DataModelling", schema, meta => new[]
                                                                                                                              {
                                                                                                                                  _leaf("Types",
                                                                                                                                        "HierarchyStructure",
                                                                                                                                        meta,
                                                                                                                                        view
                                                                                                                                        : _view("Types",
                                                                                                                                                () =>
                                                                                                                                                    new
                                                                                                                                                        EntityListView("MetaType"))),
                                                                                                                                  _leaf("Fields",
                                                                                                                                        "DataStructure",
                                                                                                                                        meta,
                                                                                                                                        view
                                                                                                                                        : _view("Fields",
                                                                                                                                                () =>
                                                                                                                                                    new
                                                                                                                                                        EntityListView("MetaField"))),
                                                                                                                                  _leaf("Indexes",
                                                                                                                                        "DataSearching",
                                                                                                                                        meta,
                                                                                                                                        view
                                                                                                                                        : _view("Indexes",
                                                                                                                                                () =>
                                                                                                                                                    new
                                                                                                                                                        EntityListView("MetaIndex"))),
                                                                                                                                  _leaf("Lifecycle",
                                                                                                                                        "History",
                                                                                                                                        meta),
                                                                                                                                  _leaf("Sync",
                                                                                                                                        "Sync",
                                                                                                                                        meta,
                                                                                                                                        view
                                                                                                                                        : _view("Sync",
                                                                                                                                                () =>
                                                                                                                                                    new
                                                                                                                                                        EntityListView("MetaField")))
                                                                                                                              }),

                                                                         // None of Data Handling's leaves are wired to a view in the structure diagram --
                                                                         // purely structural for now.
                                                                         _group("Data Handling", "DataQuality", schema, dh => new[]
                                                                                                                              {
                                                                                                                                  _leaf("Data Shape",
                                                                                                                                        "Data1",
                                                                                                                                        dh),
                                                                                                                                  _leaf("Duck Typing",
                                                                                                                                        "Puzzle",
                                                                                                                                        dh),
                                                                                                                                  _leaf("Unstructured Data",
                                                                                                                                        "DataCleaning",
                                                                                                                                        dh),
                                                                                                                                  _leaf("Syntaxes",
                                                                                                                                        "Code",
                                                                                                                                        dh),
                                                                                                                                  _leaf("Units",
                                                                                                                                        "Checklist",
                                                                                                                                        dh),
                                                                                                                                  _leaf("Schema Style",
                                                                                                                                        "Design",
                                                                                                                                        dh),
                                                                                                                                  _leaf("Synonyms",
                                                                                                                                        "Link",
                                                                                                                                        dh),
                                                                                                                                  _leaf("Visualization",
                                                                                                                                        "MindMap1",
                                                                                                                                        dh)
                                                                                                                              })
                                                                     });
    }

    public static AppNavNode BuildConnections()
    {
        return _group("Connections", "Plug", null, connections => new[]
                                                                  {
                                                                      _leaf("Status", "Check", connections),
                                                                      _leaf("Providers", "Server", connections,
                                                                            view: _view("Providers",
                                                                                        () => new EntityListView("IProvider"))),
                                                                      _leaf("Destinations", "Destination", connections,
                                                                            view: _view("Destinations",
                                                                                        () => new EntityListView("IDestination"))),
                                                                      _leaf("Credentials", "Key", connections,
                                                                            view: _view("Credentials",
                                                                                        () => new EntityListView("ICredential"))),
                                                                      _leaf("Movements", "Transfer1", connections),
                                                                      _leaf("Jobs", "Folder17", connections,
                                                                            view: _view("Job Controller",
                                                                                        () => new PlaceholderView("Job Controller"))),
                                                                      _leaf("Connector Space", "Database5", connections,
                                                                            view: _view("Connector Space",
                                                                                        () => new PlaceholderView("Connector Space")))
                                                                  });
    }

    /// <summary>
    ///     Settings' own second-level flyout (the structure diagram's separate "Settings Nav"
    ///     panel). None of its leaves are wired to a view yet -- the diagram doesn't define their
    ///     content, only their grouping, so that's all this reproduces for now.
    /// </summary>
    public static AppNavNode BuildSettings()
    {
        return _group("Settings", "Settings", null, settings => new[]
                                                                {
                                                                    _group("Silo Settings", "Settings", settings, silo => new[]
                                                                                                                          {
                                                                                                                              _leaf("Assemblies",
                                                                                                                                    "Modules",
                                                                                                                                    silo),
                                                                                                                              _leaf("Branding",
                                                                                                                                    "Design",
                                                                                                                                    silo),
                                                                                                                              _leaf("Security/Scripting",
                                                                                                                                    "Shield",
                                                                                                                                    silo),
                                                                                                                              _leaf("Snapshots",
                                                                                                                                    "Record",
                                                                                                                                    silo),
                                                                                                                              _leaf("Backup/Restore",
                                                                                                                                    "Backup",
                                                                                                                                    silo),
                                                                                                                              _leaf("Providers",
                                                                                                                                    "Server",
                                                                                                                                    silo),
                                                                                                                              _leaf("Network & Energy",
                                                                                                                                    "Network",
                                                                                                                                    silo),
                                                                                                                              _leaf("Auditing & Undo",
                                                                                                                                    "Undo",
                                                                                                                                    silo),
                                                                                                                              _leaf("Data Transfer",
                                                                                                                                    "TransferData",
                                                                                                                                    silo),
                                                                                                                              _leaf("Startup Layout",
                                                                                                                                    "Layers",
                                                                                                                                    silo),
                                                                                                                              _leaf("Keyboard",
                                                                                                                                    "Command",
                                                                                                                                    silo)
                                                                                                                          }),
                                                                    _group("Owner Settings", "Key2", settings, owner => new[]
                                                                                                                        {
                                                                                                                            _leaf("Access Control",
                                                                                                                                  "Lock",
                                                                                                                                  owner),
                                                                                                                            _leaf("Encryption",
                                                                                                                                  "Padlock",
                                                                                                                                  owner)
                                                                                                                        }),
                                                                    _group("User Settings", "FaceRecognition", settings,
                                                                           user => new[]
                                                                                   {
                                                                                       _leaf("Developer Settings", "Coding",
                                                                                             user),
                                                                                       _leaf("Display Preferences", "Opacity",
                                                                                             user),
                                                                                       _leaf("Personalization", "Design", user)
                                                                                   }),
                                                                    _group("App Settings", "Settings", settings, app => new[]
                                                                                                                        {
                                                                                                                            _leaf("Auto-Update",
                                                                                                                                  "Refresh",
                                                                                                                                  app),
                                                                                                                            _leaf("Registered Handlers",
                                                                                                                                  "Link1",
                                                                                                                                  app)
                                                                                                                        })
                                                                });
    }

    // --- construction helpers -------------------------------------------------------------

    private static Geometry _icon(string resourceKey)
    {
        return (Geometry)Application.Current!.FindResource(resourceKey);
    }

    private static AppView _view(string name, Func<UIElement> contentBuilder)
    {
        return new SimpleAppView(name, contentBuilder);
    }

    /// <summary>Builds a childless node.</summary>
    private static AppNavNode _leaf(string text, string iconKey, AppNavItem? parent, string? description = null, AppView? view = null)
    {
        var item = new AppNavItem(text, _icon(iconKey), description ?? text, view, parent);
        return new AppNavNode(item);
    }

    /// <summary>
    ///     Builds a node with children. The item is constructed first -- before its children --
    ///     since each child needs it as <see cref="AppNavItem.Parent" />.
    /// </summary>
    private static AppNavNode _group(string  text, string iconKey, AppNavItem? parent, Func<AppNavItem, IReadOnlyList<AppNavNode>> children,
                                     string? description = null, AppView? view = null)
    {
        var item = new AppNavItem(text, _icon(iconKey), description ?? text, view, parent);
        return new AppNavNode(item, children(item));
    }
}