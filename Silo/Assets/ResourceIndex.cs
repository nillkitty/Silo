using System.Collections;
using System.IO;
using System.Reflection;
using System.Resources;
using Serilog;
using Silo.Contracts.Resources;

namespace Silo.Assets;

/// <summary>
///     Indexes the resources in all of the app's own (first-party) assemblies -- not Microsoft's,
///     not third-party packages -- once at startup, so consumers such as the Icon Viewer can just query it.
///     The slow part, reading the assemblies, runs in the background (<see cref="BuildAssembliesAsync" />, started
///     from <c>App.OnStartup</c>); the quick part, the application's resource dictionary, is read on the UI thread
///     (<see cref="BuildDictionary" />). <see cref="Entries" /> waits for the background part when it has not finished.
/// </summary>
/// <remarks>
///     <para>
///         Only <em>locations</em> are recorded: compiled-resource keys are read straight from each
///         assembly's <c>.g.resources</c> without loading their values, and dictionary entries are
///         recorded by key without instantiating them. Anything heavier is deferred to
///         <see cref="ResourceEntry.Open" /> / <see cref="ResourceEntry.GetValue" />.
///     </para>
///     <para>
///         "First-party" is a whitelist (<see cref="FirstPartyPrefixes" />), not a blacklist of
///         <c>System</c>/<c>Microsoft</c>: a blacklist also lets Serilog, AvalonDock, Markdig,
///         YamlDotNet and every other package through. Assemblies loaded after
///         <see cref="Build" /> that match are indexed as they arrive.
///     </para>
/// </remarks>
public sealed class ResourceIndex : IResourceIndex
{
    private readonly List<ResourceEntry> _entries           = [];
    private readonly HashSet<Assembly>   _indexedAssemblies = [];
    private readonly object              _lock              = new();

    // set while this thread is the one indexing the assemblies, so a Changed handler reading Entries does not wait for itself
    [ThreadStatic] private static bool _indexing;

    private          Task?                        _assemblies;
    private          bool                         _dictionaryBuilt;
    private volatile IReadOnlyList<ResourceEntry> _snapshot = [];

    /// <summary>
    ///     Assembly names (and name prefixes, matched at a '.' boundary) that count as first-party.
    ///     Set before <see cref="Build" /> to change what gets indexed.
    /// </summary>
    public static string[] FirstPartyPrefixes { get; set; } = ["Silo", "Telefrag"];

    public static ResourceIndex Instance { get; } = new();

    /// <summary>
    ///     Every indexed resource. When the assemblies are still being read in the background this waits for that to finish
    ///     (it never needs the UI thread, so waiting on it is safe from anywhere).
    /// </summary>
    public IReadOnlyList<ResourceEntry> Entries
    {
        get
        {
            if (!_indexing) _assemblies?.Wait();
            return _snapshot;
        }
    }

    /// <summary>
    ///     Completes when the first-party assemblies have been indexed (completed already when <see cref="BuildAssembliesAsync" />
    ///     has not been called). Never faults.
    /// </summary>
    public Task AssembliesReady => _assemblies ?? Task.CompletedTask;

