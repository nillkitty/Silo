using Silo.Contracts.Providers.Data;
using Silo.DbModel.Meta;

namespace Silo.Providers.Definitions;

/// <summary>The rules a defined syntax has to follow, kept apart from storage and screens so they can be tested alone.</summary>
public static class SyntaxRules
{
    /// <summary>The syntax with names trimmed, empty optional parts dropped and the parser key in lower case.</summary>
    public static SyntaxRecord Normalize(SyntaxRecord syntax)
    {
        return syntax with
               {
                   Name        = (syntax.Name ?? string.Empty).Trim(),
                   Alias       = Blank(syntax.Alias),
                   Description = Blank(syntax.Description),
                   Parser      = syntax.Kind == SyntaxKind.Parser ? Blank(syntax.Parser)?.ToLowerInvariant() : null,
                   Pattern     = syntax.Kind == SyntaxKind.Pattern && !string.IsNullOrEmpty(syntax.Pattern) ? syntax.Pattern : null,
                   Example     = string.IsNullOrEmpty(syntax.Example) ? null : syntax.Example
               };
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    ///     Everything wrong with the syntax, one message each (empty when it is acceptable). <paramref name="others" /> are the other syntaxes in the
    ///     Silo (not this one), whose names and aliases this one's must not repeat (case is ignored). The example, when there is one, has to be valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(SyntaxRecord syntax, IEnumerable<SyntaxRecord> others)
    {
        var s        = Normalize(syntax);
        var problems = new List<string>();
        var taken    = others.ToList();

        if (DefinitionNames.Check("The name", s.Name) is { } nameProblem) problems.Add(nameProblem);
        else if (Clash(s.Name, taken) is { } nameClash) problems.Add($"The name \"{s.Name}\" is already used by the syntax \"{nameClash}\".");

        if (s.Alias is not null)
        {
            if (DefinitionNames.Check("The alias", s.Alias) is { } aliasProblem) problems.Add(aliasProblem);
            else if (string.Equals(s.Alias, s.Name, StringComparison.OrdinalIgnoreCase)) problems.Add("The alias is the same as the name.");
            else if (Clash(s.Alias, taken) is { } aliasClash) problems.Add($"The alias \"{s.Alias}\" is already used by the syntax \"{aliasClash}\".");
        }

        if (!StoredSyntax.TryCreate(s, out var built, out string? buildError)) problems.Add(buildError ?? "The syntax cannot be built.");
        else if (s.Example is not null && !built!.TryParse(s.Example, out _, out string? exampleError)) problems.Add($"The example is not valid under this syntax: {exampleError}");

        return problems;
    }

    private static string? Clash(string name, IEnumerable<SyntaxRecord> others)
    {
        foreach (var o in others)
            if (string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase) || (o.Alias is not null && string.Equals(o.Alias, name, StringComparison.OrdinalIgnoreCase)))
                return o.Name;

        return null;
    }

    /// <summary>Throws a <see cref="DefinitionException" /> listing every problem, if there are any; otherwise gives the normalized syntax.</summary>
    public static SyntaxRecord Require(SyntaxRecord syntax, IEnumerable<SyntaxRecord> others)
    {
        var problems = Validate(syntax, others);
        if (problems.Count > 0) throw new DefinitionException(problems);
        return Normalize(syntax);
    }
}
