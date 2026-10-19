using System.Text.RegularExpressions;

namespace Silo.Providers.Definitions;

/// <summary>What a name given to an enum or a syntax may look like, so that <c>enum:Name</c> and <c>syntax:Name</c> are always written the same way.</summary>
public static class DefinitionNames
{
    public const int MaxLength = 64;

    private static readonly Regex Shape = new(@"\A[A-Za-z_][A-Za-z0-9_.\-]*\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Whether the text is a usable name: letters, digits, underscore, dot and dash, not starting with a digit, at most <see cref="MaxLength" /> long.</summary>
    public static bool IsValid(string? name)
    {
        return name is { Length: > 0 and <= MaxLength } && Shape.IsMatch(name);
    }

    /// <summary>What is wrong with the name (called <paramref name="what" /> in the message), or null when it is fine.</summary>
    public static string? Check(string what, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return $"{what} is required.";
        if (name.Length > MaxLength) return $"{what} is longer than {MaxLength} characters.";
        if (!Shape.IsMatch(name)) return $"{what} \"{name}\" can only use letters, digits, underscore, dot and dash, and cannot start with a digit or dash.";
        return null;
    }

    /// <summary>The name an <c>enum:</c> or <c>syntax:</c> reference ends in: the part after the last slash, trimmed.</summary>
    public static string LastSegment(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return string.Empty;
        string text = reference.Trim();
        int slash   = text.LastIndexOf('/');
        return slash >= 0 ? text[(slash + 1)..].Trim() : text;
    }
}
