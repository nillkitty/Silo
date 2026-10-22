using Silo.DbModel.Meta;
using Silo.Providers.Data;
using Silo.Providers.Definitions;

namespace Silo.Labs.Parser;

/// <summary>The token type of a spec: an enum of the Silo, or one the spec defines on the spot (and may add to the Silo's Enums).</summary>
public static class SpecEnums
{
    /// <summary>The enum the tokenizer's kinds make: whole numbers in the order of the kinds, 0 up.</summary>
    public static EnumRecord ToRecord(TokenizerSpec tokenizer)
    {
        var values = tokenizer.Kinds.Select((k, i) => new EnumValueRecord(0, k, i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        return new EnumRecord(0, tokenizer.TokenType, null, EnumType.Integral, EnumFlags.None, values);
    }

    /// <summary>What is wrong with the token type, one message each: the name, kinds used twice, kinds the rules make that the enum does not have.</summary>
    public static IReadOnlyList<string> Problems(ParserSpecDoc doc, SpecEnvironment env)
    {
        var t = doc.Tokenizer;
        if (doc.Mode != SpecMode.Tokenizer && t.Kinds.Count == 0 && t.Rules.Count == 0) return [];

        var problems = new List<string>();
        var store    = env.Enums;
        var known    = t.Kinds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (t.ExistingEnum)
        {
            if (store is not null)
            {
                if (store.FindEnum(t.TokenType) is not { } found) problems.Add($"There is no enum called '{t.TokenType}' in the Silo.");
                else known = found.Values.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }
        else
        {
            var others = store?.GetEnums().Select(e => e.Name) ?? [];
            problems.AddRange(EnumRules.Validate(ToRecord(t), others));
        }

        for (int i = 0; i < t.Rules.Count; i++)
            if (t.Rules[i].Kind is { } kind && !known.Contains(kind))
                problems.Add($"Tokenizer rule {i + 1} makes '{kind}', which is not a value of {t.TokenType}.");

        return problems;
    }

    /// <summary>
    ///     Makes the spec's token type agree with the Silo: a name that is an enum there is that enum (its values become the kinds); an enum
    ///     that has gone from the Silo is kept as one the spec defines. With no Silo nothing changes.
    /// </summary>
    public static void Resolve(TokenizerSpec t, SpecEnvironment env)
    {
        if (env.Enums is not { } store) return;

        if (store.FindEnum(t.TokenType) is { } found)
        {
            t.ExistingEnum   = true;
            t.IncludeInEnums = false;
            t.Kinds          = found.Values.Select(v => v.Name).ToList();
        }
        else if (t.ExistingEnum)
        {
            t.ExistingEnum = false;
        }
    }

    /// <summary>
    ///     When the spec defines its token type and asks to include it: adds the enum to the Silo's Enums, after which it is an enum of the Silo
    ///     that the spec uses. Returns whether one was added. Throws <see cref="DefinitionException" /> when the enum is not acceptable.
    /// </summary>
    public static bool Include(ParserSpecDoc doc, SpecEnvironment env)
    {
        var t = doc.Tokenizer;
        if (t.ExistingEnum || !t.IncludeInEnums || env.Enums is not { } store) return false;

        var saved = store.SaveEnum(EnumRules.Require(ToRecord(t), store.GetEnums().Select(e => e.Name)));
        t.ExistingEnum   = true;
        t.IncludeInEnums = false;
        t.Kinds          = saved.Values.Select(v => v.Name).ToList();
        return true;
    }

    /// <summary>Adds a kind if the name is free (any case); returns what is wrong with it, or null when it was added.</summary>
    public static string? AddKind(TokenizerSpec t, string name)
    {
        if (CheckKind(t, name) is { } problem) return problem;

        t.Kinds.Add(name.Trim());
        return null;
    }

    /// <summary>What is wrong with adding a kind of this name; null when it can be added.</summary>
    public static string? CheckKind(TokenizerSpec t, string name)
    {
        name = (name ?? string.Empty).Trim();
        if (t.ExistingEnum) return $"{t.TokenType} is an enum of the Silo: change its values in the Enums view.";
        if (name.Length == 0) return "A kind needs a name.";
        if (name.Length > EnumRules.MaxLabelLength) return $"A kind's name is at most {EnumRules.MaxLabelLength} characters.";
        if (t.Kinds.Contains(name, StringComparer.OrdinalIgnoreCase)) return $"There is already a kind called '{name}' (names are the same whatever their case).";

        return null;
    }
}

/// <summary>The checks of a spec that need more than the spec: its token type against the Silo's enums, its imports against the other specs.</summary>
public static class SpecChecks
{
    /// <summary>Every problem of the spec that the compiler alone cannot see.</summary>
    public static IReadOnlyList<SpecDiagnostic> Run(ParserSpecDoc doc, SpecEnvironment env)
    {
        var list = new List<SpecDiagnostic>();

        foreach (string problem in SpecEnums.Problems(doc, env)) list.Add(new SpecDiagnostic("(token type)", problem));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in doc.Fragments)
            if (!seen.Add(f.Name)) list.Add(new SpecDiagnostic("(saved)", $"There are two saved pieces called '{f.Name}'."));
            else if (f.Kind == FragmentKind.Token && SpecCatalog.Token(f.Name) is not null) list.Add(new SpecDiagnostic("(saved)", $"The token '{f.Name}' has the name of a built-in token."));

        if (SpecImports.FindCycle(doc, env.LoadSpec) is { } cycle)
            list.Add(new SpecDiagnostic("(imports)", "Specs cannot import each other in a circle: " + string.Join(" → ", cycle)));

        foreach (string import in doc.Imports)
            if (!string.Equals(import, doc.Name, StringComparison.OrdinalIgnoreCase) && env.LoadSpec(import) is null)
                list.Add(new SpecDiagnostic("(imports)", $"The imported spec '{import}' is not in the Silo."));

        return list;
    }
}
