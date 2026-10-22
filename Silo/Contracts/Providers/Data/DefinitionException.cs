namespace Silo.Providers.Data;

/// <summary>
///     A definition (an enum, a syntax) that cannot be saved as it stands: the message says what is wrong, in words that
///     can
///     be shown to the person who wrote it. Several problems are joined into one message, one per line.
/// </summary>
public sealed class DefinitionException : Exception
{
    /// <summary>Every problem found, one string each.</summary>
    public IReadOnlyList<string> Problems { get; }

    public DefinitionException(string message) : base(message)
    {
        Problems = [message];
    }

    public DefinitionException(IReadOnlyList<string> problems) : base(string.Join(Environment.NewLine, problems))
    {
        Problems = problems;
    }
}