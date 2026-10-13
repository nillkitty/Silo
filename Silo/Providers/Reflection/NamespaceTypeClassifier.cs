using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Telefrag.DI;
using IoPath = System.IO.Path;
using ReflectedAssembly = System.Reflection.Assembly;

namespace Silo.Providers;

/// <summary>
///     <see cref="ITypeClassifier" /> that sorts types into <see cref="TypeCategory" />s by where they sit in the namespace
///     hierarchy. A type's namespace is matched from the most specific segment to the least: <c>Silo.Providers.Format</c>
///     is tried, then <c>Silo.Providers</c>, then <c>Silo</c>, and the first rule found decides. So a rule on
///     <c>Silo.Plugins</c> overrides the one on <c>Silo</c> for everything beneath it.
/// </summary>
/// <remarks>
///     <para>
///         Out of the box: <c>Silo</c> is <see cref="TypeCategory.SiloTypes" />; <c>System</c>, <c>Microsoft</c>,
///         <c>Windows</c> and <c>MS</c> are <see cref="TypeCategory.MicrosoftTypes" />; the namespaces of the libraries Silo
///         ships with (<c>Newtonsoft</c>, <c>Serilog</c>, <c>YamlDotNet</c>, <c>Markdig</c>, ...) are
///         <see cref="TypeCategory.ThirdPartyLibraryTypes" />. Add or change rules with <see cref="AddRule" />.
///     </para>
///     <para>
///         A namespace says nothing about who wrote a type that matches no rule (or has no namespace at all), so those fall
///         back to where the assembly came from: Silo's own assembly is Silo's; one in the .NET runtime folder is
///         Microsoft's; one embedded in the single-file build, or sitting next to Silo, is a third-party library; and
///         anything else -- a dynamic assembly, or one loaded from elsewhere such as a Silo's own <c>assemblies</c> folder --
///         is a <see cref="TypeCategory.UserTypes" /> assembly.
///     </para>
/// </remarks>
[Singleton(typeof(ITypeClassifier))]
public class NamespaceTypeClassifier : ITypeClassifier
{
    private static readonly string RuntimeDirectory = Normalize(RuntimeEnvironment.GetRuntimeDirectory());
    private static readonly string AppDirectory     = Normalize(AppContext.BaseDirectory);

    private readonly ConcurrentDictionary<string, TypeCategory> _byNamespace = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TypeCategory> _rules       = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<ReflectedAssembly, TypeCategory> _byAssembly = new();

    public NamespaceTypeClassifier()
    {
        AddRule("Silo", TypeCategory.SiloTypes);

        foreach (var root in new[] { "System", "Microsoft", "Windows", "MS" })
            AddRule(root, TypeCategory.MicrosoftTypes);

        foreach (var root in new[]
                             {
                                 "Newtonsoft", "Serilog", "YamlDotNet", "Markdig", "LibGit2Sharp", "Telefrag", "AvalonDock",
                                 "Dirkster", "EasyWindowsTerminalControl", "SQLitePCL"
                             })
            AddRule(root, TypeCategory.ThirdPartyLibraryTypes);
    }

    public TypeCategory Classify(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var ns = type.Namespace;
        if (!string.IsNullOrEmpty(ns))
        {
            if (_byNamespace.TryGetValue(ns, out var known)) return known;
            if (Match(ns) is { } matched) return _byNamespace[ns] = matched;
        }

        // no rule covers it: the namespace cannot say, so the assembly's origin decides (cached per assembly)
        return _byAssembly.GetOrAdd(type.Assembly, ClassifyByOrigin);
    }

    /// <summary>
    ///     Sets the category for a namespace and everything beneath it (e.g. <c>"Acme.Widgets"</c> covers
    ///     <c>Acme.Widgets.Controls</c>), replacing any earlier rule for that exact namespace.
    /// </summary>
    public void AddRule(string namespacePrefix, TypeCategory category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespacePrefix);

        _rules[namespacePrefix.Trim().Trim('.')] = category;
        _byNamespace.Clear();
    }

    /// <summary>Removes the rule for exactly this namespace; returns false if there was none.</summary>
    public bool RemoveRule(string namespacePrefix)
    {
        var removed = _rules.TryRemove(namespacePrefix.Trim().Trim('.'), out _);
        if (removed) _byNamespace.Clear();
        return removed;
    }

    /// <summary>The rule on the longest matching namespace prefix, or null if no rule covers it.</summary>
    private TypeCategory? Match(string ns)
    {
        for (var candidate = ns; candidate.Length > 0;)
        {
            if (_rules.TryGetValue(candidate, out var category)) return category;

            var dot = candidate.LastIndexOf('.');
            candidate = dot < 0 ? "" : candidate[..dot];
        }

        return null;
    }

    private static TypeCategory ClassifyByOrigin(ReflectedAssembly assembly)
    {
        if (assembly == typeof(NamespaceTypeClassifier).Assembly) return TypeCategory.SiloTypes;
        if (assembly.IsDynamic) return TypeCategory.UserTypes;

        string location;
        try
        {
            location = assembly.Location;
        }
        catch (NotSupportedException)
        {
            location = "";
        }

        if (string.IsNullOrEmpty(location)) return TypeCategory.ThirdPartyLibraryTypes; // embedded (single-file) build

        var directory = Normalize(IoPath.GetDirectoryName(location) ?? "");
        if (directory == RuntimeDirectory) return TypeCategory.MicrosoftTypes;
        if (directory == AppDirectory) return TypeCategory.ThirdPartyLibraryTypes;

        return TypeCategory.UserTypes;
    }

    private static string Normalize(string directory)
    {
        return directory.Length == 0 ? "" : IoPath.GetFullPath(directory).TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar);
    }
}
