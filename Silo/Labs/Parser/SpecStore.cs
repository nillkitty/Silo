using Serilog;
using Silo.Storage;

namespace Silo.Labs.Parser;

/// <summary>
///     Where the Parser Lab keeps its work in the Silo: each spec as one stored item (<c>lab:parser:spec:&lt;name&gt;</c>,
///     the Explorer lists them
///     with the other saved things) and the samples and tests of it beside it (<c>lab:parser:book:&lt;name&gt;</c>). With
///     no Silo open nothing
///     can be kept (<see cref="IsAvailable" />).
/// </summary>
public static class SpecStore
{
    public const string SpecPrefix = "lab:parser:spec:";
    public const string BookPrefix = "lab:parser:book:";

    /// <summary>Whether a Silo is open to keep things in.</summary>
    public static bool IsAvailable => Stored.DefaultStore() is not null;

    /// <summary>The names of the specs kept in the Silo.</summary>
    public static IReadOnlyList<string> Names()
    {
        try
        {
            var store = Stored.DefaultStore();
            return store?.ListStored(SpecPrefix)
                         .Select(r => r.Key[SpecPrefix.Length..])
                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                         .ToList() ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing the saved parser specs failed");
            return [];
        }
    }

    /// <summary>Whether a spec with this name is kept.</summary>
    public static bool Exists(string name)
    {
        return Stored.At<ParserSpecDoc>(SpecPrefix + name).Exists;
    }

    /// <summary>The spec kept under <paramref name="name" />; null when there is none (or it cannot be read).</summary>
    public static ParserSpecDoc? Load(string name)
    {
        try
        {
            var item = Stored.At<ParserSpecDoc>(SpecPrefix + name);
            return item.IsAvailable && item.TryGet(out var doc) ? doc : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Loading the parser spec {Name} failed", name);
            return null;
        }
    }

    /// <summary>The samples and tests kept for the spec; an empty book when there are none.</summary>
    public static SpecTestBook LoadBook(string name)
    {
        try
        {
            var item = Stored.At<SpecTestBook>(BookPrefix + name);
            return item.IsAvailable && item.TryGet(out var book) && book is not null ? book : new SpecTestBook();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Loading the samples of parser spec {Name} failed", name);
            return new SpecTestBook();
        }
    }

    /// <summary>Keeps the spec and its samples and tests under the spec's name.</summary>
    /// <exception cref="InvalidOperationException">No Silo is open, or the spec has no name.</exception>
    public static void Save(ParserSpecDoc doc, SpecTestBook book)
    {
        if (string.IsNullOrWhiteSpace(doc.Name)) throw new InvalidOperationException("Give the spec a name first (the Name parameter).");

        var spec = Stored.At<ParserSpecDoc>(SpecPrefix + doc.Name);
        if (!spec.IsAvailable) throw new InvalidOperationException("Open a Silo to keep the spec in.");

        spec.Set(doc);
        Stored.At<SpecTestBook>(BookPrefix + doc.Name).Set(book);
    }

    /// <summary>Forgets a spec and its samples and tests; false when there was none.</summary>
    public static bool Delete(string name)
    {
        Stored.At<SpecTestBook>(BookPrefix         + name).Delete();
        return Stored.At<ParserSpecDoc>(SpecPrefix + name).Delete();
    }
}