using System.Globalization;
using Silo.Contracts.Providers.Data;
using Silo.DbModel.Meta;

namespace Silo.Providers.Definitions;

/// <summary>
///     The rules a defined enum has to follow, kept apart from storage and screens so they can be tested alone: what a value
///     of each <see cref="EnumType" /> looks like (and its one canonical text form), and what makes a whole enum acceptable.
/// </summary>
public static class EnumRules
{
    public const int MaxLabelLength = 128;

    /// <summary>Whether an enum of this type can be a bitmask (<see cref="EnumFlags.Bitmask" />): only integral and byte ones.</summary>
    public static bool SupportsBitmask(EnumType type) => type is EnumType.Integral or EnumType.Byte;

    /// <summary>
    ///     Reads a value of the given type and gives it in its canonical text form: decimal for integers (hex like <c>0x1F</c> is accepted),
    ///     the shortest round-trip form for floats, <c>true</c>/<c>false</c> for bools (also yes/no, on/off, 1/0), the <c>D</c> form for guids,
    ///     the text as written for strings.
    /// </summary>
    public static bool TryNormalizeValue(EnumType type, string? text, out string canonical, out string? error)
    {
        canonical = string.Empty;
        error     = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "A value is required.";
            return false;
        }

        string t = text.Trim();
        switch (type)
        {
            case EnumType.Integral:
                if (TryUnsigned(t, out ulong u))
                {
                    canonical = u.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                error = $"\"{t}\" is not a whole number of 0 or more (decimal, or hex like 0x1F).";
                return false;

            case EnumType.Byte:
                if (TryUnsigned(t, out ulong b) && b <= 255)
                {
                    canonical = b.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                error = $"\"{t}\" is not a byte (0 to 255).";
                return false;

            case EnumType.Signed:
                if (TrySigned(t, out long s))
                {
                    canonical = s.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                error = $"\"{t}\" is not a whole number.";
                return false;

            case EnumType.Float:
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d))
                {
                    canonical = d.ToString("R", CultureInfo.InvariantCulture);
                    return true;
                }

                error = $"\"{t}\" is not a number.";
                return false;

            case EnumType.Bool:
                switch (t.ToLowerInvariant())
                {
                    case "true" or "yes" or "on" or "1":
                        canonical = "true";
                        return true;
                    case "false" or "no" or "off" or "0":
                        canonical = "false";
                        return true;
                }

                error = $"\"{t}\" is not true or false.";
                return false;

            case EnumType.Guid:
                if (Guid.TryParse(t, out var g))
                {
                    canonical = g.ToString("D", CultureInfo.InvariantCulture);
                    return true;
                }

                error = $"\"{t}\" is not a guid.";
                return false;

            default: // String
                canonical = text;
                return true;
        }
    }

