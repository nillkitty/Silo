using Silo.Providers.Data.Validation;
using System.Security.Cryptography;
using System.Text;
using Silo.DbModel.Worlds;

namespace Silo.Sync;

/// <summary>What absorbing a source schema changed in the Silo.</summary>
/// <param name="World">The World the source is kept as.</param>
/// <param name="Types">The types of the World, with their fields, as they are now.</param>
/// <param name="TypesAdded">How many types are new.</param>
/// <param name="FieldsAdded">How many fields are new.</param>
/// <param name="FieldsChanged">How many fields that were there have a different type or flags now.</param>
/// <param name="FieldsGone">The fields the Silo has that the source no longer describes (kept: a mapping may use them).</param>
public sealed record AbsorbResult(
    World                                    World,
    IReadOnlyList<(WorldType Type, IReadOnlyList<WorldField> Fields)> Types,
    int                                      TypesAdded,
    int                                      FieldsAdded,
    int                                      FieldsChanged,
    int                                      FieldsGone);

/// <summary>
///     Takes the schema a source describes (<see cref="ISchema" />) into the Silo: a <see cref="World" /> for the source, a
///     <see cref="WorldType" /> for each of its types and a <see cref="WorldField" /> for each field, in the World own database. Run again, it
///     brings them up to date: names match what is there, so ids and any mappings that refer to them stay.
/// </summary>
public static class WorldSchemaAbsorber
{
    /// <summary>The key of the World for a source URI: stable, and fit for a file name.</summary>
    public static string KeyFor(Uri source)
    {
        string prefix = source.IsFile ? "file" : source.Scheme.ToLowerInvariant();
        string hash   = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(source.AbsoluteUri.ToLowerInvariant())))[..10].ToLowerInvariant();
        return $"{prefix}-{hash}";
    }

    /// <summary>Absorbs <paramref name="schema" /> as the World with <paramref name="worldKey" />, creating it if the Silo has none.</summary>
    public static AbsorbResult Absorb(IDatabaseProvider db, IEntityStore store, string worldKey, ISchema schema)
    {
        var world = store.ListWorlds().FirstOrDefault(w => string.Equals(w.Key, worldKey, StringComparison.OrdinalIgnoreCase));
        if (world is null)
        {
            world = new World
                    {
                        Id          = store.ListWorlds().Select(w => w.Id).DefaultIfEmpty(0).Max() + 1,
                        Key         = worldKey,
                        DisplayName = schema.SchemaDisplayName,
                        Provider    = schema.SchemaProvider,
                        Uri         = schema.SchemaUri ?? new Uri("silo://" + worldKey + "/"),
                        Flags       = WorldFlags.None
                    };
            db.AddItem(world);
        }

        var types  = store.ListItems<WorldType>(world.Key).ToList();
        var fields = store.ListItems<WorldField>(world.Key).ToList();
        int nextType  = types.Select(t => t.Id).DefaultIfEmpty(0).Max() + 1;
        int nextField = fields.Select(f => f.Id).DefaultIfEmpty(0).Max() + 1;
        int typesAdded = 0, fieldsAdded = 0, fieldsChanged = 0, fieldsGone = 0;

        var result = new List<(WorldType, IReadOnlyList<WorldField>)>();
        foreach (var described in schema.GetSchema())
        {
            var type = types.FirstOrDefault(t => string.Equals(t.Name, described.Name, StringComparison.OrdinalIgnoreCase));
            if (type is null)
            {
                type = new WorldType
                       {
                           Id          = nextType++,
                           Name        = described.Name,
                           DisplayName = described.Name,
                           NativeName  = described.NativeName ?? described.Name,
                           Provider    = world.Provider,
                           Flags       = WorldTypeFlags.None,
                           World       = world
                       };
                db.AddItem(type);
                types.Add(type);
                typesAdded++;
            }

            var mine = fields.Where(f => f.WorldType?.Id == type.Id).ToList();
            var kept = new List<WorldField>();
            for (int i = 0; i < described.Fields.Count; i++)
            {
                var source = described.Fields[i];
                var field  = mine.FirstOrDefault(f => string.Equals(f.Name, source.Name, StringComparison.OrdinalIgnoreCase));
                bool isNew = field is null;
                field ??= new WorldField { Id = nextField++, Name = source.Name, World = world, WorldType = type };

                string dataType = source.DataType.FullName ?? source.DataType.Name;
                bool   changed  = field.Index != i || field.NativeType != source.NativeType || field.DataType != dataType || field.Flags != source.Flags;
                if (!isNew && !changed)
                {
                    kept.Add(field);
                    continue;
                }

                field.Index      = i;
                field.NativeType = source.NativeType;
                field.DataType   = dataType;
                field.Flags      = source.Flags;
                db.AddItem(field);
                fields.Add(field);
                kept.Add(field);
                if (isNew) fieldsAdded++;
                else fieldsChanged++;
            }

            fieldsGone += mine.Count(f => described.Fields.All(d => !string.Equals(d.Name, f.Name, StringComparison.OrdinalIgnoreCase)));
            result.Add((type, kept));
        }

        return new AbsorbResult(world, result, typesAdded, fieldsAdded, fieldsChanged, fieldsGone);
    }
}
