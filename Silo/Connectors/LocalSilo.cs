using System.ComponentModel;
using System.Windows.Threading;
using Serilog;
using Silo.Certificates;
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
public class LocalSilo : VirtualRoot<OpenSilo>, INodeProvider<OpenSilo>, IUriConnector
{
    /// <summary>The scheme of the addresses of things inside the Silo (<c>silo:///active/...</c>).</summary>
    public UriSchemeScope HandlesScheme(string scheme)
    {
        return string.Equals(scheme, "silo", StringComparison.OrdinalIgnoreCase) ? UriSchemeScope.Private : UriSchemeScope.Unknown;
    }

    private bool      _refreshQueued;
    private SiloNode? _saved;
    private SiloNode? _scalars;
    private SiloNode? _security;

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

    private static IconRef Icon(string key)
    {
        return IconRef.FromKey(key);
    }

    /// <summary>A folder node; its children (if there are any to read) are read when it is first expanded.</summary>
    private static SiloNode Folder(string text, Uri uri, string icon, Func<IEnumerable<SiloNode>>? children = null, string kind = "Folder",
                                   bool   expanded = false, object? model = null)
    {
        var iconRef = Icon(icon);
        return new SiloNode(model ?? new FolderModel(text, kind, uri, iconRef), text, uri, iconRef, expanded ? 1 : null)
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
            _security = Folder("Security", Under("security"), "Shield", () =>
                                                                         [
                                                                             MakeCertificatesFolder(),
                                                                             Folder("Secrets", Under("security", "secrets"), "Key",
                                                                                    MakeSecrets)
                                                                         ]);
            yield return _security;

            yield return Folder("Prefs", Under("prefs"), "Settings", () =>
                                                                     [
                                                                         Folder("Preferences", Under("prefs", "preferences"),
                                                                                "Settings",    MakePreferences),
                                                                         Folder("Window Prefs", Under("prefs", "window-prefs"),
                                                                                "Design", MakeWindowPrefs)
                                                                     ]);

            yield return Folder("Audit Log", Under("audit-log"), "History", MakeAuditLog);
        }
    }

    // The same values are shown under whichever folder they are filed in; each folder reads the store again when it is
    // expanded, so it shows what is stored now.

    private IEnumerable<SiloNode> MakeObjectShapeFolders()
    {
        var  items = GetSavedItems();
        foreach (var shape in SavedItems.ObjectShapes)
        {
            var ofShape = items.Where(i => i.Shape == shape).ToList();
            if (ofShape.Count == 0) continue;
            string name = shape.ToString();
            yield return Folder($"{name} ({ofShape.Count})",                         Under("saved-objects", name), "DataStructure",
                                () => MakeTypeFolders(shape, "saved-objects", name), expanded: true);
        }
    }

    private IEnumerable<SiloNode> MakeTypeFolders(DataShape shape, string root = "saved-scalars", string? shapeFolder = null)
    {
        var groups = GetSavedItems()
                    .Where(i => i.Shape == shape)
                    .GroupBy(i => i.TypeLabel, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            string label = group.Key;
            var    path  = shapeFolder is null ? new[] { root, label } : new[] { root, shapeFolder, label };
            yield return Folder($"{label} ({group.Count()})", Under(path), "Cube", () => group.Select(MakeItemNode).ToList(), "Type", true);
        }
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

        var items = store.GetSecrets()
                         .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                         .Select(s => new SiloNode(new SecretDetails(s), s.Key, Under("security", "secrets", s.Id.ToString()), Icon("Key"),
                                                   null))
                         .ToList();
        return items;
    }

    private IEnumerable<SiloNode> MakePreferences()
    {
        if (Data?.Data is not IPreferenceStore store) return [];

        var items = store.GetPreferences()
                         .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                         .Select(p => new SiloNode(new PreferenceDetails(p, store), p.Key, Under("prefs", "preferences", p.Key),
                                                   Icon("Settings"), null))
                         .ToList();
        return items;
    }

    /// <summary>
    ///     Security, Certificates: the keypairs (a certificate with its private key) and the public certificates kept in the Silo, in two folders
    ///     so which is which is plain. Each folder carries the commands that add to it (install for keypairs, import for certificates), and an
    ///     authority's keypair holds what it issued, with each certificate's status.
    /// </summary>
    private SiloNode MakeCertificatesFolder()
    {
        var vault = CertificateActions.OpenVault(Data);
        var all   = vault?.List() ?? [];
        var pairs = all.Where(e => e.HasKey).ToList();
        var certs = all.Where(e => !e.HasKey).ToList();
        var keys  = vault?.CanKeepKeys == true;

        SiloNode Leaf(VaultEntry e)
        {
            var model = new CertificateNodeModel(e, Data!);
            var node  = new SiloNode(model, e.Name, model.Uri, model.Icon, null);
            if (vault is not null && e.HasKey && CertificateInfo.CanIssue(e.Certificate))
                node.Children.Add(IssuedFolder(vault, e));

            return node;
        }

        return Folder("Certificates", Under("security", "certificates"), CertificateInfo.StoreIcon, () =>
                                                                                              [
                                                                                                  Folder($"Keypairs ({pairs.Count})", Under("security", "certificates", "keypairs"), CertificateInfo.KeypairIcon,
                                                                                                         () => pairs.Select(Leaf).ToList(), "Folder", true,
                                                                                                         new CertificateFolderModel(Data!, CertificateFolderKind.Keypairs, pairs.Count, certs.Count, keys)),
                                                                                                  Folder($"Public Certificates ({certs.Count})", Under("security", "certificates", "public"), CertificateInfo.CertificateIcon,
                                                                                                         () => certs.Select(Leaf).ToList(), "Folder", true,
                                                                                                         new CertificateFolderModel(Data!, CertificateFolderKind.Public, pairs.Count, certs.Count, keys))
                                                                                              ], "Folder", true,
                      new CertificateFolderModel(Data!, CertificateFolderKind.All, pairs.Count, certs.Count, keys));
    }

    /// <summary>What an authority issued (that this Silo recorded), each with its status; revoked ones are told apart by their icon.</summary>
    private SiloNode IssuedFolder(CertificateVault vault, VaultEntry authority)
    {
        var records = vault.Issued(authority.Id);
        var held    = vault.List().ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var revoked = records.Count(r => r.Revoked);
        var text    = revoked > 0 ? $"Issued ({records.Count}, {revoked} revoked)" : $"Issued ({records.Count})";

        return Folder(text, Under("security", "certificates", authority.Id, "issued"), CertificateInfo.IssuedIcon, () => records.Select(r =>
                                                                                                                                  {
                                                                                                                                      var model = new IssuedCertificateModel(r, held.GetValueOrDefault(r.Thumbprint), Data!);
                                                                                                                                      return new SiloNode(model, r.Revoked ? $"{r.Name} (revoked)" : r.Name, model.Uri, model.Icon, null);
                                                                                                                                  }).ToList(), "Folder", false,
                      new CertificateFolderModel(Data!, CertificateFolderKind.Issued, 0, records.Count, true, authority));
    }

    private static IEnumerable<SiloNode> MakeWindowPrefs()
    {
        return [];
    }

    private static IEnumerable<SiloNode> MakeAuditLog()
    {
        return [];
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
                                                      foreach (var folder in new[] { _saved, _scalars, _security })
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
         .Icon(IconRef.FromUri(new Uri("pack://application:,,,/Silo;component/Assets/Silo.ico", UriKind.Absolute)))
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
            CertificateEvents.Changed               += w.OnStored;
            AppDesignLevel.Instance.PropertyChanged += w.OnLevel;
        }

        private void OnStored(object? sender, EventArgs e)
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
            CertificateEvents.Changed               -= OnStored;
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
    public IconRef? Icon   => IconRef.FromKey("Key");
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
public sealed class PreferenceDetails(PreferenceRecord preference, IPreferenceStore? store = null) : INodeDetails, IRealmed
{
    /// <summary>Where the preference is kept (to edit it in the text editor); null when that is not known.</summary>
    public IPreferenceStore? Store => store;

    public IconRef? Icon   => IconRef.FromKey("Settings");
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