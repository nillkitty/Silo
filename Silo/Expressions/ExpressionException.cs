namespace Silo.Expressions;

/// <summary>An expression or template that cannot be read, or that cannot be evaluated (an unknown variable, a bad argument).</summary>
public sealed class ExpressionException : Exception
{
    /// <summary>An error at a place in the source text.</summary>
    /// <param name="message">What is wrong, in a sentence a person can read.</param>
    /// <param name="position">Where in the source, counted from 0.</param>
    /// <param name="length">How many characters the error covers.</param>
    public ExpressionException(string message, int position, int length = 1) : base(message)
    {
        Position = position;
        Length   = Math.Max(1, length);
    }

    /// <summary>Where in the source text the problem is, counted from 0.</summary>
    public int Position { get; }

    /// <summary>How many characters of the source the problem covers.</summary>
    public int Length { get; }

    /// <summary>The name of the variable that does not exist, when that is what went wrong; null for any other error.</summary>
    public string? MissingVariableName { get; init; }

    /// <summary>The message with the position: <c>column 7: Unknown variable "your:blue".</c></summary>
    public string Describe() => $"column {Position + 1}: {Message}";
}
