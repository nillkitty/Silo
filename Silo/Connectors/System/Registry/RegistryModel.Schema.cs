using Silo.DbModel.Worlds;

namespace Silo.Connectors.System;

/// <summary>A registry key as a data source: one type, <c>RegistryValue</c>, with a row for each value in the key.</summary>
public partial class RegistryModel : ISchema
{
    /// <summary>The one type a registry source has.</summary>
    public const string ValueTypeName = "RegistryValue";

    /// <inheritdoc />
    public string SchemaProvider => "registry";

    /// <inheritdoc />
    public string SchemaDisplayName => ContainerName;

    /// <inheritdoc />
    public Uri? SchemaUri => Uri;

    /// <inheritdoc />
    public IReadOnlyList<SchemaType> GetSchema()
    {
        return
        [
            new SchemaType(ValueTypeName, "RegistryValue",
            [
                new SchemaField("Path",    "string",            typeof(string), WorldFieldFlags.Key | WorldFieldFlags.AbsolutePath),
                new SchemaField("KeyPath", "string",            typeof(string), WorldFieldFlags.Reference),
                new SchemaField("Name",    "string",            typeof(string), WorldFieldFlags.RelativePath),
                new SchemaField("Kind",    "RegistryValueKind", typeof(string)),
                new SchemaField("Data",    "object",            typeof(string), WorldFieldFlags.Nullable)
            ])
        ];
    }

    /// <inheritdoc />
    public IEnumerable<WorldObject> GetObjects(string typeName, CancellationToken cancel = default)
    {
        if (!string.Equals(typeName, ValueTypeName, StringComparison.OrdinalIgnoreCase) || !CanEnumerate) yield break;

        foreach (var item in EnumerateItems(false, cancel))
        {
            int mark = item.Name.IndexOf('?');
            if (mark < 0) continue;

            string signature = item.Signature;
            int    colon     = signature.IndexOf(':');
            yield return new WorldObject(item.Name, item.Name,
                                         new Dictionary<string, object?>
                                         {
                                             ["Path"]    = item.Name,
                                             ["KeyPath"] = item.Name[..mark],
                                             ["Name"]    = item.Name[(mark + 1)..],
                                             ["Kind"]    = colon > 0 ? signature[..colon] : string.Empty,
                                             ["Data"]    = colon > 0 ? signature[(colon + 1)..] : signature
                                         });
        }
    }
}
