using System.Net;
using System.Text.RegularExpressions;

namespace Silo.ExternalTools;

/// <summary>
///     A DNS host or domain name ("www.example.com"), the target of the external tools registered for names. Only names made
///     of letters, digits, hyphens and underscores are taken, so a name can go on a command line as it is.
/// </summary>
public sealed partial record DomainName
{
    private DomainName(string value) => Value = value;

    /// <summary>The name, lower case, without the root dot.</summary>
    public string Value { get; }

    /// <summary>The first label ("www" of "www.example.com").</summary>
    public string FirstLabel => Value.Split('.')[0];

    public override string ToString() => Value;

    /// <summary>
    ///     Reads a name: labels of up to 63 letters, digits, hyphens and underscores (not starting or ending with a hyphen),
    ///     separated by dots, 253 characters at most; a trailing root dot is dropped. An IP address is not a name.
    /// </summary>
    public static bool TryParse(string? text, out DomainName? name)
    {
        name = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string value = text.Trim().TrimEnd('.').ToLowerInvariant();
        if (value.Length is 0 or > 253 || IPAddress.TryParse(value, out _) || !NamePattern().IsMatch(value)) return false;

        name = new DomainName(value);
        return true;
    }

    /// <summary>The name in <paramref name="text" />, or null when it is not one (see <see cref="TryParse" />).</summary>
    public static DomainName? From(string? text) => TryParse(text, out var name) ? name : null;

    [GeneratedRegex(@"^(?!-)[a-z0-9_-]{1,63}(?<!-)(\.(?!-)[a-z0-9_-]{1,63}(?<!-))*$")]
    private static partial Regex NamePattern();
}
