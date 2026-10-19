using System.Text;

namespace Silo.Expressions;

/// <summary>A piece of a <see cref="Template" />: some text, or an expression to be replaced by its value.</summary>
/// <param name="Position">Where in the template source the piece starts.</param>
/// <param name="Length">How many characters of the source it covers.</param>
public abstract record TemplatePart(int Position, int Length);

/// <summary>Text that is written as it is (with <c>$${</c> already turned into <c>${</c>).</summary>
public sealed record TextPart(int Position, int Length, string Text) : TemplatePart(Position, Length);

/// <summary>A <c>${expression}</c>.</summary>
public sealed record ExpressionPart(int Position, int Length, Expr Expression) : TemplatePart(Position, Length);

/// <summary>
///     Text with expressions in it, <c>Hello ${your:name}, that is ${upper(your:blue)}</c>: <see cref="Evaluate" /> replaces every <c>${...}</c> by the
///     value of the expression inside. <c>$${</c> is a literal <c>${</c>.
/// </summary>
/// <remarks>A template is read once (<see cref="Parse" />) and can then be evaluated as often as wanted, against different scopes.</remarks>
public sealed class Template
{
    private Template(string source, IReadOnlyList<TemplatePart> parts)
    {
        Source = source;
        Parts  = parts;
        Variables = parts.OfType<ExpressionPart>().SelectMany(p => ExpressionEvaluator.VariablesOf(p.Expression)).ToList();
    }

    /// <summary>The text the template was read from.</summary>
    public string Source { get; }

    /// <summary>The template's pieces, in order.</summary>
    public IReadOnlyList<TemplatePart> Parts { get; }

    /// <summary>The variables its expressions refer to, in order (a variable used twice is listed twice).</summary>
    public IReadOnlyList<VariableExpr> Variables { get; }

    /// <summary>Whether the template has no expressions, so it is always the same text.</summary>
    public bool IsLiteral => Parts.All(p => p is TextPart);

    /// <summary>Reads a template.</summary>
    /// <exception cref="ExpressionException">An expression in it is wrong (or a <c>${</c> is never closed).</exception>
    public static Template Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var parts = new List<TemplatePart>();
        var text  = new StringBuilder();
        int textStart = 0;
        int i = 0;

        void FlushText(int end)
        {
            if (text.Length > 0) parts.Add(new TextPart(textStart, end - textStart, text.ToString()));
            text.Clear();
        }

        while (i < source.Length)
        {
            if (source[i] == '$' && i + 2 < source.Length && source[i + 1] == '$' && source[i + 2] == '{')
            {
                // $${ is a literal ${
                if (text.Length == 0) textStart = i;
                text.Append("${");
                i += 3;
                continue;
            }

            if (source[i] == '$' && i + 1 < source.Length && source[i + 1] == '{')
            {
                FlushText(i);
                int start = i;
                var expr  = ExpressionParser.ParseInTemplate(source, i + 2, out int end);
                parts.Add(new ExpressionPart(start, end - start, expr));
                i         = end;
                textStart = i;
                continue;
            }

            if (text.Length == 0) textStart = i;
            text.Append(source[i]);
            i++;
        }

        FlushText(source.Length);
        return new Template(source, parts);
    }

    /// <summary>Reads a template without throwing; <paramref name="error" /> says what is wrong when it is false.</summary>
    public static bool TryParse(string? source, out Template? template, out ExpressionException? error)
    {
        template = null;
        error    = null;
        if (source is null)
        {
            error = new ExpressionException("There is no text.", 0);
            return false;
        }

        try
        {
            template = Parse(source);
            return true;
        }
        catch (ExpressionException ex)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>The template's text with every expression replaced by its value.</summary>
    /// <exception cref="ExpressionException">An expression fails (an undefined variable, with <see cref="MissingVariableBehavior.Throw" />).</exception>
    public string Evaluate(IVariableScope scope, EvaluationOptions? options = null)
    {
        options ??= new EvaluationOptions();

        var sb = new StringBuilder();
        foreach (var part in Parts)
        {
            switch (part)
            {
                case TextPart t:
                    sb.Append(t.Text);
                    break;
                case ExpressionPart e:
                    try
                    {
                        sb.Append(ExpressionValues.ToText(ExpressionEvaluator.Evaluate(e.Expression, scope, options)));
                    }
                    catch (ExpressionException ex) when (ex.MissingVariableName is not null && options.Missing == MissingVariableBehavior.KeepPlaceholder)
                    {
                        sb.Append(Source, e.Position, e.Length);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Reads <paramref name="source" /> and evaluates it in one go.</summary>
    public static string Expand(string source, IVariableScope scope, EvaluationOptions? options = null) => Parse(source).Evaluate(scope, options);
}
