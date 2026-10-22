using Silo.Providers.Data;

namespace Silo.DbModel.Global;

/// <summary>
///     A value of any shape, written as text: one row per key and person. The table behind <see cref="IStoredStore" />
///     and <c>Stored&lt;T&gt;</c>; what <c>your:green</c> (a constant of the person's own) and a shortcut key binding are
///     kept in.
/// </summary>
/// <remarks>
///     <see cref="UserEntity.User" /> says whose it is; a row without a user is the Silo's shared one. <see cref="Key" />
///     is compared without regard to case.
/// </remarks>
public class StoredItem : TimedUserEntity
{
    /// <summary>The value as text, written as <see cref="Format" /> says.</summary>
    public string Data { get; set; } = string.Empty;

    /// <summary>How <see cref="Data" /> is written (<c>json</c>).</summary>
    public string Format { get; set; } = "json";

    public int Id { get; set; }

    /// <summary>What the value is stored under, such as <c>your:green</c> or <c>shortcut:blue</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    ///     The assembly-qualified name of the type the value was written from. Information only: reading never creates a
    ///     type from it.
    /// </summary>
    public string TypeName { get; set; } = string.Empty;
}

/// <summary>
///     The same table in the scratch (X) database: what is only remembered for the session (where the person was), as
///     opposed to <see cref="StoredItem" />
///     in the main database, which the Silo keeps.
/// </summary>
public class SessionStoredItem : StoredItem
{
}