using Serilog;
using Silo.Storage;

namespace Silo.Labs.Regexes;

/// <summary>The regexes kept in the Silo (<c>lab:regex:saved:&lt;name&gt;</c>, listed in the Explorer under Saved).</summary>
public static class RegexStore
{
    public const string Prefix = "lab:regex:saved:";

    public static bool IsAvailable => Stored.DefaultStore() is not null;

    public static IReadOnlyList<string> Names()
    {
        try
        {
            var store = Stored.DefaultStore();
            return store?.ListStored(Prefix).Select(r => r.Key[Prefix.Length..]).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing the saved regexes failed");
            return [];
        }
    }

    public static bool Exists(string name)
    {
        return Stored.At<RegexDoc>(Prefix + name).Exists;
    }

    public static RegexDoc? Load(string name)
    {
        try
        {
            var item = Stored.At<RegexDoc>(Prefix + name);
            return item.IsAvailable && item.TryGet(out var doc) && doc is not null ? doc : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Loading the regex {Name} failed", name);
            return null;
        }
    }

    public static void Save(RegexDoc doc)
    {
        if (string.IsNullOrWhiteSpace(doc.Name)) throw new InvalidOperationException("Give the regex a name first.");

        var item = Stored.At<RegexDoc>(Prefix + doc.Name.Trim());
        if (!item.IsAvailable) throw new InvalidOperationException("Open a Silo to keep the regex in.");

        item.Set(doc);
    }

    public static bool Delete(string name)
    {
        return Stored.At<RegexDoc>(Prefix + name).Delete();
    }
}
