using System.ComponentModel;
using System.Windows.Threading;
using Serilog;
using Silo.Connectors.Virtual;
using Silo.Design;
using Silo.Model;
using Silo.Providers;
using Silo.Providers.Data;
using Silo.Providers.Data.Shape;
using Silo.Storage;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors;

/// <summary>
///     Container which holds items which are stored in the local Silo: the Silo's own folders, not the world around it.
///     <code>
///     Your Silo (New Silo.silo)
///       Saved Objects          what is stored, by DataShape (Linear, Columnar, Tabular, ...), then by type
///       Saved Scalars          what is stored as a single value, by type
///       Dependencies           (developer)  Assemblies
///       Security               (author)     Certificates, Secrets
///       Prefs                  (author)     Preferences, Window Prefs
///       Audit Log              (author)
///     </code>
///     The folders a level of the app does not show are left out; the ones with nowhere to read from yet are empty.
/// </summary>
/// <remarks>
///     Every folder reads its contents when it is first expanded, so the list is what is stored then; the tree refreshes
///     itself when something is stored (see <see cref="Stored.Changed" />) or the design level changes.
/// </remarks>
[Transient(typeof(IConnector))]
public class LocalSilo : VirtualRoot<OpenSilo>, INodeProvider<OpenSilo>
{
    private bool      _refreshQueued;
    private SiloNode? _saved;
    private SiloNode? _scalars;

    public OpenSilo? Data { get; }

    // ----- what is in the Silo ------------------------------------------------------------------------------------------

