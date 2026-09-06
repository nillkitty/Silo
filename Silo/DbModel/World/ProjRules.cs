using Telefrag.Common;

namespace Silo.DbModel.World;

/// <summary>
/// Defines if and how this WorldType auto-projects a connector into the metaverse.
/// </summary>
public class ProjRules
{
    /// <summary>
    /// A filter which, if defined, must match the objects, or they will not be projected.
    /// </summary>
    public WorldFilter? Filter { get; set; }

    /// <summary>
    /// Gets or sets whether projection is enabled.
    /// </summary>
    public bool Enabled { get; set; }
}

public record WorldFilter(
    int             Id,
    FilterOperation Operation,
    WorldFilter?    Ref,
    WorldField?     Field,
    string?         Value);

public enum FilterOperation
{
    /// <summary>
    /// Always evaluates to true (filters nothing)
    /// </summary>
    True = 0,

    /// <summary>
    /// Always evaluates to false (filters all objects)
    /// </summary>
    Negate = 1,

    /// <summary>
    /// Combine with a value to negate its result
    /// </summary>
    False = Negate | True,

    /// <summary>
    /// Performs a string match 
    /// </summary>
    FieldValueStringMatch = 2,

    /// <summary>
    /// Performs a numeric match
    /// </summary>
    FieldValueNumericMatch = 4,

    /// <summary>
    /// Performs a reference match
    /// </summary>
    FieldValueReferenceMatch = 8,

    /// <summary>
    /// Evaluates to true if a value is present (not null)
    /// </summary>
    FieldValueExists = 0x10,

    /// <summary>
    /// Evaluates to true if a value is present and contains at least one byte (non-empty)
    /// </summary>
    FieldValuePresent = 0x20,

    /// <summary>
    /// Also requires that the referenced Filter rule must also evaluate to true.
    /// </summary>
    AndChained = 0x40,

    /// <summary>
    /// Falls back to the referenced Filter rule if this rule does not pass, creating a logical 'OR'.
    /// </summary>
    OrChained = 0x80
}