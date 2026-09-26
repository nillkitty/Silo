## Universal Database (`u.db`)
The Universal database holds ambient reference data that's the same for every Silo — the kind of thing you'd otherwise have to import yourself into each one: zip codes, area codes, US states, and units of measure/conversions.

### Tables
| Table | Description |
|--|--|
| `UniversalType` | Declares a type of universal data that's installed or present — its `Name`/`DisplayName`, the `TableName` that stores it, when it was `Introduced`, and its `SourceProvider`/`SourceUri` plus `DataExpires`/`DataUpdated` for keeping it fresh. |
| `UniversalProvider` | A possible source for a type of Universal data (`SourceProvider`, a human `Description`), and whether it's actually been `Included` in this Silo. |
| `Unit` | A unit of measure, conversion, prefix, or suffix definition — `Name`/`NamePlural`, `Formula`, `Formatter`, `Scale`, and whether it's `Reciprocal`. Related units link to each other via `Target`/`Relation`. See the [Units](Units) page for the tag syntax these support. |
| `UnitRelation` | A relation chain used by a `Unit` (its `Scale`, whether `Reciprocal`, and the `Next` link in the chain). |

### Enums
**`UnitType`** — `Alias`, `Conversion`, `Unit`, `Prefix`, `Suffix`.

### See Also
* [Data](Data)
* [Units](Units)
