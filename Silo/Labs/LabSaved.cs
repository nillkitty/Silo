using Serilog;
using Silo.Storage;

namespace Silo.Labs;

/// <summary>
///     The items a Lab keeps in the Silo for the person (a regex, a stream setup, a menu...): <c>lab:&lt;id&gt;:saved:&lt;name&gt;</c>, listed in the Explorer under Saved.
/// </summary>
internal sealed class LabSaved<T>(string labId) where T : class
{
    public string Prefix { get; } = $"lab:{labId}:saved:";

    public bool IsAvailable => Stored.DefaultStore() is not null;

    public IReadOnlyList<string> Names()
    {
        try
        {
            var store = Stored.DefaultStore();
            return store?.ListStored(Prefix).Select(r => r.Key[Prefix.Length..]).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing what {Lab} keeps failed", labId);
            return [];
        }
    }

    public bool Exists(string name) => Stored.At<T>(Prefix + name).Exists;

    public T? Load(string name)
    {
        try
        {
            var item = Stored.At<T>(Prefix + name);
            return item.IsAvailable && item.TryGet(out var value) ? value : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Loading {Name} of {Lab} failed", name, labId);
            return null;
        }
    }

    /// <summary>Keeps the item under the name. Throws <see cref="InvalidOperationException" /> (with what to say to the person) when it cannot be kept.</summary>
    public void Save(string name, T value)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Give it a name first.");

        var item = Stored.At<T>(Prefix + name.Trim());
        if (!item.IsAvailable) throw new InvalidOperationException("Open a Silo to keep it in.");

        item.Set(value);
    }

    public bool Delete(string name) => Stored.At<T>(Prefix + name).Delete();
}
