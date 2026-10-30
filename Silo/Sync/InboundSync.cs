using Silo.Providers.Data.Validation;
using System.Globalization;
using Silo.DbModel.Meta;
using Silo.DbModel.Worlds;

namespace Silo.Sync;

/// <summary>What one inbound sync did.</summary>
/// <param name="Seen">The objects read from the source.</param>
/// <param name="Joined">Objects linked to a Metaverse object that was already there (by a join rule).</param>
/// <param name="Projected">Objects that got a new Metaverse object (by the projection rule).</param>
/// <param name="Updated">Linked objects whose Metaverse object took new values.</param>
/// <param name="Unchanged">Linked objects that had nothing new.</param>
/// <param name="Unlinked">Objects no rule linked (they stay in the World only).</param>
/// <param name="Gone">Linked objects the source no longer has; the links are kept (read-only sync), so the person can see them.</param>
/// <param name="Notes">Things worth saying: rules that are stored but not supported yet, the first few missing objects.</param>
public sealed record SyncResult(int Seen, int Joined, int Projected, int Updated, int Unchanged, int Unlinked, int Gone, IReadOnlyList<string> Notes)
{
    public override string ToString()
    {
        return $"{Seen} read: {Projected} projected, {Joined} joined, {Updated} updated, {Unchanged} unchanged, {Unlinked} not linked, {Gone} gone from the source";
    }
}

/// <summary>
///     The inbound half of the Metaverse sync, read-only towards the source: reads the objects of a World type and flows them into the
///     Metaverse under the type <see cref="WorldMapping" />.
///     <list type="number">
///         <item>An object that has a <see cref="Connector" /> already updates its Metaverse object from the mapped fields.</item>
///         <item>Else the mapping <see cref="JoinRules" /> look for an existing Metaverse object to link it to.</item>
///         <item>Else, if the <see cref="ProjRules" /> are enabled and the filter passes, it gets a new Metaverse object.</item>
///         <item>Else it is left unlinked.</item>
///     </list>
///     Only fields with a <see cref="FieldMapping" /> that has <see cref="FieldMappingFlags.Import" /> flow. Nothing is written to the source and
///     nothing is deleted: a source object that went away leaves its connector in place and is counted in <see cref="SyncResult.Gone" />.
/// </summary>
public sealed class InboundSync(IDatabaseProvider db, IEntityStore store)
{
    /// <summary>
    ///     Makes sure the type has a mapping: when it has none, adds one to a Metaverse type of the given name that imports every field and
    ///     projects every object. Returns the mapping and whether it was created.
    /// </summary>
    public (WorldMapping Mapping, bool Created) EnsureMapping(World world, WorldType type, IReadOnlyList<WorldField> fields, string mvTypeName)
    {
        var existing = store.ListItems<WorldMapping>(world.Key).Where(m => m.WorldType?.Id == type.Id).OrderByDescending(m => m.Priority).FirstOrDefault();
        if (existing is not null) return (existing, false);

        var mapping = new WorldMapping
                      {
                          Id         = store.ListItems<WorldMapping>(world.Key).Select(m => m.Id).DefaultIfEmpty(0).Max() + 1,
                          World      = world,
                          WorldType  = type,
                          MvTypeName = mvTypeName,
                          Priority   = 0,
                          Projection = new ProjRules { Enabled = true }
                      };
        db.AddItem(mapping);

        int next = store.ListItems<FieldMapping>(world.Key).Select(m => m.Id).DefaultIfEmpty(0).Max() + 1;
        foreach (var field in fields)
            db.AddItem(new FieldMapping
                       {
                           Id          = next++,
                           Field       = field,
                           MvTypeName  = mvTypeName,
                           MvFieldName = field.Name,
                           Conversion  = string.Empty,
                           Flags       = FieldMappingFlags.Import
                       });

        return (mapping, true);
    }

