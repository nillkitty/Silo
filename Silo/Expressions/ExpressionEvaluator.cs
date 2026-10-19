using System.Collections;
using System.Globalization;
using Silo.Unicode;

namespace Silo.Expressions;

/// <summary>What an evaluation does with a variable that does not exist.</summary>
public enum MissingVariableBehavior
{
    /// <summary>Fail with an <see cref="ExpressionException" /> that names the variable and says where it is.</summary>
    Throw,

    /// <summary>Treat it as null (nothing is written for it in a template).</summary>
    Empty,

    /// <summary>In a template, leave the <c>${...}</c> as it was written.</summary>
    KeepPlaceholder
}

/// <summary>The settings of an evaluation.</summary>
public sealed record EvaluationOptions
{
    /// <summary>What to do about a variable that is not defined. The default is <see cref="MissingVariableBehavior.Throw" />.</summary>
    public MissingVariableBehavior Missing { get; init; } = MissingVariableBehavior.Throw;

    /// <summary>The functions an expression can call; the built-in ones when null.</summary>
    public ExpressionFunctions? Functions { get; init; }
}

/// <summary>Evaluates parsed expressions against a scope.</summary>
public static class ExpressionEvaluator
{
    /// <summary>The value of the expression.</summary>
    /// <exception cref="ExpressionException">A variable or function does not exist, or an operation does not fit its values.</exception>
    public static object? Evaluate(Expr expr, IVariableScope scope, EvaluationOptions? options = null)
    {
        options ??= new EvaluationOptions();
        return Eval(expr, scope, options, lenient: options.Missing == MissingVariableBehavior.Empty);
    }

    /// <summary>Reads and evaluates the text as one expression.</summary>
    public static object? Evaluate(string source, IVariableScope scope, EvaluationOptions? options = null) => Evaluate(ExpressionParser.Parse(source), scope, options);

    /// <summary>The variables an expression refers to, in the order they appear.</summary>
    public static IReadOnlyList<VariableExpr> VariablesOf(Expr expr)
    {
        var list = new List<VariableExpr>();
        Walk(expr, list);
        return list;
    }

    private static void Walk(Expr e, List<VariableExpr> list)
    {
        switch (e)
        {
            case VariableExpr v:
                list.Add(v);
                break;
            case MemberExpr m:
                Walk(m.Target, list);
                break;
            case IndexExpr i:
                Walk(i.Target, list);
                Walk(i.Index, list);
                break;
            case CallExpr c:
                foreach (var a in c.Arguments) Walk(a, list);
                break;
            case BinaryExpr b:
                Walk(b.Left, list);
                Walk(b.Right, list);
                break;
        }
    }

    private static object? Eval(Expr e, IVariableScope scope, EvaluationOptions options, bool lenient)
    {
        switch (e)
        {
            case LiteralExpr l:
                return l.Value;

            case VariableExpr v:
                if (scope.TryGetVariable(v.Prefix, v.Name, out object? value)) return value;
                if (lenient) return null;
                throw new ExpressionException($"There is no variable \"{v.FullName}\".", v.Position, v.FullName.Length) { MissingVariableName = v.FullName };

            case MemberExpr m:
                return Member(m, Eval(m.Target, scope, options, lenient));

            case IndexExpr i:
                return Index(i, Eval(i.Target, scope, options, lenient), Eval(i.Index, scope, options, lenient));

            case CallExpr c:
            {
                var functions = options.Functions ?? ExpressionFunctions.Default;
                if (!functions.TryGet(c.Name, out var function)) throw new ExpressionException($"There is no function \"{c.Name}\".", c.Position, c.Name.Length);

                var args = c.Arguments.Select(a => Eval(a, scope, options, lenient)).ToList();
                try
                {
                    return function(args);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidCastException or FormatException or OverflowException)
                {
                    throw new ExpressionException(ex.Message, c.Position, c.Name.Length);
                }
            }

            case BinaryExpr { Operator: "??" } b:
            {
                // the point of ?? is to be told "missing", so the left side never fails for a missing variable
                object? left = Eval(b.Left, scope, options, lenient: true);
                return left ?? Eval(b.Right, scope, options, lenient);
            }

            case BinaryExpr { Operator: "+" } b:
                return Add(Eval(b.Left, scope, options, lenient), Eval(b.Right, scope, options, lenient));

            default:
                throw new ExpressionException($"The expression {e.GetType().Name} cannot be evaluated.", e.Position);
        }
    }

    private static object? Add(object? left, object? right)
    {
        if (ExpressionValues.IsNumber(left) && ExpressionValues.IsNumber(right))
        {
            if (left is long or int or short or byte && right is long or int or short or byte)
                return Convert.ToInt64(left, CultureInfo.InvariantCulture) + Convert.ToInt64(right, CultureInfo.InvariantCulture);
            return Convert.ToDouble(left, CultureInfo.InvariantCulture) + Convert.ToDouble(right, CultureInfo.InvariantCulture);
        }

        return ExpressionValues.ToText(left) + ExpressionValues.ToText(right);
    }

    private static object? Member(MemberExpr m, object? target)
    {
        switch (target)
        {
            case null:
                return null;
            case IDictionary<string, object?> dict:
                return dict.TryGetValue(m.Name, out object? v) ? v : null;
            case string s when m.Name.Equals("length", StringComparison.OrdinalIgnoreCase):
                return (long)CodePoint.Enumerate(s).Count();
            case CodePoint cp when m.Name.Equals("hex", StringComparison.OrdinalIgnoreCase):
                return cp.Hex;
            case CodePoint cp when m.Name.Equals("value", StringComparison.OrdinalIgnoreCase):
                return (long)cp.Value;
            case ICollection c when m.Name.Equals("count", StringComparison.OrdinalIgnoreCase) || m.Name.Equals("length", StringComparison.OrdinalIgnoreCase):
                return (long)c.Count;
            default:
                throw new ExpressionException($"\"{m.Name}\" is not a member of {Describe(target)}.", m.Position);
        }
    }

    private static object? Index(IndexExpr e, object? target, object? index)
    {
        switch (target)
        {
            case null:
                return null;
            case IDictionary<string, object?> dict:
                return dict.TryGetValue(ExpressionValues.ToText(index), out object? v) ? v : null;
            case string s:
            {
                var cps = CodePoint.Enumerate(s).ToList();
                long i = ExpressionValues.ToInteger(index, "index");
                if (i < 0 || i >= cps.Count) throw new ExpressionException($"There is no character {i} in a text of {cps.Count}.", e.Position);
                return cps[(int)i].Text;
            }
            case IList list:
            {
                long i = ExpressionValues.ToInteger(index, "index");
                if (i < 0 || i >= list.Count) throw new ExpressionException($"There is no item {i} in a list of {list.Count}.", e.Position);
                return list[(int)i];
            }
            default:
                throw new ExpressionException($"{Describe(target)} cannot be indexed.", e.Position);
        }
    }

    private static string Describe(object target) => target switch
    {
        string => "text",
        CodePoint => "a code point",
        IEnumerable => "a list",
        _ => target.GetType().Name
    };
}
