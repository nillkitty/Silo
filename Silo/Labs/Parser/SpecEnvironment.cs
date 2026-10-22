using Silo.Providers.Data;

namespace Silo.Labs.Parser;

/// <summary>
///     What a spec can reach outside itself: the other specs kept in the Silo (for imports) and the Silo's enums (for the token type). A
///     class of its own so that the rules about imports and token types can be tested without a Silo.
/// </summary>
public class SpecEnvironment
{
    /// <summary>Where the enums of the active Silo come from; the Lab's session sets it (null: no Silo, so no enums).</summary>
    public static Func<IEnumStore?> EnumSource { get; set; } = () => null;

    public virtual ParserSpecDoc? LoadSpec(string name) => SpecStore.Load(name);

    public virtual IReadOnlyList<string> SpecNames() => SpecStore.Names();

    public virtual IEnumStore? Enums => EnumSource();
}

/// <summary>Which specs import which: a spec may use the rules of another, never in a circle.</summary>
public static class SpecImports
{
    /// <summary>A cycle of imports that <paramref name="doc" /> is part of or reaches (first name = last name); null when there is none.</summary>
    public static IReadOnlyList<string>? FindCycle(ParserSpecDoc doc, Func<string, ParserSpecDoc?> load)
    {
        string root = NameOf(doc);
        return Walk(root, doc, doc, load, [root], []);
    }

    /// <summary>The cycle importing <paramref name="import" /> would make in <paramref name="doc" />; null when it would not.</summary>
    public static IReadOnlyList<string>? CycleIfImported(ParserSpecDoc doc, string import, Func<string, ParserSpecDoc?> load)
    {
        string root = NameOf(doc);
        if (string.Equals(root, import, StringComparison.OrdinalIgnoreCase)) return [root, root];

        var path = Walk(import, Resolve(import, doc, load), doc, load, [root, import], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root });
        return path;
    }

    private static string NameOf(ParserSpecDoc doc) => string.IsNullOrWhiteSpace(doc.Name) ? "(this spec)" : doc.Name;

    private static ParserSpecDoc? Resolve(string name, ParserSpecDoc root, Func<string, ParserSpecDoc?> load)
    {
        return string.Equals(name, NameOf(root), StringComparison.OrdinalIgnoreCase) ? root : load(name);
    }

    // depth first: a name met again while it is still on the way down is the cycle
    private static IReadOnlyList<string>? Walk(string name, ParserSpecDoc? doc, ParserSpecDoc root, Func<string, ParserSpecDoc?> load, List<string> path, HashSet<string> done)
    {
        if (doc is null) return null;

        // 'done' holds names whose imports were all followed without finding the root again
        foreach (string next in doc.Imports)
        {
            int at = path.FindIndex(p => string.Equals(p, next, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) return [.. path.Skip(at), next];

            if (!done.Add(next)) continue;

            path.Add(next);
            var found = Walk(next, Resolve(next, root, load), root, load, path, done);
            if (found is not null) return found;

            path.RemoveAt(path.Count - 1);
        }

        return null;
    }
}