    private static bool TryUnsigned(string t, out ulong value)
    {
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(t.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

        return ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TrySigned(string t, out long value)
    {
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            bool ok = ulong.TryParse(t.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hex) && hex <= long.MaxValue;
            value   = ok ? (long)hex : 0;
            return ok;
        }

        return long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The enum with names trimmed, empty descriptions dropped and every value that can be read given in its canonical form (ones that cannot are left as typed, for <see cref="Validate" /> to report).</summary>
    public static EnumRecord Normalize(EnumRecord definition)
    {
        var values = new List<EnumValueRecord>(definition.Values.Count);
        foreach (var v in definition.Values)
        {
            string? value = v.Value;
            if (TryNormalizeValue(definition.Type, value, out string canonical, out _)) value = canonical;
            values.Add(v with { Name = (v.Name ?? string.Empty).Trim(), Value = value, Description = Blank(v.Description) });
        }

        return definition with { Name = (definition.Name ?? string.Empty).Trim(), Description = Blank(definition.Description), Values = values };
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    ///     Everything wrong with the enum, one message each (empty when it is acceptable). <paramref name="otherEnumNames" /> are the names of the
    ///     other enums in the Silo, which this one's name must not repeat (case is ignored). Expects a normalized enum; normalizes it itself otherwise.
    /// </summary>
    public static IReadOnlyList<string> Validate(EnumRecord definition, IEnumerable<string> otherEnumNames)
    {
        var e        = Normalize(definition);
        var problems = new List<string>();

        if (DefinitionNames.Check("The name", e.Name) is { } nameProblem) problems.Add(nameProblem);
        else if (otherEnumNames.Any(n => string.Equals(n, e.Name, StringComparison.OrdinalIgnoreCase))) problems.Add($"There is already an enum named \"{e.Name}\".");

        if (!Enum.IsDefined(e.Type)) problems.Add("The enum's type is not one of the known types.");

        if ((e.Flags & EnumFlags.Bitmask) != 0 && !SupportsBitmask(e.Type)) problems.Add($"A {e.Type} enum cannot be a bitmask; only Integral and Byte ones can.");

        if (e.Type == EnumType.Bool && e.Values.Count > 2) problems.Add("A Bool enum can have at most two values (true and false).");

        var names  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in e.Values)
        {
            if (v.Name.Length == 0)
            {
                problems.Add("A value has no name.");
            }
            else
            {
                if (v.Name.Length > MaxLabelLength) problems.Add($"The name \"{Shorten(v.Name)}\" is longer than {MaxLabelLength} characters.");
                if (!names.Add(v.Name)) problems.Add($"The name \"{v.Name}\" is used by more than one value.");
            }

            if (!TryNormalizeValue(e.Type, v.Value, out string canonical, out string? error))
            {
                problems.Add($"Value \"{(v.Name.Length > 0 ? v.Name : "(unnamed)")}\": {error}");
            }
            else if (values.TryGetValue(canonical, out string? first))
            {
                problems.Add($"The value {canonical} is given to both \"{first}\" and \"{v.Name}\".");
            }
            else
            {
                values[canonical] = v.Name;
            }
        }

        return problems;
    }

    private static string Shorten(string text) => text.Length <= 24 ? text : text[..24] + "...";

    /// <summary>Throws a <see cref="DefinitionException" /> listing every problem, if there are any.</summary>
    public static EnumRecord Require(EnumRecord definition, IEnumerable<string> otherEnumNames)
    {
        var problems = Validate(definition, otherEnumNames);
        if (problems.Count > 0) throw new DefinitionException(problems);
        return Normalize(definition);
    }

    /// <summary>The enum with that value added, or (when the value has an id the enum already has) replaced in place.</summary>
    public static EnumRecord WithValue(EnumRecord definition, EnumValueRecord value)
    {
        var values = definition.Values.ToList();
        int at     = value.Id > 0 ? values.FindIndex(v => v.Id == value.Id) : -1;
        if (at >= 0) values[at] = value;
        else values.Add(value with { Id = 0 }); // an id this enum does not have is stale: the value is new here
        return definition with { Values = values };
    }

    /// <summary>The enum without the value that has that id (unchanged when it has none).</summary>
    public static EnumRecord WithoutValue(EnumRecord definition, int valueId)
    {
        return definition with { Values = definition.Values.Where(v => v.Id != valueId).ToList() };
    }

    /// <summary>
    ///     A value to offer for the next one added to the enum, in canonical form: one more than the largest for numbers (the next bit, for a bitmask),
    ///     false then true for bools, a fresh guid for guids; empty when there is nothing sensible (strings, or the numbers have run out).
    /// </summary>
    public static string SuggestNextValue(EnumRecord definition)
    {
        var taken = definition.Values.Select(v => TryNormalizeValue(definition.Type, v.Value, out string c, out _) ? c : null).Where(c => c is not null).Select(c => c!).ToList();
        bool bitmask = (definition.Flags & EnumFlags.Bitmask) != 0;

        switch (definition.Type)
        {
            case EnumType.Integral or EnumType.Byte:
            {
                var numbers = taken.Select(c => ulong.Parse(c, CultureInfo.InvariantCulture)).ToList();
                ulong max   = numbers.Count == 0 ? 0 : numbers.Max();
                ulong next;
                if (numbers.Count == 0) next = bitmask ? 1UL : 0UL;
                else if (!bitmask) next = max == ulong.MaxValue ? max : max + 1;
                else if (max == 0) next = 1;
                else
                {
                    int top = System.Numerics.BitOperations.Log2(max);
                    next = top >= 63 ? 0 : 1UL << (top + 1);
                }

                if (next == 0 && numbers.Count > 0) return string.Empty;
                if (numbers.Contains(next)) return string.Empty;
                if (definition.Type == EnumType.Byte && next > 255) return string.Empty;
                return next.ToString(CultureInfo.InvariantCulture);
            }

            case EnumType.Signed:
            {
                var numbers = taken.Select(c => long.Parse(c, CultureInfo.InvariantCulture)).ToList();
                if (numbers.Count == 0) return "0";
                long max = numbers.Max();
                return max == long.MaxValue ? string.Empty : (max + 1).ToString(CultureInfo.InvariantCulture);
            }

            case EnumType.Float:
            {
                var numbers = taken.Select(c => double.Parse(c, CultureInfo.InvariantCulture)).ToList();
                return numbers.Count == 0 ? "0" : Math.Floor(numbers.Max() + 1).ToString("R", CultureInfo.InvariantCulture);
            }

            case EnumType.Bool:
                return !taken.Contains("false") ? "false" : !taken.Contains("true") ? "true" : string.Empty;

            case EnumType.Guid:
                return Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);

            default:
                return string.Empty;
        }
    }

    /// <summary>The label a value goes by in the enum, or null when none of its known values matches (the value is compared in its canonical form).</summary>
    public static string? LabelFor(EnumRecord definition, string? value)
    {
        if (!TryNormalizeValue(definition.Type, value, out string wanted, out _)) return null;

        foreach (var v in definition.Values)
            if (TryNormalizeValue(definition.Type, v.Value, out string have, out _) && string.Equals(have, wanted, StringComparison.Ordinal))
                return v.Name;

        return null;
    }

    /// <summary>The value (canonical text) of the known value with that label (case is ignored), or null.</summary>
    public static string? ValueFor(EnumRecord definition, string? label)
    {
        if (label is null) return null;

        foreach (var v in definition.Values)
            if (string.Equals(v.Name, label.Trim(), StringComparison.OrdinalIgnoreCase) && TryNormalizeValue(definition.Type, v.Value, out string canonical, out _))
                return canonical;

        return null;
    }
}
