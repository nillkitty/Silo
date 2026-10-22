using System.Text;
using System.Text.RegularExpressions;

namespace Silo.Labs.Parser;

/// <summary>
///     The Parser Lab's terminal language: <c>"text" as &lt;target&gt;</c>, tried on the spec being built. The target is a rule (<c>mac-addr</c> or
///     <c>[mac-addr]</c>), <c>syntax:name</c> (a rule, or a spec kept in the Silo), <c>enum:Name</c> (one of the tokenizer's kinds), <c>type:Name</c>
///     (a plain CLR value: <c>int</c>, <c>guid</c>...) or <c>tokens</c> (cut it into tokens). More <c>as</c> convert the value on
///     (<c>"1234ABCD99A0" as mac-addr as hex-colon</c>). A line with no <c>as</c> is parsed with the spec's own rule.
/// </summary>
public static class ParserRepl
{
    private static readonly Regex AsWord = new(@"\s+as\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Runs a line; the answer is the text to show (several lines when it did not match).</summary>
    public static string Run(ParserWorkspace workspace, string line)
    {
        line = line.Trim();
        if (line.Length == 0) return string.Empty;

        var (text, tail) = Split(line);
        var targets = tail is null ? [] : AsWord.Split(" " + tail).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

        string? first = targets.FirstOrDefault();
        if (first is null) return Show(workspace, text, null, null);

        var    outcome = Resolve(workspace, text, first, out string label, out var spec);
        object? value  = outcome.Raw;
        if (!outcome.Success) return Failure(text, outcome);

        var sb = new StringBuilder();
        if (targets.Count == 1)
        {
            sb.Append(label).Append(": ").Append(Display(spec, outcome));
            return sb.ToString();
        }

        sb.Append(label).Append(": ").Append(outcome.Value);
        foreach (string format in targets.Skip(1))
        {
            try
            {
                string name = Unbracket(format);
                value = SpecCatalog.Formats.Contains(name, StringComparer.OrdinalIgnoreCase) ? SpecValues.ConvertTo(name, value) : SpecValues.Coerce(name, value);
                sb.AppendLine().Append("  → ").Append(name).Append(": ").Append(value is string s ? s : SpecValues.Format(value));
            }
            catch (Exception ex)
            {
                sb.AppendLine().Append("  → ").Append(format).Append(": ").Append(ex.Message);
                break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Splits the line into the text and what follows its <c>as</c>.</summary>
    private static (string Text, string? Tail) Split(string line)
    {
        if (line.StartsWith('"'))
        {
            var  sb = new StringBuilder();
            int  i  = 1;
            bool closed = false;
            for (; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\\' && i + 1 < line.Length)
                {
                    char n = line[++i];
                    sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => n });
                }
                else if (c == '"')
                {
                    closed = true;
                    i++;
                    break;
                }
                else
                {
                    sb.Append(c);
                }
            }

            if (closed)
            {
                string tail = line[i..].Trim();
                if (tail.Length == 0) return (sb.ToString(), null);
                if (tail.StartsWith("as ", StringComparison.OrdinalIgnoreCase)) return (sb.ToString(), tail);
            }
        }

        var m = AsWord.Match(line);
        return m.Success ? (line[..m.Index], line[(m.Index + m.Length)..]) : (line, null);
    }

    private static string Unbracket(string target)
    {
        target = target.Trim();
        return target.Length >= 2 && target[0] == '[' && target[^1] == ']' ? target[1..^1].Trim() : target;
    }

    private static string Show(ParserWorkspace ws, string text, string? rule, CompiledSpec? spec)
    {
        spec ??= ws.Compiled;
        var outcome = SpecEval.Eval(spec, text, rule);
        string label = spec.Doc.Mode == SpecMode.Tokenizer ? "tokens" : rule ?? spec.DefaultRule ?? "(no rule)";
        return outcome.Success ? $"{label}: {Display(spec, outcome)}" : Failure(text, outcome);
    }

    private static string Display(CompiledSpec? spec, EvalOutcome outcome)
    {
        // a spec that says how its value is usually written shows it that way (a MAC address as 1234.abcd.99a0)
        if (spec is not null && spec.Doc.Mode == SpecMode.Parser && outcome.Raw is byte[] && spec.Doc.ConvertsTo.FirstOrDefault(f => SpecCatalog.Formats.Contains(f, StringComparer.OrdinalIgnoreCase)) is { } format)
            return SpecValues.ConvertTo(format, outcome.Raw);

        return outcome.Value;
    }

    private static string Failure(string text, EvalOutcome outcome)
    {
        var sb = new StringBuilder();
        sb.Append("error: ").Append(outcome.Error ?? "no match");
        if (outcome.ErrorPosition >= 0)
        {
            sb.Append(" (at ").Append(outcome.ErrorPosition).Append(')');
            if (text.Length > 0 && text.Length < 200 && !text.Contains('\n') && outcome.ErrorPosition <= text.Length)
                sb.AppendLine().Append("  ").AppendLine(text).Append("  ").Append(' ', outcome.ErrorPosition).Append('^');
        }

        return sb.ToString();
    }

    /// <summary>Parses <paramref name="text" /> as what <paramref name="target" /> names.</summary>
    private static EvalOutcome Resolve(ParserWorkspace ws, string text, string target, out string label, out CompiledSpec? spec)
    {
        target = Unbracket(target);
        spec   = ws.Compiled;
        label  = target;

        string kind = string.Empty;
        string name = target;
        int    colon = target.IndexOf(':');
        if (colon > 0 && target[..colon] is "syntax" or "enum" or "type")
        {
            kind = target[..colon];
            name = target[(colon + 1)..].Trim();
        }

        if (kind is "" && string.Equals(name, "tokens", StringComparison.OrdinalIgnoreCase))
        {
            label = "tokens";
            return SpecEval.Tokens(spec, text);
        }

        if (kind == "enum") return ParseEnum(ws, text, name);

        if (kind == "type" || (kind == "" && ws.Doc.Find(name) is null && SpecStore.Names().All(n => !SameSpec(n, name)) && ClrTypes.Resolve(name) is not null))
        {
            label = name;
            return ParseType(text, name);
        }

        if (kind is "" or "syntax")
        {
            if (ws.Doc.Find(name) is { } rule)
            {
                label = rule.Name;
                return SpecEval.Eval(spec, text, rule.Name);
            }

            string? saved = SpecStore.Names().FirstOrDefault(n => SameSpec(n, name));
            if (saved is not null && SpecStore.Load(saved) is { } doc)
            {
                spec  = CompiledSpec.Compile(doc);
                label = doc.Name;
                return SpecEval.Eval(spec, text, null);
            }
        }

        label = name;
        return new EvalOutcome(false, string.Empty, $"there is no {(kind.Length > 0 ? kind : "rule, spec or type")} called '{name}'", -1, null);
    }

    private static bool SameSpec(string saved, string name)
    {
        return string.Equals(saved, name, StringComparison.OrdinalIgnoreCase) || saved.EndsWith("." + name, StringComparison.OrdinalIgnoreCase);
    }

    private static EvalOutcome ParseEnum(ParserWorkspace ws, string text, string name)
    {
        var tok = ws.Doc.Tokenizer;
        bool named = string.Equals(tok.TokenType, name, StringComparison.OrdinalIgnoreCase) || name.Length == 0;
        if (!named)
            return new EvalOutcome(false, string.Empty, $"there is no enum called '{name}' (only this spec's token type, {tok.TokenType}, is known here)", -1, null);

        string? kind = tok.Kinds.FirstOrDefault(k => string.Equals(k, text.Trim(), StringComparison.OrdinalIgnoreCase));
        return kind is null
                   ? new EvalOutcome(false, string.Empty, $"expected one of {string.Join(", ", tok.Kinds)}", 0, null)
                   : new EvalOutcome(true, $"{tok.TokenType}.{kind}", null, -1, kind, text.Length);
    }

    private static EvalOutcome ParseType(string text, string type)
    {
        try
        {
            object? value = SpecValues.Coerce(type, text.Trim());
            return new EvalOutcome(true, SpecValues.Format(value), null, -1, value, text.Length);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return new EvalOutcome(false, string.Empty, ex.Message, 0, null);
        }
    }
}
