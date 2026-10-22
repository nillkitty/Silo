using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using Serilog;
using Silo.Providers.Reflection;
using Telefrag.DI;
using AssemblyEntity = Silo.DbModel.Global.Assembly;
using IoFile = System.IO.File;
using IoPath = System.IO.Path;
using ReflectedAssembly = System.Reflection.Assembly;

namespace Silo.Providers;

/// <summary>
///     <see cref="IReflectionProvider" /> that reflects over the assemblies loaded into the process and keeps an index of
///     their public types by name and <see cref="TypeCategory" /> (decided by the registered
///     <see cref="ITypeClassifier" />).
///     It subscribes to <see cref="AppDomain.AssemblyLoad" />, so an assembly loaded later -- a plug-in, a Silo's own
///     assemblies -- is picked up without anyone asking.
/// </summary>
/// <remarks>
///     <para>
///         The load event only records the assembly; the reflection itself runs on a pool thread a moment later, and every
///         query first finishes any work still waiting, so an answer is never older than the assembly loads that
///         preceded the call. <see cref="TypesLoaded" /> is raised from whichever thread did the indexing.
///     </para>
///     <para>
///         Filters narrow what <see cref="GetConstructable{TType}" /> offers (it feeds pickers such as the
///         default-provider prompts) and answer <see cref="Filter" />. They are the ones added with
///         <see cref="AddFilter" /> (see
///         <see cref="Filters" />) and the ones registered in the component container (<see cref="IAssemblyFilter" />,
///         <see cref="ITypeFilter" />, <see cref="IPropertyFilter" /> and the other kinds); the container's are asked for
///         once, the first
///         time they are needed. <see cref="GetTypes" /> and <see cref="FindType" /> are never filtered: asking for
///         a type by name, or for everything, means exactly that. Assembly filters get the
///         <see cref="System.Reflection.Assembly" /> or, when they are written against the Silo assembly record
///         (<see cref="AssemblyEntity" />, as <see cref="IAssemblyFilter" /> is), a record describing it.
///     </para>
///     <para>
///         Only public types are indexed, and dynamic assemblies are skipped (their contents change after they load).
///     </para>
/// </remarks>
[Singleton(typeof(IReflectionProvider))]
public sealed class AppDomainReflectionProvider : IReflectionProvider, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<AppDomainReflectionProvider>();

    // set while this thread is asking the container for filters, so a filter that uses the provider in its constructor does not send it round again
    [ThreadStatic] private static bool        _collecting;
    private readonly              List<Entry> _all = [];

    private readonly Dictionary<ReflectedAssembly, IndexedAssembly>         _assemblies    = [];
    private readonly Dictionary<string, List<Entry>>                        _byFullName    = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Entry>>                        _bySimpleName  = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(Type, TypeCategory), List<Type>> _constructable = new();
    private readonly List<IReflectionFilter>                                _filters       = [];
    private readonly object                                                 _initLock      = new();
    private readonly object                                                 _lock          = new();
    private readonly ConcurrentQueue<ReflectedAssembly>                     _pending       = new();

    private ITypeClassifier? _classifier;
    private bool             _disposed;
    private int              _drainScheduled;
    private bool             _initialized;

    // the filters registered in the component container, asked for once (null until then; not kept while there is no app, as in tests)
    private volatile IReadOnlyList<IReflectionFilter>? _registered;

    /// <summary>
    ///     The classifier used to categorize types: the registered <see cref="ITypeClassifier" />, or the namespace-based
    ///     default.
    /// </summary>
    public ITypeClassifier Classifier
    {
        get => _classifier ??= App.Resolve<ITypeClassifier>() ?? new NamespaceTypeClassifier();
        init => _classifier = value;
    }

    public IEnumerable<IReflectionFilter> Filters
    {
        get
        {
            lock (_lock)
            {
                return _filters.ToArray();
            }
        }
    }

    private void EnsureReady()
    {
        Initialize();
        DrainPending();
    }

    private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs e)
    {
        // no reflection here: the loader may still be mid-way through this very load
        _pending.Enqueue(e.LoadedAssembly);
        if (Interlocked.Exchange(ref _drainScheduled, 1) != 0) return;

        Task.Run(() =>
                 {
                     Interlocked.Exchange(ref _drainScheduled, 0);
                     DrainPending();
                 });
    }

    private void DrainPending()
    {
        var added                                           = false;
        while (_pending.TryDequeue(out var assembly)) added |= IndexAssembly(assembly, null);

        if (added) RaiseTypesLoaded();
    }

    /// <summary>
    ///     Adds the assembly's public types to the index. Returns true if types were added. An assembly already indexed is
    ///     left alone, unless a different category override is asked for, which re-indexes it.
    /// </summary>
    private bool IndexAssembly(ReflectedAssembly assembly, TypeCategory? category)
    {
        if (assembly.IsDynamic) return false;

        lock (_lock)
        {
            if (AlreadyIndexed(assembly, category))
                return false;
        }

        // the reflecting happens outside the lock: it can load further assemblies, and the classifier is not ours
        var classifier = Classifier;
        var entries    = new List<Entry>();
        foreach (var type in ExportedTypes(assembly))
        {
            if (type.Name.Contains('<') || type.Name.Contains('>')) continue; // compiler generated

            entries.Add(new Entry(type, category ?? Classify(classifier, type), assembly));
        }

        lock (_lock)
        {
            if (AlreadyIndexed(assembly, category)) return false; // another thread got there first

            if (_assemblies.TryGetValue(assembly, out var previous)) Remove(previous);

            _assemblies[assembly] = new IndexedAssembly(assembly, category, entries);
            foreach (var entry in entries) Add(entry);

            _constructable.Clear();
            return entries.Count > 0;
        }
    }

    private bool AlreadyIndexed(ReflectedAssembly assembly, TypeCategory? category)
    {
        return _assemblies.TryGetValue(assembly, out var existing) && (category is null || existing.Override == category);
    }

    private void Add(Entry e)
    {
        _all.Add(e);
        foreach (var key in FullNameKeys(e.Type)) Bucket(_byFullName,     key).Add(e);
        foreach (var key in SimpleNameKeys(e.Type)) Bucket(_bySimpleName, key).Add(e);
    }

    private void Remove(IndexedAssembly indexed)
    {
        foreach (var e in indexed.Entries)
        {
            _all.Remove(e);
            foreach (var key in FullNameKeys(e.Type)) Unbucket(_byFullName,     key, e);
            foreach (var key in SimpleNameKeys(e.Type)) Unbucket(_bySimpleName, key, e);
        }

        _assemblies.Remove(indexed.Assembly);
    }

    private static List<Entry> Bucket(Dictionary<string, List<Entry>> index, string key)
    {
        if (!index.TryGetValue(key, out var list)) index[key] = list = [];
        return list;
    }

    private static void Unbucket(Dictionary<string, List<Entry>> index, string key, Entry e)
    {
        if (!index.TryGetValue(key, out var list)) return;

        list.Remove(e);
        if (list.Count == 0) index.Remove(key);
    }

    /// <summary>
    ///     The name a type is indexed under: <c>A.B.C`1</c>, and for a nested type also the dotted form
    ///     <c>A.B.Outer.Inner</c>.
    /// </summary>
    private static IEnumerable<string> FullNameKeys(Type t)
    {
        var full = t.FullName ?? t.Name;
        yield return full;
        if (full.Contains('+')) yield return full.Replace('+', '.');
    }

    /// <summary><c>List`1</c> and <c>List</c> for a generic type, so the arity does not have to be written.</summary>
    private static IEnumerable<string> SimpleNameKeys(Type t)
    {
        yield return t.Name;
        var tick = t.Name.IndexOf('`');
        if (tick > 0) yield return t.Name[..tick];
    }

    private TypeCategory Classify(ITypeClassifier classifier, Type type)
    {
        try
        {
            return classifier.Classify(type);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Classifying {type} failed", type.FullName);
            return TypeCategory.None;
        }
    }

    private static List<Type> ExportedTypes(ReflectedAssembly assembly)
    {
        try
        {
            return [.. assembly.GetExportedTypes()];
        }
        catch (NotSupportedException)
        {
            return [];
        }
        catch (Exception ex) when (ex is ReflectionTypeLoadException or TypeLoadException or IOException or BadImageFormatException)
        {
            // a reference that will not load: keep whatever types can still be read
            Logger.Debug(ex, "Exported types of {assembly} could not all be read; using the readable ones", assembly.FullName);
            try
            {
                var readable = ex is ReflectionTypeLoadException { Types: { } partial } ? partial : assembly.GetTypes();
                return readable.Where(t => t is not null && IsVisible(t)).ToList()!;
            }
            catch (ReflectionTypeLoadException rtle)
            {
                return rtle.Types.Where(t => t is not null && IsVisible(t)).ToList()!;
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    private static bool IsVisible(Type t)
    {
        try
        {
            return t.IsVisible;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void RaiseTypesLoaded()
    {
        try
        {
            TypesLoaded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "A TypesLoaded handler failed");
        }
    }

    // ---- queries --------------------------------------------------------------------------------------------

    private static bool Matches(Entry e, TypeCategory category)
    {
        return category == TypeCategory.All || (e.Category & category) != 0;
    }

    private List<Type> FindConstructable(Type contract, TypeCategory category)
    {
        List<Type> assignable;
        lock (_lock)
        {
            assignable = _all.Where(e => Matches(e, category)).Select(e => e.Type).Where(contract.IsAssignableFrom).ToList();
        }

        return assignable.Where(IsConstructable).ToList();
    }

    /// <summary>A class the container could create: concrete, closed, and with a public constructor.</summary>
    private static bool IsConstructable(Type t)
    {
        try
        {
            return t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters && !typeof(Delegate).IsAssignableFrom(t) &&
                   t.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length > 0;
        }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or FileLoadException)
        {
            return false;
        }
    }

    private bool IsFiltered(Type                                                     type, IReflectionFilter[] filters,
                            Dictionary<(IReflectionFilter, ReflectedAssembly), bool> assemblyVerdicts)
    {
        foreach (var filter in filters)
            try
            {
                var kind = filter.TypeFiltered;
                if (filter is IInterfaceFilter)
                {
                    if (type.IsInterface && filter.IsIgnore(type)) return true;
                }
                else if (kind == typeof(Type))
                {
                    if (filter.IsIgnore(type)) return true;
                }
                else if (kind == typeof(AssemblyEntity) || kind == typeof(ReflectedAssembly))
                {
                    var assembly = type.Assembly;
                    if (!assemblyVerdicts.TryGetValue((filter, assembly), out var ignore))
                        assemblyVerdicts[(filter, assembly)] =
                            ignore = filter.IsIgnore(kind == typeof(AssemblyEntity) ? Describe(assembly) : assembly);

                    if (ignore) return true;
                }
                // member filters (properties, methods, ...) do not apply to a type list
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "A reflection filter failed on {type}; the type is kept", type.FullName);
            }

        return false;
    }

    /// <summary>
    ///     The filters that apply: those added with <see cref="AddFilter" />, then those registered in the container,
    ///     each once.
    /// </summary>
    private IReflectionFilter[] AllFilters()
    {
        IReflectionFilter[] added;
        lock (_lock)
        {
            added = _filters.ToArray();
        }

        var registered = Registered();
        if (registered.Count == 0) return added;

        var all = new List<IReflectionFilter>(added);
        foreach (var filter in registered)
            if (!all.Any(f => ReferenceEquals(f, filter)))
                all.Add(filter);

        return all.ToArray();
    }

    /// <summary>
    ///     The filters registered in the component container, of every filter kind (assembly, type, interface, property,
    ///     event, method, constructor, field).
    /// </summary>
    private IReadOnlyList<IReflectionFilter> Registered()
    {
        if (_registered is { } known) return known;
        if (_collecting || App.Instance is null) return [];

        _collecting = true;
        try
        {
            var found = new List<IReflectionFilter>();
            CollectRegistered<IAssemblyFilter>(found);
            CollectRegistered<ITypeFilter>(found);
            CollectRegistered<IInterfaceFilter>(found);
            CollectRegistered<IPropertyFilter>(found);
            CollectRegistered<IEventFilter>(found);
            CollectRegistered<IMethodFilter>(found);
            CollectRegistered<IConstructorFilter>(found);
            CollectRegistered<IFieldFilter>(found);
            return _registered = found;
        }
        finally
        {
            _collecting = false;
        }
    }

    private static void CollectRegistered<TFilter>(List<IReflectionFilter> into) where TFilter : class, IReflectionFilter
    {
        try
        {
            foreach (var filter in App.Resolve<IEnumerable<TFilter>>() ?? [])
                if (filter is not null && !into.Any(f => ReferenceEquals(f, filter)))
                    into.Add(filter);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "The registered {filter}s could not be collected", typeof(TFilter).Name);
        }
    }

    /// <summary>
    ///     Whether any of <paramref name="filters" /> ignores <paramref name="item" />: an assembly, a type, or a member
    ///     (property, event, method, constructor, field).
    /// </summary>
    private bool IsFilteredItem(object item, IReflectionFilter[] filters)
    {
        switch (item)
        {
            case Type type:
                // a type is also judged by the filters of the assembly it comes from, as in GetConstructable
                return IsFiltered(type, filters, []);

            case ReflectedAssembly or AssemblyEntity:
            {
                var described = item as AssemblyEntity ?? Describe((ReflectedAssembly)item);
                foreach (var filter in filters)
                    try
                    {
                        var kind = filter.TypeFiltered;
                        if (kind == typeof(AssemblyEntity)    && filter.IsIgnore(described)) return true;
                        if (kind == typeof(ReflectedAssembly) && item is ReflectedAssembly && filter.IsIgnore(item)) return true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "A reflection filter failed on assembly {assembly}; it is kept", described.AssemblyName);
                    }

                return false;
            }

            default:
                foreach (var filter in filters)
                    try
                    {
                        if (filter is not IInterfaceFilter && filter.TypeFiltered.IsInstanceOfType(item) && filter.IsIgnore(item))
                            return true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "A reflection filter failed on {item}; it is kept", item);
                    }

                return false;
        }
    }

    private static Type? Pick(Dictionary<string, List<Entry>> index, string key, TypeCategory category)
    {
        if (!index.TryGetValue(key, out var bucket)) return null;

        return bucket.Where(e => Matches(e, category))
                     .OrderBy(e => FullNameKeys(e.Type).Contains(key, StringComparer.Ordinal) || e.Type.Name == key
                                       ? 0
                                       : 1) // exact case first
                     .ThenBy(e => Rank(e.Category))
                     .ThenBy(e => e.Type.Namespace?.Length ?? 0)
                     .ThenBy(e => e.Type.FullName, StringComparer.Ordinal)
                     .Select(e => e.Type)
                     .FirstOrDefault();
    }

    private static int Rank(TypeCategory c)
    {
        return c.HasFlag(TypeCategory.SiloTypes)              ? 0 :
               c.HasFlag(TypeCategory.UserTypes)              ? 1 :
               c.HasFlag(TypeCategory.ThirdPartyLibraryTypes) ? 2 :
               c.HasFlag(TypeCategory.MicrosoftTypes)         ? 3 : 4;
    }

    private static string? Parent(string ns)
    {
        var dot = ns.LastIndexOf('.');
        return dot < 0 ? null : ns[..dot];
    }

    /// <summary>
    ///     Trims, drops <c>global::</c> and turns <c>X&lt;,&gt;</c> into <c>X`2</c>; null for a name that cannot be
    ///     looked up (empty, or a constructed generic).
    /// </summary>
    internal static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        name = name.Trim();
        if (name.StartsWith("global::", StringComparison.Ordinal)) name = name["global::".Length..];

        var open = name.IndexOf('<');
        if (open < 0) return name.Length == 0 ? null : name;

        if (!name.EndsWith('>')) return null;

        var inside = name[(open + 1)..^1];
        if (inside.Any(c => c != ',' && !char.IsWhiteSpace(c))) return null; // List<string>: a constructed type

        return name[..open] + "`" + (inside.Count(c => c == ',') + 1);
    }

    // ---- loading --------------------------------------------------------------------------------------------

    private static TypeCategory? SingleCategory(TypeCategory category)
    {
        return category != TypeCategory.All && BitOperations.PopCount((uint)category) == 1 ? category : null;
    }

    private static ReflectedAssembly ResolveAssembly(object? target)
    {
        switch (target)
        {
            case ReflectedAssembly assembly: return assembly;
            case AssemblyName name:          return ReflectedAssembly.Load(name);
            case FileInfo file:              return LoadFromPath(file.FullName);
            case Uri { IsFile: true } uri:   return LoadFromPath(uri.LocalPath);
            case AssemblyEntity entity:
                return ResolveAssembly(entity.SourceUri is { IsFile: true } source ? source : new AssemblyName(entity.AssemblyName));
            case string text: return IoFile.Exists(text) ? LoadFromPath(text) : ReflectedAssembly.Load(new AssemblyName(text));
            default:
                throw new ArgumentException($"An assembly reference cannot be resolved from a {target?.GetType().Name ?? "null"}.",
                                            nameof(target));
        }
    }

    private static ReflectedAssembly LoadFromPath(string path)
    {
        var full = IoPath.GetFullPath(path);

        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            if (!loaded.IsDynamic && string.Equals(SafeLocation(loaded), full, StringComparison.OrdinalIgnoreCase))
                return loaded;

        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(full);
        }
        catch (FileLoadException) when (AppDomain.CurrentDomain.GetAssemblies()
                                                 .FirstOrDefault(a => a.GetName().Name == IoPath.GetFileNameWithoutExtension(full)) is
                                            { } sameName)
        {
            return sameName; // an assembly of that name is already loaded from somewhere else
        }
    }

    private static string SafeLocation(ReflectedAssembly assembly)
    {
        try
        {
            return assembly.Location;
        }
        catch (NotSupportedException)
        {
            return "";
        }
    }

    private static AssemblyEntity Describe(ReflectedAssembly assembly)
    {
        var name     = assembly.GetName().Name ?? assembly.FullName ?? "";
        var location = SafeLocation(assembly);
        return new AssemblyEntity
               {
                   AssemblyName = name,
                   SourceUri = location.Length > 0
                                   ? new Uri(location)
                                   : new Uri("assembly://" + Uri.EscapeDataString(name))
               };
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed                            =  true;
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
    }

    public event EventHandler? TypesLoaded;

    public void Initialize()
    {
        lock (_initLock)
        {
            if (_initialized) return;

            _initialized                         =  true; // before the scan, so a handler that calls back in is not sent round again
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

            // subscribed first, so nothing loaded during the scan is missed (an assembly seen twice is indexed once)
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) _pending.Enqueue(assembly);
            DrainPending();
        }
    }

    public bool AddFilter(IReflectionFilter filter)
    {
        if (filter is null) return false;

        lock (_lock)
        {
            if (_filters.Contains(filter)) return false;

            _filters.Add(filter);
            return true;
        }
    }

    public bool RemoveFilter(IReflectionFilter filter)
    {
        lock (_lock)
        {
            return filter is not null && _filters.Remove(filter);
        }
    }

    /// <summary>
    ///     Loads an assembly so its types are reflectable. <paramref name="r" /> may hand back a loaded
    ///     <see cref="System.Reflection.Assembly" />, an <see cref="AssemblyName" />, a file path, a <c>file:</c> URI, a
    ///     <see cref="System.IO.FileInfo" />, or an <see cref="AssemblyEntity" />. .NET has no reflection-only load, so
    ///     the assembly is really loaded (loading runs none of its code). A single <paramref name="category" /> overrides
    ///     what the classifier would say for every type in it (e.g. <see cref="TypeCategory.UserTypes" /> for a Silo's own
    ///     assemblies); <see cref="TypeCategory.All" /> or <see cref="TypeCategory.None" /> leaves it to the classifier.
    /// </summary>
    public AssemblyEntity Load(IAssemblyReference r, TypeCategory category)
    {
        ArgumentNullException.ThrowIfNull(r);
        Initialize();

        var assembly = ResolveAssembly(r.GetAssembly());
        if (IndexAssembly(assembly, SingleCategory(category))) RaiseTypesLoaded();
        return Describe(assembly);
    }

    public List<Type> GetConstructable<TType>(TypeCategory category = TypeCategory.All)
    {
        EnsureReady();

        var candidates = _constructable.GetOrAdd((typeof(TType), category), key => FindConstructable(key.Item1, key.Item2));

        var filters = AllFilters();
        if (filters.Length == 0) return [.. candidates];

        var assemblyVerdicts = new Dictionary<(IReflectionFilter, ReflectedAssembly), bool>();
        return candidates.Where(t => !IsFiltered(t, filters, assemblyVerdicts)).ToList();
    }

    /// <summary>
    ///     Asks the filters -- those added with <see cref="AddFilter" /> and those registered in the container -- whether
    ///     <paramref name="item" /> is to be ignored. An <see cref="System.Reflection.Assembly" /> is judged by the
    ///     <see cref="IAssemblyFilter" />s, a <see cref="Type" /> by the <see cref="ITypeFilter" />s (and the
    ///     <see cref="IInterfaceFilter" />s,
    ///     when it is an interface) and by the assembly filters of the assembly it comes from, and a
    ///     <see cref="System.Reflection.PropertyInfo" /> by the <see cref="IPropertyFilter" />s; the other member kinds
    ///     likewise by their own
    ///     filters. A filter that throws is logged and does not ignore the item. Null, and anything else, is not ignored.
    /// </summary>
    /// <returns>True when the item should be ignored.</returns>
    public bool Filter(object item)
    {
        if (item is null) return false;

        var filters = AllFilters();
        return filters.Length != 0 && IsFilteredItem(item, filters);
    }

    public List<Type> GetTypes(TypeCategory category = TypeCategory.All)
    {
        EnsureReady();
        lock (_lock)
        {
            return _all.Where(e => Matches(e, category)).Select(e => e.Type).ToList();
        }
    }

    /// <summary>
    ///     Finds a type by name. <paramref name="name" /> can be full (<c>System.Text.StringBuilder</c>), simple
    ///     (<c>StringBuilder</c>) or an open generic (<c>List&lt;&gt;</c>, <c>Dictionary&lt;,&gt;</c>, or CLR style
    ///     <c>List`1</c>); matching ignores case but prefers an exact-case hit. A simple name is first looked up inside
    ///     <paramref name="defaultNamespace" /> and each of its parents, like a C# name would be, and then anywhere. When
    ///     several types still qualify, Silo's win over user types, then third-party, then Microsoft's, then the shorter
    ///     namespace. Constructed generics such as <c>List&lt;string&gt;</c> are not looked up here and give null.
    /// </summary>
    public Type? FindType(string name, string? defaultNamespace = null, TypeCategory category = TypeCategory.All)
    {
        var key = NormalizeName(name);
        if (key is null) return null;

        EnsureReady();
        lock (_lock)
        {
            for (var ns = defaultNamespace?.Trim().Trim('.'); !string.IsNullOrEmpty(ns); ns = Parent(ns))
                if (Pick(_byFullName, ns + "." + key, category) is { } inNamespace)
                    return inNamespace;

            if (Pick(_byFullName, key, category) is { } qualified) return qualified;

            return key.Contains('.') ? null : Pick(_bySimpleName, key, category);
        }
    }

    // ---- indexing -------------------------------------------------------------------------------------------

    private sealed record Entry(Type Type, TypeCategory Category, ReflectedAssembly Assembly);

    private sealed record IndexedAssembly(ReflectedAssembly Assembly, TypeCategory? Override, List<Entry> Entries);
}