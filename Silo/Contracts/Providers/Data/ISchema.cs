using Silo.DbModel.Worlds;

namespace Silo.Providers.Data;

/// <summary>One field of a <see cref="SchemaType" /> as the source system describes it.</summary>
/// <param name="Name">The field name in the source.</param>
/// <param name="NativeType">The source own name for its type ("REG_SZ", "FileAttributes").</param>
/// <param name="DataType">The .NET type values of the field have when read.</param>
/// <param name="Flags">What the field is for (its key, the path...).</param>
public sealed record SchemaField(string Name, string NativeType, Type DataType, WorldFieldFlags Flags = WorldFieldFlags.None);

/// <summary>One kind of object a source holds (a table, a class, a kind of file system entry) and its fields.</summary>
/// <param name="Name">What the Silo calls the type.</param>
/// <param name="NativeName">The source name for it.</param>
/// <param name="Fields">The fields, in order.</param>
public sealed record SchemaType(string Name, string? NativeName, IReadOnlyList<SchemaField> Fields);

/// <summary>An object read from a source: the values of the fields of its <see cref="SchemaType" />.</summary>
/// <param name="Key">Identifies it in the source (unchanged for as long as it exists).</param>
/// <param name="AbsolutePath">Where it is in the source, when it has a place.</param>
/// <param name="Values">The values by field name; a missing field has no value.</param>
public sealed record WorldObject(string Key, string? AbsolutePath, IReadOnlyDictionary<string, object?> Values);

/// <summary>
///     A connection or node model that can describe the data it holds (its types and fields: the Silo absorbs that into a World
///     database, see <c>WorldSchemaAbsorber</c>) and list the objects of each type (what the Metaverse imports from).
/// </summary>
public interface ISchema
{
    /// <summary>The name of the World provider: stored with the World, and tells which kind of source it is ("file", "registry").</summary>
    string SchemaProvider { get; }

    /// <summary>What a World made from this source is called.</summary>
    string SchemaDisplayName { get; }

    /// <summary>Where the source is (the World root URI).</summary>
    Uri? SchemaUri { get; }

    /// <summary>The types of object the source holds, with their fields.</summary>
    IReadOnlyList<SchemaType> GetSchema();

    /// <summary>The objects of one type, as they are now (lazily; the caller may stop early).</summary>
    IEnumerable<WorldObject> GetObjects(string typeName, CancellationToken cancel = default);
}
