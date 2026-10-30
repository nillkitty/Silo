using Silo.Providers.Data.Validation;
using Serilog;
using Silo.Commanding;
using Silo.Model;
using Silo.Providers.Action;
using Silo.Ui.Windows;

namespace Silo.Sync;

/// <summary>
///     "Sync to Metaverse..." on any model that describes its data (<see cref="ISchema" />: a folder, a registry key): takes the schema
///     into the Silo as a World (<see cref="WorldSchemaAbsorber" />), gives each type a default mapping the first time, and flows the
///     objects into the Metaverse (<see cref="InboundSync" />). Read-only towards the source.
/// </summary>
public sealed class MetaverseSyncActions : IActionProvider<ISchema>
{
    public IEnumerable<ProvidedCommand> GetCommands(ISchema source)
    {
        var silo = OpenSilo.OnlySilo ?? App.Instance?.ActiveSilo;
        if (source.SchemaUri is null || silo?.Data is not (IDatabaseProvider and IEntityStore)) yield break;

        yield return ProvidedCommand.From("metaverse.sync", "Sync to _Metaverse...", () => Sync(silo, source), "Refresh", () => silo.IsWritable,
                                          "Reads the schema and objects of this source into the Silo as a World, and flows them into the Metaverse (nothing is changed in the source)",
                                          "Silo", CommandPlacement.Menu);
    }

    private static void Sync(OpenSilo silo, ISchema source)
    {
        if (silo.Data is not (IDatabaseProvider db and IEntityStore store)) return;

        var types = source.GetSchema();
        if (types.Count == 0)
        {
            App.Status("This source describes no types.");
            return;
        }

        string? mvName = PromptDialog.Ask("Sync to Metaverse", $"The Metaverse type for the objects of {source.SchemaDisplayName} (used when it has no mapping yet):", "Sync",
                                          types[0].Name);
        if (mvName is null) return;

        try
        {
            App.Status($"Reading {source.SchemaDisplayName}...");
            string worldKey = WorldSchemaAbsorber.KeyFor(source.SchemaUri!);
            var    absorbed = WorldSchemaAbsorber.Absorb(db, store, worldKey, source);
            var    sync     = new InboundSync(db, store);

            var lines = new List<string>();
            foreach (var (type, fields) in absorbed.Types)
            {
                var (_, created) = sync.EnsureMapping(absorbed.World, type, fields, types.Count == 1 ? mvName : $"{mvName}.{type.Name}");
                var result = sync.Run(absorbed.World, type, source.GetObjects(type.Name));
                lines.Add($"{type.Name}{(created ? " (new mapping)" : string.Empty)}: {result}");
                lines.AddRange(result.Notes);
            }

            App.Status($"Metaverse sync of {source.SchemaDisplayName}: {lines[0]}");
            PromptDialog.Show("Sync to Metaverse",
                              $"World \"{absorbed.World.Key}\": {absorbed.TypesAdded} type(s), {absorbed.FieldsAdded} field(s) added, {absorbed.FieldsChanged} changed.\n\n" +
                              string.Join('\n', lines), ["OK"]);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Metaverse sync of {Source} failed", source.SchemaDisplayName);
            App.Status("Metaverse sync failed: " + ex.Message);
        }
    }
}
