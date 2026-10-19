namespace Silo.Expressions;

/// <summary>A node of a parsed expression. The tree is immutable, so a parsed <see cref="Template" /> can be kept and evaluated again and again.</summary>
/// <param name="Position">Where in the source text the node starts.</param>
public abstract record Expr(int Position)
{
    /// <summary>The node as source text again (not always identical to what was parsed, but it parses to the same tree).</summary>
    public abstract string ToSource();
}

/// <summary>A number, a piece of text, <c>true</c>, <c>false</c> or <c>null</c>.</summary>
public sealed record LiteralExpr(int Position, object? Value) : Expr(Position)
{
    /// <inheritdoc />
    public override string ToSource() => Value switch
    {
        null     => "null",
        string s => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "'",
        bool b   => b ? "true" : "false",
        _        => Convert.ToString(Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };
}

/// <summary>A variable: <c>name</c>, or <c>prefix:name</c> for one that belongs to a namespace such as <c>your:</c>.</summary>
public sealed record VariableExpr(int Position, string? Prefix, string Name) : Expr(Position)
{
    /// <summary>The name as written: <c>your:blue</c>.</summary>
    public string FullName => Prefix is null ? Name : Prefix + ":" + Name;

    /// <inheritdoc />
    public override string ToSource() => FullName;
}

/// <summary>A member of a value: <c>target.name</c> (a key of a dictionary, or <c>length</c> / <c>count</c> of text and lists).</summary>
public sealed record MemberExpr(int Position, Expr Target, string Name) : Expr(Position)
{
    /// <inheritdoc />
    public override string ToSource() => Target.ToSource() + "." + Name;
}

/// <summary>An element of a list or text, or an entry of a dictionary: <c>target[index]</c>.</summary>
public sealed record IndexExpr(int Position, Expr Target, Expr Index) : Expr(Position)
{
    /// <inheritdoc />
    public override string ToSource() => Target.ToSource() + "[" + Index.ToSource() + "]";
}

/// <summary>A call of a function: <c>upper(your:blue)</c>.</summary>
public sealed record CallExpr(int Position, string Name, IReadOnlyList<Expr> Arguments) : Expr(Position)
{
    /// <inheritdoc />
    public override string ToSource() => Name + "(" + string.Join(", ", Arguments.Select(a => a.ToSource())) + ")";
}

/// <summary>An operator applied to two values: <c>+</c> (adds numbers, joins text) or <c>??</c> (the left value unless it is missing or null).</summary>
public sealed record BinaryExpr(int Position, string Operator, Expr Left, Expr Right) : Expr(Position)
{
    /// <inheritdoc />
    public override string ToSource() => "(" + Left.ToSource() + " " + Operator + " " + Right.ToSource() + ")";
}
