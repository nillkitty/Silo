## Databases
A `.silo` file is a zip archive of several SQLite databases, each with a specific role. Every Silo has a Global database; the rest are created as needed.

| File | Database | Purpose |
|--|--|--|
| `z.db` | [Global Database](GlobalDb) | Holds Header, User, File, Connection, Location, Pref, Secret, Node, Assembly, Window, Audit, and Authorization data — the only required file in a Silo. |
| `z.db` (stub) | [Stub Database](StubDb) | For an encrypted Silo, a reduced, unencrypted `z.db` (Header + key material) sits in the outer archive while the rest of the databases are wrapped in an inner encrypted archive. |
| `x.db` | [Temp Database](TempDb) | Scratch/session state — variables, pasted data, MRU lists, and other things that don't need to survive a "real" save. |
| `u.db` | [Universal Database](UniversalDb) | Ambient reference data that's the same for everyone: zip codes, area codes, states, and units of measure/conversions. |
| `w-*.db` | [World Database(s)](WorldDb) | One per connected data source (World) — the schema, fields, and cached rows in that source's own native shape, plus the rules for how it maps into the Metaverse. |
| `y.db` | [Metaverse Database](MetaverseDb) | Normalized/aggregated objects (`MvType`/`MvAttribute`/`MvObj`) derived by joining and projecting from one or more World sources. |

### See Also
* [Silo](Silo)
* [Connectors](Connectors)
