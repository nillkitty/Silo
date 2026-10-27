namespace Silo.DbModel.Meta;

/// <summary>
///     Represents a user-defined enumeration which ties known values to labels
/// </summary>
public class MvEnum : ItemContainer<MvEnumValue>
{
    /// <summary>
    ///     Flags related to this enum type
    /// </summary>
    public EnumFlags Flags { get; set; }

    /// <summary>Primary key</summary>
    public int Id { get; set; }

    /// <summary>Unique short name for the type</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Defines the type of values each MvEnumValue represents (ints, strings, guids)
    ///     as well as how they are handled internally and displayed.
    /// </summary>
    public EnumType Type { get; set; }

    /// <summary>What the enum is for, in a sentence</summary>
    public string? Description { get; set; }
}

/// <summary>
///     Values for types of enum handling
/// </summary>
public enum EnumType
{
    /// <summary>
    ///     Values of the enum are integers;  handled as int, uint, long, or ulong based on size needed.
    /// </summary>
    Integral = 0,

    /// <summary>
    ///     Values of the enum are signed values;  handled as int or long based on size needed.
    /// </summary>
    Signed = 1,

    /// <summary>
    ///     Values of the enum are float values;  handled as double
    /// </summary>
    Float = 2,

    /// <summary>
    ///     Values of the enum are strings;  handled as the stored native string values
    /// </summary>
    String = 3,

    /// <summary>
    ///     Values of the enum are bools (obvious limit of 2 per) -- used for giving meaningful
    ///     labels to 'true' and 'false' values for a given attribute or class of attributes.
    /// </summary>
    Bool = 4,

    /// <summary>
    ///     Values of the enum are unsigned byte values (0-255); handled as uint8 and displayed
    ///     in hexadecimal along with the label.
    /// </summary>
    Byte = 5,

    /// <summary>
    ///     Values of the enum are Guids (e.g. ShellClsid)
    /// </summary>
    Guid = 6
}

/// <summary>
///     Flags relevant to defined enumerations
/// </summary>
[Flags]
public enum EnumFlags
{
    None = 0,

    /// <summary>
    ///     If set, denotes that this enumeration type represents a bitmask (like [Flags])
    /// </summary>
    Bitmask = 1,

    /// <summary>
    ///     If set, only values matching known enum field values (or comprising of enum field values for each
    ///     bit value, if this is a bitmask) are imported into the metaverse, and values not matching a
    ///     MvEnumValue definition are stipped off.
    /// </summary>
    KnownValuesOnly = 2,

    /// <summary>
    ///     If set, the numeric value is always displayed instead of enum value names
    /// </summary>
    DontUseName = 4
}

/// <summary>
///     Defines a known value in an MvEnum
/// </summary>
public class MvEnumValue : Item
{
    /// <summary>
    ///     The parent Enum which owns this value
    /// </summary>
    public MvEnum Enum { get; set; } = null!;

    /// <summary>
    ///     Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    ///     The label for this value
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     The string representation of the value defined
    /// </summary>
    public string? Value { get; set; }

    /// <summary>What the value means, when the label does not say it</summary>
    public string? Description { get; set; }
}