    /// <summary>Flows <paramref name="objects" /> (all the objects of <paramref name="type" /> in the source) into the Metaverse.</summary>
    public SyncResult Run(World world, WorldType type, IEnumerable<WorldObject> objects, CancellationToken cancel = default)
    {
        var notes = new List<string>();

        var mapping = store.ListItems<WorldMapping>(world.Key).Where(m => m.WorldType?.Id == type.Id).OrderByDescending(m => m.Priority).FirstOrDefault();
        if (mapping is null)
        {
            int count = objects.Count();
            notes.Add($"\"{type.Name}\" has no mapping to the Metaverse.");
            return new SyncResult(count, 0, 0, 0, 0, count, 0, notes);
        }

        var fieldMaps = store.ListItems<FieldMapping>(world.Key)
                             .Where(m => m.Field?.WorldType?.Id == type.Id && m.Flags.HasFlag(FieldMappingFlags.Import))
                             .ToList();

        // the Metaverse type the objects flow into
        var mvTypes = store.ListItems<MvType>();
        var mvType  = mvTypes.FirstOrDefault(t => string.Equals(t.Name, mapping.MvTypeName, StringComparison.OrdinalIgnoreCase));
        if (mvType is null)
        {
            mvType = new MvType { Id = mvTypes.Select(t => t.Id).DefaultIfEmpty(0).Max() + 1, Name = mapping.MvTypeName, DisplayName = mapping.MvTypeName };
            db.AddItem(mvType);
        }

        var mvObjs     = store.ListItems<MvObj>().Where(o => o.Type?.Id == mvType.Id).ToList();
        var byId       = mvObjs.ToDictionary(o => o.Id);
        var connectors = store.ListItems<Connector>().ToList();
        var mine       = connectors.Where(c => string.Equals(c.World, world.Key, StringComparison.OrdinalIgnoreCase) &&
                                               string.Equals(c.WorldType, type.Name, StringComparison.OrdinalIgnoreCase))
                                    .ToDictionary(c => c.Key, StringComparer.Ordinal);
        long nextObj  = store.ListItems<MvObj>().Select(o => o.Id).DefaultIfEmpty(0).Max() + 1;
        long nextConn = connectors.Select(c => c.Id).DefaultIfEmpty(0).Max() + 1;

        var join = mapping.Joining;
        if (join is { Type: JoinType.FieldReferenceEqualsOrParent or JoinType.FieldReferenceEqualsOrChild or JoinType.Extension })
        {
            notes.Add($"The join rule {join.Type} is not supported yet; objects are only joined when a rule of the simpler kind says so.");
            join = null;
        }

        int joined = 0, projected = 0, updated = 0, unchanged = 0, unlinked = 0, seen = 0;
        var present = new HashSet<string>(StringComparer.Ordinal);
        var now     = DateTime.UtcNow;

        foreach (var obj in objects)
        {
            cancel.ThrowIfCancellationRequested();
            seen++;
            present.Add(obj.Key);

            var flowed = Flow(obj, fieldMaps);

            // 1. already linked: bring the Metaverse object up to date
            if (mine.TryGetValue(obj.Key, out var linked))
            {
                if (linked.MvObj is { } mv && byId.TryGetValue(mv.Id, out var current) && Apply(current, flowed, now)) updated++;
                else unchanged++;
                continue;
            }

            // 2. a join rule finds an object that is there already
            MvObj? target = null;
            if (join is { Type: not JoinType.NoJoin, MvFieldName: { Length: > 0 } mvField, WorldField: { } joinField } &&
                obj.Values.TryGetValue(joinField.Name, out var wanted) && wanted is not null)
            {
                string key = Text(wanted);
                target = mvObjs.FirstOrDefault(o => o.FieldValues.TryGetValue(mvField, out var have) && have is not null &&
                                                    string.Equals(Text(have), key, StringComparison.OrdinalIgnoreCase) &&
                                                    !connectors.Any(c => c.MvObj?.Id == o.Id && string.Equals(c.World, world.Key, StringComparison.OrdinalIgnoreCase)));
            }

            LinkedVia how = LinkedVia.Join;

            // 3. else a projection makes a new one
            if (target is null && mapping.Projection is { Enabled: true } projection && Passes(projection.Filter, obj.Values))
            {
                target = new MvObj
                         {
                             Id       = nextObj++,
                             Type     = mvType,
                             Key      = Guid.NewGuid().ToString("N"),
                             Created  = now,
                             Modified = now
                         };
                target.LocalUri = new Uri($"silo://metaverse/{Uri.EscapeDataString(mvType.Name)}/{target.Key}");
                how             = LinkedVia.Projection;
                mvObjs.Add(target);
                byId[target.Id] = target;
            }

            if (target is null)
            {
                unlinked++;
                continue;
            }

            Apply(target, flowed, now, force: true);
            var connector = new Connector
                            {
                                Id           = nextConn++,
                                MvObj        = target,
                                World        = world.Key,
                                WorldType    = type.Name,
                                Key          = obj.Key,
                                AbsolutePath = obj.AbsolutePath ?? string.Empty,
                                LinkedVia    = how,
                                LinkedAt     = now
                            };
            db.AddItem(connector);
            connectors.Add(connector);
            mine[obj.Key] = connector;
            if (how == LinkedVia.Join) joined++;
            else projected++;
        }

        // what the source no longer has: reported, not removed
        var gone = mine.Values.Where(c => !present.Contains(c.Key)).ToList();
        foreach (var c in gone.Take(5)) notes.Add($"No longer in the source: {c.Key}");
        if (gone.Count > 5) notes.Add($"...and {gone.Count - 5} more no longer in the source.");

        return new SyncResult(seen, joined, projected, updated, unchanged, unlinked, gone.Count, notes);
    }

