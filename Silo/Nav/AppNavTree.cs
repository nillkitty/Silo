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
///     doesn't have the real feature yet (every Settings section has a placeholder page) -- or, where the
///     nav structure diagram draws no view for it (a handful of Schema/Connections entries), a
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
                                                                                                                                        dh, "Syntaxes defined in this Silo", view: _view("Syntaxes", () => new SyntaxesView())),
                                                                                                                                  _leaf("Enums", "Modules", dh, "Enums defined in this Silo", view: _view("Enums", () => new EnumsView())),
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
                                                                                        () => new CredentialsView())),
                                                                      _leaf("Movements", "Transfer1", connections),
                                                                      _leaf("Jobs", "Folder17", connections,
                                                                            view: _view("Job Controller",
                                                                                        () => new PlaceholderView("Job Controller"))),
                                                                      _leaf("Connector Space", "Database5", connections,
                                                                            view: _view("Connector Space",
                                                                                        () => new PlaceholderView("Connector Space")))
                                                                  });
    }

    /// <summary>The text of the Settings root, which <c>MainWindow</c> treats specially (selecting it unfolds the whole branch).</summary>
    public const string SettingsText = "Settings";

    /// <summary>
    ///     Settings' own second-level flyout (the structure diagram's separate "Settings Nav" panel). The diagram defines
    ///     only the grouping, not the content, so every section (and every group) opens a placeholder page for now --
    ///     one document tab per section, keyed by its path so Settings ▸ Silo Settings ▸ Providers does not share a tab
    ///     with Connections ▸ Providers.
    /// </summary>
    public static AppNavNode BuildSettings()
    {
        return _settingsGroup(SettingsText, "Settings", null,
                              "Everything that can be set, grouped by whom it applies to: this Silo, its owner, you, and the app.",
                              settings => new[]
                                          {
                                              _settingsGroup("Silo Settings", "Settings", settings,
                                                             "Settings kept in the Silo itself, shared by everyone who opens it.",
                                                             silo => new[]
                                                                     {
                                                                         _setting("Assemblies", "Modules", silo,
                                                                                  "The assemblies (extensions) this Silo loads and carries with it."),
                                                                         _setting("Branding", "Design", silo,
                                                                                  "This Silo's name, icon and look as other people see it."),
                                                                         _setting("Security/Scripting", "Shield", silo,
                                                                                  "Which scripts and extensions in this Silo may run, and with what rights."),
                                                                         _setting("Snapshots", "Record", silo,
                                                                                  "Point-in-time snapshots of this Silo's data."),
                                                                         _setting("Backup/Restore", "Backup", silo,
                                                                                  "Backing this Silo up and restoring it from a backup."),
                                                                         _setting("Providers", "Server", silo,
                                                                                  "Which providers (data, type, format...) this Silo uses."),
                                                                         _setting("Network & Energy", "Network", silo,
                                                                                  "When this Silo may use the network and run background work (metered connections, battery)."),
                                                                         _setting("Auditing & Undo", "Undo", silo,
                                                                                  "What goes into the audit log, and how much undo history is kept."),
                                                                         _setting("Data Transfer", "TransferData", silo,
                                                                                  "Defaults for importing, exporting and moving data."),
                                                                         _setting("Startup Layout", "Layers", silo,
                                                                                  "What this Silo opens, and how its windows are arranged, when it starts."),
                                                                         _setting("Keyboard", "Command", silo,
                                                                                  "Keyboard shortcuts.")
                                                                     }),
                                              _settingsGroup("Owner Settings", "Key2", settings,
                                                             "Settings only the Silo's owner can change.",
                                                             owner => new[]
                                                                      {
                                                                          _setting("Access Control", "Lock", owner,
                                                                                   "Who may open this Silo, and what each person may do in it."),
                                                                          _setting("Encryption", "Padlock", owner,
                                                                                   "How this Silo's secrets are protected, and which key protector it uses.")
                                                                      }),
                                              _settingsGroup("User Settings", "FaceRecognition", settings,
                                                             "Your own settings: they follow you, not the Silo.",
                                                             user => new[]
                                                                     {
                                                                         _setting("Developer Settings", "Coding", user,
                                                                                  "Options for developing and debugging extensions."),
                                                                         _setting("Display Preferences", "Opacity", user,
                                                                                  "Theme, icon size and how things are shown."),
                                                                         _setting("Personalization", "Design", user,
                                                                                  "Your names, labels and layout customisations.")
                                                                     }),
                                              _settingsGroup("App Settings", "Settings", settings,
                                                             "Settings of the Silo app on this computer.",
                                                             app => new[]
                                                                    {
                                                                        _setting("Auto-Update", "Refresh", app,
                                                                                 "How and when the app updates itself."),
                                                                        _setting("Registered Handlers", "Link1", app,
                                                                                 "The file types and URI schemes the app is registered to open.")
                                                                    })
                                          });
    }

    /// <summary>A Settings section: a leaf whose placeholder page names where it sits and what it will hold.</summary>
    private static AppNavNode _setting(string text, string iconKey, AppNavItem parent, string about)
    {
        return _leaf(text, iconKey, parent, about, _settingsView(text, parent, () => about));
    }

    /// <summary>
    ///     A Settings group: has a page of its own (rather than showing its first section's), listing its sections, so
    ///     selecting the group does not look like selecting the section beneath it.
    /// </summary>
    private static AppNavNode _settingsGroup(string text, string iconKey, AppNavItem? parent, string about,
                                             Func<AppNavItem, IReadOnlyList<AppNavNode>> children)
    {
        AppNavNode? node = null;

        // the page is built when it is first shown, by which time the node and its children exist
        var view = _settingsView(text, parent, () => node is null || node.Children.Count == 0
                                                         ? about
                                                         : about + "\n\nSections: " + string.Join(", ", node.Children.Select(c => c.Header)) + ".");
        node = _group(text, iconKey, parent, children, about, view);
        return node;
    }

    private static AppView _settingsView(string text, AppNavItem? parent, Func<string> about)
    {
        var path = _pathOf(parent) is { Length: > 0 } above ? above + " / " + text : text;
        return new SimpleAppView(text, () => new PlaceholderView(text, path + "\n\n" + about() + "\n\nThis settings page is not built yet."))
               {
                   Key = "view:settings/" + path.Replace(" / ", "/")
               };
    }

    private static string _pathOf(AppNavItem? item)
    {
        var parts = new List<string>();
        for (var i = item; i is not null; i = i.Parent) parts.Insert(0, i.Text);
        return string.Join(" / ", parts);
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