    /// <summary>The kinds of thing the Silo holds, as types (those that can be told: stored values by their type).</summary>
    private IEnumerable<IType> GetSiloItemTypes()
    {
        return GetSavedItems()
              .Where(i => i.ClrType is not null)
              .Select(i => i.ClrType!)
              .Distinct()
              .Select(t => (IType)new RuntimeType(t))
              .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The things of one type that the Silo holds (at present the stored values written from that type).</summary>
    public IEnumerable<object> GetSiloItemsOfType(IType type)
    {
        type.Required();
        if ((type as RuntimeType)?.Value is not { } clr) return [];

        return GetSavedItems().Where(i => i.ClrType == clr);
    }

    private IReadOnlyList<SavedItem> GetSavedItems()
    {
        return SavedItems.Load(Data?.Data as IStoredStore ?? Stored.DefaultStore(),
                               AppDesignLevel.Instance.IsAtLeast(DesignLevel.Developer));
    }

    // ----- the tree -----------------------------------------------------------------------------------------------------

    private static Uri Under(params string[] path)
    {
        return (UriConstants.LocalSiloUri ?? new Uri("silo:///active/")).Append(path);
    }

    private static Uri Icon(string key)
    {
        return new Uri(key, UriKind.Relative);
    }

    /// <summary>A folder node; its children (if there are any to read) are read when it is first expanded.</summary>
    private static SiloNode Folder(string text, Uri uri, string icon, Func<IEnumerable<SiloNode>>? children = null, string kind = "Folder",
                                   bool   expanded = false)
    {
        var iconUri = Icon(icon);
        return new SiloNode(new FolderModel(text, kind, uri, iconUri), text, uri, iconUri, expanded ? 1 : null)
               {
                   ChildLoader =
                       children is null
                           ? null
                           : _ => Task
                                .FromResult
                                 <IReadOnlyList
                                     <SiloNode>>(children()
                                                    .ToList())
               };
    }

    /// <summary>
    ///     A leaf that says a folder has nothing in it yet and what will show up there, so an empty folder is not
    ///     mistaken for a broken one.
    /// </summary>
    private static SiloNode Nothing(string text, Uri parent)
    {
        return new SiloNode(new FolderModel(text, "Note", parent.Append("none"), Icon("Document")), text, parent.Append("none"),
                            Icon("Document"), null);
    }

    private IEnumerable<SiloNode> MakeTopFolders()
    {
        var level = AppDesignLevel.Instance;

        // what the person saved is open from the start: it should not take a hunt to find
        _saved = Folder("Saved Objects", Under("saved-objects"), "DataStructure", MakeObjectShapeFolders, expanded: true);
        yield return _saved;

        _scalars = Folder("Saved Scalars", Under("saved-scalars"), "Record", () => MakeTypeFolders(DataShape.Scalar), expanded: true);
        yield return _scalars;

        if (level.IsAtLeast(DesignLevel.Developer))
            yield return Folder("Dependencies", Under("dependencies"), "Layers",
                                () => [Folder("Assemblies", Under("dependencies", "assemblies"), "Modules", MakeAssemblies)]);

        if (level.IsAtLeast(DesignLevel.Author))
        {
            yield return Folder("Security", Under("security"), "Shield", () =>
                                                                         [
                                                                             Folder("Certificates", Under("security", "certificates"),
                                                                                    "Lock"),
                                                                             Folder("Secrets", Under("security", "secrets"), "Key",
                                                                                    MakeSecrets)
                                                                         ]);

            yield return Folder("Prefs", Under("prefs"), "Settings", () =>
                                                                     [
                                                                         Folder("Preferences", Under("prefs", "preferences"),
                                                                                "Settings",    MakePreferences),
                                                                         Folder("Window Prefs", Under("prefs", "window-prefs"),
                                                                                "Design")
                                                                     ]);

            yield return Folder("Audit Log", Under("audit-log"), "History");
        }
    }

    // The same values are shown under whichever folder they are filed in; each folder reads the store again when it is
    // expanded, so it shows what is stored now.

    private IEnumerable<SiloNode> MakeObjectShapeFolders()
    {
        var  items = GetSavedItems();
        bool any   = false;
        foreach (var shape in SavedItems.ObjectShapes)
        {
            var ofShape = items.Where(i => i.Shape == shape).ToList();
            if (ofShape.Count == 0) continue;

            any = true;
            string name = shape.ToString();
            yield return Folder($"{name} ({ofShape.Count})",                         Under("saved-objects", name), "DataStructure",
                                () => MakeTypeFolders(shape, "saved-objects", name), expanded: true);
        }

        if (!any)
            yield return Nothing("(nothing saved yet: keyboard shortcuts and other saved objects appear here)", Under("saved-objects"));
    }

    private IEnumerable<SiloNode> MakeTypeFolders(DataShape shape, string root = "saved-scalars", string? shapeFolder = null)
    {
        var groups = GetSavedItems()
                    .Where(i => i.Shape == shape)
                    .GroupBy(i => i.TypeLabel, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        bool any = false;
        foreach (var group in groups)
        {
            any = true;
            string label = group.Key;
            var    path  = shapeFolder is null ? new[] { root, label } : new[] { root, shapeFolder, label };
            yield return Folder($"{label} ({group.Count()})", Under(path), "Cube", () => group.Select(MakeItemNode).ToList(), "Type", true);
        }

        if (!any && shapeFolder is null)
            yield return Nothing("(nothing saved yet: characters and strings saved from the Unicode Explorer appear here)", Under(root));
    }

    private static SiloNode MakeItemNode(SavedItem item)
    {
        return new SiloNode(item, item.Name, item.Uri, item.Icon, null);
    }

    private static IEnumerable<SiloNode> MakeAssemblies()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
                        .OrderBy(a => a.GetName().Name, StringComparer.OrdinalIgnoreCase)
                        .Select(a => (SiloNode)new AssemblyNode(a));
    }

    /// <summary>The Silo's secrets by name, kind and provider. What a secret holds is never shown.</summary>
    private IEnumerable<SiloNode> MakeSecrets()
    {
        if (Data?.Data is not ISecretStore store) return [];

        return store.GetSecrets()
                    .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new SiloNode(new SecretDetails(s), s.Key, Under("security", "secrets", s.Id.ToString()), Icon("Key"),
                                              null));
    }

