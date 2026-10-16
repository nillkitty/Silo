using System.Linq;
using Silo.Model;

namespace Silo.Credentials;

/// <summary>What makes a credential acceptable to save. Pure, so it can be tested on its own.</summary>
public static class CredentialRules
{
    public const int MaxNameLength = 100;

    /// <summary>The reason the credential cannot be saved, or null when it can.</summary>
    /// <param name="credential">The credential to check.</param>
    /// <param name="others">The names (and ids) of the credentials that are already saved.</param>
    public static string? Check(Credential credential, IEnumerable<(int Id, string Name)> others)
    {
        string name = credential.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) return "Give the credential a name.";
        if (name.Length > MaxNameLength) return $"The name can be at most {MaxNameLength} characters.";
        if (name.StartsWith('$')) return "A name cannot start with $.";

        if (others.Any(o => o.Id != credential.Id && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
            return $"There is already a credential called \"{name}\".";

        if (string.IsNullOrEmpty(credential.Username) && string.IsNullOrEmpty(credential.Password))
            return "Enter a user name, a password, or both.";

        return null;
    }
}
