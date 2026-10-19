namespace Silo.Design;

/// <summary>
///     Which database of the Silo a thing is kept in: the columns of the storage placement sheet. The Explorer draws a
///     small badge for it on the thing's icon (see <c>RealmBadge</c>).
/// </summary>
public enum Realm
{
    /// <summary>Not known, or not stored in the Silo at all (a folder, a connection to the outside): no badge.</summary>
    None,

    /// <summary>Z: the Silo's own (global) database, <c>main</c>. Badge: a wireframe globe.</summary>
    Global,

    /// <summary>X: scratch, what is remembered at last exit, <c>x</c>. Badge: a warning triangle ("temporary").</summary>
    Scratch,

    /// <summary>Y: the metaverse, <c>y</c>. Badge: a cloud.</summary>
    Meta,

    /// <summary>W: a world, <c>w-&lt;key&gt;</c>. Badge: a globe with continents (turned differently for each world).</summary>
    World,

    /// <summary>U: universal, <c>u</c>. Badge: stars.</summary>
    Universal
}

/// <summary>
///     Implemented by the object behind a node (its <c>SiloNode.DataContext</c>) that is kept in one of the Silo's
///     databases, so the Explorer can badge its icon with where it lives.
/// </summary>
public interface IRealmed
{
    /// <summary>Where the thing is kept.</summary>
    Realm Realm { get; }

    /// <summary>For a thing in a <see cref="Realm.World" />: the key of the world (what makes one world's badge differ from another's). Otherwise null.</summary>
    string? WorldKey => null;
}

/// <summary>Names and aliases of the <see cref="Realm" />s.</summary>
public static class Realms
{
    /// <summary>The letter the placement sheet uses for the realm ("Z", "X", "Y", "W", "U"; empty for none).</summary>
    public static string Letter(Realm realm)
    {
        return realm switch
               {
                   Realm.Global    => "Z",
                   Realm.Scratch   => "X",
                   Realm.Meta      => "Y",
                   Realm.World     => "W",
                   Realm.Universal => "U",
                   _               => string.Empty
               };
    }

    /// <summary>The realm in words: "Global (the Silo's own)" ...</summary>
    public static string Name(Realm realm)
    {
        return realm switch
               {
                   Realm.Global    => "Global (the Silo's own)",
                   Realm.Scratch   => "Scratch (temporary)",
                   Realm.Meta      => "Metaverse",
                   Realm.World     => "World",
                   Realm.Universal => "Universal",
                   _               => "Not stored in the Silo"
               };
    }

    /// <summary>A sentence for a tooltip or the Properties pane: the realm, its letter, and the world when there is one.</summary>
    public static string Describe(Realm realm, string? worldKey = null)
    {
        if (realm == Realm.None) return Name(realm);

        string text = $"{Name(realm)} [{Letter(realm)}]";
        return realm == Realm.World && !string.IsNullOrWhiteSpace(worldKey) ? $"{text}: {worldKey}" : text;
    }

    /// <summary>The realm of a database alias (<c>EntitySchema</c>'s: "main", "x", "y", "u", and "w_&lt;key&gt;" / "w-&lt;key&gt;" for a world).</summary>
    public static Realm FromAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return Realm.None;

        return alias.Trim().ToLowerInvariant() switch
               {
                   "main" or "z" => Realm.Global,
                   "x"           => Realm.Scratch,
                   "y"           => Realm.Meta,
                   "u"           => Realm.Universal,
                   var a when a == "w" || a.StartsWith("w_", StringComparison.Ordinal) || a.StartsWith("w-", StringComparison.Ordinal) => Realm.World,
                   _ => Realm.None
               };
    }

    /// <summary>The world key in a world alias ("w_local" gives "local"); null for any other alias.</summary>
    public static string? WorldKeyFromAlias(string? alias)
    {
        if (alias is { Length: > 2 } && (alias.StartsWith("w_", StringComparison.OrdinalIgnoreCase) || alias.StartsWith("w-", StringComparison.OrdinalIgnoreCase)))
            return alias[2..];

        return null;
    }
}
