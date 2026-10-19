namespace Silo;

/// <summary>
///     In a data definition, succeeds without consuming anything when its children do NOT match here (negative
///     look-ahead), e.g. to stop a repeated part before a footer.
/// </summary>
public class Not : DefinitionBase<IDefPart>
{
}

/// <summary>
///     In a data definition, reads an integer from binary data. Inside a <see cref="Capture" /> the number becomes the
///     captured value, and <see cref="Let" /> makes it available to a later <see cref="Block" />.
/// </summary>
public class Int : IDefPart
{
    /// <summary>
    ///     Whether the integer is stored most significant byte first
    /// </summary>
    public bool BigEndian { get; set; }

    /// <summary>
    ///     If specified, names a variable that holds the integer for later parts (see <see cref="Block.Length" />)
    /// </summary>
    public string? Let { get; set; }

    /// <summary>
    ///     If specified, the largest value accepted
    /// </summary>
    public long? Max { get; set; }

    /// <summary>
    ///     If specified, the smallest value accepted
    /// </summary>
    public long? Min { get; set; }

    /// <summary>
    ///     Whether the integer is two's-complement signed
    /// </summary>
    public bool Signed { get; set; }

    /// <summary>
    ///     The width in bytes: 1, 2, 4 or 8
    /// </summary>
    public int Size { get; set; } = 4;

    /// <summary>
    ///     If specified, the only value accepted (e.g. a version number)
    /// </summary>
    public long? Value { get; set; }
}

/// <summary>
///     In a data definition, bounds its children to the next <see cref="Length" /> bytes, which they must match whole
///     (a length-prefixed record or chunk).
/// </summary>
public class Block : DefinitionBase<IDefPart>
{
    /// <summary>
    ///     Added to the length, e.g. <c>-4</c> when a stored length counts its own four bytes
    /// </summary>
    public int Adjust { get; set; }

    /// <summary>
    ///     The length in bytes: a number, or the name of a variable set by an <see cref="Int" />'s <see cref="Int.Let" />
    /// </summary>
    public string? Length { get; set; }
}
