## World Database(s) (`w-*.db`)
A Silo has one World database per connected data source (one per **World**), storing that source's schema and a cached mirror of its data in the source's own native shape — plus the rules for how objects from it map into the [Metaverse](MetaverseDb).

### Tables
| Table | Description |
|--|--|
| `World` | Defines a World — its `Provider` type, root `Uri`, a `Key` unique within the Silo (used in the database's own file name, and can't be changed), a renamable `DisplayName`, and `Flags`. |
| `WorldType` | A type of object a World owns — `Provider`, `Name`/`DisplayName`, `NativeName`, an optional schema `Uri`, and `Flags`. |
| `WorldField` | A field on a `WorldType` — its ordinal `Index`, `NativeName`/`NativeType`, the .NET `DataType` used to represent it, an optional schema `Uri`, and `Flags`. |
| `FieldMapping` | Maps a `WorldField` to a metaverse field (`MvTypeName`/`MvFieldName`), how values should be converted, and `Flags` controlling import/export direction. |
| `WorldMapping` | Maps a `WorldType` to a metaverse type (`MvTypeName`), with a `Priority` and optional `Joining`/`Projection`/`Provisioning` rules (see below). |
| `WorldSync` | A sync job profile: which `World`/`WorldType` to sync, on what `SyncInterval`, how to import (`ImportType`) and synchronize (`SyncType`), and at what `Priority`. |

### Join, Projection & Provisioning rules
These configure the three ways a World object can end up linked into the Metaverse (see `LinkedVia` on [Metaverse Database](MetaverseDb)), and hang off a `WorldMapping`:

* **`JoinRules`** — how to auto-join a `WorldType`'s objects to an *existing* metaverse object, by comparing an `MvFieldName` against a `WorldField` (`Type` says how: `NoJoin`, `FieldValueEquals`, `FieldReferenceEquals`, `FieldReferenceEqualsOrParent`, `FieldReferenceEqualsOrChild`, or `Extension` for a custom join provider).
* **`ProjRules`** — whether (`Enabled`) and under what `Filter` (a `WorldFilter`, built from `FilterOperation` flags like string/numeric/reference match, existence, and `AndChained`/`OrChained` composition) a `WorldType`'s objects auto-project a *new* metaverse connector.
* **`ProvRules`** — which provider (`ProviderType`) and named rule (`RuleName`) provisions a *new* object into the Metaverse for this mapping.

### Enums
* **`ImportType`** — `None`, `Cache`, `CacheTemp`, `Delta`, `Full`.
* **`SyncType`** *(flags)* — `NoSync`, `Sync`, `Full`, `Provision`, `Join`, `Project`, `FlowToMv`, `Default`.
* **`WorldFlags`** — `None`, `NeverMirror`, `NeverCache`.
* **`WorldTypeFlags`** *(flags)* — `None`, `NeverMirror`, `NeverCache`, `AuthoritativeSource`, `CacheInTemp`, `Mapping`.
* **`WorldFieldFlags`** *(flags)* — `None`, `Key`, `Index`, `Anr`, `Reference`, `Referral`, `Creation`, `ModifyDate`, `AbsolutePath`, `RelativePath`, `Nullable`, `Multivalue`.
* **`FieldMappingFlags`** *(flags)* — `None`, `Import`, `Export`, `ImportNulls`, `ExportNulls`, `Reconciled`.
* **`FilterOperation`** *(flags)* — `True`, `Negate`, `False`, `FieldValueStringMatch`, `FieldValueNumericMatch`, `FieldValueReferenceMatch`, `FieldValueExists`, `FieldValuePresent`, `AndChained`, `OrChained`.

### See Also
* [Data](Data)
* [Metaverse Database](MetaverseDb)
* [Connectors](Connectors)
