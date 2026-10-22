namespace Silo.Providers.Data;

/// <summary>
///     One row of the Silo's <c>Secret</c> table. <see cref="Data" /> is already sealed (or, for a <c>datakey</c>, locked
///     by a
///     protector): the store never sees what is inside.
/// </summary>
/// <param name="Id">0 for a row that has not been stored yet.</param>
/// <param name="Key">A name that is not secret: a credential's name, or <c>$datakey</c>.</param>
/// <param name="Kind">What the row holds: <c>credential</c>, <c>datakey</c>.</param>
/// <param name="Provider">For a <c>datakey</c>, the <c>ISecretProtector.Id</c> that locked it.</param>
public sealed record SecretRecord(int Id, string Key, string Kind, string Provider, byte[] Data, DateTime Created, DateTime Modified);

/// <summary>
///     Keeps secrets in the Silo (the <c>Secret</c> table). A person sees the rows made for them and the Silo's shared
///     ones (rows with no
///     person); what they write is made for them.
/// </summary>
/// <remarks>Writing is a real change to the Silo: it raises <see cref="IDatabaseChanges.Changed" />.</remarks>
public interface ISecretStore
{
    /// <summary>
    ///     The rows the current person sees, of one kind or all. The Silo's shared rows come first, then the person's
    ///     own, so the last of a kind is the one that wins.
    /// </summary>
    IReadOnlyList<SecretRecord> GetSecrets(string? kind = null);

    /// <summary>One row by id, if the current person sees it.</summary>
    SecretRecord? GetSecret(int id);

    /// <summary>
    ///     Adds the row (when its <see cref="SecretRecord.Id" /> is 0) or replaces the one it names. Returns the row as
    ///     stored.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The row names an id that is not there (or not visible).</exception>
    SecretRecord SaveSecret(SecretRecord secret);

    /// <summary>Removes the row. False if it was not there.</summary>
    bool DeleteSecret(int id);
}