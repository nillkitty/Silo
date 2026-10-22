using System.IO;
using System.Windows.Markup;
using Silo.Providers.Data;

namespace Silo;

/// <summary>
///     An object permitted to be declared as a child of a Definition
/// </summary>
public interface IDefPart;

/// <summary>
///     A definition part that may repeat; see <see cref="Arity" />.
/// </summary>
public interface IHasArity
{
    /// <summary>
    ///     How many consecutive times the part matches; <see cref="Arity.None" /> takes the default for its context.
    /// </summary>
    Arity Arity { get; }
}

/// <summary>
///     Defines a Data Definition which can match against arbitrary binary data to locate
///     and deserialize the type of object it represents.
/// </summary>
/// <remarks>
///     Nested inside another definition, a definition is a group: its children match in sequence, and a
///     <see cref="DefinitionBase{TContract}.Name" /> gathers the captures made inside it under one node of that name.
/// </remarks>
public class DataDefinition : DefinitionBase<IDefPart>, IHasArity, IDataSpec
{
    public string? ContentType { get; set; }
    public Arity   Arity       { get; set; }

    /// <inheritdoc />
    public bool? MatchesData(Stream stream)
    {
        return this.Probe(stream);
    }
}

/// <summary>
///     How many times a part matches. <see cref="None" /> means unspecified: exactly once in a sequence, and any number of
///     times (<see cref="ZeroOrMore" />) as an element of a <see cref="Split" /> or <see cref="Lines" />.
/// </summary>
public enum Arity
{
    None,
    One,
    Required = One,
    ZeroOrOne,
    Optional = ZeroOrOne,
    OneOrMore,
    ZeroOrMore,
    Any = ZeroOrMore
}

/// <summary>
///     Defines a definition structure which matches plain text post-decoding,
///     prescribes which encoding to use (or any) and absorbs any encoding artifacts
///     such as BOMs.
/// </summary>
/// <remarks>
///     Text parts (<see cref="Lines" />, <see cref="Split" />, <see cref="Span" />) met in binary data are decoded as if
///     wrapped in a default <see cref="TextDefinition" />. Inside text that is already decoded, a
///     <see cref="TextDefinition" /> is a plain group.
/// </remarks>
public class TextDefinition : DefinitionBase<IDefPart>
{
    /// <summary>
    ///     If true, can match any supported encoding: a byte-order-mark decides it when present, otherwise UTF-8, then
    ///     UTF-16 (when the data looks like it), then Latin-1 are tried in turn.
    /// </summary>
    public bool AnyEncoding { get; set; } = true;

    /// <summary>
    ///     If true, absorbs a byte-order-mark from the beginning of a file
    /// </summary>
    public bool ConsumeBOM { get; set; } = true;

    /// <summary>
    ///     Explicitly specifies the encoding to use when <see cref="AnyEncoding" /> is false
    /// </summary>
    public Encoding? Encoding { get; set; } = Encoding.Default;
}

[ContentProperty(nameof(Items))]
[RuntimeNameProperty(nameof(Name))]
[DictionaryKeyProperty(nameof(Name))]
public abstract class DefinitionBase<TContract> : IDefPart
{
    /// <summary>
    ///     The element's child items
    /// </summary>
    public List<TContract> Items { get; } = [];

    /// <summary>
    ///     An optional name for this element
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>
///     Definition command that matches a single byte verbatim.
/// </summary>
public class Byte : IDefPart
{
    /// <summary>
    ///     The byte to match
    /// </summary>
    public byte Value { get; set; }

    public Byte()
    {
    }

    public Byte(byte value)
    {
        Value = value;
    }
}

/// <summary>
///     Definition command that matches a sequence of bytes verbatim. Set one of <see cref="Value" />, <see cref="Hex" />
///     or <see cref="Ascii" />.
/// </summary>
[ContentProperty(nameof(Hex))]
public class Bytes : IDefPart
{
    /// <summary>
    ///     The bytes to match, as text: each character (after <c>\</c> escapes such as <c>\x03</c>) is one byte, e.g.
    ///     <c>PK\x03\x04</c>
    /// </summary>
    public string? Ascii
    {
        get => Value is null ? null : Encoding.Latin1.GetString(Value);
        set => Value = value is null ? null : Encoding.Latin1.GetBytes(DefText.Unescape(value));
    }

    /// <summary>
    ///     The bytes to match, as hex digits; whitespace, commas, dashes and <c>0x</c> prefixes are ignored, e.g.
    ///     <c>89 50 4E 47</c>
    /// </summary>
    public string? Hex
    {
        get => Value is null ? null : Convert.ToHexString(Value);
        set => Value = value is null ? null : DefText.ParseHex(value);
    }

    /// <summary>
    ///     The bytes to match
    /// </summary>
    public byte[]? Value { get; set; }

    public Bytes()
    {
    }

    public Bytes(byte[] value)
    {
        Value = value;
    }
}