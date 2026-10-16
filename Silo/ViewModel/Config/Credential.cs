using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Documents;

using Silo.Contracts.Providers.Data.Validation;
using Silo.DbModel.Global;

namespace Silo.Model;

public class SiloItem(OpenSilo parent)
{
    public OpenSilo          Parent { get; } = parent.Required();
    public IDatabaseProvider Data { get; } = parent.Required().Data.Required();
}

/// <summary>
/// An item stored in the silo's global database.
/// </summary>
/// <param name="parent"></param>
public class GlobalItem(OpenSilo parent) : SiloItem(parent)
{
}

/// <summary>
///     A saved way of signing in: a name for it, a user name, a domain, a password and notes. It is kept in the Silo's <c>Secret</c> table,
///     sealed by the Silo's encryption (see <see cref="Silo.Credentials.CredentialService" />); only the name is stored in the clear, so
///     the list of credentials can be shown without opening anything. A connector that takes a credential finds it in the
///     <c>Metadata</c> of its connection request.
/// </summary>
/// <remarks><see cref="ToString" /> gives the name only: a credential is safe to put in a log line or a message.</remarks>
public sealed class Credential
{
    /// <summary>The row in the Silo's <c>Secret</c> table; 0 until it is saved.</summary>
    public int Id { get; set; }

    /// <summary>What the person calls it: "Sales server", "Contoso admin". Required; shown in lists.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Username { get; set; }
    public string? Domain   { get; set; }
    public string? Password { get; set; }
    public string? Notes    { get; set; }

    public DateTime Created  { get; set; } = DateTime.UtcNow;
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    /// <summary>The account as it is written to sign in: <c>DOMAIN\user</c>, else the user name; null when there is no user name.</summary>
    public string? Account => string.IsNullOrEmpty(Username) ? null : string.IsNullOrEmpty(Domain) ? Username : Domain + "\\" + Username;

    public Credential Clone() => (Credential)MemberwiseClone();

    public override string ToString() => Name;
}