    private IEnumerable<SiloNode> MakePreferences()
    {
        if (Data?.Data is not IPreferenceStore store) return [];

        return store.GetPreferences()
                    .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new SiloNode(new PreferenceDetails(p), p.Key, Under("prefs", "preferences", p.Key), Icon("Settings"),
                                              null));
    }

    // ----- keeping up with changes ----------------------------------------------------------------------------------------

    /// <summary>Re-reads the folders that have been read, once, after a burst of changes (on the UI thread).</summary>
    internal void QueueRefresh(bool everything)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (everything)
            _refreshQueued = false;

        if (_refreshQueued) return;
        _refreshQueued = true;

        dispatcher.BeginInvoke(new Action(async () =>
                                          {
                                              _refreshQueued = false;
                                              try
                                              {
                                                  if (everything)
                                                  {
                                                      // the design level can add or take away whole folders
                                                      if (Node is { ChildLoader: not null, ChildrenLoaded: true } root)
                                                          await root.ReloadChildrenAsync();
                                                  }
                                                  else
                                                  {
                                                      foreach (var folder in new[] { _saved, _scalars })
                                                          if (folder is { ChildLoader: not null, ChildrenLoaded: true })
                                                              await folder.ReloadChildrenAsync();
                                                  }
                                              }
                                              catch (Exception ex)
                                              {
                                                  Log.Warning(ex, "Refreshing the Silo's folders failed");
                                              }
                                          }), DispatcherPriority.Background);
    }

    public LocalSilo() : base(false)
    {
        Data = App.Instance?.ActiveSilo;

        var h = new HierarchyBuilder(this);
        h.Level("LocalSilo", $"Your Silo ({Data?.Name ?? "no Silo is open"})", UriConstants.LocalSiloUri)
         .Kind("Silo")
         .Icon("Server")
         .Expanded();

        if (Data is not null)
            h.Children(MakeTopFolders);

        Node = h.Build();

        if (Data is not null)
            Watcher.Start(this);
    }

    public SiloNode? GetNode()
    {
        return Node;
    }

    /// <summary>
    ///     Listens for changes without keeping the LocalSilo alive: the events are static, so a strong handler would hold
    ///     every
    ///     LocalSilo ever made. It lets go of them once the LocalSilo has gone.
    /// </summary>
    private sealed class Watcher
    {
        private readonly WeakReference<LocalSilo> _target;

        public static void Start(LocalSilo target)
        {
            var w = new Watcher(target);
            Stored.Changed                          += w.OnStored;
            AppDesignLevel.Instance.PropertyChanged += w.OnLevel;
        }

        private void OnStored(object? sender, StoredChangedEventArgs e)
        {
            if (!_target.TryGetTarget(out var t)) Stop();
            else t.QueueRefresh(false);
        }

        private void OnLevel(object? sender, PropertyChangedEventArgs e)
        {
            if (!_target.TryGetTarget(out var t)) Stop();
            else if (e.PropertyName == nameof(AppDesignLevel.Level)) t.QueueRefresh(true);
        }

        private void Stop()
        {
            Stored.Changed                          -= OnStored;
            AppDesignLevel.Instance.PropertyChanged -= OnLevel;
        }

        private Watcher(LocalSilo target)
        {
            _target = new WeakReference<LocalSilo>(target);
        }
    }
}

/// <summary>A secret as the Explorer shows it: who it is for, never what it holds.</summary>
public sealed class SecretDetails(SecretRecord secret) : INodeDetails, IRealmed
{
    public Uri?   Icon     => new("Key", UriKind.Relative);
    public string Name     => secret.Key;
    public string TypeName => "Secret";
    public Uri    Uri      => new($"silo:///active/#security/secrets/{secret.Id}", UriKind.Absolute);
    public Realm  Realm    => Realm.Global;

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        yield return ("Key", secret.Key);
        yield return ("Kind", secret.Kind);
        yield return ("Provider", secret.Provider);
        yield return ("Created", secret.Created.ToLocalTime().ToString("g"));
        yield return ("Modified", secret.Modified.ToLocalTime().ToString("g"));
    }
}

/// <summary>A saved preference as the Explorer shows it: its key, and how much is stored.</summary>
public sealed class PreferenceDetails(PreferenceRecord preference) : INodeDetails, IRealmed
{
    public Uri?   Icon     => new("Settings", UriKind.Relative);
    public string Name     => preference.Key;
    public string TypeName => "Preference";
    public Uri    Uri      => new($"silo:///active/#prefs/preferences/{Uri.EscapeDataString(preference.Key)}", UriKind.Absolute);
    public Realm  Realm    => Realm.Global;

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        yield return ("Key", preference.Key);
        yield return ("Value",
                      preference.Data.Length <= 400
                          ? preference.Data
                          : preference.Data[..400] + $"... ({preference.Data.Length} characters)");
    }
}