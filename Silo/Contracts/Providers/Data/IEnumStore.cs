using Silo.DbModel.Meta;

namespace Silo.Contracts.Providers.Data;

/// <summary>One known value of a defined enum.</summary>
/// <param name="Id">The row's id; 0 for a value not saved yet.</param>
/// <param name="Name">The label shown for the value. Unique within its enum, without regard to case.</param>
/// <param name="Value">The value in its canonical text form for the enum's <see cref="EnumType" /> (see <c>EnumRules.Normalize</c>).</param>
/// <param name="Description">What the value means, when the label does not say it.</param>
public sealed record EnumValueRecord(int Id, string Name, string? Value, string? Description = null);

/// <summary>A defined enum with all its known values.</summary>
/// <param name="Id">The row's id; 0 for an enum not saved yet.</param>
/// <param name="Name">Unique name; this is what <c>enum:Name</c> refers to.</param>
/// <param name="Description">What the enum is for.</param>
/// <param name="Type">What the values are (integers, strings, guids ...).</param>
/// <param name="Flags">Whether the enum is a bitmask, accepts only known values, or always shows numbers.</param>
/// <param name="Values">The known values, in the order they were added.</param>
public sealed record EnumRecord(int Id, string Name, string? Description, EnumType Type, EnumFlags Flags, IReadOnlyList<EnumValueRecord> Values)
{
    /// <summary>A new, empty enum of the given type.</summary>
    public static EnumRecord Create(string name, EnumType type = EnumType.Integral, EnumFlags flags = EnumFlags.None) => new(0, name, null, type, flags, []);
}

/// <summary>
///     The enums defined in a Silo (the <c>MvEnum</c> and <c>MvEnumValue</c> tables), listed, created, changed and deleted.
///     Saving checks the definition (<c>EnumRules</c>) and throws <see cref="DefinitionException" /> when it is not
///     acceptable. Every successful change is a real change to the Silo: it raises <see cref="IDatabaseChanges.Changed" />.
/// </summary>
public interface IEnumStore
{
    /// <summary>Every enum with its values, ordered by name.</summary>
    IReadOnlyList<EnumRecord> GetEnums();

    /// <summary>The enum with that id, or null.</summary>
    EnumRecord? GetEnum(int id);

    /// <summary>The enum with that name (any case), or null.</summary>
    EnumRecord? FindEnum(string name);

    /// <summary>
    ///     Adds the enum (<see cref="EnumRecord.Id" /> 0) or replaces the one it names, header and values together: values
    ///     with an id are updated, values without one are added, values the record no longer has are deleted.
    ///     Returns what was stored (ids filled in, values in canonical form).
    /// </summary>
    EnumRecord SaveEnum(EnumRecord definition);

    /// <summary>Deletes the enum and all its values. Returns whether there was one.</summary>
    bool DeleteEnum(int id);

    /// <summary>Adds the value (<see cref="EnumValueRecord.Id" /> 0) to the enum, or changes the one it names. Returns what was stored.</summary>
    EnumValueRecord SaveEnumValue(int enumId, EnumValueRecord value);

    /// <summary>Deletes one value of an enum. Returns whether there was one.</summary>
    bool DeleteEnumValue(int valueId);
}
