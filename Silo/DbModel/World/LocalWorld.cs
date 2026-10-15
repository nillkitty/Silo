namespace Silo.DbModel.Worlds;

/// <summary>
///     The built-in <b>local world</b>: the World that is authoritative for data created inside the Silo itself rather than
///     synchronised from somewhere else -- tables typed into the table editor (and, later, pasted data, notes and the like).
///     It has no connector and no upstream; its data is the true copy. It is created on first use, always has the key
///     <c>local</c> (so its database is <c>w-local.db</c> in the archive format) and is flagged
///     <see cref="WorldFlags.Authority" />.
/// </summary>
public static class LocalWorld
{
    /// <summary>The World key (used in the database file name, so it never changes).</summary>
    public const string Key = "local";

    /// <summary>The provider name stored on the World and its types: there is no connector behind it.</summary>
    public const string Provider = "local";

    /// <summary>The schema alias the world's tables are attached as (see <c>SqliteDatabaseProvider</c>).</summary>
    public const string Alias = "w_" + Key;

    /// <summary>A fresh instance of the local World record (its id is always 1: it is the only row in its own database).</summary>
    public static World Create()
    {
        return new World
               {
                   Id          = 1,
                   Key         = Key,
                   DisplayName = "Local (authored in this Silo)",
                   Provider    = Provider,
                   Uri         = new Uri("silo://local/"),
                   Flags       = WorldFlags.Authority
               };
    }
}
