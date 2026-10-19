using System.Collections;
using System.Globalization;
using Silo.Unicode;

namespace Silo.Expressions;

/// <summary>A function an expression can call: it takes the evaluated arguments and returns a value.</summary>
/// <remarks>Throw <see cref="ArgumentException" /> for a wrong argument; the evaluator reports it at the place of the call.</remarks>
public delegate object? ExpressionFunction(IReadOnlyList<object?> arguments);

/// <summary>The functions available to expressions: the built-in ones, and any a caller adds.</summary>
public sealed class ExpressionFunctions
{
    private readonly Dictionary<string, ExpressionFunction> _functions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The built-in functions, shared; do not add to it, make your own with <see cref="CreateDefault" />.</summary>
    public static ExpressionFunctions Default { get; } = CreateDefault();

    /// <summary>A new set holding the built-in functions: <c>upper</c>, <c>lower</c>, <c>trim</c>, <c>len</c>, <c>repeat</c>, <c>join</c>, <c>char</c>, <c>codepoint</c>, <c>hex</c>.</summary>
    public static ExpressionFunctions CreateDefault()
    {
        var f = new ExpressionFunctions();
        f.Add("upper",     a => Text(a, 1, "upper").ToUpperInvariant());
        f.Add("lower",     a => Text(a, 1, "lower").ToLowerInvariant());
        f.Add("trim",      a => Text(a, 1, "trim").Trim());
        f.Add("len",       a => (long)CodePoint.Enumerate(Text(a, 1, "len")).Count());
        f.Add("repeat",    Repeat);
        f.Add("join",      Join);
        f.Add("char",      Char);
        f.Add("codepoint", CodePointOf);
        f.Add("hex",       a => Number(a, 1, "hex").ToString("X", CultureInfo.InvariantCulture));
        return f;
    }

    /// <summary>Adds a function, or replaces the one with that name.</summary>
    public ExpressionFunctions Add(string name, ExpressionFunction function)
    {
        _functions[name] = function;
        return this;
    }

    /// <summary>Finds a function by name (not case sensitive).</summary>
    public bool TryGet(string name, out ExpressionFunction function) => _functions.TryGetValue(name, out function!);

    private static string Text(IReadOnlyList<object?> args, int count, string name)
    {
        if (args.Count != count) throw new ArgumentException($"{name}() takes {count} argument{(count == 1 ? string.Empty : "s")}, not {args.Count}.");
        return ExpressionValues.ToText(args[0]);
    }

    private static long Number(IReadOnlyList<object?> args, int count, string name)
    {
        if (args.Count != count) throw new ArgumentException($"{name}() takes {count} argument{(count == 1 ? string.Empty : "s")}, not {args.Count}.");
        return ExpressionValues.ToInteger(args[0], name);
    }

    private static object? Repeat(IReadOnlyList<object?> args)
    {
        if (args.Count != 2) throw new ArgumentException($"repeat() takes 2 arguments (text, count), not {args.Count}.");

        long n = ExpressionValues.ToInteger(args[1], "repeat");
        if (n is < 0 or > 10000) throw new ArgumentException("repeat() repeats 0 to 10000 times.");
        return string.Concat(Enumerable.Repeat(ExpressionValues.ToText(args[0]), (int)n));
    }

    private static object? Join(IReadOnlyList<object?> args)
    {
        if (args.Count < 1) throw new ArgumentException("join() takes a separator and then the values to join.");

        string sep   = ExpressionValues.ToText(args[0]);
        var    parts = new List<string>();
        foreach (var a in args.Skip(1))
            if (a is IEnumerable e and not string and not IEnumerable<CodePoint>) parts.AddRange(e.Cast<object?>().Select(ExpressionValues.ToText));
            else if (a is IEnumerable<CodePoint> cps) parts.AddRange(cps.Select(c => c.Text));
            else parts.Add(ExpressionValues.ToText(a));

        return string.Join(sep, parts);
    }

    private static object? Char(IReadOnlyList<object?> args)
    {
        if (args.Count == 0) throw new ArgumentException("char() takes one or more code points: char(0x1F7E2) or char('U+1F7E2').");

        var cps = new List<CodePoint>();
        foreach (var a in args)
        {
            switch (a)
            {
                case CodePoint cp:
                    cps.Add(cp);
                    break;
                case long or int:
                    long n = Convert.ToInt64(a, CultureInfo.InvariantCulture);
                    if (n is < 0 or > CodePoint.MaxValue) throw new ArgumentException($"{n} is not a code point (0 to 0x10FFFF).");
                    cps.Add(new CodePoint((int)n));
                    break;
                case string s when CodePoint.TryParseExplicit(s, out var parsed):
                    cps.Add(parsed);
                    break;
                default:
                    throw new ArgumentException($"\"{ExpressionValues.ToText(a)}\" is not a code point (write it like 0x1F7E2 or 'U+1F7E2').");
            }
        }

        return CodePoint.ToText(cps);
    }

    private static object? CodePointOf(IReadOnlyList<object?> args)
    {
        string text = Text(args, 1, "codepoint");
        var    cps  = CodePoint.Enumerate(text).ToList();
        if (cps.Count == 0) throw new ArgumentException("codepoint() needs at least one character.");
        return (long)cps[0].Value;
    }
}

/// <summary>How expression values become text, numbers and so on.</summary>
public static class ExpressionValues
{
    /// <summary>
    ///     The text a value is written as in a template: nothing for null, <c>true</c>/<c>false</c>, numbers in the invariant culture, a <see cref="CodePoint" /> as its
    ///     character, a list of code points as the characters one after the other, any other list as its items joined by commas.
    /// </summary>
    public static string ToText(object? value)
    {
        switch (value)
        {
            case null:                       return string.Empty;
            case string s:                   return s;
            case bool b:                     return b ? "true" : "false";
            case CodePoint cp:               return cp.Text;
            case IEnumerable<CodePoint> cps: return CodePoint.ToText(cps);
            case double d:                   return d.ToString("R", CultureInfo.InvariantCulture);
            case float f:                    return f.ToString("R", CultureInfo.InvariantCulture);
            case IFormattable x:             return x.ToString(null, CultureInfo.InvariantCulture);
            case IEnumerable e:              return string.Join(", ", e.Cast<object?>().Select(ToText));
            default:                         return value.ToString() ?? string.Empty;
        }
    }

    /// <summary>The value as a whole number.</summary>
    /// <exception cref="ArgumentException">It is not a number that is whole.</exception>
    public static long ToInteger(object? value, string forWhat)
    {
        switch (value)
        {
            case long l:   return l;
            case int i:    return i;
            case double d when d == Math.Floor(d) && Math.Abs(d) < 9e15: return (long)d;
            case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed): return parsed;
            case CodePoint cp: return cp.Value;
            default: throw new ArgumentException($"{forWhat}() needs a whole number, not \"{ToText(value)}\".");
        }
    }

    /// <summary>Whether the value is a number (an integer or floating-point one).</summary>
    public static bool IsNumber(object? value) => value is long or int or double or float or short or byte or decimal;
}