    /// <summary>
    ///     Whether <paramref name="assembly" /> is one of the app's own, per <see cref="FirstPartyPrefixes" />.
    /// </summary>
    public static bool IsFirstParty(Assembly assembly)
    {
        if (assembly.IsDynamic) return false;

        string? name = assembly.GetName().Name;
        if (name is null) return false;

        foreach (string prefix in FirstPartyPrefixes)
            if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    ///     Indexes everything now, on the calling thread: the assemblies (see <see cref="BuildAssembliesAsync" />), then the
    ///     application's resource dictionary (see <see cref="BuildDictionary" />). Does nothing for a part already done. Call on
    ///     the UI thread, after the application's resources (and theme dictionaries) are in place.
    /// </summary>
    public void Build()
    {
        BuildAssembliesAsync().Wait();
        BuildDictionary();
    }

    /// <summary>
    ///     Starts indexing every first-party assembly already loaded -- the manifest resources and the compiled-resource keys,
    ///     which is where the time goes -- on a background thread, and starts indexing first-party assemblies loaded from here
    ///     on. Safe to call from any thread, and more than once: later calls return the same task. The task never faults.
    /// </summary>
    public Task BuildAssembliesAsync()
    {
        lock (_lock)
        {
            return _assemblies ??= Task.Run(BuildAssemblies);
        }
    }

    private void BuildAssemblies()
    {
        _indexing = true;
        try
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

            List<ResourceEntry> found = [];
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (IsFirstParty(asm) && MarkIndexed(asm))
                    IndexAssembly(asm, found);

            Add(found);
            Log.Debug("Resource index of the assemblies built:  {count} resources", _snapshot.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Indexing the resources of the assemblies failed");
        }
        finally
        {
            _indexing = false;
        }
    }

    /// <summary>
    ///     Indexes the application's resource dictionary (its keyed entries, theme dictionaries included). Does nothing if
    ///     already called. Call on the UI thread, after the application's resources and theme are in place; it is quick.
    /// </summary>
    public void BuildDictionary()
    {
        lock (_lock)
        {
            if (_dictionaryBuilt) return;
            _dictionaryBuilt = true;
        }

        List<ResourceEntry> found = [];
        IndexDictionary(Application.Current?.Resources, found, []);

        Add(found);
        Log.Debug("Resource index of the dictionary built:  {count} resources", _snapshot.Count);
    }

    /// <summary>
    ///     Re-reads the application's resource dictionary entries, e.g. after a theme change swapped
    ///     dictionaries. Call on the UI thread.
    /// </summary>
    public void RefreshDictionaryEntries()
    {
        List<ResourceEntry> found = [];
        IndexDictionary(Application.Current?.Resources, found, []);

        lock (_lock)
        {
            _entries.RemoveAll(e => e.Kind == ResourceKind.ResourceDictionaryEntry);
            _entries.AddRange(found);
            _snapshot = _entries.ToArray();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        var asm = args.LoadedAssembly;
        if (!IsFirstParty(asm) || !MarkIndexed(asm)) return;

        List<ResourceEntry> found = [];
        IndexAssembly(asm, found);
        Add(found);
    }

    private bool MarkIndexed(Assembly asm)
    {
        lock (_lock)
        {
            return _indexedAssemblies.Add(asm);
        }
    }

    private void Add(List<ResourceEntry> found)
    {
        if (found.Count == 0) return;

        lock (_lock)
        {
            _entries.AddRange(found);
            _snapshot = _entries.ToArray();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void IndexAssembly(Assembly asm, List<ResourceEntry> found)
    {
        string asmName = asm.GetName().Name ?? asm.FullName ?? "?";

        try
        {
            foreach (string manifestName in asm.GetManifestResourceNames())
            {
                if (manifestName.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase))
                {
                    IndexCompiledResources(asm, asmName, manifestName, found);
                    continue;
                }

                string name = manifestName;
                found.Add(new ResourceEntry(ResourceKind.EmbeddedResource, name, asmName, null, name,
                                            () => asm.GetManifestResourceStream(name)));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to index resources of assembly '{asm}'", asm);
        }
    }

    private static void IndexCompiledResources(Assembly asm, string asmName, string manifestName, List<ResourceEntry> found)
    {
        using var stream = asm.GetManifestResourceStream(manifestName);
        if (stream is null) return;

        // Only ever touch Key: IDictionaryEnumerator.Entry/Value would read every resource's data.
        using var             reader = new ResourceReader(stream);
        IDictionaryEnumerator en     = reader.GetEnumerator();
        while (en.MoveNext())
        {
            if (en.Key is not string key) continue;

            var pack = new Uri($"pack://application:,,,/{asmName};component/{key}", UriKind.Absolute);
            found.Add(new ResourceEntry(ResourceKind.CompiledResource, key, asmName, pack, pack.ToString(), () => OpenPack(pack)));
        }
    }

    private static Stream? OpenPack(Uri pack)
    {
        try
        {
            return Application.GetResourceStream(pack)?.Stream;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not open resource '{uri}'", pack);
            return null;
        }
    }

    private static void IndexDictionary(ResourceDictionary? dict, List<ResourceEntry> found, HashSet<string> seen)
    {
        if (dict is null) return;

        foreach (object? key in dict.Keys)
        {
            // Only string keys can be referred to as {StaticResource Key}; skip Type/ComponentResourceKeys.
            if (key is not string keyStr || !seen.Add(keyStr)) continue;

            object k = key;
            found.Add(new ResourceEntry(ResourceKind.ResourceDictionaryEntry, keyStr, null, null, $"{{StaticResource {keyStr}}}",
                                        value: () => dict[k]));
        }

        foreach (var merged in dict.MergedDictionaries)
            IndexDictionary(merged, found, seen);
    }

    public event EventHandler? Changed;

    public IEnumerable<ResourceEntry> OfKind(ResourceKind kind)
    {
        return Entries.Where(e => e.Kind == kind);
    }

    public IEnumerable<ResourceEntry> WithExtension(params string[] extensions)
    {
        return Entries.Where(e => extensions.Any(x => string.Equals(x, e.Extension, StringComparison.OrdinalIgnoreCase)));
    }
}