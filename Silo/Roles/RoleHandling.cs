using Newtonsoft.Json;
using Serilog;
using Silo.Storage;

namespace Silo.Roles;

/// <summary>What auto-add does with an item that a <see cref="RoleHandlingRule" /> matches.</summary>
public enum HandlingAction
{
    /// <summary>Add it (and update it when it changes).</summary>
    Add,

    /// <summary>Leave it out.</summary>
    Skip
}

/// <summary>
///     A setting for one combination of file type and data format: "scripts of type <c>ps1</c> get the role Maintenance",
///     "binaries are left out", "anything that matches the spec <c>Invoice</c> gets the role Invoice". A field left null matches
///     any value, so a rule can be about the kind alone, the file type alone, the data spec alone, or any mix.
/// </summary>
/// <param name="Kind">The <see cref="ItemKind" /> it is about (by name); null for any.</param>
/// <param name="Format">The file type (an extension without its dot); null for any.</param>
/// <param name="Spec">The name of the data spec; null for any.</param>
/// <param name="Action">What to do with a matching item.</param>
/// <param name="Role">The role to give a matching item instead of the one the auto-add was set up with; null to keep that one.</param>
public sealed record RoleHandlingRule(string? Kind, string? Format, string? Spec, HandlingAction Action = HandlingAction.Add, string? Role = null)
{
    /// <summary>Whether the rule is about the item's combination.</summary>
    public bool Matches(ItemClassification item)
    {
        return (Kind   is null || string.Equals(Kind,   item.Kind.ToString(), StringComparison.OrdinalIgnoreCase)) &&
               (Format is null || string.Equals(Format.TrimStart('.'), item.Format, StringComparison.OrdinalIgnoreCase)) &&
               (Spec   is null || string.Equals(Spec,   item.Spec,   StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>How specific the rule is: a file type or a data spec outweighs a kind, and both outweigh either alone.</summary>
    public int Specificity => (Format is null ? 0 : 4) + (Spec is null ? 0 : 2) + (Kind is null ? 0 : 1);
}

/// <summary>
///     The Silo's settings for how auto-add handles each kind of item. The most specific rule that matches an item wins; with
///     none, the item is added with the auto-add's own role. Kept in the Silo (shared) as JSON under
///     <see cref="StoredKey" />, so it can be opened and edited in the text editor from Saved Objects.
/// </summary>
public sealed class RoleHandlingTable(IReadOnlyList<RoleHandlingRule> rules)
{
    /// <summary>The key the rules are stored under.</summary>
    public const string StoredKey = "roles/handling";

    /// <summary>The rules.</summary>
    public IReadOnlyList<RoleHandlingRule> Rules { get; } = rules;

    /// <summary>The rule for the combination: the most specific match; null when none matches.</summary>
    public RoleHandlingRule? Find(ItemClassification item)
    {
        return Rules.Where(r => r.Matches(item)).OrderByDescending(r => r.Specificity).FirstOrDefault();
    }

    /// <summary>Reads the rules from the Silo (none when nothing is stored or it cannot be read).</summary>
    public static RoleHandlingTable Load(IStoredStore? store)
    {
        try
        {
            if (store?.GetStored(StoredKey, StoredScope.Shared) is { } record)
                return new RoleHandlingTable(JsonConvert.DeserializeObject<List<RoleHandlingRule>>(record.Data) ?? []);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Log.Warning(ex, "The auto-add handling rules could not be read");
        }

        return new RoleHandlingTable([]);
    }

    /// <summary>Writes the rules to the Silo.</summary>
    public void Save(IStoredStore store)
    {
        store.SetStored(StoredKey, StoredCodec.TypeNameOf(typeof(List<RoleHandlingRule>)), StoredCodec.Json,
                        JsonConvert.SerializeObject(Rules, Formatting.Indented), StoredScope.Shared);
    }
}