    // ---- flow ----------------------------------------------------------------------------------

    /// <summary>The values of the object under the Metaverse field names, by the field mappings.</summary>
    private static Dictionary<string, object> Flow(WorldObject obj, IReadOnlyList<FieldMapping> maps)
    {
        var flowed = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in maps)
        {
            if (map.Field is null || string.IsNullOrEmpty(map.MvFieldName)) continue;

            obj.Values.TryGetValue(map.Field.Name, out var value);
            if (value is null)
            {
                if (map.Flags.HasFlag(FieldMappingFlags.ImportNulls)) flowed[map.MvFieldName] = string.Empty;
                continue;
            }

            flowed[map.MvFieldName] = ApplyConversion(value, map.Conversion);
        }

        return flowed;
    }

    /// <summary>A stored conversion: empty keeps the value; "upper", "lower" and "trim" change a string; others are ignored.</summary>
    private static object ApplyConversion(object value, string? conversion)
    {
        if (string.IsNullOrWhiteSpace(conversion) || value is not string text) return value;

        return conversion.Trim().ToLowerInvariant() switch
               {
                   "upper" => text.ToUpperInvariant(),
                   "lower" => text.ToLowerInvariant(),
                   "trim"  => text.Trim(),
                   _       => value
               };
    }

    /// <summary>Sets the values on the Metaverse object and saves it; false (nothing saved) when they are all the same already.</summary>
    private bool Apply(MvObj target, Dictionary<string, object> flowed, DateTime now, bool force = false)
    {
        bool changed = force;
        foreach (var (name, value) in flowed)
        {
            if (target.FieldValues.TryGetValue(name, out var have) && have is not null && Text(have) == Text(value)) continue;

            target.FieldValues[name] = value;
            changed                  = true;
        }

        if (!changed) return false;

        target.Modified = now;
        db.AddItem(target);
        return true;
    }

    private static string Text(object value)
    {
        return value switch
               {
                   DateTime d => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                   IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                   _ => value.ToString() ?? string.Empty
               };
    }

    // ---- projection filters --------------------------------------------------------------------

    /// <summary>Whether the object passes the filter (no filter passes everything).</summary>
    public static bool Passes(WorldFilter? filter, IReadOnlyDictionary<string, object?> values)
    {
        return filter is null || Evaluate(filter, values, 0);
    }

    private static bool Evaluate(WorldFilter filter, IReadOnlyDictionary<string, object?> values, int depth)
    {
        if (depth > 32) return false; // a filter that refers back to itself

        var  op       = filter.Operation;
        bool negate   = op.HasFlag(FilterOperation.Negate);
        var  field    = filter.Field is { } f && values.TryGetValue(f.Name, out var v) ? v : null;
        bool present  = filter.Field is not null && values.ContainsKey(filter.Field.Name);

        bool own;
        if (op.HasFlag(FilterOperation.FieldValueStringMatch) || op.HasFlag(FilterOperation.FieldValueReferenceMatch))
            own = field is not null && string.Equals(Text(field), filter.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        else if (op.HasFlag(FilterOperation.FieldValueNumericMatch))
            own = field is not null && double.TryParse(Text(field), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                  double.TryParse(filter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var wanted) && number.Equals(wanted);
        else if (op.HasFlag(FilterOperation.FieldValueExists))
            own = present;
        else if (op.HasFlag(FilterOperation.FieldValuePresent))
            own = present && field is not null && Text(field).Length > 0;
        else
            own = true; // True / False: no test of its own

        if (negate) own = !own;

        // chained: this test, combined with the next
        if (filter.Ref is { } next)
        {
            if (op.HasFlag(FilterOperation.AndChained)) return own && Evaluate(next, values, depth + 1);
            if (op.HasFlag(FilterOperation.OrChained)) return own || Evaluate(next, values, depth + 1);
        }

        return own;
    }
}
